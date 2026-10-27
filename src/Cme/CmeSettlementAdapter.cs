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
using System.Security.Cryptography;
using System.Text;
using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
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
/// (e.g., "JUN 25", "SEP 25") which are parsed to YYYY-MM format. For the supported
/// price-quoted interest-rate products <c>SR3</c> and <c>ZQ</c>, the implied rate is emitted in
/// decimal form as <c>(100 - SettlePrice) / 100</c>. Other products retain a null implied rate.
/// </para>
/// </remarks>
public sealed class CmeSettlementAdapter : IDataSourceAdapter<FuturesSettlementRequest, FuturesSettlement>, IPrioritizedAdapter
{
    private static readonly HashSet<string> s_priceQuotedRateProducts =
        new(StringComparer.OrdinalIgnoreCase) { "SR3", "ZQ" };

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
        ArgumentNullException.ThrowIfNull(rawStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The stable <c>cme-eod</c> provider identity attached to this adapter's provenance.</summary>
    public ProviderCode ProviderKey => new("cme-eod");

    /// <summary>The fallback rank, 100; adapters with higher ranks are attempted first.</summary>
    public int Priority => 100;

    /// <summary>Checks the request against this provider's supported routing constraints without performing I/O.</summary>
    /// <param name="request">Daily requests with a nonempty product code.</param>
    /// <returns><see langword="true"/> when the request matches the adapter's declared support; this does not guarantee provider availability.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public bool CanHandle(FuturesSettlementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProductCode.Value, nameof(request));
        return request.Frequency == DataFrequency.Daily;
    }

