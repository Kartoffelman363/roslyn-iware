// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;

//#pragma warning disable RS0016 // Add public types and members to the declared API

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    internal static class ParseSql
    {
        public static string getNamesFromSqlText(
            out ImmutableArray<string> names,
            out ImmutableArray<string> sqlNames,
            out ImmutableArray<string> sqlParameterNames,
            string sqlText)
        {
            var namesBuilder = ImmutableArray.CreateBuilder<string>();
            var sqlNamesBuilder = ImmutableArray.CreateBuilder<string>();
            var sqlParameterNamesBuilder = ImmutableArray.CreateBuilder<string>();
            for (int i = 0; i < sqlText.Length; i++)
            {
                var c = look(i);
                // Single line comment
                if (c == '-' && (c = look(++i)) == '-')
                {
                    // find newline or eof
                    while ((c = look(++i)) != '\n' && c != '\0') ;
                    continue;
                }
                // Multi line comment
                if (c == '/' && (c = look(++i)) == '*')
                {
                    // find */ or eof
                    while (((c = look(++i)) != '*' || (c = look(i + 1)) != '/') && c != '\0') ;
                    continue;
                }
                // if char + 1 is @ input parameter
                if (c == '@')
                {
                    var readFromPos = i + 1;
                    var readPosLen = 0;
                    do
                    {
                        readPosLen++;
                        c = look(readFromPos + readPosLen);
                    } while (vaildChar(c));
                    sqlParameterNamesBuilder.Add(sqlText.Substring(readFromPos, readPosLen));
                    i += readPosLen;
                    continue;
                }
                // if char is [ then parse parameter
                if (c == '[')
                {
                    var readFromPos = i + 1;
                    var readPosLen = 0;
                    c = look(readFromPos);
                    string name;
                    string sqlName;
                    while (vaildChar(c))
                    {
                        readPosLen++;
                        c = look(readFromPos + readPosLen);
                    }
                    if (c == '\0')
                    {
                        continue;
                    }
                    if (readPosLen > 0)
                    {
                        name = sqlText.Substring(readFromPos, readPosLen);
                    }
                    else
                    {
                        continue;
                    }
                    var readFromNeg = i - 1;
                    var readNegLen = 0;
                    c = look(readFromNeg);
                    while (c == ' ')
                    {
                        c = look(--readFromNeg);
                    }
                    if (c == '\0')
                    {
                        continue;
                    }
                    c = look(readFromNeg);
                    while (vaildChar(c))
                    {
                        readNegLen++;
                        c = look(readFromNeg - readNegLen);
                    }
                    if (c == '\0')
                    {
                        continue;
                    }
                    if (readNegLen > 0)
                    {
                        sqlName = sqlText.Substring(readFromNeg - readNegLen + 1, readNegLen);
                    }
                    else
                    {
                        continue;
                    }
                    // TODO-aljaz don't need to remove from string and account that I probably don't need sqlNames if I don't remove
                    // Maybe keep for future if we want to resolve wildcards to objects
                    sqlText = sqlText.Remove(i, readPosLen + 2);
                    i--;
                    namesBuilder.Add(name);
                    sqlNamesBuilder.Add(sqlName);
                }
            }
            names = namesBuilder.ToImmutableArray();
            sqlNames = sqlNamesBuilder.ToImmutable();
            sqlParameterNames = sqlParameterNamesBuilder.ToImmutable();
            return sqlText;

            char look(int index)
            {
                if (index > sqlText.Length || index < 0)
                {
                    return '\0';
                }
                return sqlText[index];
            }
        }

        private static bool vaildChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }
}
