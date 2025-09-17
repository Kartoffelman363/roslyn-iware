// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    internal class VerifySql
    {
        private class Input
        {
            public string? ConnectionString { get; set; }
            public string? SqlString { get; set; }
            public string[]? InputParameterNames { get; set; }
            public string[]? OutputParameterNames { get; set; }

            public Input(string connectionString, string sqlString, string[] inputParameterNames, string[] outputParameterNames)
            {
                ConnectionString = connectionString;
                SqlString = sqlString;
                InputParameterNames = inputParameterNames;
                OutputParameterNames = outputParameterNames;
            }
        }

        public static bool Verify(string sqlText, ImmutableArray<string> inputNames, ImmutableArray<string> outputNames, out string reason)
        {
            return Verify(sqlText, inputNames.ToArray(), outputNames.ToArray(), out reason);
        }

        public static bool Verify(string sqlText, string[] inputNames, string[] outputNames, out string reason)
        {
            reason = string.Empty;

            //TODO-aljaz read connection string from iWareDatabase.json
            var args = JsonSerializer.Serialize(
                new Input(
                    "Data Source=192.168.210.5,1437; Initial Catalog=wmsDEV; User ID=objuser; Password=iware; Connect Timeout=5; TrustServerCertificate=True; MultipleActiveResultSets=True",
                    sqlText,
                    inputNames,
                    outputNames));
            Debug.Assert(args is not null, "Could not serialize verification data");

            //TODO-aljaz add postbuild to copy Verifier to bincore
            var executableDirectoryPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Debug.Assert(executableDirectoryPath is not null);
            var sqlVerifierPath = Path.GetFullPath(Path.Combine(executableDirectoryPath, "iWareSql/Verifier/SqlVerifier.exe"));
            Debug.Assert(sqlVerifierPath is not null);
            var procInfo = new ProcessStartInfo
            {
                FileName = sqlVerifierPath,
                Arguments = $"\"{args.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            var proc = new Process { StartInfo = procInfo };
            Trace.Assert(proc.Start(), $"Could not start SqlVerifier.exe at path {sqlVerifierPath}");
            while (!proc.StandardOutput.EndOfStream)
            {
                reason += proc.StandardOutput.ReadLine();
            }
            if (reason != string.Empty && reason.Substring(0, 2) == "OK")
            {
                return true;
            }

            return false;
        }
    }
}
