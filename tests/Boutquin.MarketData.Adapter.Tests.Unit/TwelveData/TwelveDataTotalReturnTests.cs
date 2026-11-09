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

using System.Globalization;
using System.Text;

using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.TwelveData;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.TwelveData;

/// <summary>
/// Total-return computation: Close stays the split-adjusted price, AdjustedClose is computed from the
/// /dividends cash list, and every dividend or split failure fails closed.
/// </summary>
public sealed class TwelveDataTotalReturnTests
{
    private static readonly FakeClock s_clock = new();
    private static readonly IssueCode s_fetchFailed = new("FETCH_FAILED");

    private static TwelveDataPriceBarAdapter CreateAdapter(FakeHttpDataTransport transport, FakeRawDocumentStore? store = null) =>
        new(transport, store ?? new FakeRawDocumentStore(), s_clock, new WeekendOnlyCalendar("TEST"),
            Options.Create(new TwelveDataOptions { ApiKey = "test-key" }), NullLogger<TwelveDataPriceBarAdapter>.Instance);

    private static bool IsPath(Uri uri, string endpoint) => uri.AbsolutePath.EndsWith("/" + endpoint, StringComparison.Ordinal);

    private static FakeHttpDataTransport Transport(string timeSeries, string? dividends, string? splits = null)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondDynamic(u => IsPath(u, "time_series"), _ => timeSeries);
        if (dividends is not null)
        {
            transport.RespondDynamic(u => IsPath(u, "dividends"), _ => dividends);
        }

        if (splits is not null)
        {
            transport.RespondDynamic(u => IsPath(u, "splits"), _ => splits);
        }

