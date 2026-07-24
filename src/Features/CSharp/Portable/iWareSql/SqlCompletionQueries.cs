// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.SqlClient;
using static Microsoft.CodeAnalysis.CSharp.Completion.iWareSql.DbConnection;

namespace Microsoft.CodeAnalysis.CSharp.Completion.iWareSql
{
    internal class SqlCompletionQueries
    {
        private const string DbConfigFileName = "iWareDatabase.json";
        private static string? s_dbConfigFile = null;

        public static string? FindDbConfigFile(string startDir)
        {
            if (s_dbConfigFile != null)
            {
                return s_dbConfigFile;
            }

            var dir = new DirectoryInfo(startDir);

            while (dir != null)
            {
                var dbConfFile = Path.Combine(dir.FullName, DbConfigFileName);
                if (File.Exists(dbConfFile))
                {
                    s_dbConfigFile = dbConfFile;
                    return dbConfFile;
                }

                dir = dir.Parent;
            }

            return null;
        }

        public static List<string>? GetTableNames(string fileDir)
        {
            var tableNames = new List<string>();
            var configPath = FindDbConfigFile(fileDir);
            if (configPath == null)
            {
                return null;
            }

            SetSettings(configPath);
            SqlConnection conn;
            try
            {
                conn = Conn();
            }
            catch
            {
                return null;
            }

            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES";
                    cmd.CommandType = System.Data.CommandType.Text;
                    var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(0))
                        {
                            tableNames.Add(reader.GetString(0));
                        }
                    }
                }
            }
            catch
            {
                return null;
            }

            return tableNames;
        }

        // Get time of latest change made to the database schema, DateTime.MinValue if  or null if error
        public static DateTime? LastTableChangedTime(string fileDir)
        {
            var configPath = FindDbConfigFile(fileDir);
            if (configPath == null)
            {
                return null;
            }

            SetSettings(configPath);
            SqlConnection conn;
            try
            {
                conn = Conn();
            }
            catch
            {
                return null;
            }

            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT max(modify_date) AS last_modified FROM sys.tables";
                    cmd.CommandType = System.Data.CommandType.Text;
                    var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if (reader.IsDBNull(0))
                        {
                            return DateTime.MinValue;
                        }
                        return reader.GetDateTime(0);
                    }
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
