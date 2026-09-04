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
    /// Tests the diagnostics a sql block produces for names with no [Orm] declaration behind them.
    /// </summary>
    public sealed class SqlDiagnosticTests : CSharpTestBase
    {
        /// <summary>
        /// The undeclared-name warnings a source produces, as "line:column: message", which is
        /// short enough to assert on directly and still pins the span.
        /// </summary>
        private static string[] UndeclaredNameWarnings(string source)
        {
            return CreateCompilation(source).GetDiagnostics()
                .Where(d => d.Id == "CS12110")
                .OrderBy(d => d.Location.SourceSpan.Start)
                .Select(d =>
                {
                    var position = d.Location.GetLineSpan().StartLinePosition;
                    return $"{position.Line + 1}:{position.Character + 1}: {d.GetMessage()}";
                })
                .ToArray();
        }

        [Fact]
        public void ValidBlockProducesNoWarnings()
        {
            Assert.Empty(UndeclaredNameWarnings(SqlTestSource.JoinProgram));
        }

        [Fact]
        public void UnknownTableIsReported()
        {
            var warnings = UndeclaredNameWarnings(SqlTestSource.Program("SELECT 1 FROM rolez"));

            Assert.Single(warnings);
            Assert.Contains("table named 'rolez'", warnings[0]);
        }

        [Fact]
        public void UnknownColumnIsReported()
        {
            var warnings = UndeclaredNameWarnings(SqlTestSource.Program("SELECT u.bogus FROM users u"));

            Assert.Single(warnings);
            Assert.Contains("column named 'bogus'", warnings[0]);
        }

        [Fact]
        public void ColumnIsCheckedAgainstItsOwnTable()
        {
            // "surname" is on users but not on roles, so of the two occurrences only the
            // r-qualified one is wrong. This is the diagnostic counterpart of the resolution
            // tests: were the alias ignored, either both would pass or both would fail.
            var source = SqlTestSource.Program(
                """
                        SELECT 1
                            FROM users u
                            LEFT JOIN roles r ON r.id = u.role_id
                            WHERE u.surname = 'a' AND r.surname = 'b'
                """);

            var compilation = CreateCompilation(source);
            var warning = Assert.Single(compilation.GetDiagnostics().Where(d => d.Id == "CS12110"));

            Assert.Contains("column named 'surname'", warning.GetMessage());

            // Which of the two it landed on: the span covers just the column, so the qualifier is
            // the two characters in front of it.
            var span = warning.Location.SourceSpan;
            var text = compilation.SyntaxTrees.Single(t => t.ToString().Contains("sql")).GetText();
            Assert.Equal("surname", text.ToString(span));
            Assert.Equal("r.", text.ToString(new Text.TextSpan(span.Start - 2, 2)));
        }

        [Fact]
        public void ColumnsOfAnUnknownTableAreNotReportedTwice()
        {
            // One warning for the table, and nothing for its columns: the table's warning already
            // explains why the columns cannot be resolved.
            var warnings = UndeclaredNameWarnings(SqlTestSource.Program(
                "SELECT z.a, z.b, z.c FROM rolez z"));

            Assert.Single(warnings);
            Assert.Contains("table named 'rolez'", warnings[0]);
        }

        [Fact]
        public void AliasIsNeverReported()
        {
            Assert.Empty(UndeclaredNameWarnings(SqlTestSource.Program(
                "SELECT u.surname FROM users u")));
        }

        [Fact]
        public void CommonTableExpressionIsNotAnUnknownTable()
        {
            Assert.Empty(UndeclaredNameWarnings(SqlTestSource.Program(
                "WITH c AS (SELECT id FROM users) SELECT c.id FROM c")));
        }

        [Fact]
        public void TempTableIsNotAnUnknownTable()
        {
            Assert.Empty(UndeclaredNameWarnings(SqlTestSource.Program(
                "SELECT t.x FROM #tmp t")));
        }

        [Fact]
        public void MalformedSqlReportsSyntaxOnlyAndNoUndeclaredNames()
        {
            var compilation = CreateCompilation(SqlTestSource.Program("SELECT FROM WHERE u."));
            var diagnostics = compilation.GetDiagnostics();

            // The parse error is reported once, and no name warnings are invented from the wreckage.
            Assert.Contains(diagnostics, d => d.Id == "CS11111");
            Assert.DoesNotContain(diagnostics, d => d.Id == "CS12110");
        }

        [Fact]
        public void AddingAnOrmClassClearsTheWarning()
        {
            // Guards the verification cache: it is keyed on the sql text, so a schema change must
            // still be picked up rather than serving the previous compilation's answer.
            var sql = "SELECT o.id FROM orders o";

            Assert.Single(UndeclaredNameWarnings(SqlTestSource.Program(sql)));

            var withOrders = SqlTestSource.Program(sql) + """

                [Orm]
                public class orders
                {
                    public int id { get; set; }
                }
                """;

            Assert.Empty(UndeclaredNameWarnings(withOrders));
        }
    }
}
