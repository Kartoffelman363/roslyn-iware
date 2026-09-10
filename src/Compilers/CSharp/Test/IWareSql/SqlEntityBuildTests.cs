// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.IWareSql.UnitTests
{
    /// <summary>
    /// Tests the star select into an <c>[Orm]</c> class whose table members were generated, which
    /// is constructed through its builder rather than filled in member by member.
    /// </summary>
    public sealed class SqlEntityBuildTests : CSharpTestBase
    {
        /// <summary>
        /// An entity in the shape iWare.Database.Generators emits, cut down to what the compiler
        /// actually looks for: the OrmTable base and a nested builder with one AddX per column,
        /// AddRowVersion and Build.
        /// </summary>
        /// <remarks>
        /// Written out by hand rather than generated so the test exercises the compiler alone. The
        /// columns are plain auto-properties, since what makes this an entity build is the base
        /// type and the builder, not how the columns are backed.
        /// </remarks>
        private const string Entity = """
            #pragma warning disable CS8981 // type name only contains lower-cased ascii characters
            using iWare.Database;

            public class users_Values : IOrmValues<users_Values>
            {
                public int id { get; set; }
                public string name { get; set; }
                public string surname { get; set; }
                public int? role_id { get; set; }

                public users_Values Copy() => new()
                {
                    id = id, name = name, surname = surname, role_id = role_id,
                };
            }

            [Orm]
            public class users : OrmTable<users, users_Values>
            {
                public int id { get; set; }
                public string name { get; set; }
                public string surname { get; set; }
                public int? role_id { get; set; }

                public class users_Builder
                {
                    private readonly users _entity = new();

                    public users_Builder Addid(int value) { _entity.id = value; return this; }
                    public users_Builder Addname(string value) { _entity.name = value; return this; }
                    public users_Builder Addsurname(string value) { _entity.surname = value; return this; }
                    public users_Builder Addrole_id(int? value) { _entity.role_id = value; return this; }
                    public users_Builder AddRowVersion(long value) { _entity.SetRowVersion(value); return this; }

                    public users Build() => _entity;
                }

                internal void SetRowVersion(long value) => __rowVersion = value;

                protected override users CreateFrom(users_Values values, long rowVersion)
                {
                    var e = new users
                    {
                        id = values.id, name = values.name,
                        surname = values.surname, role_id = values.role_id,
                    };
                    e.SetRowVersion(rowVersion);
                    return e;
                }

                protected override string _tableName => "users";
                protected override OrmColumnRef<users_Values>[] _keys => System.Array.Empty<OrmColumnRef<users_Values>>();
                protected override OrmColumnRef<users_Values>[] _colRefs => System.Array.Empty<OrmColumnRef<users_Values>>();
                protected override users_Values _values { get; } = new();
                protected override users_Values _oldValues { get; } = new();
            }
            """;

        private static string Program(string body) => Entity + $$"""

            public class P
            {
                public static void Main()
                {
            {{body}}
                }
            }
            """;

        private static CSharpCompilation Compile(string source) =>
            CSharpCompilation.Create(
                assemblyName: "SqlEntityBuildTest",
                syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
                references: SqlTestReferences.Runtime,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        private static string[] Errors(string source) =>
            Compile(source).GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Id + ": " + d.GetMessage())
                .ToArray();

        /// <summary>The sql text actually sent, read back out of the emitted assembly.</summary>
        private static string ExpandedSql(string source)
        {
            var compilation = Compile(source);

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
        public void StarSelectIntoEntityIsADefiniteAssignment()
        {
            // usr is only declared. Filling it in member by member would need it to exist first;
            // constructing it through the builder is what makes this an assignment.
            var errors = Errors(Program("""
                        users usr;
                        sql
                        {
                        SELECT *[usr] FROM users
                        }
                        sqldo
                        {
                            System.Console.WriteLine(usr.__rowVersion);
                        }
                """));

            Assert.Empty(errors);
        }

        [Fact]
        public void EntityIsStillUnassignedAfterTheStatement()
        {
            // A query that returns no rows never runs the do-block and never assigns, so the
            // variable cannot be treated as assigned once the statement is over.
            var errors = Errors(Program("""
                        users usr;
                        sql
                        {
                        SELECT *[usr] FROM users
                        }
                        System.Console.WriteLine(usr.__rowVersion);
                """));

            Assert.Contains(errors, e => e.StartsWith("CS0165", StringComparison.Ordinal));
        }

        [Fact]
        public void ExpansionSelectsEveryColumnAndTheRowVersion()
        {
            var sql = ExpandedSql(Program("""
                        users usr;
                        sql
                        {
                        SELECT *[usr] FROM users
                        }
                        sqldo
                        {
                            System.Console.WriteLine(usr.name);
                        }
                """));

            Assert.Contains("[id]", sql, StringComparison.Ordinal);
            Assert.Contains("[name]", sql, StringComparison.Ordinal);
            Assert.Contains("[surname]", sql, StringComparison.Ordinal);
            Assert.Contains("[role_id]", sql, StringComparison.Ordinal);

            // The row version has no column on the entity - it lives outside the row's values -
            // so the expansion is the only thing that can bring it back.
            Assert.Contains("[__rowversion]", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void QualifiedStarSelectsThroughTheAlias()
        {
            var sql = ExpandedSql(Program("""
                        users usr;
                        sql
                        {
                        SELECT u.*[usr] FROM users u
                        }
                        sqldo
                        {
                            System.Console.WriteLine(usr.name);
                        }
                """));

            Assert.Contains("u.[id]", sql, StringComparison.Ordinal);
            Assert.Contains("u.[__rowversion]", sql, StringComparison.Ordinal);
        }

        [ConditionalFact(typeof(DatabaseAvailable))]
        public void RowVersionIsFilledFromTheRow()
        {
            var source = Program("""
                        users usr;
                        sql
                        {
                        SELECT *[usr] FROM users
                        }
                        sqldo
                        {
                            System.Console.WriteLine($"{usr.name}|{usr.__rowVersion != 0}");
                        }
                """);

            var compilation = CSharpCompilation.Create(
                assemblyName: "SqlEntityBuildExec_" + Guid.NewGuid().ToString("N"),
                syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
                references: SqlTestReferences.Runtime,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));

            compilation.VerifyDiagnostics();

            using var peStream = new MemoryStream();
            var emitResult = compilation.Emit(peStream);
            Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));

            var assembly = Assembly.Load(peStream.ToArray());
            var main = assembly.GetType("P")!.GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!;

            var originalOut = Console.Out;
            using var captured = new StringWriter();
            Console.SetOut(captured);
            try
            {
                main.Invoke(null, null);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw; // unreachable
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var output = captured.ToString();
            Assert.NotEqual(string.Empty, output.Trim());

            // Every row must carry a version: a rowversion column is never null and never zero.
            foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.EndsWith("|True", line, StringComparison.Ordinal);
            }
        }
    }
}
