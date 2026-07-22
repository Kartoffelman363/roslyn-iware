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
                    ErrorCode.ERR_SQL_VerificationError,
                    sqlCodeLocation,
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
                    ErrorCode.ERR_SQL_VerificationError,
                    sqlCodeLocation,
                    e.Message);
                return false;
            }
            catch (Exception e)
            {
                diagnostics.Add(
                    ErrorCode.ERR_SQL_VerificationError,
                    sqlCodeLocation,
                    e.Message);
                return false;
            }

            return true;
        }
    }
}
