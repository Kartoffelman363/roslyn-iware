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
            out ImmutableArray<string> outputNames,
            out ImmutableArray<string> inputNames,
            string sqlText)
        {
            var outputNamesBuilder = ImmutableArray.CreateBuilder<string>();
            var inputNamesBuilder = ImmutableArray.CreateBuilder<string>();
            for (int i = 0; i < sqlText.Length; i++)
            {
                var c = look(i);
                // Single line comment
                if (c == '-' && (c = look(++i)) == '-')
                {
                    // read untill newline or eof
                    while ((c = look(++i)) != '\n' && c != '\0') ;
                    continue;
                }
                // Multi line comment
                if (c == '/' && (c = look(++i)) == '*')
                {
                    // read untill */ or eof
                    while (((c = look(++i)) != '*' || (c = look(i + 1)) != '/') && c != '\0') ;
                    continue;
                }
                // if char is @ then input parameter
                if (c == '@')
                {
                    var name = readParameterName(ref i);
                    if (name != string.Empty)
                    {
                        inputNamesBuilder.Add(name);
                    }
                    continue;
                }
                // if char is [ then output parameter
                if (c == '[')
                {
                    var name = readParameterName(ref i);
                    if (name != string.Empty)
                    {
                        outputNamesBuilder.Add(name);
                    }
                    continue;
                }
            }
            outputNames = outputNamesBuilder.ToImmutableArray();
            inputNames = inputNamesBuilder.ToImmutable();
            return sqlText;

            char look(int index)
            {
                if (index > sqlText.Length || index < 0)
                {
                    return '\0';
                }
                return sqlText[index];
            }

            string readParameterName(ref int i)
            {
                var readFromPos = i + 1;
                var readPosLen = 0;
                var c = look(readFromPos);
                do
                {
                    readPosLen++;
                    c = look(readFromPos + readPosLen);
                } while (vaildChar(c));
                if (c == '\0')
                {
                    return string.Empty;
                }
                if (readPosLen > 0)
                {
                    i += readPosLen + 1;
                    return sqlText.Substring(readFromPos, readPosLen);
                }
                else
                {
                    return string.Empty;
                }
            }
        }

        private static bool vaildChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }
}
