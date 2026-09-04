// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Classification.Classifiers;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.CSharp.iWareSql;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.Classification.Classifiers;

/// <summary>
/// Colours the tables and columns named inside a sql block, so a name that resolves to an
/// <c>[Orm]</c> class or one of its members reads like the declaration it is and one that
/// resolves to nothing stays plain sql text. The block is otherwise unclassified, which is what
/// makes the difference visible at a glance without needing to read the warning.
/// </summary>
internal sealed class SqlTableSyntaxClassifier : AbstractSyntaxClassifier
{
    public override ImmutableArray<Type> SyntaxNodeTypes { get; } = [typeof(SqlTextBlockSyntax)];

    public override void AddClassifications(
        SyntaxNode node,
        TextSpan textSpan,
        SemanticModel semanticModel,
        ClassificationOptions options,
        SegmentedList<ClassifiedSpan> result,
        CancellationToken cancellationToken)
    {
        var block = (SqlTextBlockSyntax)node;

        // Classification is requested for the visible window, so skip blocks scrolled out of it
        // before doing the work of parsing their sql.
        if (!block.Span.IntersectsWith(textSpan))
        {
            return;
        }

        foreach (var reference in SqlTableResolution.Resolve(block, semanticModel.Compilation))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // An unresolved name is left alone rather than given an error colour: the warning on
            // it already says what is wrong, and colouring it red as well would double up on a
            // name that is perfectly legitimate when the table lives outside the [Orm] classes.
            if (reference.Symbol is null || !reference.Span.IntersectsWith(textSpan))
            {
                continue;
            }

            result.Add(new ClassifiedSpan(
                reference.Span,
                reference.IsTable ? ClassificationTypeNames.ClassName : GetColumnClassification(reference.Symbol)));
        }
    }

    /// <summary>
    /// Columns are backed by either a property or a field, and colouring each as what it actually
    /// is keeps the block consistent with how the same member reads in ordinary C# code.
    /// </summary>
    private static string GetColumnClassification(ISymbol symbol)
        => symbol.Kind == SymbolKind.Field
            ? ClassificationTypeNames.FieldName
            : ClassificationTypeNames.PropertyName;
}
