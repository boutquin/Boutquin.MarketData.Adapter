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
    private readonly List<Uri> _requestedUris = [];
    private readonly List<(Func<Uri, bool> Match, Func<Uri, byte[]> Body)> _dynamicResponders = [];
    private Exception? _throwOnNext;

    /// <summary>
    /// URIs that <see cref="GetAsync"/> was called with, in order. Tests can assert on the
    /// exact URL shape (query parameters, path, ordering) — e.g. that <c>mic_code=XTSE</c>
    /// was appended when <c>request.Exchange</c> was set.
    /// </summary>
    public IReadOnlyList<Uri> RequestedUris => _requestedUris.ToArray();

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
        _responses[urlSubstring] = content.ToArray();
    }

    /// <summary>
    /// Register a dynamic responder that produces content based on the requested URI. Use
    /// when tests need to vary the response by query parameter (e.g. pagination via
    /// <c>end_date</c>), or to simulate stateful sequences. Responders are checked in
    /// registration order before the static <see cref="RespondTo"/> table.
    /// </summary>
    public void RespondDynamic(Func<Uri, bool> matcher, Func<Uri, string> body)
    {
        _dynamicResponders.Add((matcher, uri => System.Text.Encoding.UTF8.GetBytes(body(uri))));
    }

    /// <summary>
    /// Make the next call throw the given exception.
    /// </summary>
    public void ThrowOnNext(Exception exception) => _throwOnNext = exception;

    public Task<Stream> GetAsync(Uri uri, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requestedUris.Add(uri);

        if (_throwOnNext is not null)
        {
            var ex = _throwOnNext;
            _throwOnNext = null;
            throw ex;
        }

        foreach (var (match, body) in _dynamicResponders)
        {
            if (match(uri))
            {
                return Task.FromResult<Stream>(new MemoryStream(body(uri)));
            }
        }

        var url = uri.ToString();
        foreach (var (substring, bytes) in _responses)
        {
            if (url.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
            }
        }

        throw new HttpRequestException($"No canned response for URL: {url}");
    }
}
