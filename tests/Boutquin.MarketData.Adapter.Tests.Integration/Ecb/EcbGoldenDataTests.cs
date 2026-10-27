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
using Boutquin.MarketData.Adapter.Ecb;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Integration.Ecb;

public sealed class EcbGoldenDataTests
{
    private static readonly FakeClock s_clock = new();

    [Fact]
    public async Task Ecb_ESTR_parses_golden_snapshot()
    {
        // Arrange — the snapshot is the live service's csvdata response for
        // EST/B.EU000A2X2A25.WT, 2024-01-02..04, captured 2026-09-21: the full attribute header,
        // and a quoted COMMENT_TS field containing commas that the adapter's comma split must not
        // let shift TIME_PERIOD / OBS_VALUE.
        var goldenData = GoldenFileHelper.LoadText("testdata/ecb/estr.csv");
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("EST/B.EU000A2X2A25.WT", goldenData);
        var rawStore = new FakeRawDocumentStore();
        var adapter = new EcbEstrAdapter(
            transport,
            rawStore,
            s_clock,
            new WeekendOnlyCalendar("TEST"),
            Options.Create(new EcbOptions()),
            NullLogger<EcbEstrAdapter>.Instance);

        var request = new OvernightFixingRequest(
            new BenchmarkName("ESTR"),
            new DateRange(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4)));

        // Act
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        // Assert
        result.Payload.Should().HaveCount(3);
        rawStore.Store.Should().ContainSingle().Which.Value.Should().Be(goldenData);

        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[0].Value.Should().Be(0.03906m);
        result.Payload[0].Units.Should().Be("decimal-rate");

        result.Payload[1].Date.Should().Be(new DateOnly(2024, 1, 3));
        result.Payload[1].Value.Should().Be(0.03904m);
        result.Payload[1].Units.Should().Be("decimal-rate");

        result.Payload[2].Date.Should().Be(new DateOnly(2024, 1, 4));
        result.Payload[2].Value.Should().Be(0.03902m);
        result.Payload[2].Units.Should().Be("decimal-rate");

        transport.RequestedUris.Should().ContainSingle()
            .Which.AbsoluteUri.Should().Be(
                "https://data-api.ecb.europa.eu/service/data/EST/B.EU000A2X2A25.WT?startPeriod=2024-01-02&endPeriod=2024-01-04&format=csvdata");
    }
}
