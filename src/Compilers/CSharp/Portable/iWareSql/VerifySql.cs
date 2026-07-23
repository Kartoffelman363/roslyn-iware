// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.SqlClient;
using static Microsoft.CodeAnalysis.CSharp.iWareSql.DbConnection;

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    internal class VerifySql
    {
        private static readonly ConcurrentDictionary<string, bool> s_sqlVerificationCache = new();
        private const string DbConfigFileName = "iWareDatabase.json";
        private static string? s_dbConfiFile = null;

        public static string? FindDbConfigFile(string startDir)
        {
            if (s_dbConfiFile != null)
            {
                return s_dbConfiFile;
            }

            var dir = new DirectoryInfo(startDir);

            while (dir != null)
            {
                var dbConfFile = Path.Combine(dir.FullName, DbConfigFileName);
                if (File.Exists(dbConfFile))
                {
                    s_dbConfiFile = dbConfFile;
                    return dbConfFile;
                }

                dir = dir.Parent;
            }

            return null;
        }

        public static bool Verify(
            string fileDir,
            string sqlText,
            SqlStatementSyntax node,
            BindingDiagnosticBag diagnostics)
        {
            string cacheKey;
            bool retVal;
            using (SHA256 sha256Hash = SHA256.Create())
            {
                var ck = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(sqlText));
                cacheKey = Convert.ToBase64String(ck);
                if (s_sqlVerificationCache.TryGetValue(cacheKey, out retVal))
                {
                    return retVal;
                }
            }

            var sqlCodeLocation = node.SqlTextBlock;
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
                diagnostics.Add(
                    ErrorCode.ERR_SQL_VerificationError,
                    sqlCodeLocation,
                    $"Missing {DbConfigFileName} file at {configPath}");
                retVal = false;
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
                diagnostics.Add(
                    ErrorCode.ERR_SQL_VerificationError,
                    sqlCodeLocation,
                    $"Could not connect to database with ConnectionString listed in {configPath}");
                retVal = false;
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
                diagnostics.Add(
                    ErrorCode.ERR_SQL_VerificationError,
                    sqlCodeLocation,
                    e.Message);
                retVal = false;
                goto end;
            }
            catch (Exception e)
            {
                diagnostics.Add(
                    ErrorCode.ERR_SQL_VerificationError,
                    sqlCodeLocation,
                    e.Message);
                retVal = false;
                goto end;
            }
end:
            s_sqlVerificationCache.TryAdd(cacheKey, retVal);
            return retVal;
        }
    }
}
