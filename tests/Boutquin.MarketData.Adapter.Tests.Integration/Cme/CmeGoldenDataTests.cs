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
using Boutquin.MarketData.Adapter.Cme;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Integration.Cme;

public sealed class CmeGoldenDataTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly FakeClock s_clock = new();

    public CmeGoldenDataTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cme-golden-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Cme_parses_golden_snapshot()
    {
        // Arrange — write the golden CSV to a temp directory
        var goldenCsv = GoldenFileHelper.LoadText("testdata/cme/settlements.csv");
        var csvPath = Path.Combine(_tempDir, "settlements.csv");
        await File.WriteAllTextAsync(csvPath, goldenCsv);

        // Set the file's last-write time to a date within the request range
        File.SetLastWriteTimeUtc(csvPath, new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var rawStore = new FakeRawDocumentStore();
        var adapter = new CmeSettlementAdapter(
            rawStore,
            s_clock,
            new WeekendOnlyCalendar("TEST"),
            Options.Create(new CmeOptions { SettlementDirectory = _tempDir, FilePattern = "*.csv" }),
            NullLogger<CmeSettlementAdapter>.Instance);

        var request = new FuturesSettlementRequest(
            new FuturesProductCode("SR3"),
            new DateRange(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31)));

        // Act
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        // Assert
        result.Payload.Should().HaveCount(3);

        result.Payload[0].ContractMonth.Should().Be(new ContractMonth("2025-06"));
        result.Payload[0].SettlePrice.Should().Be(97.50m);
        result.Payload[0].ImpliedRate.Should().Be(2.50m);

        result.Payload[1].ContractMonth.Should().Be(new ContractMonth("2025-09"));
        result.Payload[1].SettlePrice.Should().Be(97.25m);
        result.Payload[1].ImpliedRate.Should().Be(2.75m);

        result.Payload[2].ContractMonth.Should().Be(new ContractMonth("2025-12"));
        result.Payload[2].SettlePrice.Should().Be(97.00m);
        result.Payload[2].ImpliedRate.Should().Be(3.00m);
    }
}
