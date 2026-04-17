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
using Boutquin.MarketData.Adapter.NewYorkFed;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
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
}
