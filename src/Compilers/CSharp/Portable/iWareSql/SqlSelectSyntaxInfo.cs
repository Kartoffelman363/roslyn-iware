// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Roslyn.Utilities;

#pragma warning disable RS0016 // Add public types and members to the declared API
namespace Microsoft.CodeAnalysis.CSharp.SqlQueries
{
    public class SqlSelectSyntaxInfo
    {
        public readonly List<ColumnInfo> Columns = new();
        public readonly List<SqlSubquerySyntaxInfo> Subqueries = new();
        public readonly List<TableInfo> Tables = new();
        public int Start;
        public int Length;

        /// <summary>
        /// The stars in this query's select list. Kept apart from <see cref="Columns"/> because a
        /// star is not one column - it stands for however many the source behind it has, which is
        /// only known once the sources are resolved.
        /// </summary>
        public readonly List<StarInfo> Stars = new();

        /// <summary>
        /// Every identifier this query writes, with where it sits in the parsed text.
        /// </summary>
        /// <remarks>
        /// <see cref="Columns"/> and <see cref="Tables"/> answer "what does this query select and
        /// select from", which is what completion needs. This answers "what is written at this
        /// position", which is what colouring, hover and go-to-definition need, and it therefore
        /// covers the whole query - WHERE, ON, GROUP BY and ORDER BY included - not just the select
        /// list. Occurrences belong to the query that wrote them; a nested query owns its own.
        /// </remarks>
        public readonly List<SqlIdentifierOccurrence> Occurrences = new();

        /// <summary>The query this one is nested in, for resolving a qualifier declared further out.</summary>
        public SqlSelectSyntaxInfo? Parent;

        public enum SqlOccurrenceKind
        {
            /// <summary>A table's own name, as written in FROM or JOIN.</summary>
            TableName,

            /// <summary>Something standing for a source without being its name: an alias where it
            /// is introduced or used to qualify something, and the star of "u.*".</summary>
            SourceRef,

            /// <summary>A column name.</summary>
            Column,
        }

        public sealed class SqlIdentifierOccurrence
        {
            public SqlOccurrenceKind Kind { get; }

            /// <summary>The identifier as written, or "*" for a star.</summary>
            public string Text { get; }

            /// <summary>What qualified it, if anything - the "u" of "u.id" or "u.*".</summary>
            public string? Qualifier { get; }

            public int Offset { get; }
            public int Length { get; }

            public SqlIdentifierOccurrence(SqlOccurrenceKind kind, string text, string? qualifier, int offset, int length)
            {
                Kind = kind;
                Text = text;
                Qualifier = qualifier;
                Offset = offset;
                Length = length;
            }
        }

        public sealed class StarInfo
        {
            /// <summary>The qualifier as written ("u" in "u.*"), or null for a bare star.</summary>
            public string? Qualifier { get; }

            /// <summary>Covers the whole star expression, qualifier included.</summary>
            public int Offset { get; }
            public int Length { get; }

            public StarInfo(string? qualifier, int offset, int length)
            {
                Qualifier = qualifier;
                Offset = offset;
                Length = length;
            }
        }

        public abstract class ColumnOrTableInfo(MultiPartIdentifier? name, Identifier? alias)
        {
            public MultiPartIdentifier? Name = name;
            public Identifier? Alias = alias;

            public string? GetNameString()
            {
                if (Name == null)
                {
                    return null;
                }
                var tableNameIdentifiers = Name.Identifiers;
                if (tableNameIdentifiers.IsEmpty())
                {
                    return null;
                }
                return tableNameIdentifiers[^1].Value;
            }

            public string? GetAliasString()
            {
                return Alias?.Value;
            }

            public bool CompareAlias(string other)
            {
                return GetAliasString()?.Equals(other, StringComparison.InvariantCultureIgnoreCase) ?? false;
            }

            public bool CompareName(string other)
            {
                return GetNameString()?.Equals(other, StringComparison.InvariantCultureIgnoreCase) ?? false;
            }

