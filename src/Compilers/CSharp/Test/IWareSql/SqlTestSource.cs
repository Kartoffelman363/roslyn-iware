// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.CSharp.IWareSql.UnitTests
{
    /// <summary>
    /// The source these tests compile, kept in one place so the compile-time and execution tests
    /// are demonstrably talking about the same program.
    /// </summary>
    /// <remarks>
    /// This mirrors the example_simple project: the same two tables, the same join, and the same
    /// shape of output bindings. It is written against plain int/string columns rather than the
    /// generated Domain types that example_simple uses, because those come from the
    /// iWare.Domain.Generators source generator - running a generator inside the test compilation
    /// would test the generator rather than the sql feature, and nothing here depends on the
    /// column types being domains.
    ///
    /// The [Orm] and [DbField] attributes are not declared anywhere: the compiler synthesizes them
    /// into every compilation (see OrmAttributesSource), which is exactly what makes them
    /// available to example_simple without a using directive either.
    /// </remarks>
    internal static class SqlTestSource
    {
        /// <summary>The two tables, matching example_simple's orm/Tables.cs.</summary>
        /// <remarks>
        /// The pragma is example_simple's too: table classes are named for the database, so they
        /// are all-lowercase and every one of them trips CS8981. Suppressing it here keeps the
        /// tests able to assert that a valid block compiles with no diagnostics at all.
        /// </remarks>
        public const string Tables = """
            #pragma warning disable CS8981 // type name only contains lower-cased ascii characters

            [Orm]
            public class users
            {
                public int id { get; set; }
                public string name { get; set; }
                public string surname { get; set; }
                public int role_id { get; set; }

                // Deliberately private: the schema walk only takes public members, so this must
                // not be usable as a column.
                private string someUnknownAndUnknowableString { get; set; }
            }

            [Orm]
            public class roles
            {
                public int id { get; set; }
                public string name { get; set; }
            }
            """;

        /// <summary>
        /// A program whose sql block joins the two tables, with "id" and "name" appearing on both
        /// so that alias resolution has something to get wrong.
        /// </summary>
        public const string JoinProgram = Tables + """

            public class UserWithRole
            {
                public users user = new();
                public roles role = new();
            }

            public class P
            {
                public static void Main()
                {
                    System.Collections.Generic.List<UserWithRole> rows = new();
                    UserWithRole row = new();

                    sql
                    {
                    SELECT u.id[row.user.id], u.name[row.user.name], r.id[row.role.id], r.name[row.role.name]
                        FROM users u
                        LEFT JOIN roles r ON r.id = u.role_id
                    }
                    sqldo
                    {
                        rows.Add(row);
                        row = new();
                    }
                    sqlempty
                    {
                        System.Console.WriteLine("No result");
                    }

                    foreach (var r in rows)
                    {
                        System.Console.WriteLine($"{r.user.name} {r.user.surname} ({r.user.id}): {r.role.name} ({r.role.id})");
                    }
                }
            }
            """;

        /// <summary>
        /// <see cref="JoinProgram"/> with an inner join, for the tests that actually run it.
        /// </summary>
        /// <remarks>
        /// example_simple's left join is the right thing to mirror when only compiling, and it is
        /// what <see cref="JoinProgram"/> keeps. Executing it is a different matter: a left join
        /// with no matching role yields NULL for r.id, and the int column standing in for
        /// example_simple's RoleIdDomain here has no way to represent that. The domain types model
        /// null and plain int does not, so the executing tests join in a way that cannot produce
        /// one rather than pulling the generator into the test compilation.
        /// </remarks>
        public static string InnerJoinProgram => JoinProgram.Replace("LEFT JOIN", "INNER JOIN");

        /// <summary>
        /// Wraps a sql block in the minimum program around it, so a test can state just the sql.
        /// </summary>
        public static string Program(string sqlBody) => Tables + $$"""

            public class P
            {
                public static void Main()
                {
                    users u = new();
                    roles r = new();

                    sql
                    {
            {{sqlBody}}
                    }
                }
            }
            """;
    }
}
