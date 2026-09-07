// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

#pragma warning disable RS0016 // Add public types and members to the declared API
namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    /// <summary>
    /// One table named in a sql block, together with where its name sits in the text that was
    /// handed to ScriptDom. Offsets are into the placeholder-substituted sql text produced by
    /// <see cref="SqlTextMap.Create"/>, which is width-preserving, so they map onto the original
    /// source through <see cref="SqlTextMap"/>.
    /// </summary>
    public readonly struct SqlTableReference
    {
        /// <summary>The base (right-most) identifier, so "dbo.users" reports as "users".</summary>
        public string Name { get; }
        public int Offset { get; }
        public int Length { get; }

        public SqlTableReference(string name, int offset, int length)
        {
            Name = name;
            Offset = offset;
            Length = length;
        }
    }

    /// <summary>
    /// One column named in a sql block, with the tables it could belong to.
    /// </summary>
    public readonly struct SqlColumnReference
    {
        public string ColumnName { get; }

        /// <summary>
        /// The tables this column may resolve against: exactly one when the column was written
        /// with a qualifier (the "u" in "u.id" binds it to one table), and every table in scope
        /// when it was written bare ("id"), leaving the caller to pick the one that actually
        /// declares it. Empty when the qualifier names something with no [Orm] class behind it,
        /// such as a derived table, in which case the column is skipped rather than guessed at.
        /// </summary>
        public ImmutableArray<string> CandidateTables { get; }
        public int Offset { get; }
        public int Length { get; }

        public SqlColumnReference(string columnName, ImmutableArray<string> candidateTables, int offset, int length)
        {
            ColumnName = columnName;
            CandidateTables = candidateTables;
            Offset = offset;
            Length = length;
        }
    }

    /// <summary>
    /// One occurrence of an alias standing for a table - both where it is introduced
    /// (the "u" in "FROM users u") and where it is used to qualify a column (the "u" in "u.id").
    /// </summary>
    /// <remarks>
    /// Unlike a <see cref="SqlTableReference"/> the text at this span is not the table's name, so
    /// it carries the name it stands for instead. An alias is never reported as undeclared: it
    /// resolves by construction, and when the table behind it has no [Orm] class that table's own
    /// occurrence already carries the warning.
    /// </remarks>
    public readonly struct SqlAliasReference
    {
        /// <summary>The table the alias stands for, not the alias text itself.</summary>
        public string TableName { get; }
        public int Offset { get; }
        public int Length { get; }

        public SqlAliasReference(string tableName, int offset, int length)
        {
            TableName = tableName;
            Offset = offset;
            Length = length;
        }
    }

    /// <summary>
    /// A star in the select list - the "a.*" of "SELECT a.*[obj]" - and the tables it could stand
    /// for.
    /// </summary>
    public readonly struct SqlStarReference
    {
        /// <summary>
        /// One entry when the star is qualified ("a.*"), every table in scope when it is bare
        /// ("*"). A bare star is only expandable when that leaves exactly one candidate.
        /// </summary>
        public ImmutableArray<string> CandidateTables { get; }

        /// <summary>
        /// The qualifier as written, for re-emitting the columns it stood for ("a" gives
        /// "a.id, a.name"). Null for a bare star, whose columns are emitted unqualified.
        /// </summary>
        public string? Qualifier { get; }

        /// <summary>Covers the whole star expression, qualifier included.</summary>
        public int Offset { get; }
        public int Length { get; }

        public SqlStarReference(ImmutableArray<string> candidateTables, string? qualifier, int offset, int length)
        {
            CandidateTables = candidateTables;
            Qualifier = qualifier;
            Offset = offset;
            Length = length;
        }
    }

    /// <summary>
    /// Everything a sql block names that can be resolved against the [Orm] classes.
    /// </summary>
    public readonly struct SqlReferences
    {
        public ImmutableArray<SqlTableReference> Tables { get; }
        public ImmutableArray<SqlColumnReference> Columns { get; }
        public ImmutableArray<SqlAliasReference> Aliases { get; }
        public ImmutableArray<SqlStarReference> Stars { get; }

        public SqlReferences(
            ImmutableArray<SqlTableReference> tables,
            ImmutableArray<SqlColumnReference> columns,
            ImmutableArray<SqlAliasReference> aliases,
            ImmutableArray<SqlStarReference> stars)
        {
            Tables = tables;
            Columns = columns;
            Aliases = aliases;
            Stars = stars;
        }

        public static SqlReferences Empty { get; } = new SqlReferences(
            ImmutableArray<SqlTableReference>.Empty,
            ImmutableArray<SqlColumnReference>.Empty,
            ImmutableArray<SqlAliasReference>.Empty,
            ImmutableArray<SqlStarReference>.Empty);

        public bool IsEmpty => Tables.IsEmpty && Columns.IsEmpty && Aliases.IsEmpty && Stars.IsEmpty;
    }

    /// <summary>
    /// Collects the tables and columns referenced by an already-parsed sql fragment.
    /// </summary>
    /// <remarks>
    /// This deliberately takes a parsed <see cref="TSqlFragment"/> rather than the text: the
    /// callers already parse the block, and parsing is much the most expensive thing any of them
    /// does.
    /// </remarks>
    public static class SqlTableReferences
    {
        public static SqlReferences Collect(TSqlFragment fragment)
        {
            var visitor = new Visitor();
            fragment.Accept(visitor);
            return visitor.GetReferences();
        }

        /// <summary>
        /// The tables visible to one query, chained to the scope of the query enclosing it so a
        /// correlated subquery can still see the outer aliases. Maps the name a column may be
        /// qualified by - the alias when there is one, the table name when there is not - onto the
        /// table it stands for, or onto null when it stands for something with no [Orm] class,
        /// such as a derived table.
        /// </summary>
        private sealed class Scope
        {
            private readonly Dictionary<string, string?> _byQualifier =
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            public Scope(Scope? parent)
            {
                Parent = parent;
            }

            public Scope? Parent { get; }

            public void Add(string qualifier, string? tableName)
                => _byQualifier[qualifier] = tableName;

            public bool TryResolve(string qualifier, out string? tableName)
            {
                for (var scope = this; scope is not null; scope = scope.Parent)
                {
                    if (scope._byQualifier.TryGetValue(qualifier, out tableName))
                    {
                        return true;
                    }
                }

                tableName = null;
                return false;
            }

            /// <summary>Every table in scope, nearest first, for resolving an unqualified column.</summary>
            public ImmutableArray<string> AllTables()
            {
                var builder = ImmutableArray.CreateBuilder<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (var scope = this; scope is not null; scope = scope.Parent)
                {
                    foreach (var tableName in scope._byQualifier.Values)
                    {
                        if (tableName is not null && seen.Add(tableName))
                        {
                            builder.Add(tableName);
                        }
                    }
                }

                return builder.ToImmutable();
            }
        }

        private sealed class Visitor : TSqlFragmentVisitor
        {
            private readonly ImmutableArray<SqlTableReference>.Builder _tables =
                ImmutableArray.CreateBuilder<SqlTableReference>();

            private readonly ImmutableArray<SqlColumnReference>.Builder _columns =
                ImmutableArray.CreateBuilder<SqlColumnReference>();

            private readonly ImmutableArray<SqlAliasReference>.Builder _aliases =
                ImmutableArray.CreateBuilder<SqlAliasReference>();

            private readonly ImmutableArray<SqlStarReference>.Builder _stars =
                ImmutableArray.CreateBuilder<SqlStarReference>();

            // A CTE introduces a name that is a table for the rest of the statement but has no
            // [Orm] class behind it, so those names must not be reported as unknown. Names are
            // collected across the whole fragment rather than scoped to the statement that
            // declares them - over-permissive, but only ever suppresses a diagnostic, and the
            // alternative is tracking WITH-clause scope through nested queries.
            private readonly HashSet<string> _cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            private Scope _scope = new Scope(parent: null);

            public override void Visit(CommonTableExpression node)
            {
                if (node.ExpressionName?.Value is { } name)
                {
                    _cteNames.Add(name);
                }

                base.Visit(node);
            }

            /// <summary>
            /// Each query specification is its own naming scope, so "u" can mean one table in the
            /// outer query and a different one inside a subquery. The scope is pushed before the
            /// query's clauses are walked and popped afterwards, which resolves every column
            /// underneath it against the right set of tables.
            /// </summary>
            public override void ExplicitVisit(QuerySpecification node)
            {
                var scope = new Scope(_scope);

                if (node.FromClause is { } fromClause)
                {
                    foreach (var tableReference in fromClause.TableReferences)
                    {
                        AddToScope(tableReference, scope);
                    }
                }

                var saved = _scope;
                _scope = scope;
                base.ExplicitVisit(node);
                _scope = saved;
            }

            /// <summary>
            /// Registers what each table reference in a FROM clause can be qualified by. Only the
            /// shape of the clause is walked here; the navigable table entries themselves are
            /// added by <see cref="Visit(NamedTableReference)"/> during the ordinary traversal.
            /// </summary>
            private static void AddToScope(TableReference tableReference, Scope scope)
            {
                switch (tableReference)
                {
                    case NamedTableReference named:
                        var tableName = named.SchemaObject?.BaseIdentifier?.Value;
                        if (tableName is { Length: > 0 })
                        {
                            // An aliased table can only be qualified by its alias, so the table
                            // name is deliberately not registered as well when one is present.
                            scope.Add(named.Alias?.Value ?? tableName, tableName);
                        }

                        break;

                    case QualifiedJoin qualified:
                        AddToScope(qualified.FirstTableReference, scope);
                        AddToScope(qualified.SecondTableReference, scope);
                        break;

                    case UnqualifiedJoin unqualified:
                        AddToScope(unqualified.FirstTableReference, scope);
                        AddToScope(unqualified.SecondTableReference, scope);
                        break;

                    case JoinParenthesisTableReference parenthesis:
                        AddToScope(parenthesis.Join, scope);
                        break;

                    case TableReferenceWithAlias { Alias.Value: { Length: > 0 } alias }:
                        // Derived tables, table-valued functions and table variables all introduce
                        // a name that is not an [Orm] class. Registering it as resolving to
                        // nothing stops its columns being looked up against a same-named table.
                        scope.Add(alias, null);
                        break;
                }
            }

            public override void Visit(NamedTableReference node)
            {
                // Only NamedTableReference lands here: table variables (@t) parse as
                // VariableTableReference and table-valued functions as
                // SchemaObjectFunctionTableReference, so neither needs excluding by hand.
                var identifier = node.SchemaObject?.BaseIdentifier;
                if (identifier?.Value is { Length: > 0 } name)
                {
                    // Temp tables (#t, ##t) live only for the life of the connection and have no
                    // declaration to point at.
                    if (name[0] != '#')
                    {
                        _tables.Add(new SqlTableReference(name, identifier.StartOffset, identifier.FragmentLength));

                        // The alias where it is introduced ("FROM users u"), so that the "u" there
                        // points at the same class its uses do.
                        if (node.Alias is { Value.Length: > 0 } alias)
                        {
                            _aliases.Add(new SqlAliasReference(name, alias.StartOffset, alias.FragmentLength));
                        }
                    }
                }

                base.Visit(node);
            }

            public override void Visit(SelectStarExpression node)
            {
                string? qualifier = null;
                ImmutableArray<string> candidates;

                if (node.Qualifier?.Identifiers is { Count: > 0 } identifiers)
                {
                    // "a.*", or "dbo.users.*" where the table is still the last identifier. The
                    // qualifier is kept as written so the expansion re-qualifies its columns the
                    // same way rather than inventing a spelling the query did not use.
                    qualifier = string.Join(".", identifiers.Select(static i => i.Value));

                    var tableQualifier = identifiers[identifiers.Count - 1]?.Value;
                    candidates = tableQualifier is { Length: > 0 } name
                        && _scope.TryResolve(name, out var resolved)
                        && resolved is not null
                            ? ImmutableArray.Create(resolved)
                            : ImmutableArray<string>.Empty;
                }
                else
                {
                    candidates = _scope.AllTables();
                }

                _stars.Add(new SqlStarReference(candidates, qualifier, node.StartOffset, node.FragmentLength));

                base.Visit(node);
            }

            public override void Visit(ColumnReferenceExpression node)
            {
                var identifiers = node.MultiPartIdentifier?.Identifiers;

                // "SELECT *" carries no identifiers at all, and there is nothing to resolve.
                if (identifiers is not { Count: > 0 })
                {
                    base.Visit(node);
                    return;
                }

                var columnIdentifier = identifiers[identifiers.Count - 1];
                if (columnIdentifier?.Value is not { Length: > 0 } columnName)
                {
                    base.Visit(node);
                    return;
                }

                ImmutableArray<string> candidates;
                if (identifiers.Count >= 2)
                {
                    // Qualified: "u.id", or "dbo.users.id" where the qualifier is still the
                    // identifier immediately before the column.
                    var qualifierIdentifier = identifiers[identifiers.Count - 2];
                    if (qualifierIdentifier?.Value is not { Length: > 0 } qualifier)
                    {
                        base.Visit(node);
                        return;
                    }

                    // A qualifier resolving to null is a derived table; one that does not resolve
                    // at all is most likely a CTE or a table this compilation cannot see. Either
                    // way there is nothing to bind the column to, so it is left alone.
                    if (_scope.TryResolve(qualifier, out var resolved) && resolved is not null)
                    {
                        candidates = ImmutableArray.Create(resolved);

                        // The qualifier itself names the table, so it gets an entry of its own and
                        // becomes navigable in the same way the column beside it is.
                        _aliases.Add(new SqlAliasReference(
                            resolved,
                            qualifierIdentifier.StartOffset,
                            qualifierIdentifier.FragmentLength));
                    }
                    else
                    {
                        candidates = ImmutableArray<string>.Empty;
                    }
                }
                else
                {
                    candidates = _scope.AllTables();
                }

                if (!candidates.IsEmpty)
                {
                    _columns.Add(new SqlColumnReference(
                        columnName,
                        candidates,
                        columnIdentifier.StartOffset,
                        columnIdentifier.FragmentLength));
                }

                base.Visit(node);
            }

            public SqlReferences GetReferences()
            {
                var tables = _tables.ToImmutable();

                if (_cteNames.Count > 0)
                {
                    var filtered = ImmutableArray.CreateBuilder<SqlTableReference>(_tables.Count);
                    foreach (var table in tables)
                    {
                        if (!_cteNames.Contains(table.Name))
                        {
                            filtered.Add(table);
                        }
                    }

                    tables = filtered.ToImmutable();
                }

                // Aliases need no equivalent filtering: one standing for a CTE simply resolves to
                // no [Orm] class, and nothing is reported for an alias either way.
                return new SqlReferences(tables, _columns.ToImmutable(), _aliases.ToImmutable(), _stars.ToImmutable());
            }
        }
    }
}
