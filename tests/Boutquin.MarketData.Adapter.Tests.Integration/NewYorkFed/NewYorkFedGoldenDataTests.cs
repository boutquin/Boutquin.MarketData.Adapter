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
using Boutquin.MarketData.Adapter.NewYorkFed;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Integration.NewYorkFed;

public sealed class NewYorkFedGoldenDataTests
{
    private static readonly FakeClock s_clock = new();

    [Fact]
    public async Task NewYorkFed_SOFR_parses_golden_snapshot()
    {
        // Arrange
        var goldenData = GoldenFileHelper.LoadText("testdata/nyfed/sofr.json");
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("rates/secured/sofr", goldenData);
        var rawStore = new FakeRawDocumentStore();
        var adapter = new NewYorkFedSofrAdapter(
            transport,
            rawStore,
            s_clock,
            new WeekendOnlyCalendar("TEST"),
            Options.Create(new NewYorkFedOptions()),
            NullLogger<NewYorkFedSofrAdapter>.Instance);

        var request = new OvernightFixingRequest(
            new BenchmarkName("SOFR"),
            new DateRange(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4)));

        // Act
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        // Assert
        result.Payload.Should().HaveCount(3);

        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[0].Value.Should().Be(0.0531m);
        result.Payload[0].Units.Should().Be("decimal-rate");

        result.Payload[1].Date.Should().Be(new DateOnly(2024, 1, 3));
        result.Payload[1].Value.Should().Be(0.0532m);
        result.Payload[1].Units.Should().Be("decimal-rate");

        result.Payload[2].Date.Should().Be(new DateOnly(2024, 1, 4));
        result.Payload[2].Value.Should().Be(0.0533m);
        result.Payload[2].Units.Should().Be("decimal-rate");
    }
}
