// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.IdentityModel.Tokens;

#pragma warning disable RS0016 // Add public types and members to the declared API
namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    public sealed class OrmColumn
    {
        public string PropertyName { get; }
        public string ColumnName { get; }

        /// <summary>
        /// The property or field this column was built from, so a column named in a sql block can
        /// be navigated to the member backing it. Null only for columns not produced by walking a
        /// compilation. See <see cref="OrmTable.Symbol"/>.
        /// </summary>
        public ISymbol? Symbol { get; }

        internal OrmColumn(string propertyName, string columnName, ISymbol? symbol = null)
        {
            PropertyName = propertyName;
            ColumnName = columnName;
            Symbol = symbol;
        }
    }

    public sealed class OrmTable
    {
        public string TableName { get; }
        public ImmutableArray<OrmColumn> Columns { get; }

        public INamedTypeSymbol? Symbol { get; }

        public bool IsTenantTable { get; }

        /// <summary>
        /// The column written as <paramref name="columnName"/> in sql. Matches the database column
        /// name first and the property name second - they differ only when [DbField(Label=...)]
        /// renames one - and is case-insensitive because sql identifiers are.
        /// </summary>
        public OrmColumn? FindColumn(string columnName)
        {
            foreach (var column in Columns)
            {
                if (string.Equals(column.ColumnName, columnName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return column;
                }
            }

            foreach (var column in Columns)
            {
                if (string.Equals(column.PropertyName, columnName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return column;
                }
            }

            return null;
        }

        internal OrmTable(string tableName, ImmutableArray<OrmColumn> columns, INamedTypeSymbol? symbol = null, bool isTenantTable = true)
        {
            TableName = tableName;
            Columns = columns;
            Symbol = symbol;
            IsTenantTable = isTenantTable;
        }
    }

    public sealed class OrmSchema
    {
        public ImmutableArray<OrmTable> Tables { get; }
        private readonly Dictionary<string, OrmTable> _byName;

        internal OrmSchema(ImmutableArray<OrmTable> tables)
        {
            Tables = tables;
            _byName = new Dictionary<string, OrmTable>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables)
            {
                // First declaration wins if two [Orm] classes somehow resolve to the same table name.
                _byName.TryAdd(table.TableName, table);
            }
        }

        public OrmTable? FindTable(string tableName) =>
            _byName.TryGetValue(tableName, out var table) ? table : null;
    }

    /// <summary>
    /// Walks a <see cref="Compilation"/>'s source types and referenced-assembly types for
    /// classes marked with a marker attribute named "Orm"/"OrmAttribute", producing a schema
    /// usable in place of a live database connection.
    ///
    /// Cached per-Compilation instance: compilations are immutable, and a new one is produced
    /// whenever the user edits, so a ConditionalWeakTable keyed on the Compilation reference
    /// gives exact invalidation for free - no modify_date/timestamp polling required the way
    /// SqlCompletionQueries needed against a live DB.
    /// </summary>
    public static class OrmSchemaProvider
    {
        private static readonly ConditionalWeakTable<Compilation, OrmSchema> s_cache = new();

        public static OrmSchema GetSchema(Compilation compilation)
        {
            return s_cache.GetValue(compilation, static c => Build(c));
        }

        private static OrmSchema Build(Compilation compilation)
        {
            var tables = new List<OrmTable>();
            var seenAssemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);

            void WalkAssembly(IAssemblySymbol assembly)
            {
                if (!seenAssemblies.Add(assembly))
                {
                    return;
                }

                WalkNamespace(assembly.GlobalNamespace, tables);
            }

            WalkAssembly(compilation.Assembly);

            // Also pick up [Orm] classes declared in referenced libraries, not just this
            // compilation's own source - GetAttributes() works the same way over PE-backed
            // symbols as it does over source-backed ones, so this is the same walk.
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol referencedAssembly)
                {
                    WalkAssembly(referencedAssembly);
                }
            }

            return new OrmSchema(tables.ToImmutableArray());
        }

        private static void WalkNamespace(INamespaceSymbol ns, List<OrmTable> tables)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol nestedNamespace)
                {
                    WalkNamespace(nestedNamespace, tables);
                }
                else if (member is INamedTypeSymbol type)
                {
                    WalkType(type, tables);
                }
            }
        }

        private static void WalkType(INamedTypeSymbol type, List<OrmTable> tables)
        {
            TryAddTable(type, tables);

            // Recurse into nested types in case entity classes are nested (e.g. inside a
            // partial "schema" container class) rather than declared at namespace scope.
            foreach (var nestedType in type.GetTypeMembers())
            {
                WalkType(nestedType, tables);
            }
        }

        private static void TryAddTable(INamedTypeSymbol type, List<OrmTable> tables)
        {
            var ormAttribute = type.GetAttributes().FirstOrDefault(a => IsMarkerAttribute(a.AttributeClass, "Orm"));
            if (ormAttribute is null)
            {
                return;
            }

            var tableName = GetNamedArgumentString(ormAttribute, "TableName") ?? type.Name;
            bool isTenantTable = !(GetNamedArgumentBoolean(ormAttribute, "NonTenantTable") ?? false);

            tables.Add(new OrmTable(tableName, GetColumns(type), type, isTenantTable));
        }

        /// <summary>
        /// The columns a type declares, by the same rule whether or not it is marked [Orm].
        /// </summary>
        /// <remarks>
        /// Applied to an [Orm] class this builds that table's column list. It is also applied to
        /// the target of a wildcard binding ("SELECT a.*[obj]"), which deliberately need not be an
        /// [Orm] class: a plain DTO can be filled from a table as long as every member it declares
        /// corresponds to a column. Sharing the rule is the point - a member that counts as a
        /// column on one side has to count as one on the other, [DbField(Label=...)] renames
        /// included, or the two would disagree about what "matching" means.
        /// </remarks>
        public static ImmutableArray<OrmColumn> GetColumns(INamedTypeSymbol type)
        {
            var columns = ImmutableArray.CreateBuilder<OrmColumn>();
            foreach (var member in type.GetMembers())
            {
                // Columns can be either auto-properties ("public string Foo { get; set; }")
                // or plain fields ("public string Foo;") - the example ORM classes use both
                // styles across different tables, so both need to be picked up here. Skip
                // implicitly-declared members so an auto-property's compiler-generated backing
                // field doesn't get counted as a second, duplicate column alongside the
                // property itself.
                if (member.DeclaredAccessibility != Accessibility.Public || member.IsStatic || member.IsImplicitlyDeclared)
                {
                    continue;
                }

                string propertyName;
                switch (member)
                {
                    case IPropertySymbol { IsIndexer: false } property:
                        propertyName = property.Name;
                        break;
                    case IFieldSymbol field:
                        propertyName = field.Name;
                        break;
                    default:
                        continue;
                }

                var dbField = member.GetAttributes().FirstOrDefault(a => IsMarkerAttribute(a.AttributeClass, "DbField"));
                bool ignoreColumn = GetNamedArgumentBoolean(dbField, "Ignore") ?? false;

                if (ignoreColumn)
                {
                    continue;
                }

                var columnName = GetNamedArgumentString(dbField, "Label") ?? propertyName;

                columns.Add(new OrmColumn(propertyName, columnName, member));
            }

            return columns.ToImmutable();
        }

        // Matched by simple name only (not full namespace) since OrmAttribute/DbFieldAttribute
        // are user-defined, not compiler-shipped - this is deliberately permissive so the
        // attributes can live in the same project as the [Orm] classes or in a referenced
        // library, without the compiler needing to know which assembly/namespace they came
        // from. Tighten to a full metadata-name comparison (attributeClass.ToDisplayString())
        // if you need to guard against an unrelated attribute of the same short name colliding.
        private static bool IsMarkerAttribute(INamedTypeSymbol? attributeClass, string shortName) =>
            attributeClass is { } ac && (ac.Name == shortName || ac.Name == shortName + "Attribute");

        private static string? GetNamedArgumentString(AttributeData? attribute, string argumentName)
        {
            if (attribute is null)
            {
                return null;
            }

            foreach (var (key, value) in attribute.NamedArguments)
            {
                if (key == argumentName)
                {
                    return value.Value as string;
                }
            }

            return null;
        }

        private static bool? GetNamedArgumentBoolean(AttributeData? attribute, string argumentName)
        {
            if (attribute is null)
            {
                return null;
            }

            foreach (var (key, value) in attribute.NamedArguments)
            {
                if (key == argumentName)
                {
                    return value.Value as bool?;
                }
            }

            return null;
        }
    }
}
#pragma warning restore RS0016 // Add public types and members to the declared API
