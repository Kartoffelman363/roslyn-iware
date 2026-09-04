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
    /// A table named inside a sql block, located in the source file and resolved against the
    /// <c>[Orm]</c> classes the compilation can see.
    /// </summary>
    public readonly struct ResolvedSqlTable
    {
        public string Name { get; }
        public TextSpan Span { get; }

        /// <summary>The <c>[Orm]</c> class backing this table, or null if no such class exists.</summary>
        public INamedTypeSymbol? Symbol { get; }

        public ResolvedSqlTable(string name, TextSpan span, INamedTypeSymbol? symbol)
        {
            Name = name;
            Span = span;
            Symbol = symbol;
        }
    }

    /// <summary>
    /// Resolves the tables written inside a sql block to their declarations, for the IDE features
    /// that need to point at one: go-to-definition and classification.
    /// </summary>
    /// <remarks>
    /// This deliberately mirrors what the compiler does in VerifySql rather than sharing a code
    /// path with it - the compiler reports diagnostics into a binding pass, the IDE answers
    /// questions about a caret position - but both render the block through
    /// <see cref="SqlTextMap.Create"/> and read tables out with
    /// <see cref="SqlTableReferences.Collect"/>, so they agree on every span.
    /// </remarks>
    public static class SqlTableResolution
    {
        // Extracting tables depends only on the block's text, so it survives the edits that do not
        // touch the sql. Resolving them against the schema does not, and is redone every call.
        private static readonly ConcurrentDictionary<string, ImmutableArray<SqlTableReference>> s_tableCache = new();

        private const int MaxCacheEntries = 500;

        public static ImmutableArray<ResolvedSqlTable> ResolveTables(SqlTextBlockSyntax block, Compilation compilation)
        {
            var map = SqlTextMap.Create(block.Segments, out var sqlText);

            var tables = GetTables(sqlText);
            if (tables.IsEmpty)
            {
                return ImmutableArray<ResolvedSqlTable>.Empty;
            }

            var schema = OrmSchemaProvider.GetSchema(compilation);
            var builder = ImmutableArray.CreateBuilder<ResolvedSqlTable>(tables.Length);

            foreach (var table in tables)
            {
                builder.Add(new ResolvedSqlTable(
                    table.Name,
                    map.MapToSource(table.Offset, table.Length),
                    schema.FindTable(table.Name)?.Symbol));
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// The table whose name covers <paramref name="position"/>, or null if the caret is not on
        /// one. The end of the span counts as being on the table so that go-to-definition works
        /// with the caret just past the final character, as it does for ordinary identifiers.
        /// </summary>
        public static ResolvedSqlTable? FindTableAt(SqlTextBlockSyntax block, Compilation compilation, int position)
        {
            foreach (var table in ResolveTables(block, compilation))
            {
                if (table.Span.IntersectsWith(position))
                {
                    return table;
                }
            }

            return null;
        }

        private static ImmutableArray<SqlTableReference> GetTables(string sqlText)
        {
            if (s_tableCache.TryGetValue(sqlText, out var cached))
            {
                return cached;
            }

            var tables = ImmutableArray<SqlTableReference>.Empty;

            TSqlParser parser = new TSql180Parser(initialQuotedIdentifiers: true);
            using (var reader = new StringReader(sqlText))
            {
                var fragment = parser.Parse(reader, out var errors);

                // Same rule the compiler applies: a block that does not parse cleanly is being
                // typed, and the names ScriptDom recovered from it are not worth colouring or
                // navigating to.
                if (errors.Count == 0 && fragment is not null)
                {
                    tables = SqlTableReferences.Collect(fragment);
                }
            }

            // Unbounded growth would pin every version of every block the user has typed. The
            // cache is a pure optimisation, so dropping all of it is always safe.
            if (s_tableCache.Count >= MaxCacheEntries)
            {
                s_tableCache.Clear();
            }

            s_tableCache.TryAdd(sqlText, tables);
            return tables;
        }
    }
}
