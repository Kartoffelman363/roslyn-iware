// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;

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
            //var body = originalBinder.BindPossibleEmbeddedStatement(_syntax.Statement, diagnostics);
            var body = this.BindEmbeddedBlock(_syntax.Block, diagnostics);

            var iDataRecordType = Compilation.GetTypeByMetadataName("System.Data.IDataRecord");

            return new BoundSqlDoClause(_syntax, /*this.Locals, */body, this.Locals);
        }

        protected override ImmutableArray<LocalSymbol> BuildLocals()
        {
            //var _recordType = SyntaxFactory.QualifiedName(
            //    SyntaxFactory.QualifiedName(
            //        SyntaxFactory.IdentifierName("System"),
            //        SyntaxFactory.IdentifierName("Data")),
            //    SyntaxFactory.IdentifierName("IDataRecord").WithTrailingTrivia(SyntaxFactory.Space));

            //var recordType = Compilation.GetTypeByMetadataName("System.Data.IDataRecord");
            var recordType = SyntaxFactory.ParseTypeName("System.Data.IDataRecord");

            var locals = ArrayBuilder<LocalSymbol>.GetInstance();
            var recordLocal = SourceLocalSymbol.MakeLocal(
                containingSymbol: this.ContainingMemberOrLambda,
                scopeBinder: this,
                allowRefKind: false,
                allowScoped: false,
                typeSyntax: recordType,
                identifierToken: SyntaxFactory.Identifier("record"),
                declarationKind: LocalDeclarationKind.RegularVariable,
                initializer: null
            );
            locals.Add(recordLocal);

            //originalBinder.Locals.Add(recordLocal);
            return locals.ToImmutableAndFree();
        }
    }
}
