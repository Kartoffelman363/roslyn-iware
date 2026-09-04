// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.iWareSql;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.GoToDefinition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.GoToDefinition;

[ExportLanguageService(typeof(IGoToDefinitionSymbolService), LanguageNames.CSharp), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class CSharpGoToDefinitionSymbolService() : AbstractGoToDefinitionSymbolService
{
    protected override Task<ISymbol> FindRelatedExplicitlyDeclaredSymbolAsync(Project project, ISymbol symbol, CancellationToken cancellationToken)
        => Task.FromResult(symbol);

    /// <summary>
    /// Adds go-to-definition on the tables and columns named inside a sql block, which the
    /// ordinary path cannot resolve: those are plain sql text, not C# expressions, so there is no
    /// bound node under the caret to ask for a symbol. Everywhere else this defers to the base.
    /// </summary>
    public override async Task<(ISymbol? symbol, Project project, TextSpan boundSpan)> GetSymbolProjectAndBoundSpanAsync(
        Document document, SemanticModel semanticModel, int position, CancellationToken cancellationToken)
    {
        var reference = await TryGetSqlReferenceAsync(document, semanticModel, position, cancellationToken).ConfigureAwait(false);
        if (reference is { Symbol: { } referenceSymbol })
        {
            return (referenceSymbol, document.Project, reference.Value.Span);
        }

        return await base.GetSymbolProjectAndBoundSpanAsync(document, semanticModel, position, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ResolvedSqlReference?> TryGetSqlReferenceAsync(
        Document document, SemanticModel semanticModel, int position, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        // FindToken(position) lands on the token to the right of the caret, so a caret sitting
        // immediately after a name would miss it; walking up from the token covers the common
        // case and FindReferenceAt's span check settles the boundary either way.
        var block = root.FindToken(position).Parent?.FirstAncestorOrSelf<SqlTextBlockSyntax>();
        if (block is null || !block.Span.Contains(position))
        {
            return null;
        }

        return SqlTableResolution.FindReferenceAt(block, semanticModel.Compilation, position);
    }

    protected override int? GetTargetPositionIfControlFlow(SemanticModel semanticModel, SyntaxToken token)
    {
        var node = token.GetRequiredParent();

        switch (token.Kind())
        {
            case SyntaxKind.ContinueKeyword:
                var foundContinuedLoop = TryFindContinuableConstruct(node);

                return foundContinuedLoop?.IsContinuableConstruct() == true
                    ? foundContinuedLoop.GetFirstToken().Span.Start
                    : null;

            case SyntaxKind.BreakKeyword:
                if (token.GetPreviousToken().IsKind(SyntaxKind.YieldKeyword))
                {
                    goto case SyntaxKind.YieldKeyword;
                }

                var foundBrokenLoop = TryFindBreakableConstruct(node);

                return foundBrokenLoop?.IsBreakableConstruct() == true
                    ? foundBrokenLoop.GetLastToken().Span.End
                    : null;

            case SyntaxKind.YieldKeyword:
            case SyntaxKind.ReturnKeyword:
                {
                    var foundReturnableConstruct = TryFindContainingReturnableConstruct(node);
                    if (foundReturnableConstruct is null)
                    {
                        return null;
                    }

                    var symbol = semanticModel.GetDeclaredSymbol(foundReturnableConstruct);
                    if (symbol is null)
                    {
                        // for lambdas
                        return foundReturnableConstruct.GetFirstToken().Span.Start;
                    }

                    return symbol.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0;
                }

            case SyntaxKind.GotoKeyword:
            case SyntaxKind.DefaultKeyword:
            case SyntaxKind.CaseKeyword:
                {
                    if (node.FirstAncestorOrSelf<GotoStatementSyntax>() is not GotoStatementSyntax gotoStatement)
                        return null;

                    if (semanticModel.GetOperation(gotoStatement) is not IBranchOperation gotoOperation)
                        return null;

                    Debug.Assert(gotoOperation is { BranchKind: BranchKind.GoTo });
                    var target = gotoOperation.Target;
                    return target.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()?.SpanStart;
                }
        }

        return null;

        static SyntaxNode? TryFindContinuableConstruct(SyntaxNode? node)
        {
            while (node is not null && !node.IsContinuableConstruct())
            {
                var kind = node.Kind();

                if (node.IsReturnableConstruct() ||
                    SyntaxFacts.GetTypeDeclarationKind(kind) != SyntaxKind.None)
                {
                    return null;
                }

                node = node.Parent;
            }

            return node;
        }

        static SyntaxNode? TryFindBreakableConstruct(SyntaxNode? node)
        {
            while (node is not null && !node.IsBreakableConstruct())
            {
                if (node.IsReturnableConstruct() ||
                    SyntaxFacts.GetTypeDeclarationKind(node.Kind()) != SyntaxKind.None)
                {
                    return null;
                }

                node = node.Parent;
            }

            return node;
        }

        static SyntaxNode? TryFindContainingReturnableConstruct(SyntaxNode? node)
        {
            while (node is not null && !node.IsReturnableConstruct())
            {
                if (SyntaxFacts.GetTypeDeclarationKind(node.Kind()) != SyntaxKind.None)
                {
                    return null;
                }

                node = node.Parent;
            }

            return node;
        }
    }
}
