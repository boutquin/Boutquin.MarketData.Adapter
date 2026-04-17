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
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.TwelveData;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Integration.TwelveData;

public sealed class TwelveDataGoldenDataTests
{
    private static readonly FakeClock s_clock = new();

    [Fact]
    public async Task TwelveData_parses_golden_snapshot()
    {
        // Arrange
        var goldenData = GoldenFileHelper.LoadText("testdata/twelvedata/daily-aapl.json");
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("symbol=AAPL", goldenData);
        var rawStore = new FakeRawDocumentStore();
        var adapter = new TwelveDataPriceBarAdapter(
            transport,
            rawStore,
            s_clock,
            new WeekendOnlyCalendar("TEST"),
            Options.Create(new TwelveDataOptions { ApiKey = "test-key" }),
            NullLogger<TwelveDataPriceBarAdapter>.Instance);

        var request = new PriceHistoryRequest(
            [new Symbol("AAPL")],
            new DateRange(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4)));

        // Act
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        // Assert — values are strings in JSON but parsed to decimal/long
        result.Payload.Should().HaveCount(3);

        // Twelve Data returns values in reverse chronological order
        var bar0 = result.Payload[0];
        bar0.Date.Should().Be(new DateOnly(2024, 1, 4));
        bar0.Open.Should().Be(182.15m);
        bar0.High.Should().Be(183.09m);
        bar0.Low.Should().Be(180.88m);
        bar0.Close.Should().Be(181.91m);
        bar0.AdjustedClose.Should().Be(181.91m); // No adjClose in Twelve Data; close is used
        bar0.Volume.Should().Be(71878500L);

        var bar1 = result.Payload[1];
        bar1.Date.Should().Be(new DateOnly(2024, 1, 3));
        bar1.Open.Should().Be(184.22m);
        bar1.High.Should().Be(185.88m);
        bar1.Low.Should().Be(183.43m);
        bar1.Close.Should().Be(184.25m);
        bar1.AdjustedClose.Should().Be(184.25m);
        bar1.Volume.Should().Be(58414500L);

        var bar2 = result.Payload[2];
        bar2.Date.Should().Be(new DateOnly(2024, 1, 2));
        bar2.Open.Should().Be(187.15m);
        bar2.High.Should().Be(188.44m);
        bar2.Low.Should().Be(183.89m);
        bar2.Close.Should().Be(185.64m);
        bar2.AdjustedClose.Should().Be(185.64m);
        bar2.Volume.Should().Be(82488700L);
    }
}
