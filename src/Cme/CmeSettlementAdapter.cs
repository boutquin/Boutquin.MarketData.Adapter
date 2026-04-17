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

using System.Globalization;

using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.Shared;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boutquin.MarketData.Adapter.Cme;

/// <summary>
/// Reads CME futures settlement data from local CSV files and returns canonical
/// <see cref="FuturesSettlement"/> records with full provenance and coverage metadata.
/// </summary>
/// <remarks>
/// <para>
/// CME does not offer a free REST API for settlement prices. This adapter scans a
/// configured directory for CSV files matching a glob pattern, parses rows in the
/// standard CME settlement format, and emits <see cref="FuturesSettlement"/> records
/// for the requested product code and date range.
/// </para>
/// <para>
/// The CSV format expects columns: Month, Open, High, Low, Last, Change, Settle,
/// Est. Volume, Prior Day OI. The "Month" column contains contract month identifiers
/// (e.g., "JUN 25", "SEP 25") which are parsed to YYYY-MM format. The implied rate
/// is computed as <c>100 - SettlePrice</c> for interest rate futures.
/// </para>
/// </remarks>
public sealed class CmeSettlementAdapter : IDataSourceAdapter<FuturesSettlementRequest, FuturesSettlement>, IPrioritizedAdapter
{
    private static readonly Dictionary<string, int> s_monthMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JAN"] = 1,
        ["FEB"] = 2,
        ["MAR"] = 3,
        ["APR"] = 4,
        ["MAY"] = 5,
        ["JUN"] = 6,
        ["JUL"] = 7,
        ["AUG"] = 8,
        ["SEP"] = 9,
        ["OCT"] = 10,
        ["NOV"] = 11,
        ["DEC"] = 12
    };

    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly CmeOptions _options;
    private readonly ILogger<CmeSettlementAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CmeSettlementAdapter"/> class.
    /// </summary>
    /// <param name="rawStore">Store for persisting raw CSV content before parsing.</param>
    /// <param name="clock">Clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">Business calendar for computing calendar-aware coverage.</param>
    /// <param name="options">CME adapter configuration options.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public CmeSettlementAdapter(
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("cme-eod")] IBusinessCalendar calendar,
        IOptions<CmeOptions> options,
        ILogger<CmeSettlementAdapter> logger)
    {
        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProviderCode ProviderKey => new("cme-eod");

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanHandle(FuturesSettlementRequest request) =>
        request.ProductCode.Value.Length > 0;

    /// <inheritdoc />
    public async Task<DataEnvelope<IReadOnlyList<FuturesSettlement>>> FetchAsync(
        FuturesSettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var files = Directory.GetFiles(_options.SettlementDirectory, _options.FilePattern);
            var settlements = new List<FuturesSettlement>();

            foreach (var file in files)
            {
                var csv = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);

                var storeKey = $"cme/settlements/{request.ProductCode}/{request.Range.From:yyyyMMdd}-{request.Range.To:yyyyMMdd}";
                await _rawStore.SaveAsync(storeKey, csv, cancellationToken).ConfigureAwait(false);

                var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2)
                {
                    continue;
                }

                // Skip header row
                for (var i = 1; i < lines.Length; i++)
                {
                    var columns = lines[i].Split(',');
                    if (columns.Length < 7)
                    {
                        continue;
                    }

                    var monthField = columns[0].Trim().Trim('"');
                    var settleField = columns[6].Trim().Trim('"');

                    if (string.IsNullOrWhiteSpace(settleField))
                    {
                        continue;
                    }

                    var contractMonth = ParseContractMonth(monthField);
                    if (contractMonth is null)
                    {
                        continue;
                    }

                    if (!decimal.TryParse(settleField, NumberStyles.Number, CultureInfo.InvariantCulture, out var settlePrice))
                    {
                        continue;
                    }

                    var impliedRate = 100m - settlePrice;

                    // Use the file's last-write date as the settlement date
                    var fileDate = DateOnly.FromDateTime(File.GetLastWriteTimeUtc(file));

                    if (fileDate < request.Range.From || fileDate > request.Range.To)
                    {
                        continue;
                    }

                    settlements.Add(new FuturesSettlement(
                        fileDate,
                        request.ProductCode,
                        contractMonth.Value,
                        settlePrice,
                        impliedRate));
                }
            }

            _logger.LogInformation(
                "Loaded {Count} CME {ProductCode} settlements from local files",
                settlements.Count,
                request.ProductCode);

            var issues = new List<DataIssue>();
            if (settlements.Count == 0)
            {
                issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Error,
                    $"No CME settlement data found for product {request.ProductCode}."));
            }

            var returnedDates = settlements.Select(s => s.Date).ToHashSet();
            var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
                _calendar, request.Range, request.Frequency, returnedDates);
            issues.AddRange(gapIssues);

            var provenance = new DataProvenance(
                Provider: new ProviderCode("cme-eod"),
                Dataset: request.ProductCode.Value,
                LicenseFlag: LicenseType.Free,
                RetrievalMode: RetrievalMode.Snapshot,
                Freshness: FreshnessClass.EndOfDay,
                RetrievedAtUtc: _clock.UtcNow,
                SourceUrl: null,
                DataDate: settlements.Count > 0 ? settlements.Max(s => s.Date) : null);

            return new DataEnvelope<IReadOnlyList<FuturesSettlement>>(
                Payload: settlements,
                Coverage: coverage,
                Issues: issues,
                Provenance: [provenance]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load CME settlements for {ProductCode}", request.ProductCode);

            var errorCoverage = new DataCoverage(
                RequestedPoints: 0,
                ReturnedPoints: 0,
                MissingPoints: 0,
                CoverageRatio: 0m);

            var errorIssue = new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Error,
                $"Failed to load CME settlements for {request.ProductCode}: {ex.Message}");

            return new DataEnvelope<IReadOnlyList<FuturesSettlement>>(
                Payload: [],
                Coverage: errorCoverage,
                Issues: [errorIssue],
                Provenance: []);
        }
    }

    /// <summary>
    /// Parses a CME contract month identifier (e.g., "JUN 25") into YYYY-MM format (e.g., "2025-06").
    /// </summary>
    /// <param name="monthField">The raw contract month string from the CSV.</param>
    /// <returns>The contract month in YYYY-MM format, or <see langword="null"/> if parsing fails.</returns>
    private static ContractMonth? ParseContractMonth(string monthField)
    {
        var parts = monthField.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        if (!s_monthMap.TryGetValue(parts[0], out var month))
        {
            return null;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var twoDigitYear))
        {
            return null;
        }

        var year = twoDigitYear + 2000;
        return new ContractMonth(year, month);
    }
}
