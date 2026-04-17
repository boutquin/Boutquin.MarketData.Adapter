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
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Fred;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
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
            Options.Create(options ?? new FredOptions { ApiKey = "test-key", NormalizePercentToDecimal = true }),
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
}