    /// <summary>Reads settlements bound to the requested product and dates from configured CME settlement files.</summary>
    /// <param name="request">Daily requests with a nonempty product code.</param>
    /// <param name="cancellationToken">Cancels transport, raw-payload persistence and parsing where those operations support cancellation.</param>
    /// <returns>A task yielding canonical records together with provider provenance, coverage and data-quality issues. Empty or partial responses retain their failure or coverage information.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="OperationCanceledException">The caller or a dependency cancels the operation.</exception>
    /// <remarks>Successful fetches include raw-payload persistence and normalization. Inspect the envelope's issues and coverage before using its records; a returned envelope alone does not certify complete data.</remarks>
    public async Task<DataEnvelope<IReadOnlyList<FuturesSettlement>>> FetchAsync(
        FuturesSettlementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanHandle(request))
        {
            throw new ArgumentException("CME settlements require daily frequency.", nameof(request));
        }
        try
        {
            var root = ResolvePath(_options.SettlementDirectory);
            var files = Directory.GetFiles(_options.SettlementDirectory, _options.FilePattern);
            var settlements = new List<FuturesSettlement>();
            var issues = new List<DataIssue>();

            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.Length < 10 || fileName[8] != '-' ||
                    !DateOnly.TryParseExact(fileName.AsSpan(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var fileDate) ||
                    !string.Equals(fileName[9..], request.ProductCode.Value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (fileDate < request.Range.From || fileDate > request.Range.To)
                {
                    continue;
                }
                var relative = Path.GetRelativePath(root, ResolvePath(file));
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    issues.Add(new DataIssue(new IssueCode("UNSAFE_SOURCE_PATH"), IssueSeverity.Warning,
                        "Skipped a CME settlement link outside the configured directory."));
                    continue;
                }
                var csv = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);

                var sourceFileId = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(file))));
                var storeKey = FormattableString.Invariant($"cme/settlements/{request.ProductCode}/{request.Range.From:yyyyMMdd}-{request.Range.To:yyyyMMdd}/{sourceFileId}");
                await _rawStore.SaveAsync(storeKey, csv, cancellationToken).ConfigureAwait(false);

                var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2)
                {
                    continue;
                }

                if (!TryParseCsvLine(lines[0], out var header))
                {
                    issues.Add(new DataIssue(new IssueCode("SCHEMA_MISMATCH"), IssueSeverity.Warning,
                        "CME settlement CSV has an invalid header."));
                    continue;
                }

                var monthIndex = Array.FindIndex(header, column =>
                    string.Equals(column, "Month", StringComparison.OrdinalIgnoreCase));
                var settleIndex = Array.FindIndex(header, column =>
                    string.Equals(column, "Settle", StringComparison.OrdinalIgnoreCase));
                if (monthIndex < 0 || settleIndex < 0)
                {
                    issues.Add(new DataIssue(new IssueCode("SCHEMA_MISMATCH"), IssueSeverity.Warning,
                        "CME settlement CSV is missing the Month or Settle column."));
                    continue;
                }

                var malformedRows = 0;
                for (var i = 1; i < lines.Length; i++)
                {
                    if (fileDate < request.Range.From || fileDate > request.Range.To)
                    {
                        continue;
                    }

                    if (!TryParseCsvLine(lines[i], out var columns) ||
                        columns.Length <= Math.Max(monthIndex, settleIndex))
                    {
                        malformedRows++;
                        continue;
                    }

                    var monthField = columns[monthIndex];
                    var settleField = columns[settleIndex];

                    if (string.IsNullOrWhiteSpace(settleField))
                    {
                        malformedRows++;
                        continue;
                    }

                    var contractMonth = ParseContractMonth(monthField);
                    if (contractMonth is null)
                    {
                        malformedRows++;
                        continue;
                    }

                    if (!decimal.TryParse(settleField, NumberStyles.Number, CultureInfo.InvariantCulture, out var settlePrice))
                    {
                        malformedRows++;
                        continue;
                    }

                    decimal? impliedRate = s_priceQuotedRateProducts.Contains(request.ProductCode.Value.Trim())
                        ? (100m - settlePrice) / 100m
                        : null;

                    settlements.Add(new FuturesSettlement(
                        fileDate,
                        request.ProductCode,
                        contractMonth.Value,
                        settlePrice,
                        impliedRate));
                }

                if (malformedRows > 0)
                {
                    issues.Add(new DataIssue(new IssueCode("MALFORMED_ROW"), IssueSeverity.Warning,
                        string.Format(CultureInfo.InvariantCulture,
                            "Skipped {0} malformed CME settlement row(s) for product {1} on {2:yyyy-MM-dd}.",
                            malformedRows, request.ProductCode, fileDate)));
                }
            }

            _logger.LogInformation(
                "Loaded {Count} CME {ProductCode} settlements from local files",
                settlements.Count,
                request.ProductCode);

            if (settlements.Count == 0 && CoverageComputation.CountExpectedPoints(_calendar, request.Range, request.Frequency) > 0)
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
        catch (OperationCanceledException)
        {
            throw;
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
                $"Failed to load CME settlements for {request.ProductCode}.");

            return new DataEnvelope<IReadOnlyList<FuturesSettlement>>(
                Payload: [],
                Coverage: errorCoverage,
                Issues: [errorIssue],
                Provenance: []);
        }
    }

    private static string ResolvePath(string path)
    {
        var absolute = Path.GetFullPath(path);
        var current = Path.GetPathRoot(absolute)!;
        foreach (var part in absolute[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("CME source link cannot be resolved.");
            }
        }
        return current;
    }

    private static bool TryParseCsvLine(string line, out string[] columns)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var afterQuote = false;

        for (var i = 0; i < line.Length; i++)
        {
            var current = line[i];
            if (quoted)
            {
                if (current == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (current == '"')
                {
                    quoted = false;
                    afterQuote = true;
                }
                else
                {
                    field.Append(current);
                }
            }
            else if (afterQuote)
            {
                if (current == ',')
                {
                    fields.Add(field.ToString().Trim());
                    field.Clear();
                    afterQuote = false;
                }
                else if (!char.IsWhiteSpace(current))
                {
                    columns = [];
                    return false;
                }
            }
            else if (current == ',')
            {
                fields.Add(field.ToString().Trim());
                field.Clear();
            }
            else if (current == '"' && field.ToString().All(char.IsWhiteSpace))
            {
                field.Clear();
                quoted = true;
            }
            else if (current == '"')
            {
                columns = [];
                return false;
            }
            else
            {
                field.Append(current);
            }
        }

        if (quoted)
        {
            columns = [];
            return false;
        }

        fields.Add(field.ToString().Trim());
        columns = [.. fields];
        return true;
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

        if (parts[1].Length != 2 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var twoDigitYear))
        {
            return null;
        }

        var year = twoDigitYear + 2000;
        return new ContractMonth(year, month);
    }
}
