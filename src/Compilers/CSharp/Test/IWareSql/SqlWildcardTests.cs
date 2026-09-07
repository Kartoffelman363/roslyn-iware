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
using Microsoft.CodeAnalysis.CSharp.iWareSql;
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

        /// <summary>
        /// Every name in the block resolved, as "written-text -> symbol", ordered by position.
        /// </summary>
        private static string[] ResolveAll(string source)
        {
            var compilation = CreateCompilation(source);
            var tree = compilation.SyntaxTrees.Single(t => t.ToString().Contains("sql"));
            var block = tree.GetRoot().DescendantNodes().OfType<SqlTextBlockSyntax>().Single();
            var text = tree.GetText();

            return SqlTableResolution.Resolve(block, compilation)
                .OrderBy(r => r.Span.Start)
                .Select(r => $"{text.ToString(r.Span)} -> " + (r.Symbol is null
                    ? "<unresolved>"
                    : r.Symbol.Kind == SymbolKind.NamedType
                        ? r.Symbol.Name
                        : $"{r.Symbol.ContainingType.Name}.{r.Symbol.Name}"))
                .ToArray();
        }

        /// <summary>
        /// The text a block is rendered as before being parsed - what the compiler verifies and
        /// what completion reads its query out of.
        /// </summary>
        private static string RenderedSql(string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var block = tree.GetRoot().DescendantNodes().OfType<SqlTextBlockSyntax>().Single();
            SqlTextMap.Create(block.Segments, out var sqlText);
            return sqlText;
        }

        [Fact]
        public void RenderedBlockWithWildcardsParsesCleanly()
        {
            // Completion builds its picture of the query by parsing this text, so a block that does
            // not parse leaves it with nothing to offer - no aliases, no columns. A binding whose
            // segment kind the renderer does not know falls through to its own source text, and
            // "*[obj]" is not valid sql, which is exactly how wildcards broke completion once.
            var source = Program("""
                        SELECT
                            u.*[myUsers],
                            r.*[myRoles]

                            FROM users u
                            LEFT JOIN roles r ON r.id = u.role_id
                            ORDER BY u.id DESC
                """);

            var rendered = RenderedSql(source);
            Assert.DoesNotContain("[myUsers]", rendered);

            var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql180Parser(initialQuotedIdentifiers: true);
            using var reader = new System.IO.StringReader(rendered);
            var fragment = parser.Parse(reader, out var errors);

            Assert.Empty(errors);

            // Both tables are recoverable, which is what completion needs in order to offer "u"
            // and "r" as things to type after.
            var query = Microsoft.CodeAnalysis.CSharp.SqlQueries.SqlSelectSyntaxInfo.GetInfoFromFragment(fragment);
            Assert.NotNull(query);
            AssertEx.SetEqual(
                new[] { "users", "roles" },
                query!.Tables.Select(t => t.GetNameString()).ToArray());
        }

        [Fact]
        public void SelectListEndingInACommaRecoversNothing()
        {
            // A select list left ending in a comma - exactly how it looks while the next column is
            // being typed - is unrecoverable for ScriptDom: no query specification comes back, so
            // there are no tables in it at all. Explicit bindings behave identically, so this is
            // not something the star binding introduced.
            //
            // Completion copes with it deliberately rather than by accident: QueryInfo keeps the
            // last SyntaxInfo that did parse ("?? SyntaxInfo") and carries on offering that while
            // the text is mid-edit. That fallback is the reason this limitation does not need
            // fixing - but it only has something to hold if the block parsed cleanly at some
            // earlier keystroke, which is why a binding that never parses (as the wildcard did not,
            // before it was taught to the renderer) breaks completion outright instead of degrading.
            var withStars = RenderedSql(Program("""
                        SELECT
                            u.*[myUsers],

                            FROM users u
                """));

            var withExplicitBindings = RenderedSql(Program("""
                        SELECT
                            u.id[myUsers.id],

                            FROM users u
                """));

            foreach (var rendered in new[] { withStars, withExplicitBindings })
            {
                var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql180Parser(initialQuotedIdentifiers: true);
                using var reader = new System.IO.StringReader(rendered);
                var fragment = parser.Parse(reader, out var errors);

                Assert.NotEmpty(errors);
                Assert.Null(Microsoft.CodeAnalysis.CSharp.SqlQueries.SqlSelectSyntaxInfo.GetInfoFromFragment(fragment));
            }
        }

        [Fact]
        public void RenderedWildcardIsAPlainStar()
        {
            var rendered = RenderedSql(Program("        SELECT u.*[myUsers] FROM users u"));

            Assert.Contains("u.*", rendered);
            Assert.DoesNotContain("myUsers", rendered);
        }

        [Fact]
        public void WildcardOverADerivedTableWorksLikeOverTheTable()
        {
            // The point of the whole source model: a derived table is asked for its own columns, so
            // this has to behave exactly as "FROM users u" does - including expanding the inner
            // star to work out what those columns are.
            AssertEx.Empty(Errors(Program(
                "        SELECT u.*[myUsers] FROM (SELECT * FROM users) u")));

            var sql = ExpandedSql(Program(
                "        SELECT u.*[myUsers] FROM (SELECT * FROM users) u"));

            Assert.Contains("u.[id] [__c0]", sql);
            Assert.Contains("u.[name] [__c1]", sql);
            Assert.Contains("u.[surname] [__c2]", sql);
            Assert.Contains("u.[role_id] [__c3]", sql);

            // The subquery itself is left exactly as written - only the outer star is expanded.
            Assert.Contains("FROM (SELECT * FROM users) u", sql);
        }

        [Fact]
        public void WildcardOverADerivedTableWithAnExplicitColumnList()
        {
            // The derived table only offers what it selects, so a target needing more than that is
            // an error rather than something that fails at the server.
            var errors = Errors(Program(
                "        SELECT u.*[myUsers] FROM (SELECT id, name FROM users) u"));

            Assert.Contains(errors, e => e.Contains("users.surname"));
        }

        [Fact]
        public void ColumnsOfADerivedTableResolveToTheMembersBehindThem()
        {
            var resolved = ResolveAll(Program(
                "        SELECT u.surname FROM (SELECT * FROM users) u"));

            // Selected straight through, so the member behind the column survives the subquery.
            Assert.Contains("surname -> users.surname", resolved);
        }

        [Fact]
        public void ColumnNotOfferedByADerivedTableIsReported()
        {
            var warnings = CreateCompilation(Program(
                "        SELECT u.surname FROM (SELECT id FROM users) u"))
                .GetDiagnostics()
                .Where(d => d.Id == "CS12110")
                .Select(d => d.GetMessage())
                .ToArray();

            Assert.Contains(warnings, w => w.Contains("surname"));
        }

        /// <summary>
        /// What a source offers as columns, as "qualifier: a, b, c".
        /// </summary>
        private static string[] SourceColumns(string sqlBody)
        {
            var compilation = CreateCompilation(Program("        " + sqlBody));
            var schema = OrmSchemaProvider.GetSchema(compilation);
            var root = SqlTableResolution.Parse(RenderedSql(Program("        " + sqlBody)));
            Assert.NotNull(root);

            return SqlSourceResolution.GetSources(root!, schema)
                .Select(s => $"{s.Qualifier}: {string.Join(", ", s.Columns.Select(c => c.Name))}")
                .ToArray();
        }

        [Fact]
        public void SubqueryOfAStarOffersTheColumnsBehindIt()
        {
            // A subquery's own select list holds no columns when it is written with a star, so
            // without resolving that star the subquery looks like it offers nothing - which is what
            // completion had to work from after typing "u.".
            AssertEx.Equal(
                new[] { "u: id, name, surname, role_id" },
                SourceColumns("SELECT u.id FROM (SELECT * FROM users) u"));
        }

        [Fact]
        public void SubqueryStarResolvesThroughSeveralLevels()
        {
            AssertEx.Equal(
                new[] { "u: id, name, surname, role_id" },
                SourceColumns("SELECT u.id FROM (SELECT * FROM (SELECT * FROM users) i) u"));
        }

        [Fact]
        public void SubqueryWithAQualifiedStarOffersThatTablesColumns()
        {
            AssertEx.Equal(
                new[] { "u: id, name, surname, role_id" },
                SourceColumns("SELECT u.id FROM (SELECT t.* FROM users t) u"));
        }

        [Fact]
        public void SubqueryExplicitColumnsAreNotWidenedByTheStarHandling()
        {
            AssertEx.Equal(
                new[] { "u: id, name" },
                SourceColumns("SELECT u.id FROM (SELECT id, name FROM users) u"));
        }

        [Fact]
        public void StarAndItsQualifierResolveToTheTable()
        {
            // The classifier, hover and go-to-definition all read these, so a star that resolves
            // to nothing is a star with no colour on it - which is what "a.*" looked like before
            // it was recorded here, while the "a.id" beside it was coloured.
            var resolved = ResolveAll(Program("        SELECT a.*[myUsers] FROM users a"));

            Assert.Contains("a -> users", resolved);
            Assert.Contains("* -> users", resolved);
        }

        [Fact]
        public void BareStarResolvesToTheOnlyTableInScope()
        {
            Assert.Contains("* -> users", ResolveAll(Program("        SELECT *[myUsers] FROM users")));
        }

        [Fact]
        public void AmbiguousBareStarResolvesToNothing()
        {
            // The star is still recorded - it is a name at a position like any other - but it must
            // not pick one of the two sources, so it carries no symbol and nothing colours it.
            var resolved = ResolveAll(Program(
                "        SELECT *[myUsers] FROM users u LEFT JOIN roles r ON r.id = u.role_id"));

            Assert.DoesNotContain(resolved, r => r.StartsWith("* ->") && !r.EndsWith("<unresolved>"));
        }

        [Fact]
        public void UnboundStarStillResolves()
        {
            // Colouring follows what the sql means, not whether a binding was attached to it.
            Assert.Contains("* -> users", ResolveAll(Program("        SELECT a.* FROM users a")));
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
