// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.iWareSql;
using TableReference = Microsoft.CodeAnalysis.CSharp.iWareSql.TableReference;
using Microsoft.CodeAnalysis.Completion;

#pragma warning disable RS0016 // Add public types and members to the declared API
namespace Microsoft.CodeAnalysis.CSharp.Completion.iWareSql
{
    public class QueryInfo
    {
        public readonly List<SourceReference> SourceReferences = new();
        private readonly HashSet<string> _tableReferencesNamesSet = new();
        public DateTime LastUpdated = DateTime.MinValue;
        public DateTime LastAttemptedUpdate = DateTime.MinValue;
        public DateTime LastAttemptedColumnUpdate = DateTime.MinValue;
        public SqlSelectSyntaxInfo? SyntaxInfo = null;
        private SqlSelectSyntaxInfo? _currentQuery = null;
        public SqlSelectSyntaxInfo? CurrentQuery
        {
            get => _currentQuery ?? SyntaxInfo;
            set => _currentQuery = value;
        }

        public void Merge(List<string> tableNames)
        {
            var tableNamesSet = new HashSet<string>(tableNames);

            //Remove all tableReferences not in tableNames
            SourceReferences.RemoveAll(ts =>
            {
                if (ts is TableReference tr && !tableNamesSet.Contains(tr.TableName))
                {
                    _tableReferencesNamesSet.Remove(tr.TableName);
                    return true;
                }
                return false;
            });

            // Add tableNames which don't exist in tableReferences
            foreach (var tableName in tableNames)
            {
                if (!_tableReferencesNamesSet.Contains(tableName))
                {
                    SourceReferences.Add(new TableReference(tableName));
                    _tableReferencesNamesSet.Add(tableName);
                }
            }

            LastUpdated = DateTime.Now;
        }

        public List<string> GetTableNames()
        {
            return SourceReferences
                .OfType<TableReference>()
                .Select(tr => tr.TableName)
                .ToList();
        }

        public List<(string Alias, string TableName)> GetTableAliases()
        {
            List<(string, string)> aliases = new();

            foreach (var sr in SourceReferences)
            {
                if (!string.IsNullOrEmpty(sr.Alias))
                {
                    switch (sr)
                    {
                        case TableReference tr:
                            aliases.Add((tr.Alias!, tr.TableName));
                            break;
                        case SubqueryReference sqr:
                            aliases.Add((sqr.Alias!, "Subquery"));
                            break;
                    }
                }
            }

            return aliases;

            //TODO aljaz naj se aliasi nastavijo v Update metodah

            /*
            // Add all tables with an alias
            var tables = CurrentQuery?.Tables;
            if (tables != null)
            {
                aliases.AddRange(tables
                    .Select(tab => (Alias: tab.GetAliasString(), Name: tab.GetNameString()))
                    .Where(tab => tab.Alias != null && tab.Name != null)
                    .Select(tab => (tab.Alias!, tab.Name!)));
            }

            // Add all direct subqueries with an alias
            var subqueries = CurrentQuery?.Subqueries;
            if (subqueries != null)
            {
                aliases.AddRange(subqueries
                    .Select(sub => (Alias: sub.GetAliasString(), Name: "Subquery"))
                    .Where(sub => sub.Alias != null)
                    .Select(sub => (sub.Alias!, sub.Name)));
            }

            return aliases;
            */
        }

        public async Task UpdateTableNames(CompletionContext context)
        {
            // Prevent function from firing too frequently
            if ((DateTime.Now - LastAttemptedUpdate).TotalSeconds < 5)
            {
                return;
            }
            LastAttemptedUpdate = DateTime.Now;

            // Table names now come from [Orm]-annotated classes in the compilation instead of
            // a live database, via OrmSchemaProvider - see SqlCompletionQueries.GetTableNames.
            var compilation = await context.Document.Project.GetCompilationAsync(context.CancellationToken).ConfigureAwait(false);
            if (compilation == null)
            {
                return;
            }

            var tableNames = SqlCompletionQueries.GetTableNames(compilation);
            Merge(tableNames);
        }

