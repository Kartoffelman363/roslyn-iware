// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp.Completion.iWareSql;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Roslyn.Utilities;

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

    private readonly QueryInfo _tableReferences = new();

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

    private abstract class SourceReference
    {
        public List<string> _columnNames = new();
    }

    private class TableReference : SourceReference
    {
        public string? _tableName;
        private readonly HashSet<string> _columnNamesSet = new();
        public DateTime _lastUpdatedColumns = DateTime.MinValue;
        public DateTime _lastAttemptedUpdateColumns = DateTime.MinValue;

        public TableReference(string? tableName)
        {
            _tableName = tableName;
        }

        public void MergeColumns(List<string> columnNames)
        {
            Helpers.Merge(_columnNames, columnNames, _columnNamesSet);

            _lastUpdatedColumns = DateTime.Now;
        }

        public void UpdateColumnNames(CompletionContext context)
        {
            // Anonymous table
            if (_tableName == null)
            {
                return;
            }

            // Prevent function from firing too frequently
            if ((DateTime.Now - _lastAttemptedUpdateColumns).TotalSeconds < 5)
            {
                return;
            }
            _lastAttemptedUpdateColumns = DateTime.Now;

            // No db settings file
            var filePath = context.Document.FilePath;
            if (filePath == null)
            {
                return;
            }

            // Last update more recent than last change on database
            var lastUpdated = SqlCompletionQueries.TableChangedTime(filePath, _tableName);
            if (lastUpdated == null || lastUpdated < _lastUpdatedColumns)
            {
                return;
            }

            var columnNames = SqlCompletionQueries.GetColumnNamesFromTable(filePath, _tableName);
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

    private class QueryInfo
    {
        public readonly List<TableReference> _tableReferences = new();
        private readonly HashSet<string> _tableReferencesNamesSet = new();
        public DateTime _lastUpdated = DateTime.MinValue;
        public DateTime _lastAttemptedUpdate = DateTime.MinValue;
        public DateTime _lastAttemptedColumnUpdate = DateTime.MinValue;
        public SqlSelectSyntaxInfo? _syntaxInfo = null;
        public SqlSelectSyntaxInfo? _currentQuery = null;
        public SqlSelectSyntaxInfo? CurrentQuery
        {
            get => _currentQuery ?? _syntaxInfo;
            set => _currentQuery = value;
        }

        public void Merge(List<string> tableNames)
        {
            var tableNamesSet = new HashSet<string>(tableNames);

            //Remove all tableReferences not in tableNames
            _tableReferences.RemoveAll(tr =>
            {
                if (tr._tableName != null && !tableNamesSet.Contains(tr._tableName!))
                {
                    _tableReferencesNamesSet.Remove(tr._tableName);
                    return true;
                }
                return false;
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
            return _tableReferences
                .ConvertAll(tr => tr._tableName)
                .OfType<string>()
                .ToList();
        }

        public List<(string Alias, string TableName)> GetTableAliases()
        {
            List<(string, string)> aliases = new();

            // Add all tables with an alias
            var tables = CurrentQuery?._tables;
            if (tables != null)
            {
                aliases.AddRange(tables
                    .Select(tab => (Alias: tab.GetAliasString(), Name: tab.GetNameString()))
                    .Where(tab => tab.Alias != null && tab.Name != null)
                    .Select(tab => (tab.Alias!, tab.Name!)));
            }

            // Add all direct subqueries with an alias
            var subqueries = CurrentQuery?._subqueries;
            if (subqueries != null)
            {
                aliases.AddRange(subqueries
                    .Select(sub => (Alias: sub.GetAliasString(), Name: "Subquery"))
                    .Where(sub => sub.Alias != null)
                    .Select(sub => (sub.Alias!, sub.Name)));
            }

            return aliases;
        }

        public void UpdateTableNames(CompletionContext context)
        {
            // Prevent function from firing too frequently
            if ((DateTime.Now - _lastAttemptedUpdate).TotalSeconds < 5)
            {
                return;
            }
            _lastAttemptedUpdate = DateTime.Now;

            var filePath = context.Document.FilePath;
            if (filePath == null)
            {
                return;
            }

            var lastUpdated = SqlCompletionQueries.LastTableChangedTime(filePath);
            if (lastUpdated == null || lastUpdated < _lastUpdated)
            {
                return;
            }

            var tableNames = SqlCompletionQueries.GetTableNames(filePath);
            if (tableNames != null)
            {
                Merge(tableNames);
            }
        }

        public void UpdateQuerySyntaxInfo(SqlTextBlockSyntax sqlBlock, CompletionContext context)
        {
            var sqlStatement = sqlBlock.Segments;
            var sqlStatementString = sqlStatement.ToFullString();
            var relativePosition = context.Position - sqlStatement.FullSpan.Start;
            _syntaxInfo = SqlSelectSyntaxInfo.GetInfoFromString(sqlStatementString) ?? _syntaxInfo;
            CurrentQuery = _syntaxInfo?.GetQueryAtCursorPosition(relativePosition);
        }

        public TableReference? GetTableReferenceByNameOrAlias(string nameOrAlias)
        {
            return GetTableReferenceByName(nameOrAlias) ?? GetTableReferenceByAlias(nameOrAlias);
        }

        public TableReference? GetTableReferenceByName(string tableName)
        {
            return _tableReferences.Find(tr => tr._tableName == tableName);
        }

        public TableReference? GetTableReferenceByAlias(string alias)
        {
            var tableInfo = CurrentQuery?._tables.Find(tab => tab.CompareAlias(alias));
            var tableName = tableInfo?.GetNameString();
            if (tableName != null)
            {
                return _tableReferences.Find(tr => tr._tableName?.Equals(tableName, StringComparison.InvariantCultureIgnoreCase) ?? false);
            }

            var subqueryInfo = CurrentQuery?._subqueries.Find(subquery => subquery.CompareAlias(alias));
            if (subqueryInfo != null)
            {
                // Return anonymous table reference
                var anonymousTable = new TableReference(null);
                anonymousTable._columnNames = subqueryInfo._columns
                    .Select(col => col.GetNameString())
                    .OfType<string>()
                    .ToList();
                return anonymousTable;
            }
            return null;
        }

        // List of column names or aliaes defined in the local context
        public List<string> GetLocallyReferencedColumnNames()
        {
            List<string> columnNames = new();

            List<SqlSelectSyntaxInfo.ColumnInfo> columns =
            [
                .. CurrentQuery?._columns ?? [],
                .. CurrentQuery?._subqueries.SelectMany(subquery => subquery._columns) ?? [],
            ];

            columnNames.AddRange(columns
                .Select(col => col.GetAliasString() ?? col.GetNameString())
                .OfType<string>());

            return columnNames;
        }

        // List of column names belonging to tables referenced in the local context
        public List<string> GetLocallyReferencedTableColumnNames()
        {
            List<string> columnNames = new();

            foreach (var tableReference in GetLocallyReferencedTableReferences())
            {
                columnNames.AddRange(tableReference._columnNames);
            }

            return columnNames;
        }

        public List<TableReference> GetLocallyReferencedTableReferences()
        {
            List<TableReference> tableReferences = new();

            var syntaxTables = CurrentQuery?._tables;
            if (syntaxTables != null)
            {
                tableReferences.AddRange(_tableReferences
                    .Where(tr => syntaxTables
                        .Any(st => st.GetNameString()?.Equals(tr._tableName) ?? false)));
            }

            return tableReferences;
        }

        // Update column list of tables referenced in the local context
        public void UpdateLocallyReferencedTableColumnNames(CompletionContext context)
        {
            if ((DateTime.Now - _lastAttemptedColumnUpdate).TotalSeconds < 5)
            {
                return;
            }
            _lastAttemptedColumnUpdate = DateTime.Now;

            var tableReferences = GetLocallyReferencedTableReferences().Where(tr => !string.IsNullOrEmpty(tr._tableName));

            // TODO aljaz use GetColumnNamesFromTables and TablesChangedTime to reduce number of queries

            var tableNames = tableReferences
                .Select(tr => tr._tableName)
                .OfType<string>()
                .ToList();

            // Prevent function from firing too frequently
            //if ((DateTime.Now - _lastUpdatedColumns).TotalSeconds < 5)
            //{
            //    return;
            //}

            // No db settings file
            var filePath = context.Document.FilePath;
            if (filePath == null)
            {
                return;
            }

            if (tableNames.IsEmpty())
            {
                return;
            }

            var tablesChangedTimes = SqlCompletionQueries.TablesChangedTime(filePath, tableNames);
            List<TableReference> updateTables = new();
            updateTables.AddRange(tableReferences
                .Where(tr => tablesChangedTimes
                    .Any(tct => (tr._tableName?
                        .Equals(tct.Table, StringComparison.InvariantCultureIgnoreCase) ?? false) && tr._lastUpdatedColumns < tct.Time)));

            var columnsAndTables = SqlCompletionQueries.GetColumnNamesFromTables(filePath, updateTables.Select(ut => ut._tableName!).ToList());

            foreach (var ut in updateTables)
            {
                ut.MergeColumns(columnsAndTables
                    .Where(cat => cat.Table.Equals(ut._tableName, StringComparison.InvariantCultureIgnoreCase))
                    .Select(cat => cat.Column)
                    .ToList());
            }
            /*
            foreach (var tableReference in tableReferences)
            {
                tableReference.UpdateColumnNames(context);
            }
            */
        }
    }

    // If found match returns true
    private bool dotTokenCompletion(SyntaxToken? token, CompletionContext context)
    {
        if (token.HasValue)
        {
            var dotToken = token;

            // previous token is dot and token before that is table name
            if (dotToken.ToString() == "." || (dotToken = dotToken.Value.GetPreviousToken()).ToString() == ".")
            {
                // Get table reference for name of token before dot
                var tableName = dotToken.Value.GetPreviousToken().ToString();
                var tableReference = _tableReferences.GetTableReferenceByNameOrAlias(tableName);
                if (tableReference == null)
                {
                    return false;
                }

                // Update if not anonymous table
                if (tableReference._tableName != null)
                {
                    tableReference.UpdateColumnNames(context);
                }

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

                return true;
            }
        }

        return false;
    }

    public override async Task ProvideCompletionsAsync(CompletionContext context)
    {
        if (!System.Diagnostics.Debugger.IsAttached)
        {
            System.Diagnostics.Debugger.Launch();
        }

        var tree = await context.Document.GetSyntaxTreeAsync(context.CancellationToken).ConfigureAwait(false);
        var root = tree?.GetRoot(context.CancellationToken);
        var token = root?.FindToken(context.Position);

        // Walk up to see if we're inside your custom SqlBlockSyntax node
        var sqlBlock = token?.Parent?.AncestorsAndSelf()
            .FirstOrDefault(n => n.IsKind(SyntaxKind.SqlTextBlock)) as SqlTextBlockSyntax;

        if (sqlBlock is null)
        {
            return;
        }

        _tableReferences.UpdateTableNames(context); // TODO aljaz do we always want to be updating tableNames?
        _tableReferences.UpdateQuerySyntaxInfo(sqlBlock, context);
        _tableReferences.UpdateLocallyReferencedTableColumnNames(context);

        if (dotTokenCompletion(token, context))
        {
            return;
        }

        foreach (var alias in _tableReferences.GetTableAliases())
        {
            var displayText = $"{alias.Alias} [{alias.TableName}]";
            context.AddItem(CompletionItem.Create(
                //displayText: $"{alias.Alias} [{alias.TableName}]",
                displayText: displayText,
                filterText: displayText,
                sortText: displayText,
                properties: ImmutableDictionary<string, string>.Empty.Add("InsertionText", alias.Alias),
                rules: s_sqlCompletionRules,
                tags: [WellKnownTags.Keyword]));
        }

        foreach (var tableName in _tableReferences.GetTableNames())
        {
            context.AddItem(CompletionItem.Create(
                displayText: tableName,
                filterText: tableName,
                sortText: tableName,
                rules: s_sqlCompletionRules,
                tags: [WellKnownTags.Keyword]));
        }

        foreach (var column in _tableReferences.GetLocallyReferencedColumnNames())
        {
            context.AddItem(CompletionItem.Create(
                displayText: column,
                filterText: column,
                sortText: column,
                rules: s_sqlCompletionRules,
                tags: [WellKnownTags.Keyword]));
        }

        foreach (var column in _tableReferences.GetLocallyReferencedTableColumnNames())
        {
            context.AddItem(CompletionItem.Create(
                displayText: column,
                filterText: column,
                sortText: column,
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

    public override Task<CompletionChange> GetChangeAsync(
    Document document, CompletionItem item, char? commitKey, CancellationToken cancellationToken)
    {
        if (item.TryGetProperty("InsertionText", out var insertionText))
        {
            return Task.FromResult(CompletionChange.Create(
                new TextChange(item.Span, insertionText)));
        }

        return base.GetChangeAsync(document, item, commitKey, cancellationToken);
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
