// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.IWareSql.UnitTests
{
    /// <summary>
    /// Tests the wildcard output binding, <c>SELECT a.*[obj]</c>, which fills every member of the
    /// bracketed object from the columns of the table the star stands for.
    /// </summary>
    public sealed class SqlWildcardTests : CSharpTestBase
    {
        private const string Target = """
            #pragma warning disable CS8981

            [Orm]
            public class users
            {
                public int id { get; set; }
                public string name { get; set; }
                public string surname { get; set; }
                public int role_id { get; set; }
            }

            [Orm]
            public class roles
            {
                public int id { get; set; }
                public string name { get; set; }
            }
            """;

        private static string Program(string sqlBody, string extra = "") => Target + extra + $$"""

            public class P
            {
                public static void Main()
                {
                    users myUsers = new();
                    roles myRoles = new();

                    sql
                    {
            {{sqlBody}}
                    }
                }
            }
            """;

        private static string[] Errors(string source) =>
            CreateCompilation(source).GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage())
                .ToArray();

        /// <summary>
        /// The sql text the statement will actually send, after wildcard expansion.
        /// </summary>
        /// <remarks>
        /// The expanded text only exists on the bound node, which has no public surface, and it
        /// reaches run time as the literal handed to SqlCommands.Begin. Reading it back out of the
        /// emitted assembly's user-string heap is therefore the closest observation point to what
        /// the server is really going to be sent.
        /// </remarks>
        private static string ExpandedSql(string source)
        {
            // Emitted rather than merely bound, because the expanded text only becomes observable
            // once lowering has put it into the assembly - which needs the references a sql block
            // binds against.
            var compilation = CSharpCompilation.Create(
                assemblyName: "SqlWildcardTest",
                syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
                references: SqlTestReferences.Runtime,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var peStream = new MemoryStream();
            var result = compilation.Emit(peStream);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            peStream.Position = 0;
            using var peReader = new PEReader(peStream);
            var metadata = peReader.GetMetadataReader();

            var handle = MetadataTokens.UserStringHandle(0);
            while (true)
            {
                handle = metadata.GetNextHandle(handle);
                if (handle.IsNil)
                {
                    break;
                }

                var value = metadata.GetUserString(handle);
                if (value.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            throw new Xunit.Sdk.XunitException("no SELECT literal was emitted");
        }

        [Fact]
        public void WildcardExpandsToOneAliasedColumnPerMember()
        {
            var sql = ExpandedSql(Program("        SELECT a.*[myUsers] FROM users a"));

            // The star is gone, replaced by every column of users, each qualified the way the star
            // was and aliased so the read loop can find it by name.
            Assert.DoesNotContain("*", sql);
            Assert.Contains("a.[id] [__c0]", sql);
            Assert.Contains("a.[name] [__c1]", sql);
            Assert.Contains("a.[surname] [__c2]", sql);
            Assert.Contains("a.[role_id] [__c3]", sql);
            Assert.Contains("FROM users a", sql);
        }

        [Fact]
        public void BareWildcardExpandsUnqualified()
        {
            var sql = ExpandedSql(Program("        SELECT *[myUsers] FROM users"));

            Assert.Contains("[id] [__c0]", sql);
            Assert.DoesNotContain(".[id]", sql);
        }

        [Fact]
        public void ExplicitBindingsKeepTheirAliasesAlongsideAWildcard()
        {
            var sql = ExpandedSql(Program(
                "        SELECT a.name[myRoles.name], a.*[myUsers] FROM users a"));

            // The explicit binding was numbered first, so the wildcard's columns continue after it.
            Assert.Contains("[__c0]", sql);
            Assert.Contains("a.[id] [__c1]", sql);
        }

        [Fact]
        public void UnboundStarSurvivesExpansionUntouched()
        {
            Assert.Contains("*", ExpandedSql(Program("        SELECT * FROM users")));
        }

        [Fact]
        public void QualifiedWildcardBindsEveryMember()
        {
            AssertEx.Empty(Errors(Program("        SELECT a.*[myUsers] FROM users a")));
        }

        [Fact]
        public void TableNameQualifiedWildcardWorks()
        {
            AssertEx.Empty(Errors(Program("        SELECT users.*[myUsers] FROM users")));
        }

        [Fact]
        public void BareWildcardWorksWithASingleTable()
        {
            AssertEx.Empty(Errors(Program("        SELECT *[myUsers] FROM users")));
        }

        [Fact]
        public void BareWildcardIsAmbiguousAcrossAJoin()
        {
            var errors = Errors(Program(
                "        SELECT *[myUsers] FROM users u LEFT JOIN roles r ON r.id = u.role_id"));

            Assert.Single(errors);
            Assert.Contains("ambiguous", errors[0]);
        }

        [Fact]
        public void MemberWithNoMatchingColumnIsAnError()
        {
            // roles has no "surname", so filling a users-shaped object from it cannot work.
            var errors = Errors(Program("        SELECT r.*[myUsers] FROM roles r"));

            Assert.Contains(errors, e => e.Contains("users.surname") && e.Contains("roles"));
        }

        [Fact]
        public void TargetNeedNotBeAnOrmClass()
        {
            var dto = """

                public class UserDto
                {
                    public int id { get; set; }
                    public string name { get; set; }
                }
                """;

            var source = Target + dto + """

                public class P
                {
                    public static void Main()
                    {
                        UserDto dto = new();

                        sql
                        {
                        SELECT a.*[dto] FROM users a
                        }
                    }
                }
                """;

            AssertEx.Empty(Errors(source));
        }

        [Fact]
        public void UnknownTableIsAnError()
        {
            var errors = Errors(Program("        SELECT a.*[myUsers] FROM rolez a"));

            Assert.Contains(errors, e => e.Contains("rolez"));
        }

        [Fact]
        public void GetterOnlyMemberIsReported()
        {
            var readOnlyDto = """

                public class ReadOnlyDto
                {
                    public int id { get; }
                }
                """;

            var source = Target + readOnlyDto + """

                public class P
                {
                    public static void Main()
                    {
                        ReadOnlyDto dto = new();

                        sql
                        {
                        SELECT a.*[dto] FROM users a
                        }
                    }
                }
                """;

            Assert.NotEmpty(Errors(source));
        }

        [Fact]
        public void UnboundStarIsLeftAlone()
        {
            // A star with no binding is ordinary sql and must not be expanded or complained about.
            AssertEx.Empty(Errors(Program("        SELECT * FROM users")));
        }

        [Fact]
        public void WildcardAndExplicitBindingsCoexist()
        {
            AssertEx.Empty(Errors(Program(
                "        SELECT a.*[myUsers], a.name[myRoles.name] FROM users a")));
        }
    }
}
