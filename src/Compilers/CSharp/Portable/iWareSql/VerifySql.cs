// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Net.Http;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.SqlClient;
using SqlVerifier;
using static Microsoft.CodeAnalysis.CSharp.iWareSql.DbConnection;

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    internal class VerifySql
    {
        //private static HttpClient? _client = null;

        /*
        private static VerifierInput VerifierInput(
            string configPath,
            string sqlString,
            SqlStatementSyntax node)
        {
            //int index = 0;
            //var tokens = segments.Select(seg =>
            //    seg switch
            //    {
            //        SqlInputIdentifierSegmentSyntax inSeg => new VerifierToken(
            //            inSeg.SqlIdentifierToken.ToFullString().Trim(),
            //            TokenType.Output,
            //            index++),
            //        SqlOutputIdentifierSegmentSyntax outSeg => new VerifierToken(
            //            outSeg.SqlIdentifierToken.ToFullString().Trim(),
            //            TokenType.Input,
            //            index++),
            //        SqlTextSegmentSyntax textSeg => new VerifierToken(
            //            textSeg.SqlTextToken.ToFullString().Trim(),
            //            TokenType.Text,
            //            index++),
            //        _ => VerifierToken.UnreachableVerifierToken()
            //    }).ToArray();
            
            return new VerifierInput() { ConfigPath = configPath, SqlString = sqlString, Tokens = [] };
        }
        */

        public static bool Verify2(
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
                    node,
                    $"Missing iWareDatabase.json file at {configPath}");
                return false;
            }

            SetSettings(configPath);
            SqlConnection conn;
            try
            {
                conn = Conn();
            }
            catch
            {
                diagnostics.Add(
                    ErrorCode.WRN_SQL_VerificationWarn,
                    node,
                    $"Could not connect to database with ConnectionString listed in {configPath}");
                return false;
            }

            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "sp_describe_first_result_set";
                    cmd.CommandType = System.Data.CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@tsql", sqlText);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (SqlException e) //{Name = "SqlException" FullName = "Microsoft.Data.SqlClient.SqlException"}
            {
                diagnostics.Add(
                    ErrorCode.WRN_SQL_VerificationWarn,
                    node,
                    e.Message);
                return false;
            }
            catch (Exception e)
            {
                diagnostics.Add(
                    ErrorCode.ERR_SQL_VerificationError,
                    node,
                    e.Message);
                return false;
            }

            return true;
        }

        /*
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
                    $"1 Missing iWareDatabase.json file at {configPath}");
                return false;
            }

            var reason = VerifySqlAsync(
                VerifierInput(
                    configPath,
                    sqlText,
                    node)).Result;

            if (reason is null || reason.Status is null)
            {
                reason = VerifierOutput.Unknown();
            }
            if (reason.Message is null)
            {
                reason.Message = "Unknown";
            }

            switch (reason.Status)
            {
                case StatusCode.Success:
                    return true;
                case StatusCode.Warning:
                    diagnostics.Add(ErrorCode.WRN_SQL_VerificationWarn, node.SqlKeyword, "2 " + reason.Message);
                    break;
                case StatusCode.Error:
                    diagnostics.Add(ErrorCode.ERR_SQL_VerificationError, node.SqlKeyword, "10 " + reason.Message);
                    break;
            }

            //if (!string.IsNullOrEmpty(reason.Message))
            //{
            //    if (reason.Status)
            //    {
            //        diagnostics.Add(ErrorCode.ERR_SQL_VerificationError, node.SqlKeyword, reason.Message);
            //    }
            //    else
            //    {
            //        diagnostics.Add(ErrorCode.WRN_SQL_VerificationWarn, node.SqlKeyword, "2 " + reason.Message);
            //    }
            //}

            if (reason.Diagnostics == null)
            {
                return false;
            }
            var segments = node.SqlTextBlock.Segments.ToImmutableArray();
            foreach (var e in reason.Diagnostics)
            {
                if (e.Index < 0 || e.Index >= segments.Length)
                {
                    diagnostics.Add(ErrorCode.WRN_SQL_VerificationWarn, node.SqlTextBlock.Location, "3 " + e.Text ?? "");
                    continue;
                }
                var segment = segments[e.Index];
                if (e.Status == StatusCode.Warning)
                {
                    diagnostics.Add(ErrorCode.WRN_SQL_VerificationWarn, segment.Location, "4 " + e.Text ?? "");
                }
                else if (e.Status == StatusCode.Error)
                {
                    diagnostics.Add(ErrorCode.ERR_SQL_VerificationError, segment.Location, "2 " + e.Text ?? "");
                }
                else
                {
                    diagnostics.Add(ErrorCode.ERR_SQL_VerificationError, node.SqlTextBlock.Location, "3 " + e.Text ?? "");
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
                return VerifierOutput.Error("Http request error " + e.Message);
            }
            catch (Exception e)
            {
                return VerifierOutput.Error("Tuki" + e.Message);
            }
            return VerifierOutput.Unavailable();
        }
        */
    }
}
