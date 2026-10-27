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

using Boutquin.MarketData.Abstractions.Diagnostics;
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
        // Arrange — the snapshot is the Bank's zero-coupon CSV lookup response for
        // dFrom=2024-01-08&dTo=2024-01-15, captured 2026-09-21: a Date column, 120 ZCnnnYR
        // columns (0.25y..30y), a trailing empty column, six business-day rows, no "na" cells.
        var goldenData = GoldenFileHelper.LoadText("testdata/bankofcanada/zero-coupon-yield-curve.csv");
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("lookupPage=lookup_yield_curve.php", goldenData);
        var rawStore = new FakeRawDocumentStore();
        var adapter = new BankOfCanadaYieldCurveAdapter(
            transport,
            rawStore,
            s_clock,
            new WeekendOnlyCalendar("TEST"),
            Options.Create(new BankOfCanadaOptions()),
            NullLogger<BankOfCanadaYieldCurveAdapter>.Instance);

        var request = new YieldCurveQuoteRequest(new YieldCurveId("BOC-ZERO"), new DateOnly(2024, 1, 15));

        // Act
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        // Assert — the latest row (2024-01-15) is served, one quote per term column.
        result.Payload.Should().HaveCount(120);
        result.Payload.Should().OnlyContain(q => q.Date == new DateOnly(2024, 1, 15));
        result.Payload.Select(q => q.Tenor).Should().OnlyHaveUniqueItems();

        var lastRow = goldenData.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1]
            .Split(',', StringSplitOptions.TrimEntries);
        for (var node = 1; node <= 120; node++)
        {
            // The provider's published grid is quarterly: derive expected tenor from
            // grid position independently of production's ZCnnnYR header parser.
            var months = node * 3;
            var expectedTenor = months % 12 == 0 ? $"{months / 12}Y" : $"{months}M";
            var expectedRate = decimal.Parse(lastRow[node], System.Globalization.CultureInfo.InvariantCulture);
            result.Payload.Should().ContainSingle(quote => quote.Tenor == expectedTenor)
                .Which.Rate.Should().Be(expectedRate);
        }

        result.Payload.Should().Contain(q => q.Tenor == "3M" && q.Rate == 0.0505707m);
        result.Payload.Should().Contain(q => q.Tenor == "6M" && q.Rate == 0.0498938m);
        result.Payload.Should().Contain(q => q.Tenor == "9M" && q.Rate == 0.0484741m);
        result.Payload.Should().Contain(q => q.Tenor == "1Y" && q.Rate == 0.0470544m);
        result.Payload.Should().Contain(q => q.Tenor == "15M" && q.Rate == 0.0424389m);
        result.Payload.Should().Contain(q => q.Tenor == "2Y" && q.Rate == 0.0378675m);
        result.Payload.Should().Contain(q => q.Tenor == "5Y" && q.Rate == 0.0318758m);
        result.Payload.Should().Contain(q => q.Tenor == "10Y" && q.Rate == 0.0321504m);
        result.Payload.Should().Contain(q => q.Tenor == "30Y" && q.Rate == 0.0315861m);

        result.Issues.Should().NotContain(i => i.Code == new IssueCode("DATE_ROLLBACK"));
        result.Coverage.ReturnedPoints.Should().Be(1);
        result.Provenance.Should().ContainSingle().Which.DataDate.Should().Be(new DateOnly(2024, 1, 15));

        transport.RequestedUris.Should().ContainSingle()
            .Which.AbsoluteUri.Should().Be(
                "https://www.bankofcanada.ca/stats/results/csv?lookupPage=lookup_yield_curve.php&dFrom=2023-12-16&dTo=2024-01-15");
    }
}
