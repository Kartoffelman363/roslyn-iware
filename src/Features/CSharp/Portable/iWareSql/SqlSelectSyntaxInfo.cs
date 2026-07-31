// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Completion.iWareSql
{
    internal class SqlSelectSyntaxInfo
    {
        public readonly List<ColumnInfo> _columns = new();
        public readonly List<SqlSubquerySyntaxInfo> _subqueries = new();
        public readonly List<TableInfo> _tables = new();
        public int _start;
        public int _length;

        public abstract class ColumnOrTableInfo(MultiPartIdentifier? name, Identifier? alias)
        {
            public MultiPartIdentifier? _name = name;
            public Identifier? _alias = alias;

            public string? GetNameString()
            {
                if (_name == null)
                {
                    return null;
                }
                var tableNameIdentifiers = _name.Identifiers;
                if (tableNameIdentifiers.IsEmpty())
                {
                    return null;
                }
                return tableNameIdentifiers[^1].Value;
            }

            public string? GetAliasString()
            {
                return _alias?.Value;
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
                var idents = _name?.Identifiers;
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
                return $"{string.Join(".", _name?.Identifiers.Select(id => id.Value) ?? [])}{(_alias != null ? $", {_alias.Value}" : null)}";
            }
        }

        public class ColumnInfo(MultiPartIdentifier? name, Identifier? alias) : ColumnOrTableInfo(name, alias) { }

        public class TableInfo(MultiPartIdentifier? name, Identifier? alias) : ColumnOrTableInfo(name, alias) { }

        public class SqlSubquerySyntaxInfo : SqlSelectSyntaxInfo
        {
            public Identifier? _alias;

            public SqlSubquerySyntaxInfo(Identifier? subqueryAlias) : base()
            {
                _alias = subqueryAlias;
            }

            public SqlSubquerySyntaxInfo(QuerySpecification qs, Identifier? subqueryAlias = null) : base(qs)
            {
                _alias = subqueryAlias;
            }

            public bool CompareAlias(string other)
            {
                return _alias?.Value.Equals(other, StringComparison.InvariantCultureIgnoreCase) ?? false;
            }

            public string? GetAliasString()
            {
                return _alias?.Value;
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

        public static SqlSelectSyntaxInfo? GetInfoFromString(string sqlBlock)
        {
            var parser = new TSql180Parser(true, SqlEngineType.All);
            using (TextReader sr = new StringReader(sqlBlock))
            {
                var tree = parser.Parse(sr, out _);
                var visitor = new SqlQueryVisitor();
                tree.Accept(visitor);
                return visitor.VisitedQuery;
            }
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
            _start = qs.StartOffset;
            _length = qs.FragmentLength;

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
                        _columns.Add(new ColumnInfo(columnName, alias));
                        break;

                        //TODO aljaz figure out what to do with star expression
                        /*
                        case SelectStarExpression starExpression:
                            _columns.Add(new Column("*", null));
                            break;
                        */
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
            _subqueries.AddRange(finder.Found);
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
                    _tables.Add(new TableInfo(tableName, tableAlias));
                    break;

                case QueryDerivedTable derived when derived.QueryExpression is QuerySpecification innerQs:
                    var derivedAlias = derived.Alias;
                    _subqueries.Add(new SqlSubquerySyntaxInfo(innerQs, derivedAlias));
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
            if (cursorPosition >= _start && cursorPosition < _start + _length)
            {
                return _subqueries.Find(subquery => subquery.GetQueryAtCursorPosition(cursorPosition) != null) ?? this;
            }
            return null;
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
