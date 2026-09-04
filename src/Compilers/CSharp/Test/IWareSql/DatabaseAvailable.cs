// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Roslyn.Test.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.IWareSql.UnitTests
{
    /// <summary>
    /// Skips a test when there is no database to run it against.
    /// </summary>
    /// <remarks>
    /// The execution tests run a real query, so they need both iWareDatabase.json beside the test
    /// assembly and a server that answers on the connection string inside it. Neither is available
    /// on a machine that has only checked the repo out, so rather than failing there they are
    /// skipped with a reason saying which half is missing.
    ///
    /// The probe runs once per test run and its result is reused: opening a connection costs a
    /// round trip, and the answer cannot change part-way through a run in any way worth tracking.
    /// </remarks>
    public sealed class DatabaseAvailable : ExecutionCondition
    {
        private const string ConfigFileName = "iWareDatabase.json";
        private const int ProbeTimeoutSeconds = 5;

        private static readonly Lazy<string?> s_unavailableReason = new(Probe);

        public override bool ShouldSkip => s_unavailableReason.Value is not null;

        public override string SkipReason => s_unavailableReason.Value ?? "";

        /// <summary>
        /// The directory the emitted test assembly is loaded from, which is also where the
        /// iWare runtime looks for its configuration - the same relationship the file has to
        /// example_simple's executable.
        /// </summary>
        public static string ConfigDirectory =>
            Path.GetDirectoryName(typeof(DatabaseAvailable).GetTypeInfo().Assembly.Location)!;

        /// <returns>null when the database can be reached, otherwise why it cannot.</returns>
        private static string? Probe()
        {
            var configPath = Path.Combine(ConfigDirectory, ConfigFileName);
            if (!File.Exists(configPath))
            {
                return $"{ConfigFileName} is not next to the test assembly ({configPath})";
            }

            string? connectionString;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                connectionString = document.RootElement.TryGetProperty("ConnectionString", out var value)
                    ? value.GetString()
                    : null;
            }
            catch (Exception e)
            {
                return $"{ConfigFileName} could not be read: {e.Message}";
            }

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return $"{ConfigFileName} has no ConnectionString";
            }

            try
            {
                // Override whatever timeout the file asks for: this is a liveness probe, and a
                // long one would stall the whole run on a machine with no server at all.
                var builder = new SqlConnectionStringBuilder(connectionString)
                {
                    ConnectTimeout = ProbeTimeoutSeconds
                };

                using var connection = new SqlConnection(builder.ConnectionString);
                connection.Open();
                return null;
            }
            catch (Exception e)
            {
                return $"the database in {ConfigFileName} is not reachable: {e.Message}";
            }
        }
    }
}
