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
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.Tiingo;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Calendars.Holidays;
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
    public void Empty_symbols_are_rejected_at_the_request_boundary()
    {
        Action act = () => new PriceHistoryRequest([], MakeRange(), DataFrequency.Daily);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("symbols.Count");
    }

    // ---------- Exchange-hint filtering (CA research regression defense) ----------

    [Fact]
    public void CanHandle_returns_true_when_Exchange_is_null_preserving_back_compat()
    {
        // Callers that do not set Exchange must continue to work — this keeps every
        // existing consumer unaffected by the 1.1.0 Exchange-hint addition.
        var request = new PriceHistoryRequest([new Symbol("AAPL")], MakeRange());
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Theory]
    [InlineData(ExchangeCode.XNYS)]
    [InlineData(ExchangeCode.XNAS)]
    public void CanHandle_returns_true_for_supported_US_exchanges(ExchangeCode exchange)
    {
        var request = new PriceHistoryRequest([new Symbol("AAPL")], MakeRange()) { Exchange = exchange };
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Theory]
    [InlineData(ExchangeCode.XTSE)]
    [InlineData(ExchangeCode.NEOE)]
    [InlineData(ExchangeCode.XLON)]
    [InlineData(ExchangeCode.XPAR)]
    public void CanHandle_returns_false_for_non_US_exchanges(ExchangeCode exchange)
    {
        // 2026-04-19 regression: bare "XIC" (Exchange: null) went to Tiingo and returned
        // Cimarex Energy Company — an unrelated US equity that shares the 3-letter symbol
        // with iShares S&P/TSX Capped Composite ETF. With the Exchange hint set to XTSE,
        // the adapter now self-excludes from CanHandle and the orchestrator routes to
        // TwelveData instead of returning garbage.
        var request = new PriceHistoryRequest([new Symbol("XIC")], MakeRange()) { Exchange = exchange };
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

    [Theory]
    [InlineData("../iex/price")]
    [InlineData("AAPL/MSFT")]
    [InlineData("AAPL?token=attacker")]
    public async Task FetchAsync_rejects_provider_invalid_ticker_before_transport_or_raw_persistence(string ticker)
    {
        var transport = new FakeHttpDataTransport();
        var rawStore = new FakeRawDocumentStore();
        var adapter = CreateAdapter(transport, rawStore);

        Func<Task> act = () => adapter.FetchAsync(
            new PriceHistoryRequest([new Symbol(ticker)], MakeRange()),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("request");
        transport.RequestedUris.Should().BeEmpty();
        rawStore.SavedKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_rejects_ticker_exceeding_the_raw_store_segment_boundary()
    {
        var transport = new FakeHttpDataTransport();
        var rawStore = new FakeRawDocumentStore();
        var adapter = CreateAdapter(transport, rawStore);

        Func<Task> act = () => adapter.FetchAsync(
            new PriceHistoryRequest([new Symbol(new string('A', 251))], MakeRange()),
            CancellationToken.None);

        await act.Should().ThrowExactlyAsync<ArgumentException>()
            .WithParameterName("request");
        transport.RequestedUris.Should().BeEmpty();
        rawStore.SavedKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_accepts_ticker_at_the_raw_store_segment_boundary()
    {
        var ticker = new string('A', 250);
        var transport = new FakeHttpDataTransport();
        transport.RespondTo($"tiingo/daily/{ticker}/prices", "[]");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new PriceHistoryRequest([new Symbol(ticker)], MakeRange()),
            CancellationToken.None);

        transport.RequestedUris.Should().ContainSingle();
        result.Issues.Should().NotContain(issue => issue.Code == new IssueCode("FETCH_FAILED"));
    }

    [Theory]
    [InlineData("BRK-B")]
    [InlineData("BF.B")]
    public async Task FetchAsync_preserves_legitimate_Tiingo_ticker_endpoint(string ticker)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo($"tiingo/daily/{ticker}/prices", "[]");
        var adapter = CreateAdapter(transport);

        await adapter.FetchAsync(
            new PriceHistoryRequest([new Symbol(ticker)], MakeRange()),
            CancellationToken.None);

        transport.RequestedUris.Should().ContainSingle()
            .Which.AbsolutePath.Should().Be($"/tiingo/daily/{ticker}/prices");
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
    public async Task FetchAsync_records_a_single_symbol_failure()
    {
        var transport = new FakeHttpDataTransport();
        // No response for AAPL — will throw HttpRequestException
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], MakeRange()), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(issue => issue.Code == new IssueCode("FETCH_FAILED"));
    }

    [Fact]
    public async Task FetchAsync_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(cancellation.Token));
        var adapter = CreateAdapter(transport);

        Func<Task> act = () => adapter.FetchAsync(
            new PriceHistoryRequest([new Symbol("AAPL")], MakeRange()), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_keeps_transport_exception_detail_out_of_consumer_issues()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new IOException("credential=secret; path=/srv/private/raw.json"));
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new PriceHistoryRequest([new Symbol("AAPL")], MakeRange()), CancellationToken.None);

        result.Issues.Should().ContainSingle(issue => issue.Code == new IssueCode("FETCH_FAILED"))
            .Which.Message.Should().Be("Failed to fetch data for symbol 'AAPL'.");
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

    // ---------- Calendar-aware coverage (feature-calendar-aware-coverage) ----------

    private static string TiingoBarJson(params (string date, decimal close)[] bars)
    {
        var sb = new System.Text.StringBuilder("[");
        for (var i = 0; i < bars.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $$"""{"date":"{{bars[i].date}}T00:00:00+00:00","open":100,"high":{{Math.Max(101m, bars[i].close)}},"low":{{Math.Min(99m, bars[i].close)}},"close":{{bars[i].close}},"adjClose":{{bars[i].close}},"volume":1000}""");
        }
        sb.Append(']');
        return sb.ToString();
    }

    [Fact]
    public async Task FetchAsync_business_day_range_reports_complete_coverage()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("AAPL", TiingoBarJson(
            ("2024-01-08", 100m), ("2024-01-09", 100m), ("2024-01-10", 100m),
            ("2024-01-11", 100m), ("2024-01-12", 100m)));
        var adapter = CreateAdapter(transport);

        var request = new PriceHistoryRequest(
            [new Symbol("AAPL")],
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(5);
        result.Coverage.ReturnedPoints.Should().Be(5);
        result.Coverage.Status.Should().Be(CompletenessStatus.Complete);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_excludes_provider_bars_outside_inclusive_request_range()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("AAPL", TiingoBarJson(
            ("2024-01-07", 100m), ("2024-01-08", 101m),
            ("2024-01-12", 102m), ("2024-01-15", 103m)));
        var adapter = CreateAdapter(transport);
        var request = new PriceHistoryRequest(
            [new Symbol("AAPL")],
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Select(bar => bar.Date).Should().Equal(
            new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12));
        result.Provenance.Should().ContainSingle().Which.DataDate.Should().Be(new DateOnly(2024, 1, 12));
    }

    [Fact]
    public async Task FetchAsync_weekend_only_range_reports_no_unexpected_gaps()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("AAPL", "[]");
        var adapter = CreateAdapter(transport);

        var request = new PriceHistoryRequest(
            [new Symbol("AAPL")],
            new DateRange(new DateOnly(2024, 1, 6), new DateOnly(2024, 1, 7)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(0);
        result.Coverage.CoverageRatio.Should().Be(1.0m);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_holiday_within_range_is_not_flagged_as_gap()
    {
        // MLK Day 2024 = Mon Jan 15 (NYSE closed, USNY holiday).
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("AAPL", TiingoBarJson(
            ("2024-01-16", 100m), ("2024-01-17", 100m),
            ("2024-01-18", 100m), ("2024-01-19", 100m)));
        var usny = HolidayCalendarFactory.Create("USNY");
        var adapter = CreateAdapter(transport, calendar: usny);

        var request = new PriceHistoryRequest(
            [new Symbol("AAPL")],
            new DateRange(new DateOnly(2024, 1, 15), new DateOnly(2024, 1, 19)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(4);
        result.Coverage.ReturnedPoints.Should().Be(4);
        result.Coverage.Status.Should().Be(CompletenessStatus.Complete);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_missing_business_day_emits_unexpected_gap()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("AAPL", TiingoBarJson(
            ("2024-01-08", 100m), ("2024-01-09", 100m), ("2024-01-10", 100m)));
        var adapter = CreateAdapter(transport);

        var request = new PriceHistoryRequest(
            [new Symbol("AAPL")],
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
        var gaps = result.Issues.Where(i => i.Code == new IssueCode("UNEXPECTED_GAP")).ToList();
        gaps.Should().HaveCount(2);
    }
}
