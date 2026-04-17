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

using System.IO.Compression;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.FamaFrench;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Integration.FamaFrench;

public sealed class FamaFrenchGoldenDataTests
{
    private static readonly FakeClock s_clock = new();

    [Fact]
    public async Task FamaFrench_parses_golden_snapshot()
    {
        // Arrange — load the golden CSV and wrap it in a ZIP
        var csvText = GoldenFileHelper.LoadText("testdata/famafrench/factors.csv");
        var zipBytes = CreateZipFromCsv("F-F_Research_Data_Factors_daily.CSV", csvText);

        var transport = new FakeHttpDataTransport();
        transport.RespondToWithBytes("F-F_Research_Data_Factors_daily_CSV.zip", zipBytes);
        var rawStore = new FakeRawDocumentStore();
        var adapter = new FamaFrenchFactorAdapter(
            transport,
            rawStore,
            s_clock,
            new WeekendOnlyCalendar("TEST"),
            Options.Create(new FamaFrenchOptions()),
            NullLogger<FamaFrenchFactorAdapter>.Instance);

        var request = new FactorSeriesRequest(
            new FactorDatasetId("F-F_Research_Data_Factors_daily"),
            new DateRange(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31)));

        // Act
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        // Assert
        result.Payload.Should().HaveCount(3);

        var obs0 = result.Payload[0];
        obs0.Date.Should().Be(new DateOnly(2024, 1, 2));
        obs0.Factors["Mkt-RF"].Should().Be(0.0025m);
        obs0.Factors["SMB"].Should().Be(0.001m);
        obs0.Factors["HML"].Should().Be(-0.0005m);

        var obs1 = result.Payload[1];
        obs1.Date.Should().Be(new DateOnly(2024, 1, 3));
        obs1.Factors["Mkt-RF"].Should().Be(0.0035m);
        obs1.Factors["SMB"].Should().Be(0.002m);
        obs1.Factors["HML"].Should().Be(-0.001m);

        var obs2 = result.Payload[2];
        obs2.Date.Should().Be(new DateOnly(2024, 1, 4));
        obs2.Factors["Mkt-RF"].Should().Be(-0.0015m);
        obs2.Factors["SMB"].Should().Be(0.0005m);
        obs2.Factors["HML"].Should().Be(0.0002m);
    }

    private static byte[] CreateZipFromCsv(string entryName, string csvContent)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(csvContent);
        }

        return ms.ToArray();
    }
}
