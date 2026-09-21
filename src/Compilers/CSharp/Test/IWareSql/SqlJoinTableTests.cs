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
    /// Tests the star select into an <c>OrmJoinTable</c>, which reads one generated entity per
    /// table of the query and leaves a table that matched nothing null.
    /// </summary>
    public sealed class SqlJoinTableTests : CSharpTestBase
    {
        private const string Entities = """
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
                public int id { get => _values.id; set => _values.id = value; }
                public string name { get => _values.name; set => _values.name = value; }
                public string surname { get => _values.surname; set => _values.surname = value; }
                public int? role_id { get => _values.role_id; set => _values.role_id = value; }

                public class users_Builder
                {
                    private readonly users_Values _builderValues = new();
                    private long _builderRowVersion = 0;

                    public users_Builder Addid(int value) { _builderValues.id = value; return this; }
                    public users_Builder Addname(string value) { _builderValues.name = value; return this; }
                    public users_Builder Addsurname(string value) { _builderValues.surname = value; return this; }
                    public users_Builder Addrole_id(int? value) { _builderValues.role_id = value; return this; }
                    public users_Builder AddRowVersion(long value) { _builderRowVersion = value; return this; }

                    public users Build() => new(_builderValues, _builderRowVersion);
                }

                protected override users CreateFrom(users_Values values, long rowVersion) => new(values, rowVersion);

                [DbField(Ignore = true)]
                public override string __tableName => "users";
                [DbField(Ignore = true)]
                public override bool __nonTenantTable => false;
                [DbField(Ignore = true)]
                public override OrmColumnRef<users_Values>[] __keys => System.Array.Empty<OrmColumnRef<users_Values>>();
                protected override OrmColumnRef<users_Values>[] _colRefs => System.Array.Empty<OrmColumnRef<users_Values>>();
                protected override users_Values _values { get; set; }
                [DbField(Ignore = true)]
                public override users_Values __oldValues { get; protected set; }

                public override users Copy()
                {
                    var copy = new users();
                    copy._values = _values.Copy();
                    return copy;
                }

                public users() { _values = new(); __oldValues = new(); }

                public users(users_Values values, long rowVersion)
                    : base(rowVersion)
                {
                    _values = values;
                    __oldValues = values.Copy();
                }
            }

            public class roles_Values : IOrmValues<roles_Values>
            {
                public int id { get; set; }
                public string name { get; set; }

                public roles_Values Copy() => new() { id = id, name = name };
            }

            [Orm]
            public class roles : OrmTable<roles, roles_Values>
            {
                public int id { get => _values.id; set => _values.id = value; }
                public string name { get => _values.name; set => _values.name = value; }

                public class roles_Builder
                {
                    private readonly roles_Values _builderValues = new();
                    private long _builderRowVersion = 0;

                    public roles_Builder Addid(int value) { _builderValues.id = value; return this; }
                    public roles_Builder Addname(string value) { _builderValues.name = value; return this; }
                    public roles_Builder AddRowVersion(long value) { _builderRowVersion = value; return this; }

                    public roles Build() => new(_builderValues, _builderRowVersion);
                }

                protected override roles CreateFrom(roles_Values values, long rowVersion) => new(values, rowVersion);

                [DbField(Ignore = true)]
                public override string __tableName => "roles";
                [DbField(Ignore = true)]
                public override bool __nonTenantTable => false;
                [DbField(Ignore = true)]
                public override OrmColumnRef<roles_Values>[] __keys => System.Array.Empty<OrmColumnRef<roles_Values>>();
                protected override OrmColumnRef<roles_Values>[] _colRefs => System.Array.Empty<OrmColumnRef<roles_Values>>();
                protected override roles_Values _values { get; set; }
                [DbField(Ignore = true)]
                public override roles_Values __oldValues { get; protected set; }

                public override roles Copy()
                {
                    var copy = new roles();
                    copy._values = _values.Copy();
                    return copy;
                }

                public roles() { _values = new(); __oldValues = new(); }

                public roles(roles_Values values, long rowVersion)
                    : base(rowVersion)
                {
                    _values = values;
                    __oldValues = values.Copy();
                }
            }

            #nullable enable

            public abstract class OrmJoinTable
            {
            }

            public class UsersWithRoles : OrmJoinTable
            {
                public users users { get; set; } = null!;
                public roles? roles { get; set; }
            }

            public class UsersWithTwoRoles : OrmJoinTable
            {
                public users users { get; set; } = null!;
                public roles? r1 { get; set; }
                public roles? r2 { get; set; }
            }

            public class RolesNamedAfterUsers : OrmJoinTable
            {
                public roles? u { get; set; }
            }

            public class NoDefaultConstructor : OrmJoinTable
            {
                public NoDefaultConstructor(int unused) { }

                public users users { get; set; } = null!;
            }

            public class NoEntities : OrmJoinTable
            {
                public string label { get; set; } = "";
            }
            """;

        private static string Program(string body) => Entities + $$"""

            public class P
            {
                public static void Main()
                {
            {{body}}
                }
            }
            """;

        private static CSharpCompilation Compile(string source, string assemblyName = "SqlJoinTableTest") =>
            CSharpCompilation.Create(
                assemblyName: assemblyName,
                syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
                references: SqlTestReferences.Runtime,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));

        private static string[] Errors(string source) =>
            Compile(source).GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Id + ": " + d.GetMessage())
                .ToArray();

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

        private static string Run(string source)
        {
            var compilation = Compile(source, "SqlJoinTableExec_" + Guid.NewGuid().ToString("N"));
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

            return captured.ToString();
        }

        [Fact]
        public void JoinExpandsEveryTableWithItsRowVersion()
        {
            var sql = ExpandedSql(Program("""
                        UsersWithRoles row;
                        sql
                        {
                        SELECT *[row] FROM users u LEFT JOIN roles r ON r.id = u.role_id
                        }
                        sqldo
                        {
                            System.Console.WriteLine(row.users.name);
                        }
                """));

            Assert.Contains("[u].[id]", sql, StringComparison.Ordinal);
            Assert.Contains("[u].[role_id]", sql, StringComparison.Ordinal);
            Assert.Contains("[u].[__rowversion]", sql, StringComparison.Ordinal);
            Assert.Contains("[r].[name]", sql, StringComparison.Ordinal);
            Assert.Contains("[r].[__rowversion]", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("*", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void JoinWithoutAliasesQualifiesByTableName()
        {
            var sql = ExpandedSql(Program("""
                        UsersWithRoles row;
                        sql
                        {
                        SELECT *[row] FROM users LEFT JOIN roles ON roles.id = users.role_id
                        }
                        sqldo
                        {
                            System.Console.WriteLine(row.users.name);
                        }
                """));

            Assert.Contains("[users].[id]", sql, StringComparison.Ordinal);
            Assert.Contains("[roles].[__rowversion]", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void JoinIsADefiniteAssignment()
        {
            var errors = Errors(Program("""
                        UsersWithRoles row;
                        sql
                        {
                        SELECT *[row] FROM users u LEFT JOIN roles r ON r.id = u.role_id
                        }
                        sqldo
                        {
                            System.Console.WriteLine(row.users.__rowVersion);
                        }
                """));

            Assert.Empty(errors);
        }

        [Fact]
        public void SameTableTwiceMatchesPropertiesByAlias()
        {
            var sql = ExpandedSql(Program("""
                        UsersWithTwoRoles row;
                        sql
                        {
                        SELECT *[row] FROM users u
                            LEFT JOIN roles r1 ON r1.id = u.role_id
                            LEFT JOIN roles r2 ON r2.id = 1
                        }
                        sqldo
                        {
                            System.Console.WriteLine(row.users.name);
                        }
                """));

            Assert.Contains("[r1].[id]", sql, StringComparison.Ordinal);
            Assert.Contains("[r1].[__rowversion]", sql, StringComparison.Ordinal);
            Assert.Contains("[r2].[id]", sql, StringComparison.Ordinal);
            Assert.Contains("[r2].[__rowversion]", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void SameTableTwiceWithoutAMatchingAliasIsAnError()
        {
            var errors = Errors(Program("""
                        UsersWithRoles row;
                        sql
                        {
                        SELECT *[row] FROM users u
                            LEFT JOIN roles r1 ON r1.id = u.role_id
                            LEFT JOIN roles r2 ON r2.id = 1
                        }
                        sqldo
                        {
                        }
                """));

            Assert.Contains(errors, e => e.Contains("'roles' appears more than once in the query (r1, r2)", StringComparison.Ordinal));
        }

        [Fact]
        public void QualifiedStarIsAnError()
        {
            var errors = Errors(Program("""
                        UsersWithRoles row;
                        sql
                        {
                        SELECT u.*[row] FROM users u LEFT JOIN roles r ON r.id = u.role_id
                        }
                        sqldo
                        {
                        }
                """));

            Assert.Contains(errors, e => e.Contains("cannot be qualified with 'u'", StringComparison.Ordinal));
        }

        [Fact]
        public void MissingTableIsAnError()
        {
            var errors = Errors(Program("""
                        UsersWithRoles row;
                        sql
                        {
                        SELECT *[row] FROM users u
                        }
                        sqldo
                        {
                        }
                """));

            Assert.Contains(errors, e => e.Contains("'UsersWithRoles.roles' is a 'roles', but no table of that type appears in the query", StringComparison.Ordinal));
        }

        [Fact]
        public void PropertyNamedAfterAnAliasOfAnotherTableIsAnError()
        {
            var errors = Errors(Program("""
                        RolesNamedAfterUsers row;
                        sql
                        {
                        SELECT *[row] FROM users u LEFT JOIN roles r ON r.id = u.role_id
                        }
                        sqldo
                        {
                        }
                """));

            Assert.Contains(errors, e => e.Contains("'RolesNamedAfterUsers.u' is a 'roles', but 'u' reads from 'users'", StringComparison.Ordinal));
        }

        [Fact]
        public void JoinWithoutAParameterlessConstructorIsAnError()
        {
            var errors = Errors(Program("""
                        NoDefaultConstructor row;
                        sql
                        {
                        SELECT *[row] FROM users u
                        }
                        sqldo
                        {
                        }
                """));

            Assert.Contains(errors, e => e.Contains("'NoDefaultConstructor' needs an accessible parameterless constructor", StringComparison.Ordinal));
        }

        [Fact]
        public void JoinWithoutEntityPropertiesIsAnError()
        {
            var errors = Errors(Program("""
                        NoEntities row;
                        sql
                        {
                        SELECT *[row] FROM users u
                        }
                        sqldo
                        {
                        }
                """));

            Assert.Contains(errors, e => e.Contains("'NoEntities' declares no public properties of a generated [Orm] entity type", StringComparison.Ordinal));
        }

        [ConditionalFact(typeof(DatabaseAvailable))]
        public void UnmatchedTablesAreNull()
        {
            var output = Run(Program("""
                        UsersWithTwoRoles row;
                        int? firstRoleId;
                        int? secondRoleId;
                        sql
                        {
                        SELECT *[row], r1.id[firstRoleId], r2.id[secondRoleId] FROM users u
                            LEFT JOIN roles r1 ON r1.id = u.role_id
                            LEFT JOIN roles r2 ON r2.id = 1
                        }
                        sqldo
                        {
                            System.Console.WriteLine(
                                $"{row.users.id >= 0}|{row.users.__rowVersion != 0}|" +
                                $"{row.r1 is null}|{firstRoleId is null}|{row.r1?.id}|{firstRoleId}|" +
                                $"{row.r2 is null}|{secondRoleId is null}|{row.r2?.id}|{secondRoleId}");
                        }
                """));

            var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(lines);

            foreach (var line in lines)
            {
                var parts = line.Split('|');
                Assert.Equal("True", parts[0]);
                Assert.Equal("True", parts[1]);
                Assert.Equal(parts[3], parts[2]);
                Assert.Equal(parts[5], parts[4]);
                Assert.Equal(parts[7], parts[6]);
                Assert.Equal(parts[9], parts[8]);
            }
        }
    }
}
