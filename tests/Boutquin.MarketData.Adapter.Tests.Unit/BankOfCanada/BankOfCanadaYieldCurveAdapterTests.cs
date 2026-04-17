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

using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.BankOfCanada;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.BankOfCanada;

public sealed class BankOfCanadaYieldCurveAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static BankOfCanadaYieldCurveAdapter CreateAdapter(
        FakeHttpDataTransport transport,
        FakeRawDocumentStore? rawStore = null,
        BankOfCanadaOptions? options = null,
        IBusinessCalendar? calendar = null)
    {
        return new BankOfCanadaYieldCurveAdapter(
            transport,
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(options ?? new BankOfCanadaOptions()),
            NullLogger<BankOfCanadaYieldCurveAdapter>.Instance);
    }

    [Fact]
    public void ProviderKey_returns_bankofcanada() =>
        CreateAdapter(new FakeHttpDataTransport()).ProviderKey.Should().Be(new ProviderCode("bankofcanada"));

    [Fact]
    public void Priority_returns_100() =>
        CreateAdapter(new FakeHttpDataTransport()).Priority.Should().Be(100);

    [Fact]
    public void CanHandle_returns_true()
    {
        var request = new YieldCurveQuoteRequest(new YieldCurveId("CAD-ZERO"), new DateOnly(2024, 1, 15));
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public async Task FetchAsync_parses_json_into_yield_curve_quotes()
    {
        var json = """
            {"observations":[{"d":"2024-01-15","BD.CDN.ZERO.3M":{"v":"4.50"},"BD.CDN.ZERO.1Y":{"v":"4.25"}}]}
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofcanada", json);
        var adapter = CreateAdapter(transport);

        var request = new YieldCurveQuoteRequest(new YieldCurveId("CAD-ZERO"), new DateOnly(2024, 1, 15));
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().HaveCount(2);
        result.Payload.Should().Contain(new YieldCurveQuote("3M", 0.045m));
        result.Payload.Should().Contain(new YieldCurveQuote("1Y", 0.0425m));
    }

    [Fact]
    public async Task FetchAsync_populates_provenance()
    {
        var json = """{"observations":[{"d":"2024-01-15","BD.CDN.ZERO.3M":{"v":"4.50"}}]}""";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofcanada", json);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("CAD-ZERO"), new DateOnly(2024, 1, 15)), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        result.Provenance[0].Provider.Should().Be(new ProviderCode("bankofcanada"));
    }
}
