// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

#pragma warning disable RS0016 // Add public types and members to the declared API
namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    /// <summary>
    /// A name written inside a sql block - a table or one of its columns - located in the source
    /// file and resolved against the <c>[Orm]</c> classes the compilation can see.
    /// </summary>
    public readonly struct ResolvedSqlReference
    {
        public string Name { get; }
        public TextSpan Span { get; }

        /// <summary>
        /// The declaration behind this name: the <c>[Orm]</c> class for a table, the property or
        /// field for a column. Null when nothing declares it.
        /// </summary>
        public ISymbol? Symbol { get; }

        /// <summary>True for a table name, false for a column name.</summary>
        public bool IsTable { get; }

        /// <summary>
        /// Whether to report this name as undeclared. False for a column whose table is itself
        /// unknown - the warning on the table already says what is wrong - and for a column that
        /// matched more than one table in scope, which is sql's problem to complain about rather
        /// than ours.
        /// </summary>
        public bool ReportIfUnresolved { get; }

        public ResolvedSqlReference(string name, TextSpan span, ISymbol? symbol, bool isTable, bool reportIfUnresolved)
        {
            Name = name;
            Span = span;
            Symbol = symbol;
            IsTable = isTable;
            ReportIfUnresolved = reportIfUnresolved;
        }
    }

    /// <summary>
    /// Resolves the tables and columns written inside a sql block to their declarations, for the
    /// compiler's diagnostics and for the IDE features that need to point at one: go-to-definition,
    /// hover and classification.
    /// </summary>
    public static class SqlTableResolution
    {
        // Extracting names depends only on the block's text, so it survives the edits that do not
        // touch the sql. Resolving them against the schema does not, and is redone every call.
        private static readonly ConcurrentDictionary<string, SqlReferences> s_referenceCache = new();

        private const int MaxCacheEntries = 500;

        /// <summary>
        /// Every table and column the block names, in source order of the underlying sql.
        /// </summary>
        public static ImmutableArray<ResolvedSqlReference> Resolve(SqlTextBlockSyntax block, Compilation compilation)
        {
            var map = SqlTextMap.Create(block.Segments, out var sqlText);
            return Resolve(GetReferences(sqlText), map, compilation);
        }

        /// <summary>
        /// Resolves references already extracted from <paramref name="map"/>'s sql text. Used by
        /// the compiler, which has both to hand from verifying the block and should not parse it
        /// a second time.
        /// </summary>
        public static ImmutableArray<ResolvedSqlReference> Resolve(
            SqlReferences references,
            SqlTextMap map,
            Compilation compilation)
        {
            if (references.IsEmpty)
            {
                return ImmutableArray<ResolvedSqlReference>.Empty;
            }

            var schema = OrmSchemaProvider.GetSchema(compilation);
            var builder = ImmutableArray.CreateBuilder<ResolvedSqlReference>(
                references.Tables.Length + references.Columns.Length);

            foreach (var table in references.Tables)
            {
                builder.Add(new ResolvedSqlReference(
                    table.Name,
                    map.MapToSource(table.Offset, table.Length),
                    schema.FindTable(table.Name)?.Symbol,
                    isTable: true,
                    reportIfUnresolved: true));
            }

            foreach (var column in references.Columns)
            {
                builder.Add(ResolveColumn(column, map, schema));
            }

            return builder.ToImmutable();
        }

        private static ResolvedSqlReference ResolveColumn(SqlColumnReference column, SqlTextMap map, OrmSchema schema)
        {
            var span = map.MapToSource(column.Offset, column.Length);

            ISymbol? symbol = null;
            var matches = 0;
            var anyTableKnown = false;

            foreach (var candidateName in column.CandidateTables)
            {
                if (schema.FindTable(candidateName) is not { } table)
                {
                    continue;
                }

                anyTableKnown = true;

                if (table.FindColumn(column.ColumnName) is { } ormColumn)
                {
                    matches++;
                    symbol ??= ormColumn.Symbol;
                }
            }

            // A bare column matching two tables in scope is ambiguous. Picking one would be a coin
            // flip and would send go-to-definition to the wrong class, so nothing is resolved.
            if (matches > 1)
            {
                return new ResolvedSqlReference(column.ColumnName, span, symbol: null, isTable: false, reportIfUnresolved: false);
            }

            return new ResolvedSqlReference(
                column.ColumnName,
                span,
                matches == 1 ? symbol : null,
                isTable: false,
                // Only worth reporting when the table exists: when it does not, the table's own
                // warning already covers the problem and one per column on top is just noise.
                reportIfUnresolved: anyTableKnown);
        }

        /// <summary>
        /// The table or column whose name covers <paramref name="position"/>, or null if the caret
        /// is not on one. Only names that resolved to a declaration are returned, since every
        /// caller wants a symbol to act on.
        /// </summary>
        public static ResolvedSqlReference? FindReferenceAt(SqlTextBlockSyntax block, Compilation compilation, int position)
        {
            foreach (var reference in Resolve(block, compilation))
            {
                if (reference.Symbol is not null && reference.Span.IntersectsWith(position))
                {
                    return reference;
                }
            }

            return null;
        }

        /// <summary>
        /// Parses the sql text and extracts what it names, caching on the text itself.
        /// </summary>
        public static SqlReferences GetReferences(string sqlText)
        {
            if (s_referenceCache.TryGetValue(sqlText, out var cached))
            {
                return cached;
            }

            var references = SqlReferences.Empty;

            TSqlParser parser = new TSql180Parser(initialQuotedIdentifiers: true);
            using (var reader = new StringReader(sqlText))
            {
                var fragment = parser.Parse(reader, out var errors);

                // Same rule the compiler applies: a block that does not parse cleanly is being
                // typed, and the names ScriptDom recovered from it are not worth colouring or
                // navigating to.
                if (errors.Count == 0 && fragment is not null)
                {
                    references = SqlTableReferences.Collect(fragment);
                }
            }

            // Unbounded growth would pin every version of every block the user has typed. The
            // cache is a pure optimisation, so dropping all of it is always safe.
            if (s_referenceCache.Count >= MaxCacheEntries)
            {
                s_referenceCache.Clear();
            }

            s_referenceCache.TryAdd(sqlText, references);
            return references;
        }
    }
}
