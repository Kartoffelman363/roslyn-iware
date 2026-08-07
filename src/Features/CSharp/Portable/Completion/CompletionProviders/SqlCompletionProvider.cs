// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
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
using Microsoft.CodeAnalysis.CSharp.iWareSql;

namespace Microsoft.CodeAnalysis.CSharp.Completion.Providers;

[ExportCompletionProvider(nameof(SqlCompletionProvider), LanguageNames.CSharp), Shared]
[ExtensionOrder(After = nameof(KeywordCompletionProvider))]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class SqlCompletionProvider : CompletionProvider
{
    private readonly QueryInfo _queryInfo = new();

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public SqlCompletionProvider()
    {
    }

    /*
     * If cursor is in such a position that we're autocompleting a table property e.g.
     * tableName.<partialPropertyName>
     * then we output the siginificant property names and return true
     */
    private async Task<bool> DotTokenCompletion(SyntaxToken? token, CompletionContext context)
    {
        if (token.HasValue)
        {
            var dotToken = token;

            // previous token is dot and token before that is table name
            if (dotToken.ToString() == "." || (dotToken = dotToken.Value.GetPreviousToken()).ToString() == ".")
            {
                // Get table reference for name of token before dot
                var sourceName = dotToken.Value.GetPreviousToken().ToString();
                var sourceReference = _queryInfo.GetSourceReferenceByNameOrAlias(sourceName);
                if (sourceReference == null)
                {
                    return false;
                }

                foreach (var columnName in sourceReference.ColumnNames)
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
#if DEBUG
        if (!System.Diagnostics.Debugger.IsAttached)
        {
            System.Diagnostics.Debugger.Launch();
        }
#endif

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

        // Table names should update first since query syntax references it's results
        await _queryInfo.UpdateTableNamesAsync(context).ConfigureAwait(false);
        await _queryInfo.UpdateQuerySyntaxInfoAsync(sqlBlock, context).ConfigureAwait(false);
        //await _queryInfo.UpdateLocallyReferencedTableColumnNames(context).ConfigureAwait(false);

        if (await DotTokenCompletion(token, context).ConfigureAwait(false))
        {
            return;
        }

        foreach (var alias in _queryInfo.GetTableAliases())
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

        foreach (var tableName in _queryInfo.GetTableNames())
        {
            context.AddItem(CompletionItem.Create(
                displayText: tableName,
                filterText: tableName,
                sortText: tableName,
                rules: s_sqlCompletionRules,
                tags: [WellKnownTags.Keyword]));
        }

        foreach (var column in _queryInfo.GetLocallyReferencedColumnNames())
        {
            context.AddItem(CompletionItem.Create(
                displayText: column,
                filterText: column,
                sortText: column,
                rules: s_sqlCompletionRules,
                tags: [WellKnownTags.Keyword]));
        }

        foreach (var column in _queryInfo.GetLocallyReferencedSourcesColumnNames())
        {
            context.AddItem(CompletionItem.Create(
                displayText: column,
                filterText: column,
                sortText: column,
                rules: s_sqlCompletionRules,
                tags: [WellKnownTags.Keyword]));
        }

        foreach (var kw in SqlKeywords.SqlKeywordsList)
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
