// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.SqlClient;
using static Microsoft.CodeAnalysis.CSharp.iWareSql.DbConnection;

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    internal class VerifySql
    {
        private static readonly ConcurrentDictionary<string, Diagnosis> s_sqlVerificationCache = new();

        private class Diagnosis
        {
            public bool _retVal = true;
            public bool _hasHighlight = false;
            public ErrorCode? _errCode = null;
            public string? _errMsg = null;

            public void Highlight(BindingDiagnosticBag diagnostics, SqlTextBlockSyntax? location)
            {
                if (_hasHighlight && location != null)
                {
                    diagnostics.Add(
                    _errCode ?? ErrorCode.Void,
                    location,
                    _errMsg ?? "");
                }
            }
        }

        public static bool Verify(
            string fileDir,
            string sqlText,
            SqlStatementSyntax node,
            BindingDiagnosticBag diagnostics)
        {
            string cacheKey;
            Diagnosis? diagnosis;
            var sqlCodeLocation = node.SqlTextBlock;
            using (SHA256 sha256Hash = SHA256.Create())
            {
                var ck = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(sqlText));
                cacheKey = Convert.ToBase64String(ck);
                if (s_sqlVerificationCache.TryGetValue(cacheKey, out diagnosis))
                {
                    goto end;
                }
                diagnosis = new();
            }

            /*
            var configPath = Path.Combine(fileDir, dbConfigFileName);

            if (!File.Exists(configPath))
            {
                diagnostics.Add(
                    ErrorCode.ERR_SQL_VerificationError,
                    sqlCodeLocation,
                    $"Missing {dbConfigFileName} file at {configPath}");
                return false;
            }
            */
            var configPath = FindDbConfigFile(fileDir);
            if (configPath == null)
            {
                diagnosis._retVal = false;
                diagnosis._hasHighlight = true;
                diagnosis._errCode = ErrorCode.ERR_SQL_VerificationError;
                diagnosis._errMsg = $"Missing {DbConfigFileName} file at {configPath}";
                cacheKey = "no_config"; // TODO aljaz config cache
                goto end;
            }

            SetSettings(configPath);
            SqlConnection conn;
            try
            {
                conn = Conn();
            }
            catch
            {
                diagnosis._retVal = false;
                diagnosis._hasHighlight = true;
                diagnosis._errCode = ErrorCode.ERR_SQL_VerificationError;
                diagnosis._errMsg = $"Could not connect to database with ConnectionString listed in {configPath}";
                cacheKey = "no_conn_" + cacheKey; // TODO aljaz config cache
                goto end;
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
                diagnosis._retVal = false;
                diagnosis._hasHighlight = true;
                diagnosis._errCode = ErrorCode.ERR_SQL_VerificationError;
                diagnosis._errMsg = e.Message;
                goto end;
            }
            catch (Exception e)
            {
                diagnosis._retVal = false;
                diagnosis._hasHighlight = true;
                diagnosis._errCode = ErrorCode.ERR_SQL_VerificationError;
                diagnosis._errMsg = e.Message;
                goto end;
            }
end:
            try
            {
                s_sqlVerificationCache.TryAdd(cacheKey, diagnosis);
            }
            catch (OverflowException)
            {
                s_sqlVerificationCache.Clear();
            }
            diagnosis.Highlight(diagnostics, sqlCodeLocation);
            return diagnosis._retVal;
        }
    }
}
