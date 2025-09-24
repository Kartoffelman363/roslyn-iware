// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp.Syntax
{
    public partial class SqlStatementSyntax
    {
        public SqlStatementSyntax Update(SyntaxToken sqlKeyword, SqlTextBlockSyntax sqlContents, SqlDoClauseSyntax? sqlDo, SqlEmptyClauseSyntax? sqlEmpty, SqlEndClauseSyntax? sqlEnd)
            => Update(AttributeLists, sqlKeyword, sqlContents, sqlDo, sqlEmpty, sqlEnd);
    }
}

namespace Microsoft.CodeAnalysis.CSharp
{
    public partial class SyntaxFactory
    {
        public static SqlStatementSyntax SqlStatement(
            SqlTextBlockSyntax sqlContents,
            SqlDoClauseSyntax? sqlDo,
            SqlEmptyClauseSyntax? sqlEmpty,
            SqlEndClauseSyntax? sqlEnd)
            => SqlStatement(attributeLists: default, sqlContents, sqlDo, sqlEmpty, sqlEnd
        );

        public static SqlStatementSyntax SqlStatement(
            SyntaxToken sqlKeyword,
            SqlTextBlockSyntax sqlContents,
            SqlDoClauseSyntax sqlDo,
            SqlEmptyClauseSyntax sqlEmpty,
            SqlEndClauseSyntax sqlEnd)
            => SqlStatement(attributeLists: default, sqlKeyword, sqlContents, sqlDo, sqlEmpty, sqlEnd);
    }
}
