// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed class SqlEmptyClauseBinder : LocalScopeBinder
    {
        private readonly SqlEmptyClauseSyntax _syntax;

        public SqlEmptyClauseBinder(Binder enclosing, SqlEmptyClauseSyntax syntax)
            : base(enclosing, enclosing.Flags)
        {
            Debug.Assert(syntax != null && syntax.IsKind(SyntaxKind.SqlEmptyClause));
            _syntax = syntax;
        }

        internal override BoundSqlEmptyClause BindSqlEmptyParts(BindingDiagnosticBag diagnostics, Binder originalBinder)
        {
            var body = this.BindEmbeddedBlock(_syntax.Block, diagnostics);
            return new BoundSqlEmptyClause(_syntax, body);
        }
    }
}
