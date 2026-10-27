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
using Boutquin.MarketData.Adapter.BankOfEngland;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Calendars.Holidays;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.BankOfEngland;

public sealed class BankOfEnglandSoniaAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static BankOfEnglandSoniaAdapter CreateAdapter(
        FakeHttpDataTransport transport,
        FakeRawDocumentStore? rawStore = null,
        BankOfEnglandOptions? options = null,
        IBusinessCalendar? calendar = null)
    {
        return new BankOfEnglandSoniaAdapter(
            transport,
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(options ?? new BankOfEnglandOptions()),
            NullLogger<BankOfEnglandSoniaAdapter>.Instance);
    }

    private static DateRange MakeRange(int year = 2024, int fromMonth = 1, int fromDay = 1, int toMonth = 1, int toDay = 31) =>
        new(new DateOnly(year, fromMonth, fromDay), new DateOnly(year, toMonth, toDay));

    [Fact]
    public void ProviderKey_returns_boe_sonia() =>
        CreateAdapter(new FakeHttpDataTransport()).ProviderKey.Should().Be(new ProviderCode("boe-sonia"));

    [Fact]
    public void Priority_returns_100() =>
        CreateAdapter(new FakeHttpDataTransport()).Priority.Should().Be(100);

    [Fact]
    public void CanHandle_returns_true_for_sonia()
    {
        var request = new OvernightFixingRequest(new BenchmarkName("SONIA"), MakeRange());
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_returns_false_for_sofr()
    {
        var request = new OvernightFixingRequest(new BenchmarkName("SOFR"), MakeRange());
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
    }

    [Fact]
    public async Task FetchAsync_parses_csv_into_scalar_observations()
    {
        var csv = "DATE,IUDSOIA\r\n02 Jan 2024,5.19\r\n03 Jan 2024,5.18\r\n04 Jan 2024,\r\n";

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofengland", csv);
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(new BenchmarkName("SONIA"), MakeRange());
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        // Empty value row should be skipped
        result.Payload.Should().HaveCount(2);
        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[0].Value.Should().Be(0.0519m);
        result.Payload[1].Value.Should().Be(0.0518m);
    }

    [Fact]
    public async Task FetchAsync_rejects_a_different_series_header()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofengland", "DATE,OTHER\r\n02 Jan 2024,9.99\r\n");

        var result = await CreateAdapter(transport).FetchAsync(
            new OvernightFixingRequest(new BenchmarkName("SONIA"), MakeRange()), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("SCHEMA_MISMATCH"));
    }

    [Fact]
    public async Task FetchAsync_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(cancellation.Token));

        Func<Task> act = () => CreateAdapter(transport).FetchAsync(
            new OvernightFixingRequest(new BenchmarkName("SONIA"), MakeRange()), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_does_not_expose_transport_exception_detail()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new HttpRequestException("credential file /private/sonia-key.txt failed"));

        var result = await CreateAdapter(transport).FetchAsync(
            new OvernightFixingRequest(new BenchmarkName("SONIA"), MakeRange()));

        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("FETCH_FAILED"))
            .Which.Message.Should().NotContain("/private/sonia-key.txt");
    }

    [Fact]
    public async Task FetchAsync_populates_provenance()
    {
        var csv = "DATE,IUDSOIA\r\n02 Jan 2024,5.19\r\n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofengland", csv);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new OvernightFixingRequest(new BenchmarkName("SONIA"), MakeRange()), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        result.Provenance[0].Provider.Should().Be(new ProviderCode("boe-sonia"));
    }

    // ---------- Outgoing request URL ----------
    // Three defects stacked here until 2026-09-21: the default code IUDSNKY is not an IADB
    // series ("Invalid series code value supplied"), the URL lacked UsingCodes=Y (without it the
    // IADB ignores SeriesCodes and redirects to its HTML error page), and the parser expected
    // "02 Jan 24" while the wire carries 02 Jan 2024. Every fixture in this class is now in the
    // wire format; these tests pin the URL and the error-page handling.

    [Fact]
    public void Default_SoniaSeriesCode_is_IUDSOIA() =>
        new BankOfEnglandOptions().SoniaSeriesCode.Should().Be("IUDSOIA");

    [Fact]
    public async Task FetchAsync_with_default_options_requests_iadb_csv_export_with_UsingCodes()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofengland", "DATE,IUDSOIA\r\n02 Jan 2024,5.1863\r\n");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new OvernightFixingRequest(new BenchmarkName("SONIA"), MakeRange()), CancellationToken.None);

        const string expected = "https://www.bankofengland.co.uk/boeapps/database/_iadb-fromshowcolumns.asp?csv.x=yes&SeriesCodes=IUDSOIA&CSVF=TN&UsingCodes=Y&Datefrom=01/Jan/2024&Dateto=31/Jan/2024";
        transport.RequestedUris.Should().ContainSingle().Which.OriginalString.Should().Be(expected);
        result.Provenance.Should().ContainSingle().Which.SourceUrl.Should().Be(expected);
    }

    [Fact]
    public async Task FetchAsync_uses_configured_base_url_and_series_code()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("iadb.example.test", "DATE,XYZ\r\n02 Jan 2024,1.0\r\n");
        var adapter = CreateAdapter(transport, options: new BankOfEnglandOptions
        {
            BaseUrl = "https://iadb.example.test/db",
            SoniaSeriesCode = "XYZ",
        });

        await adapter.FetchAsync(new OvernightFixingRequest(new BenchmarkName("SONIA"), MakeRange()), CancellationToken.None);

        transport.RequestedUris.Should().ContainSingle()
            .Which.OriginalString.Should().Be("https://iadb.example.test/db/_iadb-fromshowcolumns.asp?csv.x=yes&SeriesCodes=XYZ&CSVF=TN&UsingCodes=Y&Datefrom=01/Jan/2024&Dateto=31/Jan/2024");
    }

    [Fact]
    public async Task FetchAsync_parses_live_wire_format()
    {
        // Captured 2026-09-21 for IUDSOIA, 02..05 Jan 2024: CRLF, unquoted dd MMM yyyy, no
        // header quotes, up to four decimals, non-business days simply absent.
        var csv = "DATE,IUDSOIA\r\n02 Jan 2024,5.1863\r\n03 Jan 2024,5.1863\r\n04 Jan 2024,5.187\r\n05 Jan 2024,5.1869\r\n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofengland", csv);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new OvernightFixingRequest(new BenchmarkName("SONIA"), new DateRange(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 5))),
            CancellationToken.None);

        result.Payload.Should().HaveCount(4);
        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[0].Value.Should().Be(0.051863m);
        result.Payload[3].Date.Should().Be(new DateOnly(2024, 1, 5));
        result.Payload[3].Value.Should().Be(0.051869m);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("NO_OBSERVATIONS"));
    }

    [Fact]
    public async Task FetchAsync_html_error_page_reports_schema_mismatch_not_empty_data()
    {
        // The IADB answers a bad series code (or a missing UsingCodes=Y) with a 302 to an HTML
        // error page served as HTTP 200 — no transport exception. It must not read as "no data".
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofengland", "<!DOCTYPE html>\r\n<html lang=\"en\"><body>Error Invalid series code value supplied.</body></html>");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new OvernightFixingRequest(new BenchmarkName("SONIA"), MakeRange()), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("SCHEMA_MISMATCH"))
            .Which.Severity.Should().Be(IssueSeverity.Error);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("NO_OBSERVATIONS"));
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
        result.Coverage.ReturnedPoints.Should().Be(0);
    }

    // ---------- Calendar-aware coverage (feature-calendar-aware-coverage) ----------

    [Fact]
    public async Task FetchAsync_business_day_range_reports_complete_coverage()
    {
        // Mon-Fri 2024-01-08..12 — 5 business days
        var csv = "DATE,IUDSOIA\r\n" +
                  "08 Jan 2024,5.19\r\n" +
                  "09 Jan 2024,5.19\r\n" +
                  "10 Jan 2024,5.19\r\n" +
                  "11 Jan 2024,5.19\r\n" +
                  "12 Jan 2024,5.19\r\n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofengland", csv);
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(
            new BenchmarkName("SONIA"),
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
        // Sat-Sun 2024-01-06..07 — 0 business days expected; source returns nothing
        var csv = "DATE,IUDSOIA\r\n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofengland", csv);
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(
            new BenchmarkName("SONIA"),
            new DateRange(new DateOnly(2024, 1, 6), new DateOnly(2024, 1, 7)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(0);
        result.Coverage.CoverageRatio.Should().Be(1.0m);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_holiday_within_range_is_not_flagged_as_gap()
    {
        // Mon 2024-12-23 through Fri 2024-12-27 — Wed 12-25 is a GBLO holiday (Christmas)
        // Calendar = GBLO so 4 business days expected (Mon, Tue, Thu, Fri)
        var csv = "DATE,IUDSOIA\r\n" +
                  "23 Dec 2024,4.75\r\n" +
                  "24 Dec 2024,4.75\r\n" +
                  "27 Dec 2024,4.75\r\n"; // skip 26 (Boxing Day) and 25 (Christmas) — both GBLO holidays
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofengland", csv);
        var gbloCalendar = HolidayCalendarFactory.Create("GBLO");
        var adapter = CreateAdapter(transport, calendar: gbloCalendar);

        var request = new OvernightFixingRequest(
            new BenchmarkName("SONIA"),
            new DateRange(new DateOnly(2024, 12, 23), new DateOnly(2024, 12, 27)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        // 23, 24, 27 are GBLO business days; 25 & 26 are holidays; neither weekend.
        result.Coverage.RequestedPoints.Should().Be(3);
        result.Coverage.ReturnedPoints.Should().Be(3);
        result.Coverage.Status.Should().Be(CompletenessStatus.Complete);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_missing_business_day_emits_unexpected_gap()
    {
        // Mon-Fri 2024-01-08..12, only Mon-Wed returned — Thu and Fri are unexpected gaps
        var csv = "DATE,IUDSOIA\r\n" +
                  "08 Jan 2024,5.19\r\n" +
                  "09 Jan 2024,5.19\r\n" +
                  "10 Jan 2024,5.19\r\n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofengland", csv);
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(
            new BenchmarkName("SONIA"),
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(5);
        result.Coverage.ReturnedPoints.Should().Be(3);
        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
        var gaps = result.Issues.Where(i => i.Code == new IssueCode("UNEXPECTED_GAP")).ToList();
        gaps.Should().HaveCount(2);
        gaps.Should().Contain(i => i.Message.Contains("2024-01-11"));
        gaps.Should().Contain(i => i.Message.Contains("2024-01-12"));
        gaps.Should().AllSatisfy(i => i.Severity.Should().Be(IssueSeverity.Warning));
    }
}
