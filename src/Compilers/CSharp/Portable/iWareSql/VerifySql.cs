// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SqlVerifier;

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    internal class VerifySql
    {
        private static HttpClient? _client = null;

        private static VerifierInput VerifierInput(
            string configPath,
            string sqlString,
            ImmutableArray<SqlSegmentSyntax> segments)
        {
            /*
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
            */
            return new VerifierInput() { ConfigPath = configPath, SqlString = sqlString, Tokens = [] };
        }

        public static bool Verify(
            string projectRootDir,
            string sqlText,
            SqlStatementSyntax node,
            BindingDiagnosticBag diagnostics)
        {
            if (_client == null)
            {
                _client = new HttpClient();
                _client.BaseAddress = new System.Uri("https://localhost:7002");
                _client.DefaultRequestHeaders.Accept.Clear();
                _client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            }
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
            var reason = VerifySqlAsync(
                VerifierInput(
                    configPath,
                    sqlText,
                    segments)).Result;

            if (reason is null)
            {
                reason = VerifierOutput.Unknown();
            }
            if (reason.Success)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(reason.Message))
            {
                diagnostics.Add(ErrorCode.WRN_SQL_VerificationWarn, node.SqlKeyword, reason.Message);
            }

            foreach (var e in reason.Diagnostics)
            {
                if (e.Index < 0 || e.Index >= segments.Length)
                {
                    diagnostics.Add(ErrorCode.WRN_SQL_VerificationWarn, node.SqlTextBlock.Location, e.Text ?? "");
                    continue;
                }
                var segment = segments[e.Index];
                if (e.IsWarning)
                {
                    diagnostics.Add(ErrorCode.WRN_SQL_VerificationWarn, segment.Location, e.Text ?? "");
                }
                else if (e.IsError)
                {
                    diagnostics.Add(ErrorCode.ERR_SQL_VerificationError, segment.Location, e.Text ?? "");
                }
                else
                {
                    diagnostics.Add(ErrorCode.ERR_SQL_VerificationError, node.SqlTextBlock.Location, e.Text ?? "");
                }
            }

            return false;
        }

        private static async Task<VerifierOutput> VerifySqlAsync(VerifierInput input)
        {
            try
            {
                var jsonString = JsonSerializer.Serialize(input);
                var content = new StringContent(jsonString, Encoding.UTF8, "application/json");
                var response = await _client!.PostAsync("/SqlVerifier/Verify", content).ConfigureAwait(true);
                response.EnsureSuccessStatusCode();
                ObjectToHttpContent.HttpContentToObject(response.Content, out var result);
                if (result is not null)
                {
                    return result;
                }
            }
            catch (HttpRequestException e)
            {
            }
            catch (Exception e)
            {
                return VerifierOutput.Error(e.Message);
            }
            return VerifierOutput.Error("SQL verifier unavailable");
        }
    }
}
