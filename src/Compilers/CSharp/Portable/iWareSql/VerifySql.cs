// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;

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

            /// <summary>
            /// Tables and columns named by the block, with offsets into the sql text. Cached
            /// alongside the syntax diagnosis because extracting them is part of the same
            /// ScriptDom parse and, like that parse, depends only on the text. Whether each one
            /// *exists* depends on the [Orm] classes in the compilation, which changes as the user
            /// edits, so that check is deliberately left out of the cache and redone on every call
            /// - it is only a dictionary lookup per name.
            /// </summary>
            public SqlReferences _references = SqlReferences.Empty;

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

            public void HighlightUndeclaredNames(
                BindingDiagnosticBag diagnostics,
                Compilation compilation,
                SqlTextMap sqlTextMap,
                SqlTextBlockSyntax? location)
            {
                if (location is null || _references.IsEmpty)
                {
                    return;
                }

                foreach (var reference in SqlTableResolution.Resolve(_references, sqlTextMap, compilation))
                {
                    if (reference.Symbol is not null || !reference.ReportIfUnresolved)
                    {
                        continue;
                    }

                    // A warning rather than an error: the schema is only as complete as the [Orm]
                    // classes the compilation can see, and a query against a view or a table owned
                    // by another system is legitimate even with nothing to navigate to.
                    diagnostics.Add(
                        ErrorCode.WRN_SQL_SymbolWarn,
                        Location.Create(location.SyntaxTree, reference.Span),
                        reference.IsTable
                            ? $"No [Orm] class defines a table named '{reference.Name}'"
                            : $"No [Orm] class defines a column named '{reference.Name}'");
                }
            }
        }

        public static bool Verify(
            string fileDir,
            string sqlText,
            SqlTextMap sqlTextMap,
            Compilation compilation,
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

            /*
            string? configPath;
            try
            {
                configPath = FindDbConfigFile(fileDir);
            }
            catch
            {
                configPath = null;
            }

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
                var resolvedSqlText = ResolveSQL(sqlText);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "sp_describe_first_result_set";
                    cmd.CommandType = System.Data.CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@tsql", resolvedSqlText);
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
            */

            TSqlParser parser = new TSql180Parser(initialQuotedIdentifiers: true);
            using (var reader = new StringReader(sqlText))
            {
                var fragment = parser.Parse(reader, out var errors);
                var hasErrors = errors.Count > 0;
                diagnosis._hasHighlight = hasErrors;
                if (hasErrors)
                {
                    diagnosis._errCode = ErrorCode.ERR_SQL_VerificationError;
                    diagnosis._errMsg = string.Join(Environment.NewLine, errors.Select(e => $"Line {e.Line}, Column {e.Column}: {e.Message}"));
                }
                else if (fragment is not null)
                {
                    // Only harvest names from a clean parse. While the user is still typing the
                    // block the tree is full of holes, and reporting "no such table" against
                    // whatever half-written name ScriptDom managed to recover would mean a
                    // warning that appears and disappears on almost every keystroke.
                    diagnosis._references = SqlTableReferences.Collect(fragment);
                }
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
            diagnosis.HighlightUndeclaredNames(diagnostics, compilation, sqlTextMap, sqlCodeLocation);
            return diagnosis._retVal;
        }
    }
}
