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
/// or column named inside a sql block.
/// </summary>
/// <remarks>
/// The semantic provider cannot do this on its own: these names are plain sql text rather than C#
/// expressions, so binding the token under the cursor yields no symbol. This resolves the name
/// through the [Orm] schema instead and then hands the symbol it found to the same content
/// builder the semantic provider uses, so the tooltip is identical to the one shown when hovering
/// the class or property itself. Ordered before the semantic provider, which would otherwise
/// return nothing for this position anyway.
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

        var reference = FindReference(token, semanticModel, context.Position);
        if (reference is not { Symbol: { } symbol })
        {
            return null;
        }

        return await QuickInfoUtilities.CreateQuickInfoItemAsync(
            context.Document.Project.Solution.Services,
            semanticModel,
            reference.Value.Span,
            [symbol],
            context.Options,
            cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<QuickInfoItem?> BuildQuickInfoAsync(CommonQuickInfoContext context, SyntaxToken token)
    {
        var reference = FindReference(token, context.SemanticModel, context.Position);
        if (reference is not { Symbol: { } symbol })
        {
            return null;
        }

        return await QuickInfoUtilities.CreateQuickInfoItemAsync(
            context.Services,
            context.SemanticModel,
            reference.Value.Span,
            [symbol],
            context.Options,
            context.CancellationToken).ConfigureAwait(false);
    }

    private static ResolvedSqlReference? FindReference(SyntaxToken token, SemanticModel semanticModel, int position)
    {
        var block = token.Parent?.FirstAncestorOrSelf<SqlTextBlockSyntax>();
        if (block is null)
        {
            return null;
        }

        return SqlTableResolution.FindReferenceAt(block, semanticModel.Compilation, position);
    }
}
