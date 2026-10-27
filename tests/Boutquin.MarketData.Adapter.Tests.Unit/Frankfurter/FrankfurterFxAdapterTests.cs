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

using Boutquin.Domain.Enumerations;
using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.Frankfurter;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.Frankfurter;

public sealed class FrankfurterFxAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static FrankfurterFxAdapter CreateAdapter(
        FakeHttpDataTransport transport,
        FakeRawDocumentStore? rawStore = null,
        FrankfurterOptions? options = null,
        IBusinessCalendar? calendar = null)
    {
        return new FrankfurterFxAdapter(
            transport,
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(options ?? new FrankfurterOptions()),
            NullLogger<FrankfurterFxAdapter>.Instance);
    }

    private static DateRange MakeRange(int year = 2024, int fromMonth = 1, int fromDay = 1, int toMonth = 1, int toDay = 31) =>
        new(new DateOnly(year, fromMonth, fromDay), new DateOnly(year, toMonth, toDay));

    [Fact]
    public void ProviderKey_returns_frankfurter() =>
        CreateAdapter(new FakeHttpDataTransport()).ProviderKey.Should().Be(new ProviderCode("frankfurter"));

    [Fact]
    public void Priority_returns_100() =>
        CreateAdapter(new FakeHttpDataTransport()).Priority.Should().Be(100);

    [Fact]
    public void CanHandle_returns_true_for_daily_with_pairs()
    {
        var request = new FxHistoryRequest([new FxPair(Currency.EUR, Currency.USD)], MakeRange(), DataFrequency.Daily);
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_returns_false_for_non_daily_frequency()
    {
        var request = new FxHistoryRequest([new FxPair(Currency.EUR, Currency.USD)], MakeRange(), DataFrequency.Weekly);
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
    }

    [Fact]
    public void Empty_pairs_are_rejected_at_the_request_boundary()
    {
        Action act = () => new FxHistoryRequest([], MakeRange(), DataFrequency.Daily);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("pairs.Count");
    }

    [Fact]
    public async Task FetchAsync_parses_json_into_fx_rates()
    {
        var json = """
            {"rates":{"2024-01-02":{"USD":1.0844},"2024-01-03":{"USD":1.0856}}}
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("frankfurter", json);
        var rawStore = new FakeRawDocumentStore();
        var adapter = CreateAdapter(transport, rawStore);

        var request = new FxHistoryRequest([new FxPair(Currency.EUR, Currency.USD)], MakeRange());
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().HaveCount(2);
        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[0].Rate.Should().Be(1.0844m);
        result.Payload[1].Rate.Should().Be(1.0856m);
    }

    [Fact]
    public async Task FetchAsync_records_a_single_pair_failure()
    {
        var transport = new FakeHttpDataTransport();
        // No response for USD pair — will throw
        var adapter = CreateAdapter(transport);

        var request = new FxHistoryRequest(
            [new FxPair(Currency.EUR, Currency.USD)],
            MakeRange());
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("FETCH_FAILED") && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public async Task FetchAsync_fx_propagates_caller_cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(source.Token));
        var adapter = CreateAdapter(transport);

        Func<Task> act = () => adapter.FetchAsync(new FxHistoryRequest(
            [new FxPair(Currency.EUR, Currency.USD)], MakeRange()), source.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_single_pair_coverage_counts_its_own_gaps()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondDynamic(_ => true, uri => uri.Query.Contains("symbols=USD", StringComparison.Ordinal)
            ? """{"rates":{"2024-01-08":{"USD":1},"2024-01-09":{"USD":1},"2024-01-10":{"USD":1},"2024-01-11":{"USD":1},"2024-01-12":{"USD":1}}}"""
            : """{"rates":{"2024-01-08":{"GBP":1}}}""");
        var result = await CreateAdapter(transport).FetchAsync(new FxHistoryRequest(
            [new FxPair(Currency.EUR, Currency.GBP)],
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12))));

        result.Coverage.RequestedPoints.Should().Be(5);
        result.Coverage.ReturnedPoints.Should().Be(1);
        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
    }

    [Fact]
    public async Task FetchAsync_populates_provenance()
    {
        var json = """{"rates":{"2024-01-02":{"USD":1.08}}}""";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("frankfurter", json);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new FxHistoryRequest([new FxPair(Currency.EUR, Currency.USD)], MakeRange()), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        result.Provenance[0].Provider.Should().Be(new ProviderCode("frankfurter"));
    }

    // ---------- Outgoing request URL ----------
    // Every other test in this class stubs the transport on a URL substring, which matches a
    // stale endpoint as readily as a live one. These pin the full URL, so a drift in the default
    // endpoint or in the path shape fails here instead of surfacing as FETCH_FAILED in production.
    // The unversioned path (https://api.frankfurter.dev/{from}..{to}) answers HTTP 404; the
    // service only serves the time-series route under /v1.

    [Fact]
    public async Task FetchAsync_with_default_options_requests_versioned_time_series_url()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("frankfurter", """{"rates":{"2001-05-21":{"CAD":1.5344}}}""");
        var adapter = CreateAdapter(transport);

        var request = new FxHistoryRequest(
            [new FxPair(Currency.USD, Currency.CAD)],
            new DateRange(new DateOnly(2001, 5, 21), new DateOnly(2001, 6, 1)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        transport.RequestedUris.Should().ContainSingle()
            .Which.AbsoluteUri.Should().Be("https://api.frankfurter.dev/v1/2001-05-21..2001-06-01?base=USD&symbols=CAD");
        result.Provenance.Should().ContainSingle()
            .Which.SourceUrl.Should().Be("https://api.frankfurter.dev/v1/2001-05-21..2001-06-01?base=USD&symbols=CAD");
    }

    [Fact]
    public void Default_BaseUrl_is_the_versioned_endpoint() =>
        new FrankfurterOptions().BaseUrl.Should().Be("https://api.frankfurter.dev/v1");

    [Theory]
    [InlineData("https://fx.example.test/v1")]
    [InlineData("https://fx.example.test/v1/")]
    public async Task FetchAsync_appends_time_series_path_to_configured_BaseUrl(string baseUrl)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fx.example.test", """{"rates":{"2024-01-02":{"USD":1.08}}}""");
        var adapter = CreateAdapter(transport, options: new FrankfurterOptions { BaseUrl = baseUrl });

        await adapter.FetchAsync(new FxHistoryRequest([new FxPair(Currency.EUR, Currency.USD)], MakeRange()), CancellationToken.None);

        transport.RequestedUris.Should().ContainSingle()
            .Which.AbsoluteUri.Should().Be("https://fx.example.test/v1/2024-01-01..2024-01-31?base=EUR&symbols=USD");
    }

    // ---------- Calendar-aware coverage (feature-calendar-aware-coverage) ----------
    // Frankfurter uses WeekendOnlyCalendar — TARGET publishes on weekdays; the API already
    // pre-filters to published days, but the adapter still computes coverage against the
    // full business-day expectation for consumer diagnostics.

    [Fact]
    public async Task FetchAsync_business_day_range_reports_complete_coverage()
    {
        var json = """
            {"rates":{
              "2024-01-08":{"USD":1.08},"2024-01-09":{"USD":1.08},"2024-01-10":{"USD":1.08},
              "2024-01-11":{"USD":1.08},"2024-01-12":{"USD":1.08}}}
            """;
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("frankfurter", json);
        var adapter = CreateAdapter(transport);

        var request = new FxHistoryRequest(
            [new FxPair(Currency.EUR, Currency.USD)],
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
        var json = """{"rates":{}}""";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("frankfurter", json);
        var adapter = CreateAdapter(transport);

        var request = new FxHistoryRequest(
            [new FxPair(Currency.EUR, Currency.USD)],
            new DateRange(new DateOnly(2024, 1, 6), new DateOnly(2024, 1, 7)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(0);
        result.Coverage.CoverageRatio.Should().Be(1.0m);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_missing_business_day_emits_unexpected_gap()
    {
        var json = """
            {"rates":{
              "2024-01-08":{"USD":1.08},"2024-01-09":{"USD":1.08},"2024-01-10":{"USD":1.08}}}
            """;
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("frankfurter", json);
        var adapter = CreateAdapter(transport);

        var request = new FxHistoryRequest(
            [new FxPair(Currency.EUR, Currency.USD)],
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
        var gaps = result.Issues.Where(i => i.Code == new IssueCode("UNEXPECTED_GAP")).ToList();
        gaps.Should().HaveCount(2);
    }

    [Fact]
    public async Task FetchAsync_excludes_rates_outside_requested_range_from_payload_and_coverage()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("frankfurter", """
            {"rates":{"2024-01-01":{"USD":1.1},"2024-01-02":{"USD":1.2},"2024-01-05":{"USD":1.3}}}
            """);
        var request = new FxHistoryRequest(
            [new FxPair(Currency.EUR, Currency.USD)],
            new DateRange(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4)));

        var result = await CreateAdapter(transport).FetchAsync(request);

        result.Payload.Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Coverage.RequestedPoints.Should().Be(3);
        result.Coverage.ReturnedPoints.Should().Be(1);
        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
        result.Issues.Count(i => i.Code == new IssueCode("UNEXPECTED_GAP")).Should().Be(2);
    }
}
