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
using Boutquin.MarketData.Adapter.BankOfCanada;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.BankOfCanada;

public sealed class BankOfCanadaYieldCurveAdapterTests
{
    private static readonly FakeClock s_clock = new();

    // The Bank's file: "Date" then ZCnnnYR terms in hundredths of a year, every line ending
    // in a trailing comma. A short subset of the 120 real columns keeps the fixtures readable.
    private const string Header = "Date, ZC025YR, ZC050YR, ZC075YR, ZC100YR, ZC125YR, ZC200YR, ZC1000YR, ZC3000YR, ";

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

    private static YieldCurveQuoteRequest Request(int year, int month, int day) =>
        new(new YieldCurveId("BOC-ZERO"), new DateOnly(year, month, day));

    [Fact]
    public void ProviderKey_returns_bankofcanada() =>
        CreateAdapter(new FakeHttpDataTransport()).ProviderKey.Should().Be(new ProviderCode("bankofcanada"));

    [Fact]
    public void Priority_returns_100() =>
        CreateAdapter(new FakeHttpDataTransport()).Priority.Should().Be(100);

    [Fact]
    public void CanHandle_returns_true() =>
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(Request(2024, 1, 15)).Should().BeTrue();

    [Fact]
    public void CanHandle_accepts_CAD_zero_curve() =>
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(
            new YieldCurveQuoteRequest(new YieldCurveId("CAD-ZERO"), new DateOnly(2024, 1, 15))).Should().BeTrue();

