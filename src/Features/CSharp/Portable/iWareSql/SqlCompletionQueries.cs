// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.SqlClient;
using static Microsoft.CodeAnalysis.CSharp.Completion.iWareSql.DbConnection;

namespace Microsoft.CodeAnalysis.CSharp.Completion.iWareSql
{
    //TODO aljaz handle multiple tables with the same name across these queries
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
            try
            {
                using var conn = Conn();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM iw_tables_schema WHERE tid = @tennantId OR tid IS NULL";
                cmd.CommandType = System.Data.CommandType.Text;
                cmd.Parameters.AddWithValue("@tennantId", TenantId);
                var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                    {
                        tableNames.Add(reader.GetString(0));
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
            try
            {
                using var conn = Conn();
                using var cmd = conn.CreateCommand();
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
            catch
            {
                return null;
            }
        }

        public static DateTime? TableChangedTime(string fileDir, string tableName)
        {
            var configPath = FindDbConfigFile(fileDir);
            if (configPath == null)
            {
                return null;
            }

            SetSettings(configPath);
            try
            {
                using var conn = Conn();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT modify_date FROM sys.tables WHERE name = @tableName";
                cmd.CommandType = System.Data.CommandType.Text;
                cmd.Parameters.AddWithValue("@tableName", tableName);
                var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0))
                    {
                        return null;
                    }
                    return reader.GetDateTime(0);
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        public static List<(DateTime Time, string Table)> TablesChangedTime(string fileDir, List<string> tableNames)
        {
            List<(DateTime, string)> tablesChangedTime = new();
            var inTableNames = string.Join(", ", tableNames);
            if (string.IsNullOrEmpty(inTableNames))
            {
                return tablesChangedTime;
            }

            var configPath = FindDbConfigFile(fileDir);
            if (configPath == null)
            {
                return tablesChangedTime;
            }

            SetSettings(configPath);
            try
            {
                using var conn = Conn();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT modify_date, name FROM sys.tables WHERE name IN ({inTableNames})";
                cmd.CommandType = System.Data.CommandType.Text;
                var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tablesChangedTime.Add((reader.GetDateTime(0), reader.GetString(1)));
                }
                return tablesChangedTime;
            }
            catch
            {
                return tablesChangedTime;
            }
        }

        public static List<string>? GetColumnNamesFromTable(string fileDir, string tableName)
        {
            var columnNames = new List<string>();
            var configPath = FindDbConfigFile(fileDir);
            if (configPath == null)
            {
                return null;
            }

            SetSettings(configPath);
            try
            {
                using var conn = Conn();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @tableName";
                cmd.CommandType = System.Data.CommandType.Text;
                cmd.Parameters.AddWithValue("@tableName", tableName);
                var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                    {
                        columnNames.Add(reader.GetString(0));
                    }
                }
            }
            catch
            {
                return null;
            }

            return columnNames;
        }

        public static List<(string Column, string Table)> GetColumnNamesFromTables(string fileDir, List<string> tableNames)
        {
            List<(string, string)> tablesChangedTime = new();
            var inTableNames = string.Join(", ", tableNames);
            if (string.IsNullOrEmpty(inTableNames))
            {
                return tablesChangedTime;
            }

            var configPath = FindDbConfigFile(fileDir);
            if (configPath == null)
            {
                return tablesChangedTime;
            }

            SetSettings(configPath);
            try
            {
                using var conn = Conn();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COLUMN_NAME, TABLE_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME IN ({inTableNames})";
                cmd.CommandType = System.Data.CommandType.Text;
                var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tablesChangedTime.Add((reader.GetString(0), reader.GetString(1)));
                }
                return tablesChangedTime;
            }
            catch
            {
                return tablesChangedTime;
            }
        }
    }
}
