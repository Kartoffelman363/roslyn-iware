// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Completion;
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

    /*
    public override ImmutableHashSet<char> TriggerCharacters => throw new NotImplementedException();

    internal override string Language => throw new NotImplementedException();
    */

    private static readonly string[] SqlKeywords =
{
        "SELECT", "FROM", "WHERE", "ORDER BY", "GROUP BY",
        "JOIN", "LEFT JOIN", "INNER JOIN", "HAVING", "AS", "AND", "OR"
    };

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public SqlCompletionProvider()
    {
    }

    public override async Task ProvideCompletionsAsync(CompletionContext context)
    {
        var tree = await context.Document.GetSyntaxTreeAsync(context.CancellationToken);
        var root = await tree.GetRootAsync(context.CancellationToken);
        var token = root.FindToken(context.Position);

        // Walk up to see if we're inside your custom SqlBlockSyntax node
        var sqlBlock = token.Parent?.AncestorsAndSelf()
            .FirstOrDefault(n => n.IsKind(SyntaxKind.SqlTextBlock));

        if (sqlBlock is null)
            return; // not in a sql {} block, defer to normal C# completion

        foreach (var kw in SqlKeywords)
        {
            context.AddItem(CompletionItem.Create(
                displayText: kw,
                filterText: kw,
                sortText: kw,
                rules: CompletionItemRules.Default,
                tags: [WellKnownTags.Keyword]));
        }
    }

    public override bool ShouldTriggerCompletion(SourceText text, int caretPosition, CompletionTrigger trigger, OptionSet options)
    {
        return trigger.Kind == CompletionTriggerKind.Insertion && char.IsLetter(trigger.Character);
        //return base.ShouldTriggerCompletion(text, caretPosition, trigger, options);
    }
}
