// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.SqlQueries;

#pragma warning disable RS0016 // Add public types and members to the declared API
namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    /// <summary>
    /// One column a source produces, and the <c>[Orm]</c> member behind it when there is one.
    /// </summary>
    /// <remarks>
    /// A column of a plain table always traces back to a member. One of a derived table only does
    /// when it was selected straight through - "SELECT id FROM users" keeps users.id behind it,
    /// while "SELECT id + 1 AS n" produces a column named n with nothing to point at.
    /// </remarks>
    public readonly struct SqlSourceColumn
    {
        public string Name { get; }
        public OrmColumn? Origin { get; }

        public SqlSourceColumn(string name, OrmColumn? origin)
        {
            Name = name;
            Origin = origin;
        }
    }

    /// <summary>
    /// Something a qualifier can name inside a query: a table, or a derived table.
    /// </summary>
    public sealed class SqlSource
    {
        /// <summary>What a qualifier has to say to reach it - the alias when there is one.</summary>
        public string Qualifier { get; }

        /// <summary>The <c>[Orm]</c> table behind it, or null for a derived table.</summary>
        public OrmTable? Table { get; }

        /// <summary>
        /// The table name as written, kept even when no [Orm] class matches it so that a diagnostic
        /// can name the table the user wrote rather than the alias standing for it. Null for a
        /// derived table, which has no name of its own.
        /// </summary>
        public string? TableName { get; }

        public ImmutableArray<SqlSourceColumn> Columns { get; }

        public SqlSource(string qualifier, string? tableName, OrmTable? table, ImmutableArray<SqlSourceColumn> columns)
        {
            Qualifier = qualifier;
            TableName = tableName;
            Table = table;
            Columns = columns;
        }

        public SqlSourceColumn? FindColumn(string name)
        {
            foreach (var column in Columns)
            {
                if (string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return column;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Works out what each query in a parsed block selects from, so that a qualifier can be turned
    /// into a set of columns however deeply the query is nested.
    /// </summary>
    /// <remarks>
    /// This is the piece that makes "FROM (SELECT * FROM users) u" behave like "FROM users u": the
    /// derived table is asked for its own columns, which it works out from its own sources, and an
    /// inner star is expanded the same way. The recursion is bounded by the nesting of the query
    /// and guarded against a source that somehow refers to itself.
    /// </remarks>
    public static class SqlSourceResolution
    {
        /// <summary>
        /// The sources <paramref name="query"/> selects from, keyed by what a qualifier must say.
        /// </summary>
        public static ImmutableArray<SqlSource> GetSources(SqlSelectSyntaxInfo query, OrmSchema schema)
            => GetSources(query, schema, new HashSet<SqlSelectSyntaxInfo>());

        private static ImmutableArray<SqlSource> GetSources(
            SqlSelectSyntaxInfo query,
            OrmSchema schema,
            HashSet<SqlSelectSyntaxInfo> visiting)
        {
            if (!visiting.Add(query))
            {
                // A query that reaches itself would otherwise recurse forever; it cannot be
                // resolved, and reporting nothing is better than not returning.
                return ImmutableArray<SqlSource>.Empty;
            }

            var builder = ImmutableArray.CreateBuilder<SqlSource>();

            foreach (var table in query.Tables)
            {
                var tableName = table.GetNameString();
                if (tableName is not { Length: > 0 })
                {
                    continue;
                }

                // An aliased table can only be qualified by its alias, which is what sql itself
                // enforces, so the table name is not registered as well when one is present.
                var qualifier = table.GetAliasString() ?? tableName;
                var ormTable = schema.FindTable(tableName);

                builder.Add(new SqlSource(qualifier, tableName, ormTable, ColumnsOf(ormTable)));
            }

            foreach (var subquery in query.Subqueries)
            {
                if (!subquery.IsDerivedTable || subquery.GetAliasString() is not { Length: > 0 } alias)
                {
                    continue;
                }

                builder.Add(new SqlSource(alias, tableName: null, table: null, OutputColumnsOf(subquery, schema, visiting)));
            }

            visiting.Remove(query);
            return builder.ToImmutable();
        }

        private static ImmutableArray<SqlSourceColumn> ColumnsOf(OrmTable? table)
        {
            if (table is null)
            {
                return ImmutableArray<SqlSourceColumn>.Empty;
            }

            var builder = ImmutableArray.CreateBuilder<SqlSourceColumn>(table.Columns.Length);
            foreach (var column in table.Columns)
            {
                builder.Add(new SqlSourceColumn(column.ColumnName, column));
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// The columns <paramref name="query"/> hands to whatever selects from it.
        /// </summary>
        /// <remarks>
        /// Its select list, with each star replaced by the columns of the source it names - so a
        /// subquery written as "SELECT * FROM users" offers the columns of users rather than
        /// nothing at all. Completion asks for this when deciding what to offer after a subquery
        /// alias, and the wildcard expansion asks for it when a star reads from one.
        /// </remarks>
        public static ImmutableArray<SqlSourceColumn> GetOutputColumns(SqlSelectSyntaxInfo query, OrmSchema schema)
            => OutputColumnsOf(query, schema, new HashSet<SqlSelectSyntaxInfo>());

        private static ImmutableArray<SqlSourceColumn> OutputColumnsOf(
            SqlSelectSyntaxInfo query,
            OrmSchema schema,
            HashSet<SqlSelectSyntaxInfo> visiting)
        {
            var innerSources = GetSources(query, schema, visiting);
            var builder = ImmutableArray.CreateBuilder<SqlSourceColumn>();

            foreach (var column in query.Columns)
            {
                // "SELECT id + 1 AS n" has an alias but no column name behind it; either way the
                // name the outer query sees is the alias when one was given.
                var name = column.GetAliasString() ?? column.GetNameString();
                if (name is not { Length: > 0 })
                {
                    continue;
                }

                OrmColumn? origin = null;
                if (column.GetAliasString() is null or { Length: 0 } || column.GetNameString() is { Length: > 0 })
                {
                    origin = FindOrigin(innerSources, column.GetNameString(), QualifierOf(column));
                }

                builder.Add(new SqlSourceColumn(name, origin));
            }

            foreach (var star in query.Stars)
            {
                foreach (var source in innerSources)
                {
                    if (star.Qualifier is { Length: > 0 } qualifier &&
                        !string.Equals(LastSegment(qualifier), source.Qualifier, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    builder.AddRange(source.Columns);
                }
            }

            return builder.ToImmutable();
        }

        private static OrmColumn? FindOrigin(ImmutableArray<SqlSource> sources, string? columnName, string? qualifier)
        {
            if (columnName is not { Length: > 0 })
            {
                return null;
            }

            OrmColumn? found = null;
            foreach (var source in sources)
            {
                if (qualifier is { Length: > 0 } &&
                    !string.Equals(qualifier, source.Qualifier, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (source.FindColumn(columnName) is { } column)
                {
                    if (found is not null)
                    {
                        // Ambiguous across sources - nothing worth pointing at.
                        return null;
                    }

                    found = column.Origin;
                }
            }

            return found;
        }

        private static string? QualifierOf(SqlSelectSyntaxInfo.ColumnInfo column)
        {
            var identifiers = column.Name?.Identifiers;
            return identifiers is { Count: >= 2 } ? identifiers[identifiers.Count - 2].Value : null;
        }

        /// <summary>"dbo.users" is qualified by "users"; the parts in front locate the table, not the source.</summary>
        public static string LastSegment(string qualifier)
        {
            var lastDot = qualifier.LastIndexOf('.');
            return lastDot < 0 ? qualifier : qualifier.Substring(lastDot + 1);
        }
    }
}
