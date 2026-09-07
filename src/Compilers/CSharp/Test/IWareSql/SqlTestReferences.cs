// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace Microsoft.CodeAnalysis.CSharp.IWareSql.UnitTests
{
    /// <summary>
    /// The references a compilation containing a sql block needs in order to be emitted.
    /// </summary>
    /// <remarks>
    /// Lowering a sql block resolves types well beyond the ones the source mentions:
    /// iWare.Database.SqlCommands for the read loop's calls, Microsoft.Data.SqlClient.SqlDataReader
    /// to shape it, and ImmutableArray&lt;T&gt; to carry the column names. A curated list would have
    /// to be kept in step with whatever LocalRewriter_SqlStatement looks up next, so this takes
    /// everything the test process itself can load - which has the further benefit that an assembly
    /// emitted against it can be loaded straight back into this process and run.
    /// </remarks>
    internal static class SqlTestReferences
    {
        private static ImmutableArray<MetadataReference> s_runtime;

        public static ImmutableArray<MetadataReference> Runtime
        {
            get
            {
                if (s_runtime.IsDefault)
                {
                    s_runtime = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                        .Split(Path.PathSeparator)
                        .Where(static path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
                        .ToImmutableArray();
                }

                return s_runtime;
            }
        }
    }
}
