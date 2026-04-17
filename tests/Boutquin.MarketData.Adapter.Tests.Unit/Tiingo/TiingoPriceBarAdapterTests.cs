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
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.Tiingo;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.Tiingo;

public sealed class TiingoPriceBarAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static TiingoPriceBarAdapter CreateAdapter(
        FakeHttpDataTransport transport,
        FakeRawDocumentStore? rawStore = null,
        TiingoOptions? options = null,
        IBusinessCalendar? calendar = null)
    {
        return new TiingoPriceBarAdapter(
            transport,
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(options ?? new TiingoOptions { ApiToken = "test-token" }),
            NullLogger<TiingoPriceBarAdapter>.Instance);
    }

    private static DateRange MakeRange(int year = 2024, int fromMonth = 1, int fromDay = 1, int toMonth = 1, int toDay = 31) =>
        new(new DateOnly(year, fromMonth, fromDay), new DateOnly(year, toMonth, toDay));

    [Fact]
    public void ProviderKey_returns_tiingo() =>
        CreateAdapter(new FakeHttpDataTransport()).ProviderKey.Should().Be(new ProviderCode("tiingo"));

    [Fact]
    public void Priority_returns_100() =>
        CreateAdapter(new FakeHttpDataTransport()).Priority.Should().Be(100);

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
            [
                {"date":"2024-01-02T00:00:00+00:00","open":185.50,"high":187.30,"low":184.20,"close":186.90,"adjClose":186.90,"volume":52000000},
                {"date":"2024-01-03T00:00:00+00:00","open":186.80,"high":188.10,"low":185.50,"close":187.50,"adjClose":187.50,"volume":48000000}
            ]
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/AAPL", json);
        var rawStore = new FakeRawDocumentStore();
        var adapter = CreateAdapter(transport, rawStore);

        var request = new PriceHistoryRequest([new Symbol("AAPL")], MakeRange());
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().HaveCount(2);
        result.Payload[0].Should().Be(new Bar(new DateOnly(2024, 1, 2), 185.50m, 187.30m, 184.20m, 186.90m, 186.90m, 52000000));
        result.Payload[1].Should().Be(new Bar(new DateOnly(2024, 1, 3), 186.80m, 188.10m, 185.50m, 187.50m, 187.50m, 48000000));
        rawStore.SavedKeys.Should().ContainSingle();
    }

    [Fact]
    public async Task FetchAsync_defaults_adjClose_to_close_when_null()
    {
        var json = """
            [{"date":"2024-01-02T00:00:00+00:00","open":100.0,"high":101.0,"low":99.0,"close":100.5,"adjClose":null,"volume":1000}]
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/AAPL", json);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], MakeRange()), CancellationToken.None);

        result.Payload[0].AdjustedClose.Should().Be(100.5m);
    }

    [Fact]
    public async Task FetchAsync_defaults_volume_to_zero_when_null()
    {
        var json = """
            [{"date":"2024-01-02T00:00:00+00:00","open":100.0,"high":101.0,"low":99.0,"close":100.5,"adjClose":100.5,"volume":null}]
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/AAPL", json);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], MakeRange()), CancellationToken.None);

        result.Payload[0].Volume.Should().Be(0);
    }

    [Fact]
    public async Task FetchAsync_continues_on_per_symbol_failure()
    {
        var json = """
            [{"date":"2024-01-02T00:00:00+00:00","open":100.0,"high":101.0,"low":99.0,"close":100.5,"adjClose":100.5,"volume":1000}]
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("MSFT", json);
        // No response for AAPL — will throw HttpRequestException
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new PriceHistoryRequest([new Symbol("AAPL"), new Symbol("MSFT")], MakeRange()), CancellationToken.None);

        // MSFT should succeed, AAPL should fail gracefully
        result.Payload.Should().HaveCount(1);
        result.Issues.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FetchAsync_populates_provenance()
    {
        var json = """[{"date":"2024-01-02T00:00:00+00:00","open":1,"high":2,"low":0.5,"close":1.5,"adjClose":1.5,"volume":100}]""";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo", json);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new PriceHistoryRequest([new Symbol("X")], MakeRange()), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        result.Provenance[0].Provider.Should().Be(new ProviderCode("tiingo"));
    }
}
