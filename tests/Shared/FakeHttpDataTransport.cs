// Copyright (c) 2026 Pierre G. Boutquin. All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License").
//  You may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//  See the License for the specific language governing permissions and
//  limitations under the License.
//

using Boutquin.MarketData.Transport.Http;

namespace Boutquin.MarketData.Adapter.Tests.Shared;

/// <summary>
/// Fake HTTP transport that maps URL substrings to canned responses.
/// </summary>
internal sealed class FakeHttpDataTransport : IHttpDataTransport
{
    private readonly Dictionary<string, byte[]> _responses = new(StringComparer.OrdinalIgnoreCase);
    private Exception? _throwOnNext;

    /// <summary>
    /// Register a canned response for any URL containing the given substring.
    /// </summary>
    public void RespondTo(string urlSubstring, string content)
    {
        _responses[urlSubstring] = System.Text.Encoding.UTF8.GetBytes(content);
    }

    /// <summary>
    /// Register a canned byte[] response for any URL containing the given substring.
    /// </summary>
    public void RespondToWithBytes(string urlSubstring, byte[] content)
    {
        _responses[urlSubstring] = content;
    }

    /// <summary>
    /// Make the next call throw the given exception.
    /// </summary>
    public void ThrowOnNext(Exception exception) => _throwOnNext = exception;

    public Task<Stream> GetAsync(Uri uri, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        if (_throwOnNext is not null)
        {
            var ex = _throwOnNext;
            _throwOnNext = null;
            throw ex;
        }

        var url = uri.ToString();
        foreach (var (substring, bytes) in _responses)
        {
            if (url.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<Stream>(new MemoryStream(bytes));
            }
        }

        throw new HttpRequestException($"No canned response for URL: {url}");
    }
}
