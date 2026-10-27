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
using Boutquin.MarketData.Adapter.NewYorkFed;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Calendars.Holidays;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.NewYorkFed;

public sealed class NewYorkFedSofrAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static NewYorkFedSofrAdapter CreateAdapter(
        FakeHttpDataTransport transport,
        FakeRawDocumentStore? rawStore = null,
        NewYorkFedOptions? options = null,
        IBusinessCalendar? calendar = null)
    {
        return new NewYorkFedSofrAdapter(
            transport,
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(options ?? new NewYorkFedOptions()),
            NullLogger<NewYorkFedSofrAdapter>.Instance);
    }

    private static DateRange MakeRange(int year = 2024, int fromMonth = 1, int fromDay = 1, int toMonth = 1, int toDay = 31) =>
        new(new DateOnly(year, fromMonth, fromDay), new DateOnly(year, toMonth, toDay));

    [Fact]
    public void ProviderKey_returns_nyfed_sofr() =>
        CreateAdapter(new FakeHttpDataTransport()).ProviderKey.Should().Be(new ProviderCode("nyfed-sofr"));

    [Fact]
    public void Priority_returns_100() =>
        CreateAdapter(new FakeHttpDataTransport()).Priority.Should().Be(100);

    [Fact]
    public void CanHandle_returns_true_for_sofr()
    {
        var request = new OvernightFixingRequest(new BenchmarkName("SOFR"), MakeRange());
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_returns_false_for_estr()
    {
        var request = new OvernightFixingRequest(new BenchmarkName("ESTR"), MakeRange());
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
    }

    [Fact]
    public async Task FetchAsync_rejects_foreign_benchmark_before_transport()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("newyorkfed", """{"refRates":[{"effectiveDate":"2024-01-02","percentRate":5.31}]}""");
        var request = new OvernightFixingRequest(new BenchmarkName("CORRA"), MakeRange());

        Func<Task> act = () => CreateAdapter(transport).FetchAsync(request);

        await act.Should().ThrowAsync<ArgumentException>();
        transport.RequestedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_parses_json_into_scalar_observations()
    {
        var json = """
            {"refRates":[{"effectiveDate":"2024-01-02","percentRate":5.31},{"effectiveDate":"2024-01-03","percentRate":5.32}]}
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("newyorkfed", json);
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(new BenchmarkName("SOFR"), MakeRange());
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().HaveCount(2);
        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[0].Value.Should().Be(0.0531m);
        result.Payload[0].Units.Should().Be("decimal-rate");
        result.Payload[1].Value.Should().Be(0.0532m);
    }

    [Fact]
    public async Task FetchAsync_populates_provenance()
    {
        var json = """{"refRates":[{"effectiveDate":"2024-01-02","percentRate":5.31}]}""";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("newyorkfed", json);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new OvernightFixingRequest(new BenchmarkName("SOFR"), MakeRange()), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        result.Provenance[0].Provider.Should().Be(new ProviderCode("nyfed-sofr"));
    }

    [Fact]
    public async Task FetchAsync_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(cancellation.Token));
        var request = new OvernightFixingRequest(new BenchmarkName("SOFR"), MakeRange());

        Func<Task> act = () => CreateAdapter(transport).FetchAsync(request, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_keeps_valid_fixings_when_later_rate_has_unsupported_kind()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("newyorkfed", """
            {"refRates":[{"effectiveDate":"2024-01-02","percentRate":5.31},{"effectiveDate":"2024-01-03","percentRate":{}}]}
            """);
        var request = new OvernightFixingRequest(new BenchmarkName("SOFR"), MakeRange(2024, 1, 2, 1, 3));

        var result = await CreateAdapter(transport).FetchAsync(request);

        result.Payload.Should().ContainSingle(o => o.Date == new DateOnly(2024, 1, 2) && o.Value == 0.0531m);
        result.Issues.Should().Contain(i => i.Code == new IssueCode("MALFORMED_ROW"));
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("FETCH_FAILED"));
    }

    [Fact]
    public async Task FetchAsync_keeps_valid_fixings_when_later_date_has_unsupported_kind()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("newyorkfed", """
            {"refRates":[{"effectiveDate":"2024-01-02","percentRate":5.31},{"effectiveDate":{},"percentRate":5.32}]}
            """);
        var request = new OvernightFixingRequest(new BenchmarkName("SOFR"), MakeRange(2024, 1, 2, 1, 3));

        var result = await CreateAdapter(transport).FetchAsync(request);

        result.Payload.Should().ContainSingle(o => o.Date == new DateOnly(2024, 1, 2) && o.Value == 0.0531m);
        result.Issues.Should().Contain(i => i.Code == new IssueCode("MALFORMED_ROW"));
    }

    [Fact]
    public async Task FetchAsync_failure_issue_does_not_disclose_exception_detail()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new IOException("credential file /private/secret/api-key.txt failed"));
        var request = new OvernightFixingRequest(new BenchmarkName("SOFR"), MakeRange());

        var result = await CreateAdapter(transport).FetchAsync(request);

        result.Issues.Should().ContainSingle().Which.Message.Should().Be("NY Fed SOFR fetch failed.");
    }

    // ---------- Calendar-aware coverage (feature-calendar-aware-coverage) ----------

    [Fact]
    public async Task FetchAsync_business_day_range_reports_complete_coverage()
    {
        var json = """
            {"refRates":[
              {"effectiveDate":"2024-01-08","percentRate":5.31},
              {"effectiveDate":"2024-01-09","percentRate":5.31},
              {"effectiveDate":"2024-01-10","percentRate":5.31},
              {"effectiveDate":"2024-01-11","percentRate":5.31},
              {"effectiveDate":"2024-01-12","percentRate":5.31}]}
            """;
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("newyorkfed", json);
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(
            new BenchmarkName("SOFR"),
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
        var json = """{"refRates":[]}""";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("newyorkfed", json);
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(
            new BenchmarkName("SOFR"),
            new DateRange(new DateOnly(2024, 1, 6), new DateOnly(2024, 1, 7)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(0);
        result.Coverage.CoverageRatio.Should().Be(1.0m);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_holiday_within_range_is_not_flagged_as_gap()
    {
        // MLK Day 2024 = Mon Jan 15 (USNY holiday). Request Mon-Fri Jan 15..19.
        var json = """
            {"refRates":[
              {"effectiveDate":"2024-01-16","percentRate":5.31},
              {"effectiveDate":"2024-01-17","percentRate":5.31},
              {"effectiveDate":"2024-01-18","percentRate":5.31},
              {"effectiveDate":"2024-01-19","percentRate":5.31}]}
            """;
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("newyorkfed", json);
        var usny = HolidayCalendarFactory.Create("USNY");
        var adapter = CreateAdapter(transport, calendar: usny);

        var request = new OvernightFixingRequest(
            new BenchmarkName("SOFR"),
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
        var json = """
            {"refRates":[
              {"effectiveDate":"2024-01-08","percentRate":5.31},
              {"effectiveDate":"2024-01-09","percentRate":5.31},
              {"effectiveDate":"2024-01-10","percentRate":5.31}]}
            """;
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("newyorkfed", json);
        var adapter = CreateAdapter(transport);

        var request = new OvernightFixingRequest(
            new BenchmarkName("SOFR"),
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
        var gaps = result.Issues.Where(i => i.Code == new IssueCode("UNEXPECTED_GAP")).ToList();
        gaps.Should().HaveCount(2);
    }
}
