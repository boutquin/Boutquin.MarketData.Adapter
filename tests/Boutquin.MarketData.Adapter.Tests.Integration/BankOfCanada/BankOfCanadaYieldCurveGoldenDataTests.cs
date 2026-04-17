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
using Boutquin.MarketData.Adapter.BankOfCanada;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Integration.BankOfCanada;

public sealed class BankOfCanadaYieldCurveGoldenDataTests
{
    private static readonly FakeClock s_clock = new();

    [Fact]
    public async Task BankOfCanada_YieldCurve_parses_golden_snapshot()
    {
        // Arrange
        var goldenData = GoldenFileHelper.LoadText("testdata/bankofcanada/yield-curve.json");
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("observations/group/BD.CDN.ZERO", goldenData);
        var rawStore = new FakeRawDocumentStore();
        var adapter = new BankOfCanadaYieldCurveAdapter(
            transport,
            rawStore,
            s_clock,
            new WeekendOnlyCalendar("TEST"),
            Options.Create(new BankOfCanadaOptions()),
            NullLogger<BankOfCanadaYieldCurveAdapter>.Instance);

        var request = new YieldCurveQuoteRequest(new YieldCurveId("CAD-ZERO"), new DateOnly(2024, 1, 15));

        // Act
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        // Assert
        result.Payload.Should().HaveCount(5);

        var quotes = result.Payload.OrderBy(q => q.Tenor).ToList();

        quotes.Should().Contain(q => q.Tenor == "3M" && q.Rate == 0.0501m);
        quotes.Should().Contain(q => q.Tenor == "6M" && q.Rate == 0.0488m);
        quotes.Should().Contain(q => q.Tenor == "1Y" && q.Rate == 0.0452m);
        quotes.Should().Contain(q => q.Tenor == "2Y" && q.Rate == 0.0391m);
        quotes.Should().Contain(q => q.Tenor == "5Y" && q.Rate == 0.0344m);
    }
}
