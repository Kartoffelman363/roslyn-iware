// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Collections.Generic;

namespace SqlVerifier
{
    public enum TokenType
    {
        Input,
        Output,
        Text
    }

    //TODO-aljaz not interfaces
    internal class VerifierToken
    {
        public string TokenText { get; set; }
        public TokenType TokenType { get; set; }
        public int Index { get; set; }
        public VerifierToken(string tokenText, TokenType tokenType, int index)
        {
            TokenText = tokenText;
            TokenType = tokenType;
            Index = index;
        }

        public static VerifierToken UnreachableVerifierToken()
        {
            Debug.Fail("Unreachable token");
            return new VerifierToken("Unknown", TokenType.Text, -1);
        }
    }

    internal class VerifierInput
    {
        public string ConfigPath { get; set; }
        public string SqlString { get; set; }
        public VerifierToken[] Tokens { get; set; }
    }

    internal class VerifierTokenDiagnostic
    {
        public int Index { get; set; }
        public bool IsError { get; set; }
        public bool IsWarning { get; set; }
        public string Text { get; set; }
    }

    internal class VerifierOutput
    {
        public List<VerifierTokenDiagnostic> Diagnostics { get; set; }
        public VerifierOutput()
        {
            Diagnostics = new List<VerifierTokenDiagnostic>();
        }
    }
}
