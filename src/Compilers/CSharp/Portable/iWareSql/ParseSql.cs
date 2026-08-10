// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
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
            out string sqlText)
        {
            var sqlOutputBuilder = ImmutableArray.CreateBuilder<SqlOutputIdentifierSegmentSyntax>();
            var sqlInputBuilder = ImmutableArray.CreateBuilder<SqlInputIdentifierSegmentSyntax>();
            var sqlOutputNameBuilder = ImmutableArray.CreateBuilder<string>();
            var sqlInputNameBuilder = ImmutableArray.CreateBuilder<string>();
            var stringBuilder = new StringBuilder();

            foreach (var sqlSegment in sqlSegments)
            {
                switch (sqlSegment)
                {
                    case SqlInputIdentifierSegmentSyntax sqlInputSegment:
                        {
                            var name = InputParameterName(sqlInputBuilder.Count);
                            sqlInputBuilder.Add(sqlInputSegment);
                            sqlInputNameBuilder.Add(name);
                            appendPlaceholder(sqlSegment, name);
                            break;
                        }

                    case SqlOutputIdentifierSegmentSyntax sqlOutputSegment:
                        {
                            var name = OutputAliasName(sqlOutputBuilder.Count);
                            sqlOutputBuilder.Add(sqlOutputSegment);
                            sqlOutputNameBuilder.Add(name);
                            appendPlaceholder(sqlSegment, "[" + name + "]");
                            break;
                        }

                    default:
                        stringBuilder.Append(sqlSegment.ToFullString());
                        break;
                }
            }

            sqlText = stringBuilder.ToString();
            sqlOutputs = sqlOutputBuilder.ToImmutableArray();
            sqlInputs = sqlInputBuilder.ToImmutableArray();
            sqlOutputNames = sqlOutputNameBuilder.ToImmutableArray();
            sqlInputNames = sqlInputNameBuilder.ToImmutableArray();

            void appendPlaceholder(SqlSegmentSyntax segment, string replacement)
            {
                // Keep the segment's surrounding trivia so the SQL retains its original spacing,
                // and pad the placeholder out to the width of the source text it stands in for.
                // Holding the length steady means character offsets in errors the server reports
                // against sqlText still line up with the same position in the source file.
                stringBuilder.Append(segment.GetLeadingTrivia().ToFullString());
                stringBuilder.Append(replacement);

                var replacedWidth = segment.ToString().Length;
                if (replacement.Length < replacedWidth)
                {
                    stringBuilder.Append(' ', replacedWidth - replacement.Length);
                }

                stringBuilder.Append(segment.GetTrailingTrivia().ToFullString());
            }
        }
    }
}
