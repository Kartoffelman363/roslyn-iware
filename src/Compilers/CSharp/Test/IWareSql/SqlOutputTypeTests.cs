// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.IWareSql.UnitTests
{
    /// <summary>
    /// Tests the check that an output binding's target can actually hold what the column selects
    /// into it, for columns typed as domains rather than as plain CLR types.
    /// </summary>
    public sealed class SqlOutputTypeTests : CSharpTestBase
    {
        /// <summary>
        /// A table whose key is a domain, written by hand because iWare.Domain.Generators does not
        /// run inside a test compilation - what matters here is only that the column's type
        /// derives from Domain&lt;int?&gt;, which is what the real generated domains do.
        /// </summary>
        private const string Tables = """
            #pragma warning disable CS8981
            using iWareSql.Domain.Abstractions;

            public class UserIdDomain : Domain<int?>
            {
                public UserIdDomain() : base(null) { }
                public override int? InstanceDefaultValue => null;
                public override bool InstanceIsNullable => true;
            }

            [Orm]
            public class users
            {
                public UserIdDomain id { get; set; }
                public string name { get; set; }
            }
            """;

        private static string Program(string declaration, string sqlBody) => Tables + $$"""

            public class P
            {
                public static void Main()
                {
                    {{declaration}}
                    sql
                    {
            {{sqlBody}}
                    }
                    sqldo
                    {
                    }
                }
            }
            """;

        private static string[] OutputTypeErrors(string source) =>
            CSharpCompilation.Create(
                    assemblyName: "SqlOutputTypeTest",
                    syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
                    references: SqlTestReferences.Runtime,
                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .ToArray();

        [Fact]
        public void DomainColumnReadsIntoItsUnderlyingNullable()
        {
            // Domain<int?> unwraps to int? while a nullable target unwraps to int, so comparing
            // them without normalising both reported "cannot read a int? into 'int?'".
            var errors = OutputTypeErrors(Program("int? id;", "        SELECT id[id] FROM users"));

            Assert.Empty(errors);
        }

        [Fact]
        public void DomainColumnReadsIntoTheBareUnderlyingType()
        {
            var errors = OutputTypeErrors(Program("int id;", "        SELECT id[id] FROM users"));

            Assert.Empty(errors);
        }

        [Fact]
        public void DomainColumnReadsIntoTheDomainItself()
        {
            var errors = OutputTypeErrors(Program("UserIdDomain id = new();", "        SELECT id[id] FROM users"));

            Assert.Empty(errors);
        }

        [Fact]
        public void DomainColumnIntoAMismatchedTargetIsStillReported()
        {
            var errors = OutputTypeErrors(Program("string s;", "        SELECT id[s] FROM users"));

            Assert.Contains(errors, e => e.Contains("cannot read a int into 'string'"));
        }

        [Fact]
        public void LiteralIntoADomainTargetIsStillReported()
        {
            var errors = OutputTypeErrors(Program("UserIdDomain id = new();", "        SELECT 'abababab'[id] FROM users"));

            Assert.Contains(errors, e => e.Contains("cannot read a string into 'UserIdDomain'"));
        }
    }
}
