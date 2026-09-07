// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.SqlQueries;
using Microsoft.CodeAnalysis.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

#pragma warning disable RS0016 // Add public types and members to the declared API
namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    /// <summary>
    /// A name written inside a sql block - a table, a column, or an alias standing for one -
    /// located in the source file and resolved against the <c>[Orm]</c> classes.
    /// </summary>
    public readonly struct ResolvedSqlReference
    {
        public string Name { get; }
        public TextSpan Span { get; }

        /// <summary>
        /// The declaration behind this name: the <c>[Orm]</c> class for a table or an alias of one,
        /// the property or field for a column. Null when nothing declares it.
        /// </summary>
        public ISymbol? Symbol { get; }

        /// <summary>True for a table or something standing for one, false for a column.</summary>
        public bool IsTable { get; }

        /// <summary>
        /// Whether to report this name as undeclared. False for anything whose source is itself
        /// unknown - the source's own occurrence carries that warning - and for names that could
        /// resolve against more than one source, which is sql's complaint to make rather than ours.
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
    /// Resolves what a sql block writes to the declarations behind it, for the compiler's
    /// diagnostics and for go-to-definition, hover and classification.
    /// </summary>
    /// <remarks>
    /// The parse and the query model both come from <see cref="SqlSelectSyntaxInfo"/>, which is
    /// also what completion works from, so every feature sees the same view of a block - including
    /// its subqueries.
    /// </remarks>
    public static class SqlTableResolution
    {
        // Parsing depends only on the text, so it survives edits that do not touch the sql.
        // Resolving against the schema does not, and is redone every call.
        private static readonly ConcurrentDictionary<string, SqlSelectSyntaxInfo?> s_parseCache = new();

        private const int MaxCacheEntries = 500;

        /// <summary>
        /// Parses the sql text into the query model, or null when it does not parse.
        /// </summary>
        public static SqlSelectSyntaxInfo? Parse(string sqlText)
        {
            if (s_parseCache.TryGetValue(sqlText, out var cached))
            {
                return cached;
            }

            SqlSelectSyntaxInfo? parsed = null;

            TSqlParser parser = new TSql180Parser(initialQuotedIdentifiers: true);
            using (var reader = new StringReader(sqlText))
            {
                var fragment = parser.Parse(reader, out var errors);

                // A block that does not parse cleanly is being typed, and the names ScriptDom
                // recovered from the wreckage are not worth colouring or navigating to.
                if (errors.Count == 0 && fragment is not null)
                {
                    parsed = SqlSelectSyntaxInfo.GetInfoFromFragment(fragment);
                }
            }

            if (s_parseCache.Count >= MaxCacheEntries)
            {
                s_parseCache.Clear();
            }

            s_parseCache.TryAdd(sqlText, parsed);
            return parsed;
        }

        public static ImmutableArray<ResolvedSqlReference> Resolve(SqlTextBlockSyntax block, Compilation compilation)
        {
            var map = SqlTextMap.Create(block.Segments, out var sqlText);
            return Resolve(Parse(sqlText), map, compilation);
        }

        public static ImmutableArray<ResolvedSqlReference> Resolve(
            SqlSelectSyntaxInfo? root,
            SqlTextMap map,
            Compilation compilation)
        {
            if (root is null)
            {
                return ImmutableArray<ResolvedSqlReference>.Empty;
            }

            var schema = OrmSchemaProvider.GetSchema(compilation);
            var builder = ImmutableArray.CreateBuilder<ResolvedSqlReference>();

            foreach (var query in root.FlattenQueries())
            {
                var sources = SqlSourceResolution.GetSources(query, schema);

                foreach (var occurrence in query.Occurrences)
                {
                    builder.Add(ResolveOccurrence(occurrence, query, sources, schema, map));
                }
            }

            return builder.ToImmutable();
        }

        private static ResolvedSqlReference ResolveOccurrence(
            SqlSelectSyntaxInfo.SqlIdentifierOccurrence occurrence,
            SqlSelectSyntaxInfo query,
            ImmutableArray<SqlSource> sources,
            OrmSchema schema,
            SqlTextMap map)
        {
            var span = map.MapToSource(occurrence.Offset, occurrence.Length);

            switch (occurrence.Kind)
            {
                case SqlSelectSyntaxInfo.SqlOccurrenceKind.TableName:
                    return new ResolvedSqlReference(
                        occurrence.Text,
                        span,
                        schema.FindTable(occurrence.Text)?.Symbol,
                        isTable: true,
                        reportIfUnresolved: true);

                case SqlSelectSyntaxInfo.SqlOccurrenceKind.SourceRef:
                    {
                        // For a star the text is "*", and what names the source is its qualifier;
                        // for an alias the text is the alias itself.
                        var name = occurrence.Text == "*"
                            ? occurrence.Qualifier
                            : occurrence.Text;

                        var source = name is { Length: > 0 }
                            ? FindSource(query, SqlSourceResolution.LastSegment(name), schema)
                            : SoleSource(query, schema);

                        return new ResolvedSqlReference(
                            source?.Qualifier ?? occurrence.Text,
                            span,
                            source?.Table?.Symbol,
                            isTable: true,
                            reportIfUnresolved: false);
                    }

                default:
                    {
                        var (symbol, sourceKnown) = ResolveColumn(occurrence, query, sources, schema);
                        return new ResolvedSqlReference(
                            occurrence.Text,
                            span,
                            symbol,
                            isTable: false,
                            reportIfUnresolved: sourceKnown);
                    }
            }
        }

        private static (ISymbol? Symbol, bool SourceKnown) ResolveColumn(
            SqlSelectSyntaxInfo.SqlIdentifierOccurrence occurrence,
            SqlSelectSyntaxInfo query,
            ImmutableArray<SqlSource> sources,
            OrmSchema schema)
        {
            if (occurrence.Qualifier is { Length: > 0 } qualifier)
            {
                var source = FindSource(query, SqlSourceResolution.LastSegment(qualifier), schema);
                if (source is null)
                {
                    // An unknown qualifier is a CTE or something this compilation cannot see;
                    // nothing to bind to and nothing worth complaining about.
                    return (null, false);
                }

                var column = source.FindColumn(occurrence.Text);

                // Reported only when the column is genuinely absent from a source whose columns are
                // known. A derived table whose columns could not be worked out would otherwise
                // report every column written against it, and a column that exists but carries no
                // member behind it - "SELECT id + 1 AS n" - is resolved even though it has no
                // symbol to offer.
                var columnsKnown = source.Table is not null || !source.Columns.IsEmpty;
                return (column?.Origin?.Symbol, columnsKnown && column is null);
            }

            // Unqualified: resolvable only if exactly one source in scope declares it.
            SqlSourceColumn? match = null;
            var matches = 0;
            var anyKnown = false;

            foreach (var source in sources)
            {
                if (source.Table is not null || !source.Columns.IsEmpty)
                {
                    anyKnown = true;
                }

                if (source.FindColumn(occurrence.Text) is { } column)
                {
                    matches++;
                    match ??= column;
                }
            }

            if (matches > 1)
            {
                return (null, false);
            }

            return (match?.Origin?.Symbol, anyKnown && match is null);
        }

        /// <summary>
        /// The source <paramref name="qualifier"/> names, looked for in <paramref name="query"/>
        /// and then in the queries it is nested in, so a correlated subquery can still reach out.
        /// </summary>
        public static SqlSource? FindSource(SqlSelectSyntaxInfo query, string qualifier, OrmSchema schema)
        {
            for (var scope = query; scope is not null; scope = scope.Parent)
            {
                foreach (var source in SqlSourceResolution.GetSources(scope, schema))
                {
                    if (string.Equals(source.Qualifier, qualifier, StringComparison.OrdinalIgnoreCase))
                    {
                        return source;
                    }
                }
            }

            return null;
        }

        /// <summary>The only source in scope, or null when there is not exactly one.</summary>
        public static SqlSource? SoleSource(SqlSelectSyntaxInfo query, OrmSchema schema)
        {
            var sources = SqlSourceResolution.GetSources(query, schema);
            return sources.Length == 1 ? sources[0] : null;
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
    }
}
