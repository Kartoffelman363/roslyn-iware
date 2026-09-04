// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.SqlServer.TransactSql.ScriptDom;

#pragma warning disable RS0016 // Add public types and members to the declared API
namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    /// <summary>
    /// One table named in a sql block, together with where its name sits in the text that was
    /// handed to ScriptDom. Offsets are into the placeholder-substituted sql text produced by
    /// <see cref="ParseSql.getNamesFromSqlText"/>, which is width-preserving, so they map onto
    /// the original source through <see cref="SqlTextMap"/>.
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
    /// Collects the tables referenced by an already-parsed sql fragment.
    /// </summary>
    /// <remarks>
    /// This deliberately takes a parsed <see cref="TSqlFragment"/> rather than the text: the only
    /// caller (<see cref="VerifySql"/>) already parses the block to report syntax errors, and
    /// parsing is much the most expensive thing either of them does.
    /// </remarks>
    public static class SqlTableReferences
    {
        public static ImmutableArray<SqlTableReference> Collect(TSqlFragment fragment)
        {
            var visitor = new Visitor();
            fragment.Accept(visitor);
            return visitor.GetTables();
        }

        private sealed class Visitor : TSqlFragmentVisitor
        {
            private readonly ImmutableArray<SqlTableReference>.Builder _tables =
                ImmutableArray.CreateBuilder<SqlTableReference>();

            // A CTE introduces a name that is a table for the rest of the statement but has no
            // [Orm] class behind it, so those names must not be reported as unknown. Names are
            // collected across the whole fragment rather than scoped to the statement that
            // declares them - over-permissive, but only ever suppresses a diagnostic, and the
            // alternative is tracking WITH-clause scope through nested queries.
            private readonly HashSet<string> _cteNames =
                new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            public override void Visit(CommonTableExpression node)
            {
                if (node.ExpressionName?.Value is { } name)
                {
                    _cteNames.Add(name);
                }

                base.Visit(node);
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
                    }
                }

                base.Visit(node);
            }

            public ImmutableArray<SqlTableReference> GetTables()
            {
                if (_cteNames.Count == 0)
                {
                    return _tables.ToImmutable();
                }

                var filtered = ImmutableArray.CreateBuilder<SqlTableReference>(_tables.Count);
                foreach (var table in _tables)
                {
                    if (!_cteNames.Contains(table.Name))
                    {
                        filtered.Add(table);
                    }
                }

                return filtered.ToImmutable();
            }
        }
    }
}
