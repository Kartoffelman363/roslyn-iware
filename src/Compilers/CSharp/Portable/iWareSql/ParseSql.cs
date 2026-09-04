// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis.CSharp.Syntax;

//#pragma warning disable RS0016 // Add public types and members to the declared API

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    //TODO-aljaz no longer used for parsing, raname
    internal static class ParseSql
    {
        /// <summary>
        /// The column alias emitted for the <paramref name="index"/>-th output binding.
        /// </summary>
        /// <remarks>
        /// The text the user wrote between the brackets cannot be used as the alias. It may be an
        /// arbitrary C# expression, and T-SQL ends a bracket-quoted identifier at the first
        /// unescaped <c>]</c> — so <c>[list[0]]</c> would be emitted as the identifier
        /// <c>[list[0]</c> followed by a stray <c>]</c>, which is a syntax error at the server.
        /// A synthesized alias also removes any chance of two bindings colliding on the same
        /// leaf name.
        /// </remarks>
        public static string OutputAliasName(int index)
            => "__c" + index.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// The parameter name emitted for the <paramref name="index"/>-th input binding.
        /// </summary>
        /// <remarks>
        /// As with output aliases, the source text is unusable: <c>@n.krneki</c> is not a legal
        /// <c>SqlParameter</c> name.
        /// </remarks>
        public static string InputParameterName(int index)
            => "@p" + index.ToString(CultureInfo.InvariantCulture);

        public static void getNamesFromSqlText(
            out ImmutableArray<SqlOutputIdentifierSegmentSyntax> sqlOutputs,
            out ImmutableArray<SqlInputIdentifierSegmentSyntax> sqlInputs,
            out ImmutableArray<string> sqlOutputNames,
            out ImmutableArray<string> sqlInputNames,
            SyntaxList<SqlSegmentSyntax> sqlSegments,
            out string sqlText,
            out SqlTextMap sqlTextMap)
        {
            var sqlOutputBuilder = ImmutableArray.CreateBuilder<SqlOutputIdentifierSegmentSyntax>();
            var sqlInputBuilder = ImmutableArray.CreateBuilder<SqlInputIdentifierSegmentSyntax>();
            var sqlOutputNameBuilder = ImmutableArray.CreateBuilder<string>();
            var sqlInputNameBuilder = ImmutableArray.CreateBuilder<string>();

            foreach (var sqlSegment in sqlSegments)
            {
                switch (sqlSegment)
                {
                    case SqlInputIdentifierSegmentSyntax sqlInputSegment:
                        sqlInputNameBuilder.Add(InputParameterName(sqlInputBuilder.Count));
                        sqlInputBuilder.Add(sqlInputSegment);
                        break;

                    case SqlOutputIdentifierSegmentSyntax sqlOutputSegment:
                        sqlOutputNameBuilder.Add(OutputAliasName(sqlOutputBuilder.Count));
                        sqlOutputBuilder.Add(sqlOutputSegment);
                        break;
                }
            }

            // The text itself, and the placeholders standing in for the bindings collected above,
            // are produced by SqlTextMap so that the IDE renders a block exactly the way the
            // compiler does - the numbering here walks the segments in the same order, so the
            // n-th name in these arrays is the n-th placeholder in the text.
            sqlTextMap = SqlTextMap.Create(sqlSegments, out sqlText);

            sqlOutputs = sqlOutputBuilder.ToImmutableArray();
            sqlInputs = sqlInputBuilder.ToImmutableArray();
            sqlOutputNames = sqlOutputNameBuilder.ToImmutableArray();
            sqlInputNames = sqlInputNameBuilder.ToImmutableArray();
        }
    }
}
