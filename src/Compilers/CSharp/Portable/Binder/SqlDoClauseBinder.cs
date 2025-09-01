// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed class SqlDoClauseBinder : LocalScopeBinder
    {
        private readonly SqlDoClauseSyntax _syntax;

        public SqlDoClauseBinder(Binder enclosing, SqlDoClauseSyntax syntax)
            : base(enclosing, enclosing.Flags)
        {
            Debug.Assert(syntax != null && syntax.IsKind(SyntaxKind.SqlDoClause));
            _syntax = syntax;
        }

        internal override BoundSqlDoClause BindSqlDoParts(BindingDiagnosticBag diagnostics, Binder originalBinder)
        {
            //var body = SyntaxFactory.Block(new [] { _syntax.Statement });
            //var arrowToken = SyntaxFactory.Token(SyntaxKind.EqualsGreaterThanToken);
            //var asyncToken = SyntaxFactory.Token(SyntaxKind.None);
            ////var recordParam = SyntaxFactory.Parameter()
            //TypeSyntax recordType = SyntaxFactory.QualifiedName(
            //    SyntaxFactory.QualifiedName(
            //        SyntaxFactory.IdentifierName("System"),
            //        SyntaxFactory.IdentifierName("Data")),
            //    SyntaxFactory.IdentifierName("IDataRecord").WithTrailingTrivia(SyntaxFactory.Space));
            //TypeSyntax firstLineType = SyntaxFactory.PredefinedType(SyntaxFactory.Token//(SyntaxKind.BoolKeyword).WithTrailingTrivia(SyntaxFactory.Space));
            //ParameterListSyntax parameterList = SyntaxFactory.ParameterList([
            //    SyntaxFactory.Parameter(default, default, recordType, SyntaxFactory.Identifier("record"), null),
            //    SyntaxFactory.Parameter(default, default, firstLineType, SyntaxFactory.Identifier("firstLine"), null)
            //    ]);
            //
            var body = originalBinder.BindPossibleEmbeddedStatement(_syntax.Statement, diagnostics);
            //var lambdaSyntax = SyntaxFactory.ParenthesizedLambdaExpression(
            //    asyncToken,
            //    parameterList,
            //    arrowToken,
            //    body,
            //    null);
            //var unboundLambda = BindExpression(lambdaSyntax, diagnostics);
            //var boundLambda = 
            //Debug.Assert(this.Locals == this.GetDeclaredLocalsForScope(node));
            return new BoundSqlDoClause(_syntax, /*this.Locals, */body);
        }

        //internal override ImmutableArray<LocalSymbol> GetDeclaredLocalsForScope(SyntaxNode scopeDesignator)
        //{
        //    if (_syntax == scopeDesignator)
        //    {
        //        return this.Locals;
        //    }
        //
        //    throw ExceptionUtilities.Unreachable();
        //}
        //
        //internal override ImmutableArray<LocalFunctionSymbol> GetDeclaredLocalFunctionsForScope(CSharpSyntaxNode scopeDesignator)
        //{
        //    throw ExceptionUtilities.Unreachable();
        //}
        //
        //internal override SyntaxNode ScopeDesignator
        //{
        //    get
        //    {
        //        return _syntax;
        //    }
        //}
    }
}