            public bool CompareName(MultiPartIdentifier other)
            {
                var idents = Name?.Identifiers;
                if (idents == null)
                {
                    return false;
                }
                var otherIdents = other.Identifiers;

                var lesser = Math.Min(idents.Count, otherIdents.Count);
                // Compare Identifier back to front e.g. dbo.tablename.colname
                for (var i = 1; i <= lesser; i++)
                {
                    var ident = idents[^i];
                    var otherIdent = otherIdents[^i];
                    if (!ident.Value.Equals(otherIdent.Value, StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (i == 1)
                        {
                            return false;
                        }
                        if (ident.Value == null || otherIdent.Value == null)
                        {
                            return true;
                        }
                        return false;
                    }
                }

                // At least one MultiPartIdentifier has no Identifiers
                if (lesser > 0)
                {
                    return true;
                }
                return false;
            }

            public override string ToString()
            {
                return $"{string.Join(".", Name?.Identifiers.Select(id => id.Value) ?? [])}{(Alias != null ? $", {Alias.Value}" : null)}";
            }
        }

        public class ColumnInfo(MultiPartIdentifier? name, Identifier? alias) : ColumnOrTableInfo(name, alias) { }

        public class TableInfo(MultiPartIdentifier? name, Identifier? alias) : ColumnOrTableInfo(name, alias) { }

        public class SqlSubquerySyntaxInfo : SqlSelectSyntaxInfo
        {
            public Identifier? Alias;

            /// <summary>
            /// True when this came from a FROM clause ("FROM (SELECT ...) u") and is therefore a
            /// source that a qualifier can name, as opposed to a scalar subquery in a SELECT list
            /// or WHERE clause, which is a value and names nothing.
            /// </summary>
            public bool IsDerivedTable;

            public SqlSubquerySyntaxInfo(Identifier? subqueryAlias) : base()
            {
                Alias = subqueryAlias;
            }

            public SqlSubquerySyntaxInfo(QuerySpecification qs, Identifier? subqueryAlias = null) : base(qs)
            {
                Alias = subqueryAlias;
            }

            public bool CompareAlias(string other)
            {
                return Alias?.Value.Equals(other, StringComparison.InvariantCultureIgnoreCase) ?? false;
            }

            public string? GetAliasString()
            {
                return Alias?.Value;
            }
        }

        private class SqlQueryVisitor : TSqlFragmentVisitor
        {
            public SqlSelectSyntaxInfo? VisitedQuery { get; private set; } = null;

            public override void Visit(TSqlStatement node)
            {
                var selectStatement = node as SelectStatement;
                if (selectStatement is null)
                {
                    return;
                }

                base.Visit(node);
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                VisitedQuery = new SqlSelectSyntaxInfo(node);
            }
        }

        public SqlSelectSyntaxInfo() { }

        public SqlSelectSyntaxInfo(QuerySpecification qs)
        {
            Populate(qs);
        }

        /// <summary>
        /// The query model for an already-parsed fragment.
        /// </summary>
        /// <remarks>
        /// The compiler parses a block once, to verify it, and reads the model off that same
        /// fragment rather than parsing a second time. Completion has only the text, so it goes
        /// through <see cref="GetInfoFromStringAsync"/> instead.
        /// </remarks>
        public static SqlSelectSyntaxInfo? GetInfoFromFragment(TSqlFragment fragment)
        {
            var visitor = new SqlQueryVisitor();
            fragment.Accept(visitor);
            return visitor.VisitedQuery;
        }

        public static async Task<SqlSelectSyntaxInfo?> GetInfoFromStringAsync(string sqlBlock)
        {
            return await Task.Run(() =>
            {
                var parser = new TSql180Parser(true, SqlEngineType.All);
                using (TextReader sr = new StringReader(sqlBlock))
                {
                    var tree = parser.Parse(sr, out _);
                    var visitor = new SqlQueryVisitor();
                    tree.Accept(visitor);
                    return visitor.VisitedQuery;
                }
            }).ConfigureAwait(false);
        }

        private class ScalarSubqueryFinder : TSqlFragmentVisitor
        {
            public List<SqlSubquerySyntaxInfo> Found { get; } = new();

            public override void ExplicitVisit(ScalarSubquery node)
            {
                if (node.QueryExpression is QuerySpecification qs)
                {
                    Found.Add(new(qs));
                }
                // no base.ExplicitVisit(node) call — stop here, BuildQuery(qs) already recurses
            }
        }

