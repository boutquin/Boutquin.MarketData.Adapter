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
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.TwelveData;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.TwelveData;

public sealed class TwelveDataPriceBarAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static TwelveDataPriceBarAdapter CreateAdapter(
        FakeHttpDataTransport transport,
        FakeRawDocumentStore? rawStore = null,
        TwelveDataOptions? options = null,
        IBusinessCalendar? calendar = null)
    {
        return new TwelveDataPriceBarAdapter(
            transport,
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(options ?? new TwelveDataOptions { ApiKey = "test-key" }),
            NullLogger<TwelveDataPriceBarAdapter>.Instance);
    }

    private static DateRange MakeRange(int year = 2024, int fromMonth = 1, int fromDay = 1, int toMonth = 1, int toDay = 31) =>
        new(new DateOnly(year, fromMonth, fromDay), new DateOnly(year, toMonth, toDay));

    [Fact]
    public void ProviderKey_returns_twelvedata() =>
        CreateAdapter(new FakeHttpDataTransport()).ProviderKey.Should().Be(new ProviderCode("twelvedata"));

    [Fact]
    public void Priority_returns_50() =>
        CreateAdapter(new FakeHttpDataTransport()).Priority.Should().Be(50);

    [Fact]
    public void CanHandle_returns_true_for_daily_with_symbols()
    {
        var request = new PriceHistoryRequest([new Symbol("AAPL")], MakeRange(), DataFrequency.Daily);
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_returns_false_for_non_daily_frequency()
    {
        var request = new PriceHistoryRequest([new Symbol("AAPL")], MakeRange(), DataFrequency.Weekly);
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
    }

    [Fact]
    public void CanHandle_returns_false_for_empty_symbols()
    {
        var request = new PriceHistoryRequest([], MakeRange(), DataFrequency.Daily);
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
    }

    [Fact]
    public async Task FetchAsync_parses_json_into_bars()
    {
        var json = """
            {"status":"ok","values":[{"datetime":"2024-01-02","open":"195.50","high":"197.30","low":"195.40","close":"197.20","volume":"52000000"}]}
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("twelvedata", json);
        var rawStore = new FakeRawDocumentStore();
        var adapter = CreateAdapter(transport, rawStore);

        var request = new PriceHistoryRequest([new Symbol("AAPL")], MakeRange());
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().ContainSingle();
        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[0].Open.Should().Be(195.50m);
        result.Payload[0].High.Should().Be(197.30m);
        result.Payload[0].Low.Should().Be(195.40m);
        result.Payload[0].Close.Should().Be(197.20m);
        // AdjustedClose = Close for Twelve Data
        result.Payload[0].AdjustedClose.Should().Be(197.20m);
        result.Payload[0].Volume.Should().Be(52000000);
    }

    [Fact]
    public async Task FetchAsync_returns_empty_payload_with_issue_on_error_response()
    {
        var json = """{"status":"error","message":"API key invalid"}""";

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("twelvedata", json);
        var adapter = CreateAdapter(transport);

        var request = new PriceHistoryRequest([new Symbol("AAPL")], MakeRange());
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FetchAsync_continues_on_per_symbol_failure()
    {
        var json = """
            {"status":"ok","values":[{"datetime":"2024-01-02","open":"100","high":"101","low":"99","close":"100.5","volume":"1000"}]}
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("MSFT", json);
        // No response for AAPL — will throw
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new PriceHistoryRequest([new Symbol("AAPL"), new Symbol("MSFT")], MakeRange()), CancellationToken.None);

        result.Payload.Should().HaveCount(1);
        result.Issues.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FetchAsync_populates_provenance()
    {
        var json = """{"status":"ok","values":[{"datetime":"2024-01-02","open":"1","high":"2","low":"0.5","close":"1.5","volume":"100"}]}""";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("twelvedata", json);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new PriceHistoryRequest([new Symbol("X")], MakeRange()), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        result.Provenance[0].Provider.Should().Be(new ProviderCode("twelvedata"));
    }
}
