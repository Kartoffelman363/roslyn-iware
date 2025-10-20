// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#pragma warning disable RS0016 // Add public types and members to the declared API

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SqlVerifier
{
    public enum TokenType
    {
        Input,
        Output,
        Text
    }

    //TODO-aljaz not interfaces
    public class VerifierToken
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

    public class VerifierInput
    {
        public string ConfigPath { get; set; }
        public string SqlString { get; set; }
        public VerifierToken[] Tokens { get; set; }
    }

    public class VerifierTokenDiagnostic
    {
        public int Index { get; set; }
        public bool IsError { get; set; }
        public bool IsWarning { get; set; }
        public string? Text { get; set; }
    }

    public class VerifierOutput
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }
        [JsonPropertyName("message")]
        public string Message { get; set; }
        [JsonPropertyName("diagnostics")]
        public List<VerifierTokenDiagnostic> Diagnostics { get; set; }

        public VerifierOutput()
        {
            Diagnostics = new List<VerifierTokenDiagnostic>();
            Message = "";
        }

        public static VerifierOutput Ok()
        {
            return new VerifierOutput() { Success = true };
        }

        public static VerifierOutput Unknown()
        {
            return new VerifierOutput() { Success = false, Message = "Unknown" };
        }

        public static VerifierOutput Error(string message)
        {
            return new VerifierOutput() { Success = false, Message = message };
        }
    }
}
