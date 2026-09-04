// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.iWareSql;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.CSharp.QuickInfo;

/// <summary>
/// Shows the ordinary symbol tooltip - signature, XML doc summary and all - when hovering a table
/// named inside a sql block.
/// </summary>
/// <remarks>
/// The semantic provider cannot do this on its own: a table name is plain sql text rather than a
/// C# expression, so binding the token under the cursor yields no symbol. This resolves the name
/// through the [Orm] schema instead and then hands the symbol it found to the same content
/// builder the semantic provider uses, so the tooltip is identical to the one shown when hovering
/// the class itself. Ordered before the semantic provider, which would otherwise return nothing
/// for this position anyway.
/// </remarks>
[ExportQuickInfoProvider(QuickInfoProviderNames.SqlTable, LanguageNames.CSharp), Shared]
[ExtensionOrder(Before = QuickInfoProviderNames.Semantic)]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class CSharpSqlTableQuickInfoProvider() : CommonQuickInfoProvider
{
    protected override async Task<QuickInfoItem?> BuildQuickInfoAsync(QuickInfoContext context, SyntaxToken token)
    {
        var cancellationToken = context.CancellationToken;
        var semanticModel = await context.Document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

        var table = FindTable(token, semanticModel, context.Position);
        if (table is not { Symbol: { } symbol })
        {
            return null;
        }

        return await QuickInfoUtilities.CreateQuickInfoItemAsync(
            context.Document.Project.Solution.Services,
            semanticModel,
            table.Value.Span,
            [symbol],
            context.Options,
            cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<QuickInfoItem?> BuildQuickInfoAsync(CommonQuickInfoContext context, SyntaxToken token)
    {
        var table = FindTable(token, context.SemanticModel, context.Position);
        if (table is not { Symbol: { } symbol })
        {
            return null;
        }

        return await QuickInfoUtilities.CreateQuickInfoItemAsync(
            context.Services,
            context.SemanticModel,
            table.Value.Span,
            [symbol],
            context.Options,
            context.CancellationToken).ConfigureAwait(false);
    }

    private static ResolvedSqlTable? FindTable(SyntaxToken token, SemanticModel semanticModel, int position)
    {
        var block = token.Parent?.FirstAncestorOrSelf<SqlTextBlockSyntax>();
        if (block is null)
        {
            return null;
        }

        return SqlTableResolution.FindTableAt(block, semanticModel.Compilation, position);
    }
}
