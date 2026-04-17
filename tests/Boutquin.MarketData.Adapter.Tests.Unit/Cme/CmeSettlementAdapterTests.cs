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

using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Cme;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.Cme;

public sealed class CmeSettlementAdapterTests : IDisposable
{
    private static readonly FakeClock s_clock = new();
    private readonly List<string> _tempDirs = [];

    private CmeSettlementAdapter CreateAdapter(
        string csvContent,
        CmeOptions? options = null,
        IBusinessCalendar? calendar = null)
    {
        var dir = CreateTempSettlementFile(csvContent);
        var opts = options ?? new CmeOptions();
        opts.SettlementDirectory = dir;
        opts.FilePattern = "*.csv";

        return new CmeSettlementAdapter(
            new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(opts),
            NullLogger<CmeSettlementAdapter>.Instance);
    }

    private string CreateTempSettlementFile(string csvContent)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settlements.csv"), csvContent);
        _tempDirs.Add(dir);
        return dir;
    }

    private static DateRange MakeRange(int year = 2024, int fromMonth = 1, int fromDay = 1, int toMonth = 12, int toDay = 31) =>
        new(new DateOnly(year, fromMonth, fromDay), new DateOnly(year, toMonth, toDay));

    [Fact]
    public void ProviderKey_returns_cme_eod()
    {
        var adapter = CreateAdapter("Month,Settle\n\"JUN 25\",97.50");
        adapter.ProviderKey.Should().Be(new ProviderCode("cme-eod"));
    }

    [Fact]
    public void Priority_returns_100()
    {
        var adapter = CreateAdapter("Month,Settle\n\"JUN 25\",97.50");
        adapter.Priority.Should().Be(100);
    }

    [Fact]
    public void CanHandle_returns_true_for_non_empty_product_code()
    {
        var adapter = CreateAdapter("Month,Settle\n\"JUN 25\",97.50");
        var request = new FuturesSettlementRequest(new FuturesProductCode("SR3"), MakeRange());
        adapter.CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public async Task FetchAsync_parses_csv_into_futures_settlements()
    {
        var csv = "Month,Open,High,Low,Last,Change,Settle,Est. Volume,Prior Day OI\n\"JUN 25\",97.50,97.55,97.45,97.52,+0.03,97.50,1000000,5000000\n\"SEP 25\",97.25,97.30,97.20,97.27,+0.02,97.25,800000,4000000";

        var adapter = CreateAdapter(csv);
        // Use a range that includes "today" (file's LastWriteTimeUtc is the current date)
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var request = new FuturesSettlementRequest(new FuturesProductCode("SR3"), new DateRange(today.AddDays(-1), today.AddDays(1)));
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().HaveCount(2);
        result.Payload[0].ContractMonth.Should().Be(new ContractMonth("2025-06"));
        result.Payload[0].SettlePrice.Should().Be(97.50m);
        result.Payload[0].ImpliedRate.Should().Be(2.50m);
        result.Payload[1].ContractMonth.Should().Be(new ContractMonth("2025-09"));
        result.Payload[1].SettlePrice.Should().Be(97.25m);
    }

    [Fact]
    public async Task FetchAsync_populates_provenance()
    {
        var csv = "Month,Open,High,Low,Last,Change,Settle,Est. Volume,Prior Day OI\n\"JUN 25\",97.50,97.55,97.45,97.52,+0.03,97.50,1000000,5000000";
        var adapter = CreateAdapter(csv);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = await adapter.FetchAsync(new FuturesSettlementRequest(new FuturesProductCode("SR3"), new DateRange(today.AddDays(-1), today.AddDays(1))), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        result.Provenance[0].Provider.Should().Be(new ProviderCode("cme-eod"));
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }
}
