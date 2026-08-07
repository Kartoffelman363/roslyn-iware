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
        public static Dictionary<string, List<string>> GetTableAndColumnNames(Compilation compilation)
        {
            var tables = OrmSchemaProvider.GetSchema(compilation).Tables.ToList();
            return tables
                .ToDictionary(
                    t => t.TableName,
                    t => t.Columns.Select(c => c.ColumnName).ToList());
        }
    }
}
