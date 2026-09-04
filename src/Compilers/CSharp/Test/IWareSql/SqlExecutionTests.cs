// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.IWareSql.UnitTests
{
    /// <summary>
    /// Runs the code a sql block lowers to, against a real database.
    /// </summary>
    /// <remarks>
    /// These are skipped unless a database is reachable - see <see cref="DatabaseAvailable"/>.
    /// Rather than going through CompileAndVerify, which runs the emitted assembly out of a
    /// temporary directory, the assembly is loaded into the test process: the iWare runtime reads
    /// its connection string from beside the executing assembly, and beside the test assembly is
    /// where the build has put iWareDatabase.json.
    /// </remarks>
    public sealed class SqlExecutionTests : CSharpTestBase
    {
        /// <summary>
        /// Compiles <paramref name="source"/>, loads it and runs P.Main, returning what it wrote
        /// to the console.
        /// </summary>
        private static string RunProgram(string source)
        {
            var compilation = CSharpCompilation.Create(
                assemblyName: "SqlExecutionTest_" + Guid.NewGuid().ToString("N"),
                syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
                references: RuntimeReferences,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug));

            compilation.VerifyDiagnostics();

            using var peStream = new MemoryStream();
            var emitResult = compilation.Emit(peStream);
            Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));

            var assembly = Assembly.Load(peStream.ToArray());
            var main = assembly.GetType("P")?.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(main);

            var originalOut = Console.Out;
            using var captured = new StringWriter();
            Console.SetOut(captured);
            try
            {
                main!.Invoke(null, null);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                // Unwrap so a failure in the query shows the database's own message rather than
                // "Exception has been thrown by the target of an invocation", and rethrow through
                // ExceptionDispatchInfo so the stack still points into the generated code.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw; // unreachable
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            return captured.ToString();
        }

        /// <summary>
        /// Everything this process can load, used as the compilation's references.
        /// </summary>
        /// <remarks>
        /// The emitted assembly is loaded into this same process, so compiling it against exactly
        /// what the runtime will give it is both the simplest way to be sure the two agree and the
        /// only way to get the whole set it needs. That set is wider than it looks: as well as
        /// iWare.Database and iWare.Domain.Abstractions, lowering resolves
        /// Microsoft.Data.SqlClient.SqlDataReader to shape the read loop and ImmutableArray&lt;T&gt;
        /// to carry the column names, so a curated reference set has to be kept in step with
        /// whatever LocalRewriter_SqlStatement happens to look up.
        /// </remarks>
        private static IEnumerable<MetadataReference> RuntimeReferences =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Where(static path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path));

        [ConditionalFact(typeof(DatabaseAvailable))]
        public void JoinQueryReturnsRows()
        {
            var output = RunProgram(SqlTestSource.InnerJoinProgram);

            // The fixture's contents are the database's business, not this repo's, so this asserts
            // the shape of the run rather than particular rows: the block executed, took the
            // non-empty path, and produced at least one formatted row.
            Assert.DoesNotContain("No result", output);
            Assert.NotEmpty(output.Trim());
        }

        [ConditionalFact(typeof(DatabaseAvailable))]
        public void SqlEmptyClauseRunsWhenNothingMatches()
        {
            var source = SqlTestSource.Tables + """

                public class P
                {
                    public static void Main()
                    {
                        users row = new();

                        sql
                        {
                        SELECT u.id[row.id] FROM users u WHERE 1 = 0
                        }
                        sqldo
                        {
                            System.Console.WriteLine("row");
                        }
                        sqlempty
                        {
                            System.Console.WriteLine("empty");
                        }
                        sqlend
                        {
                            System.Console.WriteLine("end");
                        }
                    }
                }
                """;

            var output = RunProgram(source);

            Assert.Contains("empty", output);
            Assert.Contains("end", output);
            Assert.DoesNotContain("row", output);
        }

        [ConditionalFact(typeof(DatabaseAvailable))]
        public void InputBindingIsPassedAsAParameter()
        {
            var source = SqlTestSource.Tables + """

                public class P
                {
                    public static void Main()
                    {
                        users row = new();
                        int wanted = -1;

                        sql
                        {
                        SELECT u.id[row.id] FROM users u WHERE u.id = @wanted
                        }
                        sqlempty
                        {
                            System.Console.WriteLine("none");
                        }
                    }
                }
                """;

            // No user has id -1, so the parameter must actually reach the server for this to take
            // the empty path rather than returning every row.
            Assert.Contains("none", RunProgram(source));
        }
    }
}