        public void UpdateQuerySyntaxInfo(Syntax.SqlTextBlockSyntax sqlBlock, CompletionContext context)
        {
            var sqlStatement = sqlBlock.Segments;
            var sqlStatementString = sqlStatement.ToFullString();
            var relativePosition = context.Position - sqlStatement.FullSpan.Start;
            SyntaxInfo = SqlSelectSyntaxInfo.GetInfoFromString(sqlStatementString) ?? SyntaxInfo;
            CurrentQuery = SyntaxInfo?.GetQueryAtCursorPosition(relativePosition);

            // If couldn't resolve query return, otherwise do merge
            if (CurrentQuery == null)
            {
                return;
            }

            // Add table aliases
            var tables = CurrentQuery.Tables;
            var tableReferences = SourceReferences.OfType<TableReference>();
            foreach (var table in tables)
            {
                if (table.Alias != null)
                {
                    var tableReference = tableReferences.First(tr => table.CompareName(tr.TableName));
                    tableReference?.Alias = table.GetAliasString();
                }
            }

            // Replace subquery references
            SourceReferences.RemoveAll(sr => sr is SubqueryReference);
            var subqueries = CurrentQuery.FlattenSubqueries();
            SourceReferences.AddRange(subqueries.Select(sq => new SubqueryReference(sq)));
        }

        public SourceReference? GetSourceReferenceByNameOrAlias(string nameOrAlias)
        {
            return GetTableReferenceByName(nameOrAlias) ?? GetSourceReferenceByAlias(nameOrAlias);
        }

        public TableReference? GetTableReferenceByName(string tableName)
        {
            return SourceReferences.OfType<TableReference>().ToList().Find(tr => tr.TableName == tableName);
        }

        public SourceReference? GetSourceReferenceByAlias(string alias)
        {
            return SourceReferences.Find(sr => sr.Alias?.Equals(alias, StringComparison.InvariantCultureIgnoreCase) ?? false);
        }

        // List of column names or aliaes defined in the local context
        public List<string> GetLocallyReferencedColumnNames()
        {
            List<string> columnNames = new();

            List<SqlSelectSyntaxInfo.ColumnInfo> columns =
            [
                .. CurrentQuery?.Columns ?? [],
                .. CurrentQuery?.Subqueries.SelectMany(subquery => subquery.Columns) ?? [],
            ];

            columnNames.AddRange(columns
                .Select(col => col.GetAliasString() ?? col.GetNameString())
                .OfType<string>());

            return columnNames;
        }

        // List of column names belonging to tables referenced in the local context
        public List<string> GetLocallyReferencedSourcesColumnNames()
        {
            return GetLocallyReferencedSourceReferences().SelectMany(sr => sr.ColumnNames).ToList();
        }

        public List<SourceReference> GetLocallyReferencedSourceReferences()
        {
            return
            [
                .. GetLocallyReferencedTableReferences(),
                .. SourceReferences.OfType<SubqueryReference>(),
            ];
        }

        public List<TableReference> GetLocallyReferencedTableReferences()
        {
            List<TableReference> sourceReferences = new();

            var syntaxTables = CurrentQuery?.Tables;
            if (syntaxTables != null)
            {
                sourceReferences.AddRange(SourceReferences
                    .OfType<TableReference>()
                    .Where(tr => syntaxTables.Any(st => st.GetNameString()?.Equals(tr.TableName) ?? false)));
            }

            return sourceReferences;
        }

        // Update column list of tables referenced in the local context
        public async Task UpdateLocallyReferencedTableColumnNames(CompletionContext context)
        {
            if ((DateTime.Now - LastAttemptedColumnUpdate).TotalSeconds < 5)
            {
                return;
            }
            LastAttemptedColumnUpdate = DateTime.Now;

            var tableReferences = GetLocallyReferencedTableReferences().Where(tr => !string.IsNullOrEmpty(tr.TableName)).ToList();
            if (tableReferences.Count < 1)
            {
                return;
            }

            // Column names now come from [Orm]/[DbField]-annotated classes in the compilation
            // instead of a live database, via OrmSchemaProvider. There's no "changed since"
            // dimension to worry about anymore (unlike sys.tables.modify_date) - OrmSchemaProvider
            // is exact for a given Compilation instance, so we can just fetch what's needed
            // directly instead of first querying which tables changed.
            var compilation = await context.Document.Project.GetCompilationAsync(context.CancellationToken).ConfigureAwait(false);
            if (compilation == null)
            {
                return;
            }

            var tableNames = tableReferences.Select(tr => tr.TableName!).ToList();
            var columnsAndTables = SqlCompletionQueries.GetColumnNamesFromTables(compilation, tableNames);

            foreach (var tr in tableReferences)
            {
                tr.MergeColumns(columnsAndTables
                    .Where(cat => cat.Table.Equals(tr.TableName, StringComparison.InvariantCultureIgnoreCase))
                    .Select(cat => cat.Column)
                    .ToList());
            }
        }
    }
}
#pragma warning restore RS0016 // Add public types and members to the declared API
