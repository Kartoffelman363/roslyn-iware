// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SqlVerifier;

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    internal class VerifySql
    {
        private static VerifierInput VerifierInput(
            string configPath,
            string sqlString,
            ImmutableArray<SqlSegmentSyntax> segments)
        {
            int index = 0;
            var tokens = segments.Select(seg =>
                seg switch
                {
                    SqlInputIdentifierSegmentSyntax inSeg => new VerifierToken(
                        inSeg.SqlIdentifierToken.ToFullString().Trim(),
                        TokenType.Output,
                        index++),
                    SqlOutputIdentifierSegmentSyntax outSeg => new VerifierToken(
                        outSeg.SqlIdentifierToken.ToFullString().Trim(),
                        TokenType.Input,
                        index++),
                    SqlTextSegmentSyntax textSeg => new VerifierToken(
                        textSeg.SqlTextToken.ToFullString().Trim(),
                        TokenType.Text,
                        index++),
                    _ => VerifierToken.UnreachableVerifierToken()
                }).ToArray();
            return new VerifierInput() { ConfigPath = configPath, SqlString = sqlString, Tokens = tokens };
        }

        public static bool Verify(
            string projectRootDir,
            string sqlText,
            SqlStatementSyntax node,
            BindingDiagnosticBag diagnostics)
        {
            var configPath = Path.Combine(projectRootDir, "iWareDatabase.json");

            if (!File.Exists(configPath))
            {
                diagnostics.Add(
                    ErrorCode.WRN_SQL_VerificationWarn,
                    node.SqlKeyword,
                    $"Missing iWareDatabase.json file at {configPath}");
                return false;
            }

            var segments = node.SqlTextBlock.Segments.ToImmutableArray();
            var args = JsonSerializer.Serialize(
                VerifierInput(
                    configPath,
                    sqlText,
                    segments));
            Debug.Assert(args is not null, "Could not serialize verification data");

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
            //Trace.Assert(proc.Start(), $"Could not start SqlVerifier.exe at path {sqlVerifierPath}");
            if (!proc.Start())
            {
                diagnostics.Add(ErrorCode.ERR_SQL_VerifierMissingError, node.Location);
                return false;
            }
            var reasonString = "";
            while (!proc.StandardOutput.EndOfStream)
            {
                reasonString += proc.StandardOutput.ReadLine();
            }
            if (reasonString != string.Empty && reasonString.Substring(0, 2) == "OK")
            {
                return true;
            }

            VerifierOutput? reason;
            try
            {
                reason = JsonSerializer.Deserialize<VerifierOutput>(reasonString);
            }
            catch
            {
                reason = null;
            }
            if (reason is null)
            {
                diagnostics.Add(ErrorCode.ERR_SQL_VerificationError, node.SqlTextBlock.Location, reasonString);
                return false;
            }

            foreach (var e in reason.Diagnostics)
            {
                if (e.Index < 0 || e.Index >= segments.Length)
                {
                    diagnostics.Add(ErrorCode.WRN_SQL_VerificationWarn, node.SqlTextBlock.Location, e.Text);
                    continue;
                }
                var segment = segments[e.Index];
                if (e.IsWarning)
                {
                    diagnostics.Add(ErrorCode.WRN_SQL_VerificationWarn, segment.Location, e.Text);
                }
                else if (e.IsError)
                {
                    diagnostics.Add(ErrorCode.ERR_SQL_VerificationError, segment.Location, e.Text);
                }
                else
                {
                    diagnostics.Add(ErrorCode.ERR_SQL_VerificationError, node.SqlTextBlock.Location, e.Text);
                }
            }

            return false;
        }
    }
}
