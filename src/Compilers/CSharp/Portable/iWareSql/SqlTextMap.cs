// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

#pragma warning disable RS0016 // Add public types and members to the declared API
namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    /// <summary>
    /// Maps a character offset in the sql text handed to ScriptDom back to a span in the original
    /// source file.
    /// </summary>
    /// <remarks>
    /// The two are very nearly the same string: input and output bindings are replaced by
    /// placeholders that <see cref="ParseSql.getNamesFromSqlText"/> pads out to the width of the
    /// text they stand in for, precisely so offsets keep lining up. That padding cannot shrink a
    /// placeholder that is <em>wider</em> than its source, though - "@a" is two characters and its
    /// "@p0" placeholder is three - so a single narrow binding is enough to shift every offset
    /// after it. Rather than assume a constant delta, this records where each segment landed and
    /// interpolates within it, which is exact for text segments and clamped inside placeholders.
    /// Table names only ever occur in text segments, so lookups for them are exact.
    /// </remarks>
    public sealed class SqlTextMap
    {
        private readonly ImmutableArray<int> _sqlStarts;
        private readonly ImmutableArray<int> _sourceStarts;
        private readonly ImmutableArray<int> _sourceWidths;
        private readonly TextSpan _fallback;

        private SqlTextMap(
            ImmutableArray<int> sqlStarts,
            ImmutableArray<int> sourceStarts,
            ImmutableArray<int> sourceWidths,
            TextSpan fallback)
        {
            _sqlStarts = sqlStarts;
            _sourceStarts = sourceStarts;
            _sourceWidths = sourceWidths;
            _fallback = fallback;
        }

        /// <summary>
        /// Renders <paramref name="sqlSegments"/> as the sql text to hand to ScriptDom, replacing
        /// each C# binding with a width-matched placeholder, and returns the map from that text
        /// back to source.
        /// </summary>
        /// <remarks>
        /// This is the single place placeholders are emitted. Both the compiler (when verifying a
        /// block) and the IDE (when resolving what the caret is on) go through it, so the two
        /// always agree about which source character a given sql offset came from.
        /// </remarks>
        public static SqlTextMap Create(SyntaxList<SqlSegmentSyntax> sqlSegments, out string sqlText)
        {
            var builder = new StringBuilder();
            var sqlStarts = ImmutableArray.CreateBuilder<int>(sqlSegments.Count);
            var sourceStarts = ImmutableArray.CreateBuilder<int>(sqlSegments.Count);
            var sourceWidths = ImmutableArray.CreateBuilder<int>(sqlSegments.Count);

            var inputCount = 0;
            var outputCount = 0;

            foreach (var segment in sqlSegments)
            {
                sqlStarts.Add(builder.Length);
                sourceStarts.Add(segment.FullSpan.Start);
                sourceWidths.Add(segment.FullSpan.Length);

                switch (segment)
                {
                    case SqlInputIdentifierSegmentSyntax:
                        AppendPlaceholder(builder, segment, ParseSql.InputParameterName(inputCount++));
                        break;

                    case SqlOutputIdentifierSegmentSyntax:
                        AppendPlaceholder(builder, segment, "[" + ParseSql.OutputAliasName(outputCount++) + "]");
                        break;

                    default:
                        builder.Append(segment.ToFullString());
                        break;
                }
            }

            sqlText = builder.ToString();
            return new SqlTextMap(
                sqlStarts.ToImmutable(),
                sourceStarts.ToImmutable(),
                sourceWidths.ToImmutable(),
                sqlSegments.Count > 0 ? sqlSegments.FullSpan : default);
        }

        private static void AppendPlaceholder(StringBuilder builder, SqlSegmentSyntax segment, string replacement)
        {
            // Keep the segment's surrounding trivia so the SQL retains its original spacing,
            // and pad the placeholder out to the width of the source text it stands in for.
            // Holding the length steady means character offsets in errors the server reports
            // against sqlText still line up with the same position in the source file.
            builder.Append(segment.GetLeadingTrivia().ToFullString());
            builder.Append(replacement);

            var replacedWidth = segment.ToString().Length;
            if (replacement.Length < replacedWidth)
            {
                builder.Append(' ', replacedWidth - replacement.Length);
            }

            builder.Append(segment.GetTrailingTrivia().ToFullString());
        }

        /// <summary>
        /// The source span corresponding to <paramref name="length"/> characters of sql text
        /// starting at <paramref name="sqlOffset"/>, or the whole block when the offset cannot be
        /// placed (empty block, or an offset past the end of the text).
        /// </summary>
        public TextSpan MapToSource(int sqlOffset, int length)
        {
            if (_sqlStarts.IsDefaultOrEmpty || sqlOffset < 0)
            {
                return _fallback;
            }

            var start = MapPosition(sqlOffset);
            var end = MapPosition(sqlOffset + length);

            return end > start
                ? TextSpan.FromBounds(start, end)
                : new TextSpan(start, length);
        }

        private int MapPosition(int sqlOffset)
        {
            var index = FindSegment(sqlOffset);
            var delta = sqlOffset - _sqlStarts[index];

            // Inside a placeholder that outgrew its source text the interpolation would run past
            // the segment, so stop at the segment's end and let the span collapse onto it.
            if (delta > _sourceWidths[index])
            {
                delta = _sourceWidths[index];
            }

            return _sourceStarts[index] + delta;
        }

        private int FindSegment(int sqlOffset)
        {
            // Largest index whose segment starts at or before the offset.
            var low = 0;
            var high = _sqlStarts.Length - 1;
            while (low < high)
            {
                var mid = low + ((high - low + 1) / 2);
                if (_sqlStarts[mid] <= sqlOffset)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return low;
        }
    }
}