    [Fact]
    public void CanHandle_rejects_US_Treasury_curve() =>
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(
            new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), new DateOnly(2024, 1, 15))).Should().BeFalse();

    [Fact]
    public void CanHandle_rejects_foreign_venue()
    {
        var request = Request(2024, 1, 15) with { Exchange = ExchangeCode.XNYS };
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
    }

    [Fact]
    public async Task FetchAsync_rejects_US_Treasury_curve_before_transport()
    {
        var transport = new FakeHttpDataTransport();
        var request = new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), new DateOnly(2024, 1, 15));

        Func<Task> act = () => CreateAdapter(transport).FetchAsync(request);

        await act.Should().ThrowAsync<ArgumentException>();
        transport.RequestedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(cancellation.Token));

        Func<Task> act = () => CreateAdapter(transport).FetchAsync(Request(2024, 1, 15), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_does_not_expose_transport_exception_detail()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new HttpRequestException("credential file /private/curve-key.txt failed"));

        var result = await CreateAdapter(transport).FetchAsync(Request(2024, 1, 15));

        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("FETCH_FAILED"))
            .Which.Message.Should().NotContain("/private/curve-key.txt");
    }

    [Fact]
    public async Task FetchAsync_rejects_currency_option_incompatible_with_CAD_curve()
    {
        var transport = new FakeHttpDataTransport();
        var adapter = CreateAdapter(transport, options: new BankOfCanadaOptions { Currency = "USD" });

        adapter.CanHandle(Request(2024, 1, 15)).Should().BeFalse();
        Func<Task> act = () => adapter.FetchAsync(Request(2024, 1, 15));
        await act.Should().ThrowAsync<ArgumentException>();
        transport.RequestedUris.Should().BeEmpty();
    }

    // ---------- Outgoing request URL ----------
    // Valet has no zero-coupon yield-curve group (the old default, observations/group/BD.CDN.ZERO,
    // answers HTTP 404). The curve is served by the Bank's CSV lookup, and these pin that URL.

    [Fact]
    public void Default_YieldCurveLookupUrl_is_the_stats_csv_lookup() =>
        new BankOfCanadaOptions().YieldCurveLookupUrl.Should().Be("https://www.bankofcanada.ca/stats/results/csv");

    [Fact]
    public async Task FetchAsync_with_default_options_requests_lookback_window_ending_on_as_of_date()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("lookup_yield_curve", Header + "\n2024-01-15, 0.0505707000, 0.0498938000, 0.0484741000, 0.0470544000, 0.0424389000, 0.0378675000, 0.0321504000, 0.0315861000, \n");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(Request(2024, 1, 15), CancellationToken.None);

        const string expected = "https://www.bankofcanada.ca/stats/results/csv?lookupPage=lookup_yield_curve.php&dFrom=2023-12-16&dTo=2024-01-15";
        transport.RequestedUris.Should().ContainSingle().Which.AbsoluteUri.Should().Be(expected);
        result.Provenance.Should().ContainSingle().Which.SourceUrl.Should().Be(expected);
        transport.RequestedUris[0].AbsoluteUri.Should().NotContain("valet", "the zero-coupon curve is not a Valet dataset");
    }

    [Theory]
    [InlineData("https://boc.example.test/stats/results/csv")]
    [InlineData("https://boc.example.test/stats/results/csv/")]
    public async Task FetchAsync_uses_configured_lookup_url(string lookupUrl)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("boc.example.test", Header + "\n2024-01-15, 0.05, 0.05, 0.05, 0.05, 0.05, 0.05, 0.05, 0.05, \n");
        var adapter = CreateAdapter(transport, options: new BankOfCanadaOptions { YieldCurveLookupUrl = lookupUrl });

        await adapter.FetchAsync(Request(2024, 1, 15), CancellationToken.None);

        transport.RequestedUris.Should().ContainSingle()
            .Which.AbsoluteUri.Should().Be("https://boc.example.test/stats/results/csv?lookupPage=lookup_yield_curve.php&dFrom=2023-12-16&dTo=2024-01-15");
    }

    // ---------- Parsing ----------

    [Fact]
    public async Task FetchAsync_maps_term_columns_to_tenor_labels_and_keeps_decimal_rates()
    {
        var csv = Header + "\n" +
                  "2024-01-15, 0.0505707000, 0.0498938000, 0.0484741000, 0.0470544000, 0.0424389000, 0.0378675000, 0.0321504000, 0.0315861000, \n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("lookup_yield_curve", csv);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(Request(2024, 1, 15), CancellationToken.None);

        result.Payload.Should().HaveCount(8);
        result.Payload.Should().OnlyContain(q => q.Date == new DateOnly(2024, 1, 15));
        result.Payload.Select(q => q.Tenor).Should().Equal("3M", "6M", "9M", "1Y", "15M", "2Y", "10Y", "30Y");
        result.Payload.Should().Contain(q => q.Tenor == "3M" && q.Rate == 0.0505707m);
        result.Payload.Should().Contain(q => q.Tenor == "30Y" && q.Rate == 0.0315861m);
        result.Issues.Should().BeEmpty();
        result.Coverage.ReturnedPoints.Should().Be(1);
    }

    [Fact]
    public async Task FetchAsync_serves_latest_row_and_reports_rollback_when_as_of_date_has_no_curve()
    {
        // The file lags publication by ~2 weeks: a request for the 26th finds rows up to the 12th.
        var csv = Header + "\n" +
                  "2024-01-11, 0.0511, 0.0501, 0.0489, 0.0475, 0.0430, 0.0382, 0.0325, 0.0319, \n" +
                  "2024-01-12, 0.0508, 0.0499, 0.0486, 0.0472, 0.0427, 0.0380, 0.0323, 0.0317, \n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("lookup_yield_curve", csv);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(Request(2024, 1, 26), CancellationToken.None);

        result.Payload.Should().HaveCount(8);
        result.Payload.Should().OnlyContain(q => q.Date == new DateOnly(2024, 1, 12));
        result.Payload.Should().Contain(q => q.Tenor == "3M" && q.Rate == 0.0508m);
        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("DATE_ROLLBACK"))
            .Which.Message.Should().Contain("2024-01-12");
        result.Provenance[0].DataDate.Should().Be(new DateOnly(2024, 1, 12));
    }

    [Fact]
    public async Task FetchAsync_skips_all_na_holiday_rows_and_falls_back_to_previous_curve()
    {
        // The Bank publishes holidays as a dated row of "na" cells.
        var csv = Header + "\n" +
                  "2023-12-29, 0.0510, 0.0500, 0.0488, 0.0474, 0.0429, 0.0381, 0.0324, 0.0318, \n" +
                  "2024-01-01, na, na, na, na, na, na, na, na, \n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("lookup_yield_curve", csv);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(Request(2024, 1, 1), CancellationToken.None);

        result.Payload.Should().HaveCount(8);
        result.Payload.Should().OnlyContain(q => q.Date == new DateOnly(2023, 12, 29));
        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("DATE_ROLLBACK"));
    }

    [Fact]
    public async Task FetchAsync_omits_individual_na_cells_but_keeps_the_row()
    {
        var csv = Header + "\n" +
                  "2024-01-15, 0.0505, na, 0.0484, 0.0470, na, 0.0378, 0.0321, 0.0315, \n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("lookup_yield_curve", csv);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(Request(2024, 1, 15), CancellationToken.None);

        result.Payload.Select(q => q.Tenor).Should().Equal("3M", "9M", "1Y", "2Y", "10Y", "30Y");
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("DATE_ROLLBACK"));
    }

    [Fact]
    public async Task FetchAsync_header_only_response_reports_empty_observations()
    {
        // The lookup answers a range with no published curve (e.g. inside the publication lag)
        // with HTTP 200 and the header line alone.
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("lookup_yield_curve", Header + "\n");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(Request(2026, 9, 21), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("EMPTY_OBSERVATIONS"));
        result.Coverage.ReturnedPoints.Should().Be(0);
        result.Coverage.CoverageRatio.Should().Be(0m);
        result.Provenance[0].DataDate.Should().BeNull();
    }

    [Fact]
    public async Task FetchAsync_unrecognised_header_reports_schema_mismatch()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("lookup_yield_curve", "<!DOCTYPE html><html><body>Error</body></html>");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(Request(2024, 1, 15), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("SCHEMA_MISMATCH"));
    }

    [Fact]
    public async Task FetchAsync_transport_failure_reports_fetch_failed()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new HttpRequestException("boom"));
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(Request(2024, 1, 15), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("FETCH_FAILED"));
        result.Provenance.Should().ContainSingle().Which.Freshness.Should().Be(FreshnessClass.Unknown);
    }

    [Fact]
    public async Task FetchAsync_populates_provenance_and_raw_store()
    {
        var csv = Header + "\n2024-01-15, 0.05, 0.05, 0.05, 0.05, 0.05, 0.05, 0.05, 0.05, \n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("lookup_yield_curve", csv);
        var rawStore = new FakeRawDocumentStore();
        var adapter = CreateAdapter(transport, rawStore);

        var result = await adapter.FetchAsync(Request(2024, 1, 15), CancellationToken.None);

        var provenance = result.Provenance.Should().ContainSingle().Subject;
        provenance.Provider.Should().Be(new ProviderCode("bankofcanada"));
        provenance.Dataset.Should().Be("BOC-ZERO");
        provenance.Freshness.Should().Be(FreshnessClass.Delayed);
        provenance.DataDate.Should().Be(new DateOnly(2024, 1, 15));
        rawStore.Store.Should().ContainKey("bankofcanada/yield-curve/BOC-ZERO/20240115")
            .WhoseValue.Should().Be(csv);
    }
}
