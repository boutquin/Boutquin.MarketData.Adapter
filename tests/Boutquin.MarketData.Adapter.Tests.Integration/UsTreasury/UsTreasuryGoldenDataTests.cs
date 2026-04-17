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
using Boutquin.MarketData.Adapter.UsTreasury;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Integration.UsTreasury;

public sealed class UsTreasuryGoldenDataTests
{
    private static readonly FakeClock s_clock = new();

    [Fact]
    public async Task UsTreasury_parses_golden_snapshot()
    {
        // Arrange
        var goldenData = GoldenFileHelper.LoadText("testdata/ustreasury/yield-curve.xml");
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("data=daily_treasury_yield_curve", goldenData);
        var rawStore = new FakeRawDocumentStore();
        var adapter = new UsTreasuryYieldAdapter(
            transport,
            rawStore,
            s_clock,
            new WeekendOnlyCalendar("TEST"),
            Options.Create(new UsTreasuryOptions()),
            NullLogger<UsTreasuryYieldAdapter>.Instance);

        var request = new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), new DateOnly(2024, 1, 15));

        // Act
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        // Assert
        result.Payload.Should().HaveCount(5);

        result.Payload.Should().Contain(q => q.Tenor == "1M" && q.Rate == 0.0553m);
        result.Payload.Should().Contain(q => q.Tenor == "3M" && q.Rate == 0.0540m);
        result.Payload.Should().Contain(q => q.Tenor == "6M" && q.Rate == 0.0527m);
        result.Payload.Should().Contain(q => q.Tenor == "1Y" && q.Rate == 0.0486m);
        result.Payload.Should().Contain(q => q.Tenor == "5Y" && q.Rate == 0.0402m);
    }
}