        private void Populate(QuerySpecification qs)
        {
            Start = qs.StartOffset;
            Length = qs.FragmentLength;

            // 1. Columns
            foreach (var element in qs.SelectElements)
            {
                switch (element)
                {
                    case SelectScalarExpression scalar:
                        // Ignore alias if typed in square brackets
                        var alias = scalar.ColumnName?.Identifier is { QuoteType: not QuoteType.SquareBracket }
                            ? scalar.ColumnName.Identifier
                            : null;
                        var columnName = scalar.Expression switch
                        {
                            ColumnReferenceExpression colRef =>
                                colRef.MultiPartIdentifier,
                            _ => null
                        };
                        Columns.Add(new ColumnInfo(columnName, alias));
                        break;

                    case SelectStarExpression starExpression:
                        // A star is not a column, so it does not go in Columns - it stands for
                        // whatever the source it names has, which needs the sources resolved first.
                        Stars.Add(new StarInfo(
                            QualifierString(starExpression.Qualifier),
                            starExpression.StartOffset,
                            starExpression.FragmentLength));
                        break;
                }
            }

            // 2. Subqueries from FROM clause (derived tables, incl. inside JOINs)
            if (qs.FromClause != null)
            {
                foreach (var tableRef in qs.FromClause.TableReferences)
                {
                    CollectTables(tableRef);
                }
            }

            // 3. Subqueries from SELECT list / WHERE clause (scalar subqueries, IN, EXISTS)
            var finder = new ScalarSubqueryFinder();
            foreach (var element in qs.SelectElements)
            {
                element.Accept(finder);
            }
            qs.WhereClause?.Accept(finder);
            Subqueries.AddRange(finder.Found);

            // 4. Every identifier written anywhere in this query, for the features that work off
            //    positions rather than off the shape of the query.
            foreach (var subquery in Subqueries)
            {
                subquery.Parent = this;
            }

            var collector = new OccurrenceCollector(Occurrences);
            qs.AcceptChildren(collector);
        }

        private static string? QualifierString(MultiPartIdentifier? qualifier) =>
            qualifier?.Identifiers is { Count: > 0 } identifiers
                ? string.Join(".", identifiers.Select(static i => i.Value))
                : null;

        /// <summary>
        /// Records where each identifier of one query sits, stopping at any query nested inside it
        /// so that every occurrence is owned by the query whose sources should resolve it.
        /// </summary>
        private sealed class OccurrenceCollector : TSqlFragmentVisitor
        {
            private readonly List<SqlIdentifierOccurrence> _occurrences;

            public OccurrenceCollector(List<SqlIdentifierOccurrence> occurrences)
            {
                _occurrences = occurrences;
            }

            /// <summary>
            /// A nested query owns its own occurrences, so descending into one here would attribute
            /// its identifiers to the wrong set of sources. The traversal is started with
            /// AcceptChildren so the outer query is not stopped by this too.
            /// </summary>
            public override void ExplicitVisit(QuerySpecification node)
            {
            }

            public override void Visit(NamedTableReference node)
            {
                if (node.SchemaObject?.BaseIdentifier is { Value.Length: > 0 } identifier)
                {
                    // Temp tables live only for the connection and have no declaration to point at.
                    if (identifier.Value[0] != '#')
                    {
                        Add(SqlOccurrenceKind.TableName, identifier.Value, null, identifier);

                        if (node.Alias is { Value.Length: > 0 } alias)
                        {
                            Add(SqlOccurrenceKind.SourceRef, alias.Value, null, alias);
                        }
                    }
                }

                base.Visit(node);
            }

            public override void Visit(ColumnReferenceExpression node)
            {
                var identifiers = node.MultiPartIdentifier?.Identifiers;
                if (identifiers is not { Count: > 0 })
                {
                    base.Visit(node);
                    return;
                }

                var column = identifiers[identifiers.Count - 1];
                var qualifier = identifiers.Count >= 2 ? identifiers[identifiers.Count - 2] : null;

                if (column is { Value.Length: > 0 })
                {
                    Add(SqlOccurrenceKind.Column, column.Value, qualifier?.Value, column);
                }

                if (qualifier is { Value.Length: > 0 })
                {
                    Add(SqlOccurrenceKind.SourceRef, qualifier.Value, null, qualifier);
                }

                base.Visit(node);
            }

