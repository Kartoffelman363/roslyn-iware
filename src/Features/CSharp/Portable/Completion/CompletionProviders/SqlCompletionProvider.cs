// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
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

    private static class Helpers
    {
        public static void Merge<T>(List<T> list, List<T> incomingList, HashSet<T> listHashSet)
        {
            var incomingHashSet = new HashSet<T>(incomingList);

            //Remove all elements from list not present in incomingList
            list.RemoveAll(tr =>
            {
                var shouldRemove = !incomingList.Contains(tr);
                if (shouldRemove)
                {
                    listHashSet.Remove(tr);
                }
                return shouldRemove;
            });

            // Add all elements from incomingList that don't exist in list
            foreach (var incoming in incomingList)
            {
                if (!listHashSet.Contains(incoming))
                {
                    list.Add(incoming);
                    listHashSet.Add(incoming);
                }
            }
        }
    }

    private class TableReference
    {
        public string _tableName;
        public List<string> _tableAliases = new();
        private readonly HashSet<string> _tableAliasesSet = new();
        public List<string> _columnNames = new();
        private readonly HashSet<string> _columnNamesSet = new();
        public DateTime _lastUpdatedColumns = DateTime.MinValue;

        public TableReference(string tableName)
        {
            _tableName = tableName;
        }

        public void MergeColumns(List<string> columnNames)
        {
            Helpers.Merge(_columnNames, columnNames, _columnNamesSet);

            _lastUpdatedColumns = DateTime.Now;
        }

        public void MergeAliases(List<string> tableAliases)
        {
            Helpers.Merge(_tableAliases, tableAliases, _tableAliasesSet);
        }

        public void UpdateColumnNames(CompletionContext context)
        {
            // Prevent function from firing too frequently
            if ((DateTime.Now - _lastUpdatedColumns).TotalSeconds < 5)
            {
                return;
            }

            var filePath = context.Document.FilePath;
            if (filePath == null)
            {
                return;
            }

            var lastUpdated = SqlCompletionQueries.TableChangedTime(filePath, _tableName);
            if (lastUpdated == null || lastUpdated < _lastUpdatedColumns)
            {
                return;
            }

            var columnNames = SqlCompletionQueries.GetColumnNames(filePath, _tableName);
            if (columnNames != null)
            {
                MergeColumns(columnNames);
            }
        }

        public List<string> GetColumnNames()
        {
            return _columnNames ?? [];
        }
    }

    private class TableReferences
    {
        public readonly List<TableReference> _tableReferences = new();
        private readonly HashSet<string> _tableReferencesNamesSet = new();
        public DateTime _lastUpdated = DateTime.MinValue;

        public void Merge(List<string> tableNames)
        {
            var tableNamesSet = new HashSet<string>(tableNames);

            //Remove all tableReferences not in tableNames
            _tableReferences.RemoveAll(tr => {
                var shouldRemove = !tableNamesSet.Contains(tr._tableName);
                if (shouldRemove)
                {
                    _tableReferencesNamesSet.Remove(tr._tableName);
                }
                return shouldRemove;
            });

            // Add tableNames which don't exist in tableReferences
            foreach (var tableName in tableNames)
            {
                if (!_tableReferencesNamesSet.Contains(tableName))
                {
                    _tableReferences.Add(new TableReference(tableName));
                    _tableReferencesNamesSet.Add(tableName);
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
            // Prevent function from firing too frequently
            if ((DateTime.Now - _lastUpdated).TotalSeconds < 5)
            {
                return;
            }

            var filePath = context.Document.FilePath;
            if (filePath == null)
            {
                return;
            }

            var lastUpdated = SqlCompletionQueries.LastTableChangedTime(filePath);
            if (lastUpdated == null && lastUpdated < _lastUpdated)
            {
                return;
            }

            var tableNames = SqlCompletionQueries.GetTableNames(filePath);
            if (tableNames != null)
            {
                Merge(tableNames);
            }
        }

        public TableReference? GetTableReference(string tableName)
        {
            return _tableReferences.Find(tr => tr._tableName == tableName);
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
        //Debugger.Launch();

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

        _tableReferences.UpdateTableNames(context); // TODO aljaz do we always want to be updating tableNames?

        if (token.HasValue)
        {
            var dotToken = token;

            // previous token is dot and token before that is table name
            if (dotToken.ToString() == "." || (dotToken = dotToken.Value.GetPreviousToken()).ToString() == ".")
            {
                // Get table reference for name of token before dot
                var tableReference = _tableReferences.GetTableReference(dotToken.Value.GetPreviousToken().ToString());
                if (tableReference != null)
                {
                    tableReference.UpdateColumnNames(context);
                    var columnNames = tableReference.GetColumnNames();
                    foreach (var columnName in columnNames)
                    {
                        context.AddItem(CompletionItem.Create(
                            displayText: columnName,
                            filterText: columnName,
                            sortText: columnName,
                            rules: s_sqlCompletionRules,
                            tags: [WellKnownTags.Keyword]));
                    }

                    return;
                }
            }
        }

        //var tableReferences = FindTableAliases(sqlBlock);
        var tableNames = _tableReferences.GetTableNames();
        foreach (var tableName in tableNames)
        {
            context.AddItem(CompletionItem.Create(
                displayText: tableName,
                filterText: tableName,
                sortText: tableName,
                rules: s_sqlCompletionRules,
                tags: [WellKnownTags.Keyword]));
        }

        foreach (var kw in s_sqlKeywords)
        {
            context.AddItem(CompletionItem.Create(
                displayText: kw,
                filterText: kw,
                sortText: kw,
                rules: s_sqlCompletionRules,
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
                rules: s_sqlCompletionRules,
                tags: [WellKnownTags.Local]));

            var withAt = "@" + symbol.Name;
            context.AddItem(CompletionItem.Create(
                displayText: withAt,
                filterText: withAt,
                sortText: withAt,
                rules: s_sqlCompletionRules,
                tags: [WellKnownTags.Local]));
        }
    }

    private static readonly CompletionItemRules s_sqlCompletionRules = CompletionItemRules.Default
    .WithCommitCharacterRule(
        CharacterSetModificationRule.Create(
            CharacterSetModificationKind.Remove,
            ' '));

    private ImmutableHashSet<char> TriggerCharacters { get; } = ['.'];
    public override bool ShouldTriggerCompletion(SourceText text, int caretPosition, CompletionTrigger trigger, OptionSet options)
    {
        return trigger.Kind == CompletionTriggerKind.Insertion && (char.IsLetter(trigger.Character) || TriggerCharacters.Contains(trigger.Character));
        //return base.ShouldTriggerCompletion(text, caretPosition, trigger, options);
    }
}
