// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp.Completion.iWareSql;

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
#pragma warning disable RS0016 // Add public types and members to the declared API
    public abstract class SourceReference
    {
        public List<string> ColumnNames { get; private set; } = new();
        public string? Alias;
    }

    public class TableReference : SourceReference
    {
        public string TableName;
        public DateTime LastUpdatedColumns = DateTime.MinValue;
        public DateTime LastAttemptedUpdateColumns = DateTime.MinValue;
        private readonly HashSet<string> _columnNamesSet = new();

        public TableReference(string tableName)
        {
            TableName = tableName;
        }

        public void MergeColumns(List<string> columnNames)
        {
            Helpers.Merge(ColumnNames, columnNames, _columnNamesSet);

            LastUpdatedColumns = DateTime.Now;
        }

        public async Task UpdateColumnNames(CompletionContext context)
        {
            // Prevent function from firing too frequently
            if ((DateTime.Now - LastAttemptedUpdateColumns).TotalSeconds < 5)
            {
                return;
            }
            LastAttemptedUpdateColumns = DateTime.Now;

            // Column names now come from [Orm]/[DbField]-annotated classes in the compilation
            // instead of a live database, via OrmSchemaProvider - see
            // SqlCompletionQueries.GetColumnNamesFromTable.
            var compilation = await context.Document.Project.GetCompilationAsync(context.CancellationToken).ConfigureAwait(false);
            if (compilation == null)
            {
                return;
            }

            var columnNames = SqlCompletionQueries.GetColumnNamesFromTable(compilation, TableName);
            if (columnNames != null)
            {
                MergeColumns(columnNames);
            }
        }
    }

    public class SubqueryReference : SourceReference
    {
        public SubqueryReference() { }

        public SubqueryReference(SqlSelectSyntaxInfo.SqlSubquerySyntaxInfo syntaxInfo)
        {
            Alias = syntaxInfo.Alias?.Value;
            UpdateColumnNames(syntaxInfo);
        }

        public void UpdateColumnNames(SqlSelectSyntaxInfo.SqlSubquerySyntaxInfo syntaxInfo)
        {
            // Use alias if exists or name
            ColumnNames.AddRange(syntaxInfo.Columns.Select(col => col.GetAliasString() ?? col.GetNameString()).OfType<string>());
        }
    }
}
#pragma warning restore RS0016 // Add public types and members to the declared API
