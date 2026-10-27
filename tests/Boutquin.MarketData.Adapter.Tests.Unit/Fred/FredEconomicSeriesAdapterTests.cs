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
using Boutquin.MarketData.Adapter.Fred;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Calendars.Holidays;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.Fred;

public sealed class FredEconomicSeriesAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static FredEconomicSeriesAdapter CreateAdapter(
        FakeHttpDataTransport transport,
        FakeRawDocumentStore? rawStore = null,
        FredOptions? options = null,
        IBusinessCalendar? calendar = null)
    {
        return new FredEconomicSeriesAdapter(
            transport,
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(options ?? new FredOptions { ApiKey = "test-key" }),
            NullLogger<FredEconomicSeriesAdapter>.Instance);
    }

    private static DateRange MakeRange(int year = 2024, int fromMonth = 1, int fromDay = 1, int toMonth = 1, int toDay = 31) =>
        new(new DateOnly(year, fromMonth, fromDay), new DateOnly(year, toMonth, toDay));

    [Fact]
    public void ProviderKey_returns_fred() =>
        CreateAdapter(new FakeHttpDataTransport()).ProviderKey.Should().Be(new ProviderCode("fred"));

    [Fact]
    public void Priority_returns_100() =>
        CreateAdapter(new FakeHttpDataTransport()).Priority.Should().Be(100);

    [Fact]
    public void CanHandle_returns_true_for_non_empty_series_id()
    {
        var request = new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), MakeRange());
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public async Task FetchAsync_skips_dot_sentinel_values()
    {
        var json = """
            {"observations":[{"date":"2024-01-02","value":"5.25"},{"date":"2024-01-03","value":"."},{"date":"2024-01-04","value":"5.30"}]}
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", json);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), MakeRange()), CancellationToken.None);

        result.Payload.Should().HaveCount(2);
        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[1].Date.Should().Be(new DateOnly(2024, 1, 4));
    }

    [Fact]
    public async Task FetchAsync_normalizes_percent_to_decimal()
    {
        var json = """
            {"observations":[{"date":"2024-01-02","value":"5.25"}]}
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", json);
        var adapter = CreateAdapter(transport, options: new FredOptions { ApiKey = "test-key", NormalizePercentToDecimal = true });

        var result = await adapter.FetchAsync(new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), MakeRange()), CancellationToken.None);

        result.Payload[0].Value.Should().Be(0.0525m);
    }

    [Fact]
    public async Task FetchAsync_preserves_non_percent_series_by_default()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", """{"observations":[{"date":"2024-01-02","value":"1000"}]}""");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new EconomicSeriesRequest(new EconomicSeriesId("GDP"), MakeRange()), CancellationToken.None);

        result.Payload.Should().ContainSingle();
        result.Payload[0].Value.Should().Be(1000m);
        result.Payload[0].Units.Should().NotBe("decimal");
    }

    [Fact]
    public async Task FetchAsync_preserves_raw_value_when_normalization_disabled()
    {
        var json = """
            {"observations":[{"date":"2024-01-02","value":"5.25"}]}
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", json);
        var adapter = CreateAdapter(transport, options: new FredOptions { ApiKey = "test-key", NormalizePercentToDecimal = false });

        var result = await adapter.FetchAsync(new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), MakeRange()), CancellationToken.None);

        result.Payload[0].Value.Should().Be(5.25m);
        result.Payload[0].Units.Should().Be("unknown");
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
            new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), MakeRange()), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_does_not_disclose_transport_exception_details()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new IOException("credential=/private/keys/sentinel-secret.txt"));
        var result = await CreateAdapter(transport).FetchAsync(
            new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), MakeRange()), CancellationToken.None);

        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("FETCH_FAILED"));
        result.Issues[0].Message.Should().NotContain("sentinel-secret");
    }

    [Theory]
    [InlineData("GDP&observation_start=1900-01-01")]
    [InlineData("GDP#truncated")]
    public async Task FetchAsync_encodes_series_id_as_one_query_value(string seriesId)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", """{"observations":[]}""");
        await CreateAdapter(transport).FetchAsync(
            new EconomicSeriesRequest(new EconomicSeriesId(seriesId), MakeRange()), CancellationToken.None);

        transport.RequestedUris.Should().ContainSingle();
        var uri = transport.RequestedUris[0];
        uri.Fragment.Should().BeEmpty();
        uri.Query.Should().Contain($"series_id={Uri.EscapeDataString(seriesId)}");
        uri.Query.Should().Contain("observation_start=2024-01-01");
    }

    [Fact]
    public async Task FetchAsync_keeps_series_id_with_path_delimiters_in_one_raw_key_segment()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", """{"observations":[]}""");
        var rawStore = new FakeRawDocumentStore();
        await CreateAdapter(transport, rawStore).FetchAsync(
            new EconomicSeriesRequest(new EconomicSeriesId("GDP/../CPI"), MakeRange()), CancellationToken.None);

        rawStore.SavedKeys.Should().ContainSingle();
        rawStore.SavedKeys[0].Should().Contain("GDP%2F..%2FCPI");
    }

    [Fact]
    public void CanHandle_rejects_intraday_frequency()
    {
        var request = new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), MakeRange(), DataFrequency.Intraday);
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
    }

    [Fact]
    public async Task FetchAsync_populates_provenance()
    {
        var json = """{"observations":[{"date":"2024-01-02","value":"5.25"}]}""";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", json);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), MakeRange()), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        result.Provenance[0].Provider.Should().Be(new ProviderCode("fred"));
    }

    // ---------- Calendar-aware coverage (feature-calendar-aware-coverage) ----------

    [Fact]
    public async Task FetchAsync_business_day_range_reports_complete_coverage()
    {
        var json = """
            {"observations":[
              {"date":"2024-01-08","value":"5.25"},
              {"date":"2024-01-09","value":"5.25"},
              {"date":"2024-01-10","value":"5.25"},
              {"date":"2024-01-11","value":"5.25"},
              {"date":"2024-01-12","value":"5.25"}]}
            """;
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", json);
        var adapter = CreateAdapter(transport);

        var request = new EconomicSeriesRequest(
            new EconomicSeriesId("DGS10"),
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
        var json = """{"observations":[]}""";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", json);
        var adapter = CreateAdapter(transport);

        var request = new EconomicSeriesRequest(
            new EconomicSeriesId("DGS10"),
            new DateRange(new DateOnly(2024, 1, 6), new DateOnly(2024, 1, 7)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(0);
        result.Coverage.CoverageRatio.Should().Be(1.0m);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_holiday_within_range_is_not_flagged_as_gap()
    {
        // MLK Day 2024 = Mon Jan 15 (USNY holiday).
        var json = """
            {"observations":[
              {"date":"2024-01-16","value":"5.25"},
              {"date":"2024-01-17","value":"5.25"},
              {"date":"2024-01-18","value":"5.25"},
              {"date":"2024-01-19","value":"5.25"}]}
            """;
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", json);
        var usny = HolidayCalendarFactory.Create("USNY");
        var adapter = CreateAdapter(transport, calendar: usny);

        var request = new EconomicSeriesRequest(
            new EconomicSeriesId("DGS10"),
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
            {"observations":[
              {"date":"2024-01-08","value":"5.25"},
              {"date":"2024-01-09","value":"5.25"},
              {"date":"2024-01-10","value":"5.25"}]}
            """;
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", json);
        var adapter = CreateAdapter(transport);

        var request = new EconomicSeriesRequest(
            new EconomicSeriesId("DGS10"),
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
        var gaps = result.Issues.Where(i => i.Code == new IssueCode("UNEXPECTED_GAP")).ToList();
        gaps.Should().HaveCount(2);
    }
}
