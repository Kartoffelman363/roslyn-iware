// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.SqlQueries;

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
#pragma warning disable RS0016 // Add public types and members to the declared API
    public abstract class SourceReference
    {
        public List<string> ColumnNames { get; protected set; } = new();
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

        public TableReference(string tableName, List<string> columnNames)
        {
            TableName = tableName;
            ColumnNames = columnNames;
            _columnNamesSet.AddRange(columnNames);
        }

        public void MergeColumns(List<string> columnNames)
        {
            Helpers.Merge(ColumnNames, columnNames, _columnNamesSet);

            LastUpdatedColumns = DateTime.Now;
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

        /// <summary>
        /// Replaces the columns with an already-resolved list.
        /// </summary>
        /// <remarks>
        /// The select list alone cannot describe a subquery written as "SELECT * FROM users": a
        /// star is not a column, so nothing lands in <see cref="SqlSelectSyntaxInfo.Columns"/> and
        /// the subquery looks like it has none. Resolving the star needs the [Orm] schema, which
        /// only the caller has, so it hands the answer in here.
        /// </remarks>
        public void SetColumnNames(IEnumerable<string> columnNames)
        {
            ColumnNames.Clear();
            ColumnNames.AddRange(columnNames);
        }
    }
}
#pragma warning restore RS0016 // Add public types and members to the declared API
