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

using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Frankfurter;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Integration.Frankfurter;

public sealed class FrankfurterGoldenDataTests
{
    private static readonly FakeClock s_clock = new();

    [Fact]
    public async Task Frankfurter_parses_golden_snapshot()
    {
        // Arrange
        var goldenData = GoldenFileHelper.LoadText("testdata/frankfurter/fx-eurusd.json");
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("base=EUR", goldenData);
        var rawStore = new FakeRawDocumentStore();
        var adapter = new FrankfurterFxAdapter(
            transport,
            rawStore,
            s_clock,
            new WeekendOnlyCalendar("TEST"),
            Options.Create(new FrankfurterOptions()),
            NullLogger<FrankfurterFxAdapter>.Instance);

        var request = new FxHistoryRequest(
            [new FxPair(CurrencyCode.EUR, CurrencyCode.USD)],
            new DateRange(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4)));

        // Act
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        // Assert
        result.Payload.Should().HaveCount(3);

        var rates = result.Payload.OrderBy(r => r.Date).ToList();

        rates[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        rates[0].BaseCurrency.Should().Be(CurrencyCode.EUR);
        rates[0].QuoteCurrency.Should().Be(CurrencyCode.USD);
        rates[0].Rate.Should().Be(1.1037m);

        rates[1].Date.Should().Be(new DateOnly(2024, 1, 3));
        rates[1].BaseCurrency.Should().Be(CurrencyCode.EUR);
        rates[1].QuoteCurrency.Should().Be(CurrencyCode.USD);
        rates[1].Rate.Should().Be(1.0948m);

        rates[2].Date.Should().Be(new DateOnly(2024, 1, 4));
        rates[2].BaseCurrency.Should().Be(CurrencyCode.EUR);
        rates[2].QuoteCurrency.Should().Be(CurrencyCode.USD);
        rates[2].Rate.Should().Be(1.0951m);
    }
}
