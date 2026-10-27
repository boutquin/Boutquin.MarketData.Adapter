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
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.TwelveData;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Calendars.Holidays;
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
    public void Empty_symbols_are_rejected_at_the_request_boundary()
    {
        Action act = () => new PriceHistoryRequest([], MakeRange(), DataFrequency.Daily);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("symbols.Count");
    }

    // ---------- Exchange-hint filtering ----------

    [Fact]
    public void CanHandle_returns_true_when_Exchange_is_null_preserving_back_compat()
    {
        var request = new PriceHistoryRequest([new Symbol("AAPL")], MakeRange());
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Theory]
    [InlineData(ExchangeCode.XTSE)]
    [InlineData(ExchangeCode.NEOE)]
    [InlineData(ExchangeCode.XNYS)]
    [InlineData(ExchangeCode.XNAS)]
    public void CanHandle_returns_true_for_supported_exchanges(ExchangeCode exchange)
    {
        // TwelveData covers CA primary (XTSE, NEOE) and US fallback (XNYS, XNAS); these
        // are the venues the etfwealthiq universe CSV declares.
        var request = new PriceHistoryRequest([new Symbol("XIC")], MakeRange()) { Exchange = exchange };
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Theory]
    [InlineData(ExchangeCode.XLON)]
    [InlineData(ExchangeCode.XPAR)]
    [InlineData(ExchangeCode.XETR)]
    public void CanHandle_returns_false_for_unsupported_exchanges(ExchangeCode exchange)
    {
        // TwelveData covers many global venues commercially, but this adapter's role in
        // the etfwealthiq pipeline is CA primary + US fallback; broader support should
        // be opted in explicitly when needed.
        var request = new PriceHistoryRequest([new Symbol("BARC")], MakeRange()) { Exchange = exchange };
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
        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("API_ERROR") && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public async Task FetchAsync_records_a_single_symbol_failure()
    {
        var transport = new FakeHttpDataTransport();
        // No response for AAPL — will throw
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], MakeRange()), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(issue => issue.Code == new IssueCode("FETCH_FAILED"));
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

    [Fact]
    public async Task FetchAsync_price_provenance_does_not_expose_api_key()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", TimeSeriesSingleBar);
        var result = await CreateAdapter(transport).FetchAsync(
            new PriceHistoryRequest([new Symbol("XIC")], MakeRange()), CancellationToken.None);

        transport.RequestedUris[0].Query.Should().Contain("apikey=test-key");
        result.Provenance.Should().ContainSingle().Which.SourceUrl.Should().NotContain("test-key");
    }

    [Fact]
    public async Task FetchAsync_price_raw_keys_distinguish_venues()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", TimeSeriesSingleBar);
        var rawStore = new FakeRawDocumentStore();
        var adapter = CreateAdapter(transport, rawStore);
        await adapter.FetchAsync(new PriceHistoryRequest([new Symbol("XIC")], MakeRange()) { Exchange = ExchangeCode.XTSE });
        await adapter.FetchAsync(new PriceHistoryRequest([new Symbol("XIC")], MakeRange()) { Exchange = ExchangeCode.NEOE });

        rawStore.SavedKeys.Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task FetchAsync_price_propagates_caller_cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(source.Token));
        var adapter = CreateAdapter(transport);

        Func<Task> act = () => adapter.FetchAsync(
            new PriceHistoryRequest([new Symbol("XIC")], MakeRange()), source.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_single_symbol_coverage_counts_its_own_gaps()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondDynamic(_ => true, uri =>
        {
            var symbol = System.Web.HttpUtility.ParseQueryString(uri.Query)["symbol"];
            return symbol == "AAPL"
                ? TwelveDataValues("2024-01-12", "2024-01-11", "2024-01-10", "2024-01-09", "2024-01-08")
                : TwelveDataValues("2024-01-08");
        });
        var result = await CreateAdapter(transport).FetchAsync(new PriceHistoryRequest(
            [new Symbol("MSFT")],
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12))));

        result.Coverage.RequestedPoints.Should().Be(5);
        result.Coverage.ReturnedPoints.Should().Be(1);
        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
    }

    [Fact]
    public async Task FetchAsync_uses_Canadian_calendar_for_XTSE_coverage()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", TwelveDataValues("2024-07-02"));
        var adapter = CreateAdapter(transport, calendar: HolidayCalendarFactory.Create("USNY"));
        var result = await adapter.FetchAsync(new PriceHistoryRequest(
            [new Symbol("XIC")],
            new DateRange(new DateOnly(2024, 7, 1), new DateOnly(2024, 7, 2)))
        { Exchange = ExchangeCode.XTSE });

        result.Coverage.RequestedPoints.Should().Be(1);
        result.Coverage.Status.Should().Be(CompletenessStatus.Complete);
    }

    // ---------- URL construction: mic_code query parameter ----------

    private const string TimeSeriesSingleBar = """
        {"status":"ok","values":[{"datetime":"2024-01-02","open":"54.52","high":"54.97","low":"54.20","close":"54.80","volume":"500000"}]}
        """;

    [Fact]
    public async Task FetchAsync_appends_mic_code_query_param_when_Exchange_is_provided()
    {
        // Same rationale as the metadata adapter: without mic_code, TwelveData picks an
        // arbitrary listing for a symbol that trades on multiple venues (e.g. XIC on
        // TSX + NEO). With mic_code=XTSE, TwelveData returns the TSX OHLC series.
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", TimeSeriesSingleBar);
        var adapter = CreateAdapter(transport);

        await adapter.FetchAsync(
            new PriceHistoryRequest([new Symbol("XIC")], MakeRange()) { Exchange = ExchangeCode.XTSE },
            CancellationToken.None);

        transport.RequestedUris[0].Query.Should().Contain("mic_code=XTSE");
    }

    [Fact]
    public async Task FetchAsync_encodes_the_ticker_as_one_query_value()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", TimeSeriesSingleBar);
        await CreateAdapter(transport).FetchAsync(
            new PriceHistoryRequest([new Symbol("XIC&mic_code=XNAS")], MakeRange()) { Exchange = ExchangeCode.XTSE },
            CancellationToken.None);

        var query = System.Web.HttpUtility.ParseQueryString(transport.RequestedUris[0].Query);
        query["symbol"].Should().Be("XIC&mic_code=XNAS");
        query.GetValues("mic_code").Should().ContainSingle().Which.Should().Be("XTSE");
    }

    [Fact]
    public async Task FetchAsync_asks_for_the_whole_last_day_of_the_range()
    {
        // TwelveData reads a date-only end_date as that day's midnight. Its daily bars since about
        // 2016 carry a later timestamp, so end_date=2026-09-21 answered through 2026-09-18 and the
        // range's last bar was silently missing (live probe, 2026-09-25: XIC/XTSE). The request's
        // range is inclusive, so the adapter must ask through the end of its last day.
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", TimeSeriesSingleBar);
        var adapter = CreateAdapter(transport);

        await adapter.FetchAsync(
            new PriceHistoryRequest([new Symbol("XIC")], MakeRange()) { Exchange = ExchangeCode.XTSE },
            CancellationToken.None);

        var query = System.Web.HttpUtility.ParseQueryString(transport.RequestedUris[0].Query);
        query["end_date"].Should().Be("2024-01-31 23:59:59");
    }

    [Fact]
    public async Task FetchAsync_omits_mic_code_when_Exchange_is_null_preserving_venue_neutral_semantics()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", TimeSeriesSingleBar);
        var adapter = CreateAdapter(transport);

        await adapter.FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], MakeRange()), CancellationToken.None);

        transport.RequestedUris[0].Query.Should().NotContain("mic_code");
    }

    [Theory]
    [InlineData(ExchangeCode.XTSE, "XTSE")]
    [InlineData(ExchangeCode.NEOE, "NEOE")]
    [InlineData(ExchangeCode.XNAS, "XNAS")]
    [InlineData(ExchangeCode.XNYS, "XNYS")]
    public async Task FetchAsync_renders_mic_code_param_as_ISO_MIC_matching_enum_member_name(
        ExchangeCode exchange, string expectedMicCode)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", TimeSeriesSingleBar);
        var adapter = CreateAdapter(transport);

        await adapter.FetchAsync(
            new PriceHistoryRequest([new Symbol("XIC")], MakeRange()) { Exchange = exchange },
            CancellationToken.None);

        transport.RequestedUris[0].Query.Should().Contain($"mic_code={expectedMicCode}");
    }

    // ---------- Pagination: TwelveData's 5000-bar outputsize cap ----------

    /// <summary>
    /// Simulates TwelveData's server-side 5000-bar cap: every page returns exactly 5000
    /// bars starting at <c>end_date</c> and walking backwards 1 day per bar (calendar-day
    /// approximation, not business-day — the adapter must not depend on perfect trading
    /// calendar alignment in the responder).
    /// </summary>
    private static string TimeSeriesPage(DateOnly pageEnd, int barsInPage)
    {
        var bars = new System.Text.StringBuilder("""{"status":"ok","values":[""");
        for (var i = 0; i < barsInPage; i++)
        {
            var d = pageEnd.AddDays(-i);
            if (i > 0)
            {
                bars.Append(',');
            }

            bars.Append(System.Globalization.CultureInfo.InvariantCulture,
                $$"""{"datetime":"{{d:yyyy-MM-dd}}","open":"100","high":"101","low":"99","close":"100.5","volume":"1000"}""");
        }
        bars.Append("]}");
        return bars.ToString();
    }

    [Fact]
    public async Task FetchAsync_paginates_backward_when_single_page_cannot_cover_the_requested_range()
    {
        // TwelveData caps a single /time_series response at 5000 bars (paid-tier hard
        // limit per https://twelvedata.com/docs/market-data/time-series). A 25-year CA
        // backtest range (e.g. XIC from 2001-06-14 to today) needs ~6300 trading days,
        // i.e. at least two pages. Regression: before the paging fix, the adapter issued
        // one request, got the 5000 most-recent bars (2008-2026), and the consumer's
        // coverage evaluator rejected the partial response — cascading to "Benchmark
        // variance is zero" and breaking every CA backtest that started pre-2008.
        var transport = new FakeHttpDataTransport();
        transport.RespondDynamic(
            matcher: uri => uri.AbsolutePath.Contains("time_series", StringComparison.Ordinal)
                         && uri.Query.Contains("XIC", StringComparison.Ordinal),
            body: uri =>
            {
                var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var endStr = q["end_date"] ?? "2026-04-17";
                // The date part only: end_date carries a time of day (see the whole-last-day test).
                var end = DateOnly.ParseExact(endStr[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                // Respond with 5000 calendar-day bars, then precisely the shorter tail
                // to the requested start. This synthetic fixture must cover its own request.
                var pageLen = end > new DateOnly(2020, 1, 1)
                    ? 5000
                    : end.DayNumber - new DateOnly(2001, 2, 15).DayNumber + 1;
                return TimeSeriesPage(end, pageLen);
            });
        var adapter = CreateAdapter(transport);

        var longRange = new DateRange(new DateOnly(2001, 2, 15), new DateOnly(2026, 4, 17));
        var result = await adapter.FetchAsync(
            new PriceHistoryRequest([new Symbol("XIC")], longRange) { Exchange = ExchangeCode.XTSE },
            CancellationToken.None);

        // Two requests issued (page 1 then page 2 with end_date = page1_first - 1 day).
        transport.RequestedUris.Count.Should().Be(2);
        // Both include mic_code.
        transport.RequestedUris.Should().AllSatisfy(u => u.Query.Should().Contain("mic_code=XTSE"));
        result.Payload.Count.Should().Be(longRange.To.DayNumber - longRange.From.DayNumber + 1);
        result.Payload[0].Date.Should().Be(longRange.From);
        result.Payload[^1].Date.Should().Be(longRange.To);
        // Payload should be de-duplicated (no overlap between pages) and cover across
        // the page boundary.
        var dates = result.Payload.Select(b => b.Date).ToHashSet();
        dates.Count.Should().Be(result.Payload.Count, "payload bars should be unique dates across pages");
    }

    [Fact]
    public async Task FetchAsync_stops_paging_after_single_page_when_range_fits()
    {
        // Short ranges (≤5000 bars) fit in one page — no second request. Pagination must
        // not fire unconditionally, only when the server returned a full 5000-bar page
        // AND the oldest bar is still > requestedStart.
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", TimeSeriesPage(new DateOnly(2024, 1, 31), 30));
        var adapter = CreateAdapter(transport);

        await adapter.FetchAsync(
            new PriceHistoryRequest([new Symbol("AAPL")], MakeRange()),
            CancellationToken.None);

        transport.RequestedUris.Count.Should().Be(1);
    }

    // ---------- Calendar-aware coverage (feature-calendar-aware-coverage) ----------

    private static string TwelveDataValues(params string[] dates)
    {
        var sb = new System.Text.StringBuilder("""{"status":"ok","values":[""");
        for (var i = 0; i < dates.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $$"""{"datetime":"{{dates[i]}}","open":"100","high":"101","low":"99","close":"100.5","volume":"1000"}""");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    [Fact]
    public async Task FetchAsync_business_day_range_reports_complete_coverage()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", TwelveDataValues(
            "2024-01-12", "2024-01-11", "2024-01-10", "2024-01-09", "2024-01-08"));
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
    public async Task FetchAsync_weekend_only_range_reports_no_unexpected_gaps()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", """{"status":"ok","values":[]}""");
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
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", TwelveDataValues(
            "2024-01-19", "2024-01-18", "2024-01-17", "2024-01-16"));
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
        transport.RespondTo("time_series", TwelveDataValues(
            "2024-01-10", "2024-01-09", "2024-01-08"));
        var adapter = CreateAdapter(transport);

        var request = new PriceHistoryRequest(
            [new Symbol("AAPL")],
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
        var gaps = result.Issues.Where(i => i.Code == new IssueCode("UNEXPECTED_GAP")).ToList();
        gaps.Should().HaveCount(2);
    }

    [Fact]
    public async Task FetchAsync_excludes_bars_outside_requested_range_from_payload_and_coverage()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", TwelveDataValues("2024-01-05", "2024-01-02", "2024-01-01"));
        var request = new PriceHistoryRequest(
            [new Symbol("AAPL")],
            new DateRange(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4)));

        var result = await CreateAdapter(transport).FetchAsync(request);

        result.Payload.Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Coverage.RequestedPoints.Should().Be(3);
        result.Coverage.ReturnedPoints.Should().Be(1);
        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
        result.Issues.Count(i => i.Code == new IssueCode("UNEXPECTED_GAP")).Should().Be(2);
    }
}
