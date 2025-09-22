// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Roslyn.Test.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests.Parsing
{
    public class SqlParsingTests : ParsingTests
    {
        public SqlParsingTests(ITestOutputHelper output) : base(output) { }
        protected override SyntaxTree ParseTree(string text, CSharpParseOptions options)
        {
            return SyntaxFactory.ParseSyntaxTree(text);
        }

        protected override CSharpSyntaxNode ParseNode(string text, CSharpParseOptions options = null)
        {
            return SyntaxFactory.ParseExpression(text);
        }

        [Fact]
        public void AwaitOnIdentifierInAsynchronousContext()
        {
            Console.WriteLine("Whatever");
        }
    }
}
