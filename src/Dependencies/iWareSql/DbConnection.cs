using Microsoft.Data.SqlClient;
using System.Text.Json;
using System.IO;

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
#pragma warning disable RS0016 // Add public types and members to the declared API
    public static class DbConnection
    {
        public static string? TenantId { get; set; }
        public static string? ConnectionString { private get; set; }
        public const string DbConfigFileName = "iWareDatabase.json";
        private static string? s_dbConfiFile = null;

        internal class DbSettings
        {
            public string? CompanyName { get; set; }
            public string? TID { get; set; }
            public string? ConnectionString { get; set; }
            public string? ExternalFile { get; set; }
        }

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

        static DbConnection()
        {
            string? settingsFile = null;
            try
            {
                if (settingsFile == null)
                {
                    settingsFile = FindDbConfigFile(Directory.GetCurrentDirectory());
                    if (!File.Exists(settingsFile))
                    {
                        settingsFile = null;
                    }
                }
            }
            catch
            {
                return;
            }
            if (settingsFile != null)
            {
                SetSettings(settingsFile);
            }
        }

        public static void SetSettings(string settingsFile)
        {
            DbSettings? settings = null;
            try
            {
                var settingsText = File.ReadAllText(settingsFile);
                settings = JsonSerializer.Deserialize<DbSettings>(settingsText);
            }
            catch { }
            TenantId = settings?.TID;
            ConnectionString = settings?.ConnectionString;
        }

        public static SqlConnection Conn()
        {
            if (ConnectionString is null)
            {
                throw new System.Exception("Missing connection string");
            }

            return Conn(ConnectionString);
        }

        public static SqlConnection Conn(string connectionString)
        {
            var conn = new SqlConnection(connectionString);
            conn.Open();

            return conn;
        }

        public static string ResolveSQL(string tenantId, string sqlString)
        {
            //ParseOptions _options = new ParseOptions() { TransactSqlVersion = TransactSqlVersion.Version170, CompatibilityLevel = DatabaseCompatibilityLevel.Current };
            //ParseResult _res = Parser.Parse(sql );

            //Parser.Parse("", null, SQLType.TSql);
            return sqlString;
        }
    }
}
#pragma warning restore RS0016 // Add public types and members to the declared API
