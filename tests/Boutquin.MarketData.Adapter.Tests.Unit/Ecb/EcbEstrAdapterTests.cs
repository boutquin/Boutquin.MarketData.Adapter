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
using Boutquin.MarketData.Adapter.Ecb;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Calendars.Holidays;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.Ecb;

public sealed class EcbEstrAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static EcbEstrAdapter CreateAdapter(
        FakeHttpDataTransport transport,
        FakeRawDocumentStore? rawStore = null,
        EcbOptions? options = null,
        IBusinessCalendar? calendar = null)
    {
        return new EcbEstrAdapter(
            transport,
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(options ?? new EcbOptions()),
            NullLogger<EcbEstrAdapter>.Instance);
    }

    private static DateRange MakeRange(int year = 2024, int fromMonth = 1, int fromDay = 1, int toMonth = 1, int toDay = 31) =>
        new(new DateOnly(year, fromMonth, fromDay), new DateOnly(year, toMonth, toDay));

    [Fact]
    public void ProviderKey_returns_ecb_estr() =>
        CreateAdapter(new FakeHttpDataTransport()).ProviderKey.Should().Be(new ProviderCode("ecb-estr"));

    [Fact]
    public void Priority_returns_100() =>
        CreateAdapter(new FakeHttpDataTransport()).Priority.Should().Be(100);

    [Fact]
    public void CanHandle_returns_true_for_estr()
    {
        var request = new OvernightFixingRequest(new BenchmarkName("ESTR"), MakeRange());
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_returns_false_for_sonia()
    {
        var request = new OvernightFixingRequest(new BenchmarkName("SONIA"), MakeRange());
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
    }

    [Fact]
    public async Task FetchAsync_rejects_direct_request_for_another_benchmark()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", "KEY,FREQ,TIME_PERIOD,OBS_VALUE\nEST.B.EU000A2X2A25.WT,B,2024-01-02,3.90");
        var adapter = CreateAdapter(transport);
        var request = new OvernightFixingRequest(new BenchmarkName("SONIA"), MakeRange());

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new IssueCode("UNSUPPORTED_BENCHMARK"));
        transport.RequestedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_parses_csv_into_scalar_observations()
    {
        var csv = "KEY,FREQ,TIME_PERIOD,OBS_VALUE\nEST.B.EU000A2X2A25.WT,B,2024-01-02,3.90\nEST.B.EU000A2X2A25.WT,B,2024-01-03,3.91";

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", csv);
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(new BenchmarkName("ESTR"), MakeRange());
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().HaveCount(2);
        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[0].Value.Should().Be(0.039m);
        result.Payload[1].Date.Should().Be(new DateOnly(2024, 1, 3));
        result.Payload[1].Value.Should().Be(0.0391m);
    }

    [Fact]
    public async Task FetchAsync_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(cancellation.Token));
        var adapter = CreateAdapter(transport);

        Func<Task> act = () => adapter.FetchAsync(
            new OvernightFixingRequest(new BenchmarkName("ESTR"), MakeRange()),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_failure_issue_does_not_expose_transport_details()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new IOException("credential=secret; path=/private/provider.json"));
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new OvernightFixingRequest(new BenchmarkName("ESTR"), MakeRange()),
            CancellationToken.None);

        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("FETCH_FAILED"));
        result.Issues[0].Message.Should().Be("Failed to fetch ESTR fixings from ECB.");
    }

    [Fact]
    public async Task FetchAsync_populates_provenance()
    {
        var csv = "KEY,FREQ,TIME_PERIOD,OBS_VALUE\nEST.B.EU000A2X2A25.WT,B,2024-01-02,3.90";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", csv);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new OvernightFixingRequest(new BenchmarkName("ESTR"), MakeRange()), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        result.Provenance[0].Provider.Should().Be(new ProviderCode("ecb-estr"));
    }

    // ---------- Outgoing request URL ----------
    // The other tests stub the transport on the substring "ecb", which matches any series key.
    // These pin the full URL: the default key used to be FM/B.U2.EUR.4F.KR.DFR.LEV — the ECB
    // Deposit Facility Rate "date of changes" series, HTTP 200 and therefore silent — not €STR.

    [Fact]
    public void Default_EstrSeriesKey_is_the_euro_short_term_rate_series() =>
        new EcbOptions().EstrSeriesKey.Should().Be("EST/B.EU000A2X2A25.WT");

    [Fact]
    public async Task FetchAsync_with_default_options_requests_estr_csvdata_url()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", "KEY,FREQ,TIME_PERIOD,OBS_VALUE\nEST.B.EU000A2X2A25.WT,B,2024-01-02,3.906");
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(
            new BenchmarkName("ESTR"),
            new DateRange(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        const string expected = "https://data-api.ecb.europa.eu/service/data/EST/B.EU000A2X2A25.WT?startPeriod=2024-01-02&endPeriod=2024-01-04&format=csvdata";
        transport.RequestedUris.Should().ContainSingle().Which.AbsoluteUri.Should().Be(expected);
        result.Provenance.Should().ContainSingle().Which.SourceUrl.Should().Be(expected);
    }

    [Fact]
    public async Task FetchAsync_uses_configured_base_url_for_estr_series()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("sdmx.example.test", "KEY,FREQ,TIME_PERIOD,OBS_VALUE\nEST.B.EU000A2X2A25.WT,B,2024-01-02,3.90");
        var adapter = CreateAdapter(transport, options: new EcbOptions
        {
            BaseUrl = "https://sdmx.example.test/data",
        });

        await adapter.FetchAsync(new OvernightFixingRequest(new BenchmarkName("ESTR"), MakeRange()), CancellationToken.None);

        transport.RequestedUris.Should().ContainSingle()
            .Which.AbsoluteUri.Should().Be("https://sdmx.example.test/data/EST/B.EU000A2X2A25.WT?startPeriod=2024-01-01&endPeriod=2024-01-31&format=csvdata");
    }

    [Fact]
    public async Task FetchAsync_rejects_non_estr_series_override()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("sdmx.example.test", "KEY,TIME_PERIOD,OBS_VALUE\nX.Y.Z,2024-01-10,1.0");
        var adapter = CreateAdapter(transport, options: new EcbOptions
        {
            BaseUrl = "https://sdmx.example.test/data",
            EstrSeriesKey = "FLOW/X.Y.Z",
        });

        var result = await adapter.FetchAsync(
            new OvernightFixingRequest(new BenchmarkName("ESTR"), MakeRange()),
            CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new IssueCode("UNSUPPORTED_SERIES"));
        transport.RequestedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_rejects_response_rows_for_another_series()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", "KEY,TIME_PERIOD,OBS_VALUE\nFLOW.X.Y.Z,2024-01-10,1.0");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new OvernightFixingRequest(new BenchmarkName("ESTR"),
                new DateRange(new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 10))),
            CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new IssueCode("SERIES_MISMATCH"));
    }

    [Fact]
    public async Task FetchAsync_parses_live_csvdata_shape_with_quoted_commas_and_crlf()
    {
        // The live response carries ~30 attribute columns, a quoted COMMENT_TS containing commas
        // after OBS_VALUE, and CRLF line endings. The adapter's plain comma split must still land
        // on TIME_PERIOD (index 4) and OBS_VALUE (index 5).
        var csv = "KEY,FREQ,BENCHMARK_ITEM,DATA_TYPE_EST,TIME_PERIOD,OBS_VALUE,OBS_STATUS,CONF_STATUS,PRE_BREAK_VALUE,COMMENT_OBS,CALCUL_START_DATE,CALCUL_END_DATE,TIME_FORMAT,BREAKS,COMMENT_TS,TITLE,UNIT_MEASURE,UNIT_MULT\r\n" +
                  "EST.B.EU000A2X2A25.WT,B,EU000A2X2A25,WT,2024-01-02,3.906,A,F,,,,,P1D,,\"ESA 2010 Sectors: S.121, S.122, S.123\",Euro short-term rate,PC,0\r\n" +
                  "EST.B.EU000A2X2A25.WT,B,EU000A2X2A25,WT,2024-01-03,3.904,A,F,,,,,P1D,,\"ESA 2010 Sectors: S.121, S.122, S.123\",Euro short-term rate,PC,0\r\n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", csv);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new OvernightFixingRequest(new BenchmarkName("ESTR"), new DateRange(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 3))),
            CancellationToken.None);

        result.Payload.Should().HaveCount(2);
        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[0].Value.Should().Be(0.03906m);
        result.Payload[1].Date.Should().Be(new DateOnly(2024, 1, 3));
        result.Payload[1].Value.Should().Be(0.03904m);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("MISSING_VALUES"));
    }

    [Fact]
    public async Task FetchAsync_parses_quoted_comma_before_date_column()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", "KEY,COMMENT_TS,TIME_PERIOD,OBS_VALUE\n" +
            "EST.B.EU000A2X2A25.WT,\"revision, confirmed\",2024-01-10,3.906");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new OvernightFixingRequest(new BenchmarkName("ESTR"),
                new DateRange(new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 10))),
            CancellationToken.None);

        result.Payload.Should().ContainSingle();
        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 10));
        result.Payload[0].Value.Should().Be(0.03906m);
    }

    [Fact]
    public async Task FetchAsync_skips_malformed_row_without_discarding_valid_rows()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", "KEY,TIME_PERIOD,OBS_VALUE\n" +
            "EST.B.EU000A2X2A25.WT,2024-01-10,3.90\n" +
            "EST.B.EU000A2X2A25.WT,BAD_DATE,3.91\n" +
            "EST.B.EU000A2X2A25.WT,2024-01-11,3.92");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new OvernightFixingRequest(new BenchmarkName("ESTR"),
                new DateRange(new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 11))),
            CancellationToken.None);

        result.Payload.Select(o => o.Date).Should().Equal(new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 11));
        result.Issues.Should().Contain(i => i.Code == new IssueCode("MALFORMED_ROW"));
    }

    [Fact]
    public async Task FetchAsync_emits_only_observations_in_requested_range()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", "KEY,TIME_PERIOD,OBS_VALUE\n" +
            "EST.B.EU000A2X2A25.WT,2024-01-09,3.89\n" +
            "EST.B.EU000A2X2A25.WT,2024-01-10,3.90\n" +
            "EST.B.EU000A2X2A25.WT,2024-01-11,3.91");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new OvernightFixingRequest(new BenchmarkName("ESTR"),
                new DateRange(new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 10))),
            CancellationToken.None);

        result.Payload.Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 1, 10));
        result.Provenance.Should().ContainSingle().Which.DataDate.Should().Be(new DateOnly(2024, 1, 10));
    }

    // ---------- Calendar-aware coverage (feature-calendar-aware-coverage) ----------

    [Fact]
    public async Task FetchAsync_business_day_range_reports_complete_coverage()
    {
        // Mon-Fri 2024-01-08..12 — 5 business days
        var csv = "KEY,FREQ,TIME_PERIOD,OBS_VALUE\n" +
                  "EST.B.EU000A2X2A25.WT,B,2024-01-08,3.90\n" +
                  "EST.B.EU000A2X2A25.WT,B,2024-01-09,3.90\n" +
                  "EST.B.EU000A2X2A25.WT,B,2024-01-10,3.90\n" +
                  "EST.B.EU000A2X2A25.WT,B,2024-01-11,3.90\n" +
                  "EST.B.EU000A2X2A25.WT,B,2024-01-12,3.90";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", csv);
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(
            new BenchmarkName("ESTR"),
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(5);
        result.Coverage.ReturnedPoints.Should().Be(5);
        result.Coverage.CoverageRatio.Should().Be(1.0m);
        result.Coverage.Status.Should().Be(CompletenessStatus.Complete);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_weekend_only_range_reports_no_unexpected_gaps()
    {
        var csv = "KEY,FREQ,TIME_PERIOD,OBS_VALUE\n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", csv);
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(
            new BenchmarkName("ESTR"),
            new DateRange(new DateOnly(2024, 1, 6), new DateOnly(2024, 1, 7)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(0);
        result.Coverage.CoverageRatio.Should().Be(1.0m);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_holiday_within_range_is_not_flagged_as_gap()
    {
        // TARGET closed on 2024-12-25 (Christmas) and 2024-12-26. Use TARGET calendar.
        var csv = "KEY,FREQ,TIME_PERIOD,OBS_VALUE\n" +
                  "EST.B.EU000A2X2A25.WT,B,2024-12-23,3.90\n" +
                  "EST.B.EU000A2X2A25.WT,B,2024-12-24,3.90\n" +
                  "EST.B.EU000A2X2A25.WT,B,2024-12-27,3.90";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", csv);
        var target = HolidayCalendarFactory.Create("TARGET");
        var adapter = CreateAdapter(transport, calendar: target);

        var request = new OvernightFixingRequest(
            new BenchmarkName("ESTR"),
            new DateRange(new DateOnly(2024, 12, 23), new DateOnly(2024, 12, 27)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(3);
        result.Coverage.ReturnedPoints.Should().Be(3);
        result.Coverage.Status.Should().Be(CompletenessStatus.Complete);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_missing_business_day_emits_unexpected_gap()
    {
        var csv = "KEY,FREQ,TIME_PERIOD,OBS_VALUE\n" +
                  "EST.B.EU000A2X2A25.WT,B,2024-01-08,3.90\n" +
                  "EST.B.EU000A2X2A25.WT,B,2024-01-09,3.90\n" +
                  "EST.B.EU000A2X2A25.WT,B,2024-01-10,3.90";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", csv);
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(
            new BenchmarkName("ESTR"),
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(5);
        result.Coverage.ReturnedPoints.Should().Be(3);
        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
        var gaps = result.Issues.Where(i => i.Code == new IssueCode("UNEXPECTED_GAP")).ToList();
        gaps.Should().HaveCount(2);
    }
}
