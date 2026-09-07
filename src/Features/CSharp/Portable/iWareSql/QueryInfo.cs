// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.iWareSql;
using Microsoft.CodeAnalysis.CSharp.SqlQueries;
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

        public void Merge(Dictionary<string, List<string>> tables)
        {
            var tableNames = tables.Keys;
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
            var tableReferences = SourceReferences.OfType<TableReference>();
            foreach (var table in tables)
            {
                var tableName = table.Key;

                var tableRefernce = tableReferences.FirstOrDefault(t => t.TableName == tableName);
                if (tableRefernce == null)
                {
                    // Create new
                    SourceReferences.Add(new TableReference(tableName, table.Value));
                    _tableReferencesNamesSet.Add(tableName);
                }
                else
                {
                    // Update column names
                    tableRefernce.MergeColumns(table.Value);
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

        public async Task UpdateTableNamesAsync(CompletionContext context)
        {
            // Table names now come from [Orm]-annotated classes in the compilation instead of
            // a live database, via OrmSchemaProvider - see SqlCompletionQueries.GetTableNames.
            var compilation = await context.Document.Project.GetCompilationAsync(context.CancellationToken).ConfigureAwait(false);
            if (compilation == null)
            {
                return;
            }

            var tables = SqlCompletionQueries.GetTableAndColumnNames(compilation);
            Merge(tables);
        }

        /// <summary>
        /// Because "SELECT id[myObj.myProp] ... WHERE id = @otherObj.prop;" aren't valid syntaxes
        /// in SQL, the C# bindings are replaced with placeholders before the text is parsed.
        /// </summary>
        /// <remarks>
        /// This defers to <see cref="SqlTextMap.Create"/>, which is what the compiler renders a
        /// block with, rather than substituting placeholders here as well. Keeping a second
        /// implementation meant every new kind of binding had to be taught to both, and a segment
        /// kind the switch had not heard of fell through to its own source text - which for a
        /// wildcard is "*[obj]", not valid sql, so the parse failed and completion silently had no
        /// query to offer aliases from.
        ///
        /// It also fixes the offsets. The placeholders substituted here were free-form and much
        /// wider than the text they replaced, so every character after a binding sat further along
        /// than the caret position being compared against it; the compiler's are padded to the
        /// width of what they stand in for, so the subtraction below is exact.
        /// </remarks>
        public async Task UpdateQuerySyntaxInfoAsync(Syntax.SqlTextBlockSyntax sqlBlock, CompletionContext context)
        {
            var sqlStatement = sqlBlock.Segments;
            SqlTextMap.Create(sqlStatement, out var sqlStatementString);
            var relativePosition = context.Position - sqlStatement.FullSpan.Start;
            SyntaxInfo = await SqlSelectSyntaxInfo.GetInfoFromStringAsync(sqlStatementString).ConfigureAwait(false) ?? SyntaxInfo;
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
                    var tableReference = tableReferences.FirstOrDefault(tr => table.CompareName(tr.TableName));
                    tableReference?.Alias = table.GetAliasString();
                }
            }

            // Replace subquery references
            SourceReferences.RemoveAll(sr => sr is SubqueryReference);
            var subqueries = CurrentQuery.FlattenSubqueries();

            // A subquery's own select list is not enough to know what it offers: "SELECT * FROM
            // users" has a star rather than columns, so resolving it against the [Orm] schema is
            // what turns it back into a column list worth suggesting.
            var compilation = await context.Document.Project.GetCompilationAsync(context.CancellationToken).ConfigureAwait(false);
            var schema = compilation is null ? null : OrmSchemaProvider.GetSchema(compilation);

            foreach (var subquery in subqueries)
            {
                var reference = new SubqueryReference(subquery);
                if (schema is not null)
                {
                    reference.SetColumnNames(
                        SqlSourceResolution.GetOutputColumns(subquery, schema).Select(static c => c.Name));
                }

                SourceReferences.Add(reference);
            }
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
    }
}
#pragma warning restore RS0016 // Add public types and members to the declared API
