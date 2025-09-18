// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed class SqlEndClauseBinder : LocalScopeBinder
    {
        private readonly SqlEndClauseSyntax _syntax;

        public SqlEndClauseBinder(Binder enclosing, SqlEndClauseSyntax syntax)
            : base(enclosing, enclosing.Flags)
        {
            Debug.Assert(syntax != null && syntax.IsKind(SyntaxKind.SqlEndClause));
            _syntax = syntax;
        }

        internal override BoundSqlEndClause BindSqlEndParts(BindingDiagnosticBag diagnostics, Binder originalBinder)
        {
            var body = this.BindEmbeddedBlock(_syntax.Block, diagnostics);
            return new BoundSqlEndClause(_syntax, body);
        }
    }
}