            public override void Visit(SelectStarExpression node)
            {
                var qualifier = QualifierString(node.Qualifier);

                // The star glyph is always the last character of the expression, however much
                // whitespace sits between it and its qualifier.
                _occurrences.Add(new SqlIdentifierOccurrence(
                    SqlOccurrenceKind.SourceRef,
                    "*",
                    qualifier,
                    node.StartOffset + node.FragmentLength - 1,
                    1));

                if (node.Qualifier?.Identifiers is { Count: > 0 } identifiers)
                {
                    var last = identifiers[identifiers.Count - 1];
                    if (last is { Value.Length: > 0 })
                    {
                        Add(SqlOccurrenceKind.SourceRef, last.Value, null, last);
                    }
                }

                base.Visit(node);
            }

            private void Add(SqlOccurrenceKind kind, string text, string? qualifier, TSqlFragment fragment)
                => _occurrences.Add(new SqlIdentifierOccurrence(
                    kind, text, qualifier, fragment.StartOffset, fragment.FragmentLength));
        }

        private void CollectTables(TableReference tableRef)
        {
            switch (tableRef)
            {
                case NamedTableReference named:
                    // SchemaObject holds up to server.database.schema.table;
                    // BaseIdentifier is the actual table name (last part)
                    var tableName = named.SchemaObject as MultiPartIdentifier;
                    var tableIdent = named.SchemaObject.Identifiers;
                    var tableAlias = named.Alias;
                    Tables.Add(new TableInfo(tableName, tableAlias));
                    break;

                case QueryDerivedTable derived when derived.QueryExpression is QuerySpecification innerQs:
                    var derivedAlias = derived.Alias;
                    Subqueries.Add(new SqlSubquerySyntaxInfo(innerQs, derivedAlias) { IsDerivedTable = true });
                    break;

                case QualifiedJoin join:
                    CollectTables(join.FirstTableReference);
                    CollectTables(join.SecondTableReference);
                    break;

                case UnqualifiedJoin uJoin:
                    CollectTables(uJoin.FirstTableReference);
                    CollectTables(uJoin.SecondTableReference);
                    break;

                    //TODO aljaz
                    // Other TableReference kinds (e.g. VariableTableReference,
                    // OpenRowsetTableReference, PivotedTableReference) fall through
            }
        }

        // Returns the deepest SQL query at the position or null if cursor is outside
        public SqlSelectSyntaxInfo? GetQueryAtCursorPosition(int cursorPosition)
        {
            if (cursorPosition >= Start && cursorPosition < Start + Length)
            {
                return Subqueries.Find(subquery => subquery.GetQueryAtCursorPosition(cursorPosition) != null) ?? this;
            }
            return null;
        }

        // Return a flattened list of subqueries for this query
        public List<SqlSubquerySyntaxInfo> FlattenSubqueries()
        {
            return Subqueries.SelectMany(subquery => subquery.FlattenQueries()).OfType<SqlSubquerySyntaxInfo>().ToList();
        }

        // Return a flattened list of subqueries for this query including itself
        public List<SqlSelectSyntaxInfo> FlattenQueries()
        {
            // No subqueries return itself
            if (Subqueries.IsEmpty())
            {
                return [this];
            }

            // Has subqueries return self and subqueries
            return [this, .. Subqueries.SelectMany(subquery => subquery.FlattenQueries())];
        }

        /*
        private static void CollectDerivedTables(TableReference tableRef, List<SqlSubqueryInfo> subqueries)
        {
            switch (tableRef)
            {
                case QueryDerivedTable derived when derived.QueryExpression is QuerySpecification innerQs:
                    var derivedAlias = derived.Alias?.Value;
                    subqueries.Add(new SqlSubqueryInfo(innerQs, derivedAlias));
                    break;

                case QualifiedJoin join:
                    CollectDerivedTables(join.FirstTableReference, subqueries);
                    CollectDerivedTables(join.SecondTableReference, subqueries);
                    break;

                case UnqualifiedJoin uJoin:
                    CollectDerivedTables(uJoin.FirstTableReference, subqueries);
                    CollectDerivedTables(uJoin.SecondTableReference, subqueries);
                    break;

                    // NamedTableReference (a plain table) -> nothing to collect
            }
        }
        */
    }
}
#pragma warning restore RS0016 // Add public types and members to the declared API
