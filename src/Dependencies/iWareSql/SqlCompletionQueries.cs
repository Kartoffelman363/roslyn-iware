// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.iWareSql;

namespace Microsoft.CodeAnalysis.CSharp.Completion.iWareSql
{
    // Previously ran live ADO.NET queries (sys.tables / INFORMATION_SCHEMA.COLUMNS) against
    // the configured database. Now reads the same shape of data from [Orm]/[DbField]-annotated
    // classes in the Compilation via OrmSchemaProvider - no connection, no config file lookup.
    //
    // The old "changed since"/timestamp methods (LastTableChangedTime, TableChangedTime,
    // TablesChangedTime) are gone: OrmSchemaProvider caches per-Compilation instance, and
    // compilations are immutable, so there's nothing to poll - a schema recompute only ever
    // happens when the user actually edits and a new Compilation is produced.
    internal static class SqlCompletionQueries
    {
        public static List<string> GetTableNames(Compilation compilation)
        {
            return OrmSchemaProvider.GetSchema(compilation).Tables
                .Select(t => t.TableName)
                .ToList();
        }

        public static List<string>? GetColumnNamesFromTable(Compilation compilation, string tableName)
        {
            return OrmSchemaProvider.GetSchema(compilation).FindTable(tableName)?.Columns
                .Select(c => c.ColumnName)
                .ToList();
        }

        public static List<(string Column, string Table)> GetColumnNamesFromTables(Compilation compilation, List<string> tableNames)
        {
            var schema = OrmSchemaProvider.GetSchema(compilation);

            return tableNames
                .Select(schema.FindTable)
                .OfType<OrmTable>()
                .SelectMany(table => table.Columns.Select(column => (column.ColumnName, table.TableName)))
                .ToList();
        }
    }
}
