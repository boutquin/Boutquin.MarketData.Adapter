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
/// Answers Twelve Data <c>/dividends</c> and <c>/splits</c> requests with canned bodies (an empty list by
/// default) and forwards every other request to the wrapped transport. Price-focused tests use it so the
/// wrapped transport sees only <c>/time_series</c> traffic.
/// </summary>
internal sealed class TwelveDataFundamentalsStubTransport(
    IHttpDataTransport inner,
    string dividendsJson = TwelveDataFundamentalsStubTransport.NoDividends,
    string splitsJson = TwelveDataFundamentalsStubTransport.NoSplits) : IHttpDataTransport
{
    public const string NoDividends = """{"dividends":[]}""";
    public const string NoSplits = """{"splits":[]}""";

    private readonly List<Uri> _fundamentalsUris = [];

    /// <summary>The intercepted <c>/dividends</c> and <c>/splits</c> requests, in order.</summary>
    public IReadOnlyList<Uri> FundamentalsUris => _fundamentalsUris.ToArray();

    public Task<Stream> GetAsync(Uri uri, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        var path = uri.AbsolutePath;
        if (path.EndsWith("/dividends", StringComparison.Ordinal) || path.EndsWith("/splits", StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _fundamentalsUris.Add(uri);
            var body = path.EndsWith("/dividends", StringComparison.Ordinal) ? dividendsJson : splitsJson;
            return Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
        }

        return inner.GetAsync(uri, headers, cancellationToken);
    }
}