        return transport;
    }

    /// <summary>Closes keyed by ISO date, emitted newest first like Twelve Data; OHLC bracket the close.</summary>
    private static string Series(params (string Date, string Close)[] bars)
    {
        var sb = new StringBuilder("""{"status":"ok","values":[""");
        var first = true;
        foreach (var (date, close) in bars.OrderByDescending(b => b.Date, StringComparer.Ordinal))
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append(CultureInfo.InvariantCulture,
                $$"""{"datetime":"{{date}}","open":"{{close}}","high":"{{close}}","low":"{{close}}","close":"{{close}}","volume":"1000"}""");
        }

        return sb.Append("]}").ToString();
    }

    private static string Dividends(params (string ExDate, string Amount)[] rows) =>
        "{\"dividends\":[" + string.Join(',', rows.Select(r => $$"""{"ex_date":"{{r.ExDate}}","amount":{{r.Amount}}}""")) + "]}";

    private static PriceHistoryRequest Request(string from, string to, ExchangeCode? exchange = null) =>
        new([new Symbol("XEQT")], new DateRange(DateOnly.Parse(from, CultureInfo.InvariantCulture), DateOnly.Parse(to, CultureInfo.InvariantCulture)))
        {
            Basis = TwelveDataPriceBarAdapter.DeliveredPriceBasis,
            Exchange = exchange,
        };

    private static double OneDayAdjustedReturn(IReadOnlyList<Bar> bars, string exDate)
    {
        var index = bars.ToList().FindIndex(b => b.Date == DateOnly.Parse(exDate, CultureInfo.InvariantCulture));
        return (double)(bars[index].AdjustedClose / bars[index - 1].AdjustedClose) - 1d;
    }

    // ---------- Hand-checked arithmetic ----------

    [Fact]
    public async Task Adjusted_closes_match_hand_computed_factors_and_close_stays_split_adjusted()
    {
        // Closes 100, 99 (ex 2.00), 98 (ex 1.00), 98. Factors: 99/101 at 01-09 and 98/99 at 01-10.
        // AdjustedClose: 01-11 = 98; 01-10 = 98; 01-09 = 99 * 98/99 = 98; 01-08 = 100 * 98/99 * 99/101 = 97.0297...
        var transport = Transport(
            Series(("2024-01-08", "100"), ("2024-01-09", "99"), ("2024-01-10", "98"), ("2024-01-11", "98")),
            Dividends(("2024-01-09", "2.00"), ("2024-01-10", "1.00")),
            TwelveDataFundamentalsStubTransport.NoSplits);

        var result = await CreateAdapter(transport).FetchAsync(Request("2024-01-08", "2024-01-11"));

        result.Issues.Should().BeEmpty();
        result.Payload.Select(b => b.Close).Should().Equal(100m, 99m, 98m, 98m);
        var adjusted = result.Payload.Select(b => b.AdjustedClose).ToArray();
        adjusted[3].Should().Be(98m);
        adjusted[2].Should().Be(98m);
        adjusted[1].Should().BeApproximately(98m, 1e-20m);
        adjusted[0].Should().BeApproximately(9800m / 101m, 1e-20m);
        // (99 + 2) / 100 - 1 = 1%; (98 + 1) / 99 - 1 = 0%.
        OneDayAdjustedReturn(result.Payload, "2024-01-09").Should().BeApproximately(0.01d, 1e-12d);
        OneDayAdjustedReturn(result.Payload, "2024-01-10").Should().BeApproximately(0d, 1e-12d);
    }

    [Fact]
    public async Task Two_ex_dates_credited_to_one_bar_return_the_exact_cash_holding_period_return()
    {
        // Ex-dates Saturday 2024-01-13 (1.00) and Sunday 2024-01-14 (2.00) both credit the 2024-01-15 bar.
        // Cash holding-period return into 01-15 is (50 + 1 + 2) / 51 - 1; chaining two separate factors would give
        // (50 + 1)(50 + 2) / (50 * 51) - 1, which differs.
        var transport = Transport(
            Series(("2024-01-11", "50"), ("2024-01-12", "51"), ("2024-01-15", "50")),
            Dividends(("2024-01-13", "1"), ("2024-01-14", "2")),
            TwelveDataFundamentalsStubTransport.NoSplits);

        var result = await CreateAdapter(transport).FetchAsync(Request("2024-01-11", "2024-01-15"));

        result.Issues.Should().BeEmpty();
        result.Payload[2].AdjustedClose.Should().Be(50m);
        OneDayAdjustedReturn(result.Payload, "2024-01-15").Should().BeApproximately(53d / 51d - 1d, 1e-15d);
    }

    [Fact]
    public async Task A_dividend_on_a_non_trading_day_is_credited_to_the_next_bar()
    {
        // Ex-date Saturday 2024-01-13: bars before it (01-12) take factor 50 / (50 + 1); 01-15 is unchanged.
        var transport = Transport(
            Series(("2024-01-11", "50"), ("2024-01-12", "51"), ("2024-01-15", "50")),
            Dividends(("2024-01-13", "1")),
            TwelveDataFundamentalsStubTransport.NoSplits);

        var result = await CreateAdapter(transport).FetchAsync(Request("2024-01-11", "2024-01-15"));

        result.Issues.Should().BeEmpty();
        result.Payload[2].AdjustedClose.Should().Be(50m);
        result.Payload[1].AdjustedClose.Should().BeApproximately(51m * 50m / 51m, 1e-20m);
        result.Payload[0].AdjustedClose.Should().BeApproximately(50m * 50m / 51m, 1e-20m);
        OneDayAdjustedReturn(result.Payload, "2024-01-15").Should().BeApproximately((50d + 1d) / 51d - 1d, 1e-12d);
    }

    // ---------- Recorded Canadian distributions (trimmed live slices, XTSE 2025) ----------

    public static TheoryData<string, string, string, decimal, string[], string[]> CanadianExDates => new()
    {
        // symbol, ex-date, amount listed by /dividends, provider-adjusted ex-date close, split-adjusted closes, provider adjust=all closes
        { "XEQT", "2025-12-30", "0.205", 0.205m,
            ["2025-12-24=40.32000", "2025-12-29=40.26000", "2025-12-30=40.049999", "2025-12-31=39.89000"],
            ["2025-12-24=40.013142", "2025-12-29=39.95360", "2025-12-30=39.58624", "2025-12-31=39.42809"] },
        { "CDZ", "2025-12-30", "0.114", 0.114m,
            ["2025-12-24=40.90000", "2025-12-29=40.90000", "2025-12-30=40.80000", "2025-12-31=40.81000"],
            ["2025-12-24=41.42196", "2025-12-29=41.42196", "2025-12-30=39.77400", "2025-12-31=39.78375"] },
        { "VCN", "2025-12-30", "0.387", 0.387m,
            ["2025-12-24=65.42000", "2025-12-29=65.21000", "2025-12-30=64.80000", "2025-12-31=64.5"],
            ["2025-12-24=64.42661", "2025-12-29=64.21981", "2025-12-30=63.80733", "2025-12-31=63.51193"] },
        { "XRB", "2025-06-02", "0.276876267748", 0.276876267748m,
            ["2025-05-29=23.15000", "2025-05-30=23.21000", "2025-06-02=23.060000", "2025-06-03=23.00000024796"],
            ["2025-05-29=23.022025", "2025-05-30=23.081692", "2025-06-02=22.87344", "2025-06-03=22.81392"] },
        { "CDZ", "2025-11-19", "0.114", 0.114m,
            ["2025-11-17=39.88000", "2025-11-18=39.83000", "2025-11-19=39.79000", "2025-11-20=39.49000"],
            ["2025-11-17=40.27828", "2025-11-18=40.22777", "2025-11-19=40.29780", "2025-11-20=39.99397"] },
        { "CDZ", "2025-10-28", "0.114", 0.114m,
            ["2025-10-24=40.36000", "2025-10-27=40.31000", "2025-10-28=40.13000", "2025-10-29=39.65000"],
            ["2025-10-24=40.65270", "2025-10-27=40.60233", "2025-10-28=40.53077", "2025-10-29=40.045976"] },
    };

    [Theory]
    [MemberData(nameof(CanadianExDates))]
    public async Task Canadian_ex_date_return_is_the_cash_holding_period_return(
        string symbol, string exDate, string amountText, decimal amount, string[] splitAdjusted, string[] providerAdjusted)
    {
        var bars = splitAdjusted.Select(p => (Date: p[..10], Close: p[11..])).ToArray();
        var transport = Transport(
            Series(bars).Replace("\"status\":\"ok\"", $$"""
                "meta":{"symbol":"{{symbol}}","interval":"1day","exchange":"TSX","mic_code":"XTSE"},"status":"ok"
                """, StringComparison.Ordinal),
            $$"""{"meta":{"symbol":"{{symbol}}","exchange":"TSX","mic_code":"XTSE"},"dividends":[{"ex_date":"{{exDate}}","amount":{{amountText}}}]}""",
            TwelveDataFundamentalsStubTransport.NoSplits);
        var request = new PriceHistoryRequest([new Symbol(symbol)],
            new DateRange(DateOnly.Parse(bars[0].Date, CultureInfo.InvariantCulture), DateOnly.Parse(bars[^1].Date, CultureInfo.InvariantCulture)))
        { Basis = TwelveDataPriceBarAdapter.DeliveredPriceBasis, Exchange = ExchangeCode.XTSE };

        var result = await CreateAdapter(transport).FetchAsync(request);

        result.Issues.Should().NotContain(i => i.Code == s_fetchFailed);
        var payload = result.Payload;
        var index = payload.ToList().FindIndex(b => b.Date == DateOnly.Parse(exDate, CultureInfo.InvariantCulture));
        var expected = (double)((payload[index].Close + amount) / payload[index - 1].Close) - 1d;
        OneDayAdjustedReturn(payload, exDate).Should().BeApproximately(expected, 1e-9d);

        // Anchor: the request's last bar carries AdjustedClose == Close; Close is the split-adjusted input.
        payload[^1].AdjustedClose.Should().Be(payload[^1].Close);
        payload.Select(b => b.Close.ToString(CultureInfo.InvariantCulture))
            .Should().Equal(bars.Select(b => decimal.Parse(b.Close, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));

        // The provider's adjust=all series is never the source: its ex-date return is not what we return.
        var provider = providerAdjusted.ToDictionary(p => p[..10], p => decimal.Parse(p[11..], CultureInfo.InvariantCulture));
        var providerReturn = (double)(provider[exDate] / provider[payload[index - 1].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)]) - 1d;
        OneDayAdjustedReturn(payload, exDate).Should().NotBe(providerReturn);
    }

    [Theory]
    [InlineData("XEQT", 0.205, 40.26, 40.049999, 39.95360, 39.58624)]
    [InlineData("CDZ", 0.114, 40.90, 40.80, 41.42196, 39.77400)]
    [InlineData("XRB", 0.276876267748, 23.21, 23.06, 23.081692, 22.87344)]
    public void Special_distribution_dates_show_the_provider_series_is_wrong(
        string symbol, double amount, double previous, double exClose, double providerPrevious, double providerEx)
    {
        // Documents why adjust=all is not used: on these dates the provider-adjusted return misses the cash
        // holding-period return by more than half a percent (four percent for CDZ).
        _ = symbol;
        var cashReturn = (exClose + amount) / previous - 1d;
        var providerReturn = providerEx / providerPrevious - 1d;
        Math.Abs(providerReturn - cashReturn).Should().BeGreaterThan(5e-3);
    }

    // ---------- Routing, anchor and windows ----------

    [Fact]
    public async Task No_dividend_window_keeps_adjusted_equal_to_close_and_skips_the_splits_call()
    {
        var transport = Transport(
            Series(("2024-01-08", "10"), ("2024-01-09", "11")),
            TwelveDataFundamentalsStubTransport.NoDividends);

        var result = await CreateAdapter(transport).FetchAsync(Request("2024-01-08", "2024-01-09"));

        result.Issues.Should().BeEmpty();
        result.Payload.Should().OnlyContain(b => b.AdjustedClose == b.Close);
        transport.RequestedUris.Should().NotContain(u => IsPath(u, "splits"));
    }

    [Fact]
    public async Task Splits_are_requested_from_the_earliest_ex_date_to_the_as_of_date()
    {
        var store = new FakeRawDocumentStore();
        var transport = Transport(
            Series(("2024-01-08", "10"), ("2024-01-09", "11")),
            Dividends(("2024-01-09", "0.1")),
            TwelveDataFundamentalsStubTransport.NoSplits);

        await CreateAdapter(transport, store).FetchAsync(Request("2024-01-08", "2024-01-09", ExchangeCode.XTSE));

        var splits = transport.RequestedUris.Single(u => IsPath(u, "splits"));
        var query = System.Web.HttpUtility.ParseQueryString(splits.Query);
        query["start_date"].Should().Be("2024-01-09");
        query["end_date"].Should().Be("2024-06-15"); // FakeClock's date: split-adjusted prices are restated to today.
        query["mic_code"].Should().Be("XTSE");
        store.SavedKeys.Should().Contain("twelvedata/splits/XEQT/XTSE/20240109-20240615");
        store.SavedKeys.Should().Contain("twelvedata/dividends/XEQT/XTSE/20240108-20240109");
    }

    [Theory]
    [InlineData("2024-01-08")] // on the first bar: the return it affects starts before the window
    [InlineData("2024-01-10")] // after the last bar: no bar on or after the ex-date yet
    public async Task Dividends_outside_the_bars_are_reported_and_do_not_move_the_anchor(string exDate)
    {
        var transport = Transport(
            Series(("2024-01-08", "10"), ("2024-01-09", "11")),
            Dividends((exDate, "0.5")));

        var result = await CreateAdapter(transport).FetchAsync(Request("2024-01-08", "2024-01-10"));

        result.Payload.Should().HaveCount(2).And.OnlyContain(b => b.AdjustedClose == b.Close);
        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("DIVIDEND_OUTSIDE_BARS")).Which.Message.Should().Contain(exDate);
        transport.RequestedUris.Should().NotContain(u => IsPath(u, "splits"));
    }

    [Fact]
    public async Task Identical_repeated_rows_count_once_and_zero_amounts_are_ignored()
    {
        var transport = Transport(
            Series(("2024-01-08", "100"), ("2024-01-09", "99")),
            Dividends(("2024-01-09", "2"), ("2024-01-09", "2"), ("2024-01-08", "0")),
            TwelveDataFundamentalsStubTransport.NoSplits);

        var result = await CreateAdapter(transport).FetchAsync(Request("2024-01-08", "2024-01-09"));

        result.Issues.Should().BeEmpty();
        OneDayAdjustedReturn(result.Payload, "2024-01-09").Should().BeApproximately(0.01d, 1e-12d);
    }

    [Fact]
    public async Task A_dividend_in_an_older_page_adjusts_bars_across_pages_with_one_dividends_call()
    {
        // Two price pages (5000-bar cap); the ex-date sits in the older page, 2010-06-15.
        var transport = new FakeHttpDataTransport();
        transport.RespondDynamic(u => IsPath(u, "time_series"), uri =>
        {
            var end = DateOnly.ParseExact(System.Web.HttpUtility.ParseQueryString(uri.Query)["end_date"]![..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var count = end > new DateOnly(2020, 1, 1) ? 5000 : end.DayNumber - new DateOnly(2008, 1, 1).DayNumber + 1;
            var bars = Enumerable.Range(0, count)
                .Select(i => end.AddDays(-i))
                .Select(d => (d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d == new DateOnly(2010, 6, 15) ? "98" : "100"))
                .ToArray();
            return Series(bars);
        });
        transport.RespondDynamic(u => IsPath(u, "dividends"), _ => Dividends(("2010-06-15", "2")));
        transport.RespondDynamic(u => IsPath(u, "splits"), _ => TwelveDataFundamentalsStubTransport.NoSplits);

        var result = await CreateAdapter(transport).FetchAsync(Request("2008-01-01", "2026-04-17"));

        transport.RequestedUris.Count(u => IsPath(u, "time_series")).Should().Be(2);
        transport.RequestedUris.Count(u => IsPath(u, "dividends")).Should().Be(1);
        result.Payload[^1].AdjustedClose.Should().Be(result.Payload[^1].Close);
        // (98 + 2) / 100 - 1 = 0: the cash dividend exactly offsets the price drop.
        OneDayAdjustedReturn(result.Payload, "2010-06-15").Should().BeApproximately(0d, 1e-12d);
        result.Payload[0].AdjustedClose.Should().BeApproximately(100m * 98m / 100m, 1e-18m);
    }

    // ---------- Fail-closed controls ----------

    [Fact]
    public async Task Dividends_http_failure_returns_an_issue_and_no_bars()
    {
        var transport = Transport(Series(("2024-01-08", "10"), ("2024-01-09", "11")), dividends: null);

        var result = await CreateAdapter(transport).FetchAsync(Request("2024-01-08", "2024-01-09"));

        transport.RequestedUris.Should().Contain(u => IsPath(u, "dividends"));
        result.Payload.Should().BeEmpty();
        result.Provenance.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == s_fetchFailed).Which.Message.Should().Contain("dividend");
        result.Coverage.ReturnedPoints.Should().Be(0);
    }

    [Theory]
    [InlineData("""{"code":429,"message":"You have run out of API credits for the current minute.","status":"error"}""")]
    [InlineData("""{"code":403,"message":"/dividends is available exclusively with grow or venture plans.","status":"error"}""")]
    [InlineData("""{"meta":{"symbol":"XEQT"}}""")]
    [InlineData("""{"dividends":[{"ex_date":"2024-01-09","amount":-0.1}]}""")]
    [InlineData("""{"dividends":[{"ex_date":"2024-01-09","amount":0.1},{"ex_date":"2024-01-09","amount":0.2}]}""")]
    [InlineData("""{"meta":{"symbol":"VCN"},"dividends":[]}""")]
    public async Task Unusable_dividend_responses_fail_closed(string dividends)
    {
        var transport = Transport(Series(("2024-01-08", "10"), ("2024-01-09", "11")), dividends, TwelveDataFundamentalsStubTransport.NoSplits);

        var result = await CreateAdapter(transport).FetchAsync(Request("2024-01-08", "2024-01-09"));

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == s_fetchFailed);
    }

    [Fact]
    public async Task Splits_failure_fails_closed()
    {
        var transport = Transport(
            Series(("2024-01-08", "10"), ("2024-01-09", "11")),
            Dividends(("2024-01-09", "0.1")),
            """{"code":429,"message":"You have run out of API credits.","status":"error"}""");

        var result = await CreateAdapter(transport).FetchAsync(Request("2024-01-08", "2024-01-09"));

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == s_fetchFailed);
    }

    [Theory]
    [InlineData("2024-01-09", true)]  // split on the ex-date
    [InlineData("2024-03-01", true)]  // split after the window: prices are restated, dividend basis unknown
    [InlineData("2024-01-08", false)] // split before the only applicable ex-date
    public async Task A_split_on_or_after_an_applicable_ex_date_makes_the_basis_unverifiable(string splitDate, bool unverifiable)
    {
        var transport = Transport(
            Series(("2024-01-08", "10"), ("2024-01-09", "11")),
            Dividends(("2024-01-09", "0.1")),
            $$"""{"splits":[{"date":"{{splitDate}}","description":"5-for-1 split","ratio":0.2,"from_factor":5,"to_factor":1}]}""");

        var result = await CreateAdapter(transport).FetchAsync(Request("2024-01-08", "2024-01-09"));

        if (unverifiable)
        {
            result.Payload.Should().BeEmpty();
            result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("DIVIDEND_SPLIT_BASIS_UNVERIFIED"));
        }
        else
        {
            result.Payload.Should().HaveCount(2);
            result.Issues.Should().BeEmpty();
        }
    }
}
