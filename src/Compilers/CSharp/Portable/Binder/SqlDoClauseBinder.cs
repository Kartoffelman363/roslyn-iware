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
            var body = originalBinder.BindPossibleEmbeddedStatement(_syntax.Statement, diagnostics);
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
