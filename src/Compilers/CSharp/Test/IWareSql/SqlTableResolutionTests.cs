// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.iWareSql;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.IWareSql.UnitTests
{
    /// <summary>
    /// Tests that the names inside a sql block resolve to the right declarations - in particular
    /// that two tables sharing a column name are told apart by the alias qualifying it.
    /// </summary>
    public sealed class SqlTableResolutionTests : CSharpTestBase
    {
        /// <summary>
        /// Resolves every name in the single sql block of <paramref name="source"/>, described as
        /// "written-text -> containing-type.member" so assertions read like the sql does.
        /// </summary>
        private static ImmutableArray<string> ResolveAll(string source)
        {
            var compilation = CreateCompilation(source);
            var tree = compilation.SyntaxTrees.Single(t => t.ToString().Contains("sql"));
            var block = tree.GetRoot().DescendantNodes().OfType<SqlTextBlockSyntax>().Single();
            var text = tree.GetText();

            return SqlTableResolution.Resolve(block, compilation)
                .OrderBy(r => r.Span.Start)
                .Select(r => $"{text.ToString(r.Span)} -> {Describe(r)}")
                .ToImmutableArray();
        }

        private static string Describe(ResolvedSqlReference reference)
        {
            if (reference.Symbol is null)
            {
                return "<unresolved>";
            }

            return reference.Symbol.Kind == SymbolKind.NamedType
                ? reference.Symbol.Name
                : $"{reference.Symbol.ContainingType.Name}.{reference.Symbol.Name}";
        }

        [Fact]
        public void SharedColumnNamesResolveThroughTheirAlias()
        {
            // "id" and "name" exist on both tables, so every one of these would resolve to
            // whichever table happened to be looked at first if the alias were being ignored.
            AssertEx.Equal(
                new[]
                {
                    "u -> users",
                    "id -> users.id",
                    "u -> users",
                    "name -> users.name",
                    "r -> roles",
                    "id -> roles.id",
                    "r -> roles",
                    "name -> roles.name",
                    "users -> users",
                    "u -> users",
                    "roles -> roles",
                    "r -> roles",
                    "r -> roles",
                    "id -> roles.id",
                    "u -> users",
                    "role_id -> users.role_id",
                },
                ResolveAll(SqlTestSource.JoinProgram));
        }

        [Fact]
        public void AliasResolvesAtItsDeclarationAndAtEveryUse()
        {
            var resolved = ResolveAll(SqlTestSource.Program(
                "SELECT u2.name FROM users u2 WHERE u2.id = 1"));

            // Three occurrences of the alias: the declaration in FROM and the two qualifiers.
            Assert.Equal(3, resolved.Count(r => r == "u2 -> users"));
        }

        [Fact]
        public void UnqualifiedColumnResolvesWhenOnlyOneTableDeclaresIt()
        {
            // "surname" is on users only, so a bare reference is unambiguous even with roles in
            // scope.
            Assert.Contains("surname -> users.surname", ResolveAll(SqlTestSource.Program(
                "SELECT surname FROM users u LEFT JOIN roles r ON r.id = u.role_id")));
        }

        [Fact]
        public void UnqualifiedColumnOnTwoTablesIsLeftUnresolved()
        {
            // "name" is on both tables. Picking one would send go-to-definition to a coin flip, so
            // nothing is resolved and - because sql itself would reject this - nothing is reported.
            Assert.Contains("name -> <unresolved>", ResolveAll(SqlTestSource.Program(
                "SELECT name FROM users u LEFT JOIN roles r ON r.id = u.role_id")));
        }

        [Fact]
        public void PrivateMemberIsNotAColumn()
        {
            Assert.Contains(
                "someUnknownAndUnknowableString -> <unresolved>",
                ResolveAll(SqlTestSource.Program("SELECT u.someUnknownAndUnknowableString FROM users u")));
        }

        [Fact]
        public void SubqueryAliasShadowsTheOuterOne()
        {
            // The inner "x" is roles and the outer "x" is users. A single flat alias map would
            // collapse the two and resolve one of them wrongly.
            var resolved = ResolveAll(SqlTestSource.Program(
                "SELECT x.surname FROM users x WHERE x.role_id IN (SELECT x.id FROM roles x)"));

            Assert.Contains("surname -> users.surname", resolved);
            Assert.Contains("id -> roles.id", resolved);
        }

        [Fact]
        public void ColumnOfADerivedTableIsNotResolvedAgainstASameNamedTable()
        {
            // "users" here is the derived table's alias, not the table, so its columns must not be
            // bound to the [Orm] class of that name.
            var resolved = ResolveAll(SqlTestSource.Program(
                "SELECT users.id FROM (SELECT 1 AS id) users"));

            Assert.DoesNotContain("id -> users.id", resolved);
        }

        [Fact]
        public void MalformedSqlResolvesNothing()
        {
            // Half-typed sql must not produce names that flicker in and out as the user types.
            Assert.Empty(ResolveAll(SqlTestSource.Program("SELECT FROM WHERE u.")));
        }

        [Fact]
        public void SpansPointAtTheNameNotTheQualifier()
        {
            var source = SqlTestSource.Program("SELECT u.surname FROM users u");
            var compilation = CreateCompilation(source);
            var tree = compilation.SyntaxTrees.Single(t => t.ToString().Contains("sql"));
            var block = tree.GetRoot().DescendantNodes().OfType<SqlTextBlockSyntax>().Single();
            var text = tree.GetText();

            var column = SqlTableResolution.Resolve(block, compilation)
                .Single(r => !r.IsTable && r.Symbol?.Name == "surname");

            // The span covers exactly "surname" - not "u.surname", and not "u".
            Assert.Equal("surname", text.ToString(column.Span));
        }
    }
}
