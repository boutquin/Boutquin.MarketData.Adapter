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

using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Fred;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Integration.Fred;

public sealed class FredGoldenDataTests
{
    private static readonly FakeClock s_clock = new();

    [Fact]
    public async Task Fred_parses_golden_snapshot()
    {
        // Arrange
        var goldenData = GoldenFileHelper.LoadText("testdata/fred/series-dgs10.json");
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("series_id=DGS10", goldenData);
        var rawStore = new FakeRawDocumentStore();
        var adapter = new FredEconomicSeriesAdapter(
            transport,
            rawStore,
            s_clock,
            new WeekendOnlyCalendar("TEST"),
            Options.Create(new FredOptions { ApiKey = "test-key", NormalizePercentToDecimal = true }),
            NullLogger<FredEconomicSeriesAdapter>.Instance);

        var request = new EconomicSeriesRequest(
            new EconomicSeriesId("DGS10"),
            new DateRange(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31)));

        // Act
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        // Assert — 4th observation has value "." and is skipped, leaving 3
        result.Payload.Should().HaveCount(3);

        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[0].Value.Should().Be(0.0395m);
        result.Payload[0].Units.Should().Be("decimal");

        result.Payload[1].Date.Should().Be(new DateOnly(2024, 1, 3));
        result.Payload[1].Value.Should().Be(0.0391m);
        result.Payload[1].Units.Should().Be("decimal");

        result.Payload[2].Date.Should().Be(new DateOnly(2024, 1, 5));
        result.Payload[2].Value.Should().Be(0.0405m);
        result.Payload[2].Units.Should().Be("decimal");
    }
}
