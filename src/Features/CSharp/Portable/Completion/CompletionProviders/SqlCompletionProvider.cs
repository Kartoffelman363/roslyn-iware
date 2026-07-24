// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp.Completion.iWareSql;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.Completion.Providers;

[ExportCompletionProvider(nameof(SqlCompletionProvider), LanguageNames.CSharp), Shared]
[ExtensionOrder(After = nameof(KeywordCompletionProvider))]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class SqlCompletionProvider : CompletionProvider
{
    private static readonly string[] s_sqlKeywords =
    [
        "SELECT",
        "DISTINCT",
        "TOP",
        "PERCENT",
        "WITH TIES",
        "AS",
        "FROM",
        "JOIN",
        "INNER JOIN",
        "LEFT JOIN",
        "LEFT OUTER JOIN",
        "RIGHT JOIN",
        "RIGHT OUTER JOIN",
        "FULL JOIN",
        "FULL OUTER JOIN",
        "CROSS JOIN",
        "CROSS APPLY",
        "OUTER APPLY",
        "ON",
        "WHERE",
        "GROUP BY",
        "HAVING",
        "ORDER BY",
        "ASC",
        "DESC",
        "OFFSET",
        "FETCH",
        "NEXT",
        "ROWS",
        "ROWS ONLY",
        "UNION",
        "UNION ALL",
        "INTERSECT",
        "EXCEPT",
        "AND",
        "OR",
        "NOT",
        "IN",
        "BETWEEN",
        "LIKE",
        "IS NULL",
        "IS NOT NULL",
        "EXISTS",
        "ANY",
        "ALL",
        "SOME",
        "CASE",
        "WHEN",
        "THEN",
        "ELSE",
        "END",
        "OVER",
        "PARTITION BY",
        "ROW_NUMBER",
        "RANK",
        "DENSE_RANK",
        "NTILE",
        "WITH",
        "PIVOT",
        "UNPIVOT",
        "FOR",
        "TABLESAMPLE",
        "INTO",
        "COUNT",
        "COUNT_BIG",
        "SUM",
        "AVG",
        "MIN",
        "MAX",
        "STDEV",
        "STDEVP",
        "VAR",
        "VARP",
        "GROUPING",
        "GROUPING_ID",
        "CHECKSUM_AGG",
        "STRING_AGG",
        "APPROX_COUNT_DISTINCT",
    ];

    private readonly TableReferences _tableReferences = new();

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public SqlCompletionProvider()
    {
    }

    private class TableReference(string tableName)
    {
        public string _tableName = tableName;
        public List<string>? _tableAliases = null;
        public List<string>? _columnNames = null;
        public DateTime? _lastUpdated = null;
    }

    private class TableReferences
    {
        public readonly List<TableReference> _tableReferences = new();
        public DateTime? _lastUpdated = null;

        public void Merge(List<string> tableNames)
        {
            var existingNames = new HashSet<string>(_tableReferences.Select(tr => tr._tableName));

            //Remove all tableReferences not in tableNames
            _tableReferences.RemoveAll(tr => !existingNames.Contains(tr._tableName));

            // Add tableNames which don't exist in tableReferences
            foreach (var tableName in tableNames)
            {
                if (!existingNames.Contains(tableName))
                {
                    _tableReferences.Add(new TableReference(tableName));
                }
            }

            _lastUpdated = DateTime.Now;
        }

        public List<string> GetTableNames()
        {
            return _tableReferences.ConvertAll(tr => tr._tableName);
        }

        public void UpdateTableNames(CompletionContext context)
        {
            var filePath = context.Document.FilePath;
            if (filePath == null)
            {
                return;
            }

            var lastUpdated = SqlCompletionQueries.LastTableChangedTime(filePath);
            if (_lastUpdated != null && lastUpdated < _lastUpdated)
            {
                return;
            }

            var tableNames = SqlCompletionQueries.GetTableNames(filePath);
            if (tableNames != null)
            {
                _lastUpdated = DateTime.Now;
                Merge(tableNames);
            }
        }
    }

    /* TODO aljaz table name aliases
    private List<TableReference> extractTables(IEnumerator<SyntaxNode> sqlNodeEnumerator)
    {
        // Here we presume that the enumerator is at the position of an SQL "FROM"
        var tableReferences = new List<TableReference>();

        while (sqlNodeEnumerator.MoveNext())
        {
            var node = sqlNodeEnumerator.Current;
            if (!node.IsKind(SyntaxKind.SqlTextSegment))
            {
                break;
            }

        }

        return tableReferences;
    }

    private List<TableReference> FindTableAliases(SyntaxNode sqlBlock)
    {
        // TODO aljaz table alias cache -- najbrz bi bil veliko boljsi ce se ze v syntax, bind fazah nagrunta kateri del je FROM izjava in se samo FROM izjave preverja s cacheom
        var tableReferences = new List<TableReference>(); // TODO aljaz should be List<List<string>> containing the tableName and it's aliases
        var sqlNodes = sqlBlock.ChildNodes();
        var sqlNodeEnumerator = sqlNodes.GetEnumerator();
        while (sqlNodeEnumerator.MoveNext())
        {
            var node = sqlNodeEnumerator.Current;
            if (node.IsKind(SyntaxKind.SqlTextSegment) && node.ToString().Equals("FROM", StringComparison.CurrentCultureIgnoreCase))
            {
                // TODO aljaz figure out merges
                tableReferences.AddRange(extractTables(sqlNodeEnumerator));
            }
        }
        return tableReferences;
    }
    */

    public override async Task ProvideCompletionsAsync(CompletionContext context)
    {
        var tree = await context.Document.GetSyntaxTreeAsync(context.CancellationToken).ConfigureAwait(false);
        var root = tree?.GetRoot(context.CancellationToken);
        var token = root?.FindToken(context.Position);

        // Walk up to see if we're inside your custom SqlBlockSyntax node
        var sqlBlock = token?.Parent?.AncestorsAndSelf()
            .FirstOrDefault(n => n.IsKind(SyntaxKind.SqlTextBlock));

        if (sqlBlock is null)
        {
            return;
        }

        //var tableReferences = FindTableAliases(sqlBlock);
        _tableReferences.UpdateTableNames(context); // TODO aljaz do we always want to be updating tableNames?
        foreach (var tableName in _tableReferences.GetTableNames())
        {
            context.AddItem(CompletionItem.Create(
                displayText: tableName,
                filterText: tableName,
                sortText: tableName,
                rules: CompletionItemRules.Default,
                tags: [WellKnownTags.Keyword]));
        }

        foreach (var kw in s_sqlKeywords)
        {
            context.AddItem(CompletionItem.Create(
                displayText: kw,
                filterText: kw,
                sortText: kw,
                rules: CompletionItemRules.Default,
                tags: [WellKnownTags.Keyword]));
        }

        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (semanticModel == null)
        {
            return;
        }

        var symbols = semanticModel
            .LookupSymbols(context.Position)
            .Where(s => s.Kind is SymbolKind.Local or SymbolKind.Parameter or SymbolKind.Field);

        foreach (var symbol in symbols)
        {
            context.AddItem(CompletionItem.Create(
                displayText: symbol.Name,
                filterText: symbol.Name,
                sortText: symbol.Name,
                rules: CompletionItemRules.Default,
                tags: [WellKnownTags.Local]));

            var withAt = "@" + symbol.Name;
            context.AddItem(CompletionItem.Create(
                displayText: withAt,
                filterText: withAt,
                sortText: withAt,
                rules: CompletionItemRules.Default,
                tags: [WellKnownTags.Local]));
        }
    }

    public override bool ShouldTriggerCompletion(SourceText text, int caretPosition, CompletionTrigger trigger, OptionSet options)
    {
        return trigger.Kind == CompletionTriggerKind.Insertion && char.IsLetter(trigger.Character);
        //return base.ShouldTriggerCompletion(text, caretPosition, trigger, options);
    }
}
