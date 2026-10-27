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
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.Cme;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
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
        IBusinessCalendar? calendar = null,
        FakeRawDocumentStore? rawStore = null,
        string productCode = "SR3")
    {
        var dir = CreateTempSettlementFile(csvContent, productCode);
        var opts = options ?? new CmeOptions();
        opts.SettlementDirectory = dir;
        opts.FilePattern = "*.csv";

        return new CmeSettlementAdapter(
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(opts),
            NullLogger<CmeSettlementAdapter>.Instance);
    }

    private string CreateTempSettlementFile(string csvContent, string productCode)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        File.WriteAllText(Path.Combine(dir, $"{today:yyyyMMdd}-{productCode}.csv"), csvContent);
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
    public void CanHandle_rejects_null_request_with_argument_exception()
    {
        var adapter = CreateAdapter("Month,Settle\n\"JUN 25\",97.50");

        var act = () => adapter.CanHandle(null!);

        act.Should().ThrowExactly<ArgumentNullException>()
            .WithParameterName("request");
    }

    [Theory]
    [InlineData("rawStore")]
    [InlineData("clock")]
    [InlineData("calendar")]
    [InlineData("options")]
    [InlineData("logger")]
    public void Constructor_rejects_each_null_dependency(string dependency)
    {
        var rawStore = dependency == "rawStore" ? null! : new FakeRawDocumentStore();
        var clock = dependency == "clock" ? null! : s_clock;
        var calendar = dependency == "calendar" ? null! : new WeekendOnlyCalendar("TEST");
        var options = dependency == "options" ? null! : Options.Create(new CmeOptions());
        var logger = dependency == "logger" ? null! : NullLogger<CmeSettlementAdapter>.Instance;

        var act = () => new CmeSettlementAdapter(rawStore, clock, calendar, options, logger);

        act.Should().ThrowExactly<ArgumentNullException>()
            .WithParameterName(dependency);
    }

    [Fact]
    public async Task FetchAsync_propagates_caller_cancellation()
    {
        var adapter = CreateAdapter("Month,Settle\n\"JUN 25\",97.50");
        var request = new FuturesSettlementRequest(new FuturesProductCode("SR3"), MakeRange());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> act = () => adapter.FetchAsync(request, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_failure_issue_does_not_expose_local_path()
    {
        var privatePath = Path.Combine(Path.GetTempPath(), $"private-secret-{Guid.NewGuid():N}");
        var adapter = new CmeSettlementAdapter(
            new FakeRawDocumentStore(),
            s_clock,
            new WeekendOnlyCalendar("TEST"),
            Options.Create(new CmeOptions { SettlementDirectory = privatePath }),
            NullLogger<CmeSettlementAdapter>.Instance);

        var result = await adapter.FetchAsync(
            new FuturesSettlementRequest(new FuturesProductCode("SR3"), MakeRange()),
            CancellationToken.None);

        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("FETCH_FAILED"));
        result.Issues[0].Message.Should().Be("Failed to load CME settlements for SR3.");
    }

    [Fact]
    public async Task FetchAsync_ignores_files_for_another_product()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        _tempDirs.Add(directory);
        const string header = "Month,Open,High,Low,Last,Change,Settle\n";
        var sr3File = Path.Combine(directory, "20240110-SR3.csv");
        var esFile = Path.Combine(directory, "20240110-ES.csv");
        File.WriteAllText(sr3File, header + "JUN 25,0,0,0,0,0,97.50");
        File.WriteAllText(esFile, header + "JUN 25,0,0,0,0,0,5355.00");
        var fileTime = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sr3File, fileTime);
        File.SetLastWriteTimeUtc(esFile, fileTime);
        var adapter = new CmeSettlementAdapter(
            new FakeRawDocumentStore(), s_clock, new WeekendOnlyCalendar("TEST"),
            Options.Create(new CmeOptions { SettlementDirectory = directory }),
            NullLogger<CmeSettlementAdapter>.Instance);

        var result = await adapter.FetchAsync(
            new FuturesSettlementRequest(new FuturesProductCode("SR3"),
                new DateRange(new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 10))),
            CancellationToken.None);

        result.Payload.Should().ContainSingle();
        result.Payload[0].SettlePrice.Should().Be(97.50m);
    }

    [Fact]
    public async Task FetchAsync_uses_filename_business_date_instead_of_copy_time()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        _tempDirs.Add(directory);
        var file = Path.Combine(directory, "20240110-SR3.csv");
        File.WriteAllText(file, "Month,Open,High,Low,Last,Change,Settle\nJUN 25,0,0,0,0,0,97.50");
        File.SetLastWriteTimeUtc(file, new DateTime(2024, 1, 11, 0, 0, 0, DateTimeKind.Utc));
        var adapter = new CmeSettlementAdapter(
            new FakeRawDocumentStore(), s_clock, new WeekendOnlyCalendar("TEST"),
            Options.Create(new CmeOptions { SettlementDirectory = directory }),
            NullLogger<CmeSettlementAdapter>.Instance);

        var result = await adapter.FetchAsync(
            new FuturesSettlementRequest(new FuturesProductCode("SR3"),
                new DateRange(new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 10))),
            CancellationToken.None);

        result.Payload.Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 1, 10));
        result.Provenance.Should().ContainSingle().Which.DataDate.Should().Be(new DateOnly(2024, 1, 10));
    }

    [Fact]
    public async Task FetchAsync_preserves_each_source_file_as_a_distinct_raw_document()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        _tempDirs.Add(directory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        File.WriteAllText(
            Path.Combine(directory, $"{today:yyyyMMdd}-SR3.csv"),
            "Month,Open,High,Low,Last,Change,Settle\n\"JUN 25\",97.50,97.55,97.45,97.52,+0.03,97.50");
        File.WriteAllText(
            Path.Combine(directory, $"{today.AddDays(1):yyyyMMdd}-SR3.csv"),
            "Month,Open,High,Low,Last,Change,Settle\n\"SEP 25\",97.25,97.30,97.20,97.27,+0.02,97.25");
        var rawStore = new FakeRawDocumentStore();
        var adapter = new CmeSettlementAdapter(
            rawStore,
            s_clock,
            new WeekendOnlyCalendar("TEST"),
            Options.Create(new CmeOptions { SettlementDirectory = directory, FilePattern = "*.csv" }),
            NullLogger<CmeSettlementAdapter>.Instance);
        var request = new FuturesSettlementRequest(
            new FuturesProductCode("SR3"),
            new DateRange(today.AddDays(-1), today.AddDays(1)));

        await adapter.FetchAsync(request, CancellationToken.None);

        rawStore.Store.Should().HaveCount(2);
        rawStore.Store.Values.Should().Contain(value => value.Contains("JUN 25", StringComparison.Ordinal));
        rawStore.Store.Values.Should().Contain(value => value.Contains("SEP 25", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchAsync_parses_csv_into_futures_settlements()
    {
        var csv = "Month,Open,High,Low,Last,Change,Settle,Est. Volume,Prior Day OI\n\"JUN 25\",97.50,97.55,97.45,97.52,+0.03,97.50,1000000,5000000\n\"SEP 25\",97.25,97.30,97.20,97.27,+0.02,97.25,800000,4000000";

        var adapter = CreateAdapter(csv);
        // Use a range that includes the filename's business date.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var request = new FuturesSettlementRequest(new FuturesProductCode("SR3"), new DateRange(today.AddDays(-1), today.AddDays(1)));
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().HaveCount(2);
        result.Payload[0].ContractMonth.Should().Be(new ContractMonth("2025-06"));
        result.Payload[0].SettlePrice.Should().Be(97.50m);
        result.Payload[0].ImpliedRate.Should().Be(0.0250m);
        result.Payload[1].ContractMonth.Should().Be(new ContractMonth("2025-09"));
        result.Payload[1].SettlePrice.Should().Be(97.25m);
    }

    [Fact]
    public async Task FetchAsync_preserves_quoted_thousands_settlement_price()
    {
        var csv = "Month,Open,High,Low,Last,Change,Settle,Est. Volume,Prior Day OI\n"
            + "\"JUN 25\",4990.00,5010.00,4980.00,5001.00,+11.00,\"5,350.00\",1000000,5000000";
        var adapter = CreateAdapter(csv, productCode: "ES");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var result = await adapter.FetchAsync(
            new FuturesSettlementRequest(new FuturesProductCode("ES"),
                new DateRange(today.AddDays(-1), today.AddDays(1))),
            CancellationToken.None);

        result.Payload.Should().ContainSingle().Which.SettlePrice.Should().Be(5350.00m);
    }

    [Fact]
    public async Task FetchAsync_reports_malformed_contract_among_valid_rows()
    {
        var csv = "Month,Open,High,Low,Last,Change,Settle,Est. Volume,Prior Day OI\n"
            + "\"JUN 25\",97.50,97.55,97.45,97.52,+0.03,97.50,1000000,5000000\n"
            + "\"SEP 25\",97.25,97.30,97.20,97.27,+0.02,not-a-price,800000,4000000";
        var adapter = CreateAdapterWithFileDate(csv, new DateOnly(2024, 1, 10));
        var request = new FuturesSettlementRequest(new FuturesProductCode("SR3"),
            new DateRange(new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 10)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().ContainSingle().Which.ContractMonth.Should().Be(new ContractMonth("2025-06"));
        result.Issues.Should().Contain(i => i.Code == new IssueCode("MALFORMED_ROW"));
    }

    [Fact]
    public async Task FetchAsync_skips_oversized_contract_year_without_discarding_valid_rows()
    {
        var csv = "Month,Settle\nJUN 25,97.50\nJUN 8000,97.25";
        var adapter = CreateAdapterWithFileDate(csv, new DateOnly(2024, 1, 10));
        var request = new FuturesSettlementRequest(new FuturesProductCode("SR3"),
            new DateRange(new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 10)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().ContainSingle().Which.ContractMonth.Should().Be(new ContractMonth("2025-06"));
        result.Issues.Should().Contain(i => i.Code == new IssueCode("MALFORMED_ROW"));
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("FETCH_FAILED"));
    }

    [Fact]
    public async Task FetchAsync_does_not_fabricate_implied_rate_for_non_rate_product()
    {
        var csv = "Month,Open,High,Low,Last,Change,Settle,Est. Volume,Prior Day OI\n"
            + "\"JUN 25\",5350.00,5360.00,5340.00,5355.00,+5.00,5355.00,1000000,5000000";
        var adapter = CreateAdapter(csv, productCode: "ES");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var result = await adapter.FetchAsync(
            new FuturesSettlementRequest(
                new FuturesProductCode("ES"),
                new DateRange(today.AddDays(-1), today.AddDays(1))),
            CancellationToken.None);

        result.Payload.Should().ContainSingle();
        result.Payload[0].ImpliedRate.Should().BeNull();
    }

    [Theory]
    [InlineData("SR3")]
    [InlineData("ZQ")]
    [InlineData("sr3")]
    [InlineData(" SR3 ")]
    public async Task FetchAsync_computes_decimal_implied_rate_for_supported_rate_products(string productCode)
    {
        var csv = "Month,Open,High,Low,Last,Change,Settle,Est. Volume,Prior Day OI\n"
            + "\"JUN 25\",97.50,97.55,97.45,97.52,+0.03,97.50,1000000,5000000";
        var adapter = CreateAdapter(csv, productCode: productCode.Trim());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var result = await adapter.FetchAsync(
            new FuturesSettlementRequest(
                new FuturesProductCode(productCode),
                new DateRange(today.AddDays(-1), today.AddDays(1))),
            CancellationToken.None);

        result.Payload.Should().ContainSingle();
        result.Payload[0].ImpliedRate.Should().Be(0.0250m);
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

    // ---------- Calendar-aware coverage (feature-calendar-aware-coverage) ----------
    // CME derives the settlement date from each filename. Use named business dates
    // to exercise calendar-aware coverage.

    private CmeSettlementAdapter CreateAdapterWithFileDate(
        string csvContent,
        DateOnly fileDate,
        IBusinessCalendar? calendar = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{fileDate:yyyyMMdd}-SR3.csv");
        File.WriteAllText(filePath, csvContent);
        _tempDirs.Add(dir);

        var opts = new CmeOptions { SettlementDirectory = dir, FilePattern = "*.csv" };
        return new CmeSettlementAdapter(
            new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(opts),
            NullLogger<CmeSettlementAdapter>.Instance);
    }

    [Fact]
    public async Task FetchAsync_single_business_day_range_reports_complete_coverage()
    {
        // Single-day range = Wed 2024-01-10, file stamped Wed. One expected business day,
        // one returned → Complete, no gap issues.
        var csv = "Month,Open,High,Low,Last,Change,Settle,Est. Volume,Prior Day OI\n" +
                  "\"JUN 25\",97.50,97.55,97.45,97.52,+0.03,97.50,1000000,5000000";
        var adapter = CreateAdapterWithFileDate(csv, new DateOnly(2024, 1, 10));

        var request = new FuturesSettlementRequest(
            new FuturesProductCode("SR3"),
            new DateRange(new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 10)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(1);
        result.Coverage.ReturnedPoints.Should().Be(1);
        result.Coverage.Status.Should().Be(CompletenessStatus.Complete);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_missing_business_day_emits_unexpected_gap()
    {
        // Request Mon-Fri but only one file exists (stamped Wed). Mon/Tue/Thu/Fri → gaps.
        var csv = "Month,Open,High,Low,Last,Change,Settle,Est. Volume,Prior Day OI\n" +
                  "\"JUN 25\",97.50,97.55,97.45,97.52,+0.03,97.50,1000000,5000000";
        var adapter = CreateAdapterWithFileDate(csv, new DateOnly(2024, 1, 10));

        var request = new FuturesSettlementRequest(
            new FuturesProductCode("SR3"),
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(5);
        result.Coverage.ReturnedPoints.Should().Be(1);
        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
        var gaps = result.Issues.Where(i => i.Code == new IssueCode("UNEXPECTED_GAP")).ToList();
        gaps.Should().HaveCount(4);
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
