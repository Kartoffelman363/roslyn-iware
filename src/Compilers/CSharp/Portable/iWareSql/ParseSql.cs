// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;

//#pragma warning disable RS0016 // Add public types and members to the declared API

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    //TODO-aljaz no longer used for parsing, raname
    internal static class ParseSql
    {
        public static void getNamesFromSqlText(
            out ImmutableArray<SqlTextSegmentSyntax> sqlOutputs,
            out ImmutableArray<SqlIdentifierSegmentSyntax> sqlInputs,
            SyntaxList<CSharpSyntaxNode> sqlSegments,
            out string sqlText)
        {
            var sqlOutputBuilder = ImmutableArray.CreateBuilder<SqlTextSegmentSyntax>();
            var sqlInputBuilder = ImmutableArray.CreateBuilder<SqlIdentifierSegmentSyntax>();
            var stringBuilder = new StringBuilder();

            //TODO how to find if segment is query, input or output?
            var isEven = false;
            foreach (var sqlSegment in sqlSegments)
            {
                if (isEven)
                {
                    switch (sqlSegment)
                    {
                        case SqlIdentifierSegmentSyntax sqlIdentifierSegment:
                            sqlInputBuilder.Add(sqlIdentifierSegment);
                            break;
                        case SqlTextSegmentSyntax sqlTextSegment:
                            sqlOutputBuilder.Add(sqlTextSegment);
                            break;
                    }
                }
                stringBuilder.Append(sqlSegment.ToFullString());
                isEven = !isEven;
            }

            sqlText = stringBuilder.ToString();
            sqlOutputs = sqlOutputBuilder.ToImmutableArray();
            sqlInputs = sqlInputBuilder.ToImmutableArray();
        }
    }
}
