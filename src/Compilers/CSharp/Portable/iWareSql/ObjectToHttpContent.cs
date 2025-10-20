// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SqlVerifier;

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    internal class ObjectToHttpContent : HttpContent
    {
        private readonly MemoryStream _ms = new MemoryStream();

        public ObjectToHttpContent(object val)
        {
            JsonSerializer.Serialize(_ms, val);
            _ms.Position = 0;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return _ms.CopyToAsync(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _ms.Length;
            return true;
        }

        internal static void HttpContentToObject(HttpContent content, out VerifierOutput? value)
        {
            value = null;
            var stream = content.ReadAsStreamAsync().Result;
            if (stream == null)
            {
                return;
            }
            stream.Position = 0;
            value = JsonSerializer.Deserialize<VerifierOutput>(stream);
        }
    }
}
