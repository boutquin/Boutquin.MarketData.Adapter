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
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.BankOfCanada;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.BankOfCanada;

public sealed class BankOfCanadaFxAdapterTests
{
    private static readonly FakeClock s_clock = new();
    private static readonly DateRange s_range = new(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 9));

    private static BankOfCanadaFxAdapter CreateAdapter(FakeHttpDataTransport transport, FakeRawDocumentStore? rawStore = null) =>
        new(transport, rawStore ?? new FakeRawDocumentStore(), s_clock, new WeekendOnlyCalendar("TEST"),
            Options.Create(new BankOfCanadaOptions()), NullLogger<BankOfCanadaFxAdapter>.Instance);

    private static FxHistoryRequest Request(params FxPair[] pairs) => new(pairs, s_range);

    [Fact]
    public async Task FetchAsync_rejects_multi_pair_envelope_without_pair_identity()
    {
        var transport = new FakeHttpDataTransport();
        var request = Request(new FxPair(Currency.USD, Currency.CAD), new FxPair(Currency.CAD, Currency.USD));
        var adapter = CreateAdapter(transport);

        adapter.CanHandle(request).Should().BeFalse();
        Func<Task> act = () => adapter.FetchAsync(request);
        await act.Should().ThrowAsync<ArgumentException>();
        transport.RequestedUris.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Currency.USD, Currency.CAD, "FXUSDCAD", "1.36")]
    [InlineData(Currency.CAD, Currency.USD, "FXCADUSD", "0.73")]
    public async Task FetchAsync_single_pair_retains_series_identity_in_provenance(
        Currency baseCurrency, Currency quoteCurrency, string series, string rateText)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo(series, $"{{\"observations\":[{{\"d\":\"2024-01-08\",\"{series}\":{{\"v\":\"{rateText}\"}}}}]}}");
        var rawStore = new FakeRawDocumentStore();
        var request = Request(new FxPair(baseCurrency, quoteCurrency));
        var adapter = CreateAdapter(transport, rawStore);

        adapter.CanHandle(request).Should().BeTrue();
        var result = await adapter.FetchAsync(request);

        result.Payload.Should().ContainSingle().Which.Rate.Should().Be(decimal.Parse(rateText, System.Globalization.CultureInfo.InvariantCulture));
        result.Provenance.Should().ContainSingle().Which.Dataset.Should().Be(series);
        rawStore.SavedKeys.Should().ContainSingle().Which.Should().Contain(series);
        result.Coverage.RequestedPoints.Should().Be(2);
        result.Coverage.ReturnedPoints.Should().Be(1);
        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(cancellation.Token));

        Func<Task> act = () => CreateAdapter(transport).FetchAsync(
            Request(new FxPair(Currency.USD, Currency.CAD)), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_does_not_expose_transport_exception_detail()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new HttpRequestException("credential file /private/fx-key.txt failed"));

        var result = await CreateAdapter(transport).FetchAsync(Request(new FxPair(Currency.USD, Currency.CAD)));

        result.Issues.Should().Contain(i => i.Code == new IssueCode("FETCH_FAILED") &&
            !i.Message.Contains("/private/fx-key.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchAsync_empty_single_pair_response_reports_no_rates()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("FXUSDCAD", "{\"observations\":[]}");

        var result = await CreateAdapter(transport).FetchAsync(Request(new FxPair(Currency.USD, Currency.CAD)));

        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new IssueCode("NO_RATES"));
    }
}
