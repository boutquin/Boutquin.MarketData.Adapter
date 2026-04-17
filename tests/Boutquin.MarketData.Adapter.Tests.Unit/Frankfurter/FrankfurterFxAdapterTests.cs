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
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Frankfurter;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
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
        var request = new FxHistoryRequest([new FxPair(CurrencyCode.EUR, CurrencyCode.USD)], MakeRange(), DataFrequency.Daily);
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_returns_false_for_non_daily_frequency()
    {
        var request = new FxHistoryRequest([new FxPair(CurrencyCode.EUR, CurrencyCode.USD)], MakeRange(), DataFrequency.Weekly);
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
    }

    [Fact]
    public void CanHandle_returns_false_for_empty_pairs()
    {
        var request = new FxHistoryRequest([], MakeRange(), DataFrequency.Daily);
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
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

        var request = new FxHistoryRequest([new FxPair(CurrencyCode.EUR, CurrencyCode.USD)], MakeRange());
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().HaveCount(2);
        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[0].BaseCurrency.Should().Be(CurrencyCode.EUR);
        result.Payload[0].QuoteCurrency.Should().Be(CurrencyCode.USD);
        result.Payload[0].Rate.Should().Be(1.0844m);
        result.Payload[1].Rate.Should().Be(1.0856m);
    }

    [Fact]
    public async Task FetchAsync_continues_on_per_pair_failure()
    {
        var json = """
            {"rates":{"2024-01-02":{"GBP":0.8601}}}
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("GBP", json);
        // No response for USD pair — will throw
        var adapter = CreateAdapter(transport);

        var request = new FxHistoryRequest(
            [new FxPair(CurrencyCode.EUR, CurrencyCode.USD), new FxPair(CurrencyCode.EUR, CurrencyCode.GBP)],
            MakeRange());
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().HaveCount(1);
        result.Issues.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FetchAsync_populates_provenance()
    {
        var json = """{"rates":{"2024-01-02":{"USD":1.08}}}""";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("frankfurter", json);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new FxHistoryRequest([new FxPair(CurrencyCode.EUR, CurrencyCode.USD)], MakeRange()), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        result.Provenance[0].Provider.Should().Be(new ProviderCode("frankfurter"));
    }
}
