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
using System.IO.Compression;

using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.Shared;
using Boutquin.MarketData.Transport.Http;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boutquin.MarketData.Adapter.FamaFrench;

/// <summary>
/// Downloads Fama-French factor return ZIP files from Ken French's data library,
/// extracts the CSV inside, and parses factor return observations into canonical
/// <see cref="FactorObservation"/> records with full provenance and coverage metadata.
/// </summary>
/// <remarks>
/// <para>
/// The adapter builds a URL of the form <c>{BaseUrl}/{DatasetName}_CSV.zip</c>, downloads the
/// ZIP archive via <see cref="IHttpDataTransport"/>, locates the first <c>.CSV</c> entry, and
/// parses factor names from the header row (the first line starting with a comma). Data rows
/// contain a date (YYYYMMDD for daily, YYYYMM for monthly) followed by factor values in
/// percentage form. The adapter divides all values by 100 to produce decimal returns.
/// </para>
/// <para>
/// Parsing stops when a blank line or a line beginning with non-digit characters is encountered,
/// which marks the transition to annual data or end of the daily section.
/// Expanded CSV content is limited to 64 MiB before decoding and persistence.
/// </para>
/// </remarks>
public sealed class FamaFrenchFactorAdapter
    : IDataSourceAdapter<FactorSeriesRequest, FactorObservation>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly FamaFrenchOptions _options;
    private readonly ILogger<FamaFrenchFactorAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FamaFrenchFactorAdapter"/> class.
    /// </summary>
    /// <param name="transport">HTTP transport for downloading ZIP archives.</param>
    /// <param name="rawStore">Store for persisting raw CSV content before parsing.</param>
    /// <param name="clock">Clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">Business calendar for computing calendar-aware coverage.</param>
    /// <param name="options">Fama-French adapter configuration options.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public FamaFrenchFactorAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("fama-french")] IBusinessCalendar calendar,
        IOptions<FamaFrenchOptions> options,
        ILogger<FamaFrenchFactorAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(rawStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The stable <c>fama-french</c> provider identity attached to this adapter's provenance.</summary>
    public ProviderCode ProviderKey => new("fama-french");

    /// <summary>The fallback rank, 100; adapters with higher ranks are attempted first.</summary>
    public int Priority => 100;

    /// <summary>Checks the request against this provider's supported routing constraints without performing I/O.</summary>
    /// <param name="request">Daily or monthly requests with a safe dataset identifier.</param>
    /// <returns><see langword="true"/> when the request matches the adapter's declared support; this does not guarantee provider availability.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public bool CanHandle(FactorSeriesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsSafeDatasetName(request.DatasetName.Value) &&
        request.Frequency is DataFrequency.Daily or DataFrequency.Monthly;
    }

    private static bool IsSafeDatasetName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    /// <summary>Retrieves daily or monthly factor observations from the selected dataset from Ken French Data Library.</summary>
    /// <param name="request">Daily or monthly requests with a safe dataset identifier.</param>
    /// <param name="cancellationToken">Cancels transport, raw-payload persistence and parsing where those operations support cancellation.</param>
    /// <returns>A task yielding canonical records together with provider provenance, coverage and data-quality issues. Empty or partial responses retain their failure or coverage information.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="OperationCanceledException">The caller or a dependency cancels the operation.</exception>
    /// <remarks>Successful fetches include raw-payload persistence and normalization. Inspect the envelope's issues and coverage before using its records; a returned envelope alone does not certify complete data.</remarks>
    public async Task<DataEnvelope<IReadOnlyList<FactorObservation>>> FetchAsync(
        FactorSeriesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Frequency is not (DataFrequency.Daily or DataFrequency.Monthly))
        {
            throw new ArgumentException("Fama-French supports daily or monthly requests.", nameof(request));
        }
        if (!IsSafeDatasetName(request.DatasetName.Value))
        {
            return BuildErrorEnvelope("Invalid Fama-French dataset name.");
        }

        var url = $"{_options.BaseUrl}/{request.DatasetName}_CSV.zip";

        try
        {
            var uri = new Uri(url);
            using var stream = await _transport.GetAsync(uri, null, cancellationToken).ConfigureAwait(false);

            // Copy the stream into a MemoryStream so we can open it as a ZipArchive.
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
            memoryStream.Position = 0;

            string csvText;
            using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read))
            {
                var csvEntry = archive.Entries.FirstOrDefault(
                    e => e.FullName.EndsWith(".CSV", StringComparison.OrdinalIgnoreCase));

                if (csvEntry is null)
                {
                    return BuildErrorEnvelope(
                        $"No CSV entry found in ZIP archive for dataset {request.DatasetName}.");
                }

                const long maximumExpandedBytes = 64L * 1024 * 1024;
                if (csvEntry.Length > maximumExpandedBytes)
                {
                    return BuildErrorEnvelope("Fama-French CSV exceeds the 64 MiB expanded-size limit.");
                }
                using var entryStream = csvEntry.Open();
                using var expanded = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await entryStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (expanded.Length + read > maximumExpandedBytes)
                    {
                        return BuildErrorEnvelope("Fama-French CSV exceeds the 64 MiB expanded-size limit.");
                    }
                    expanded.Write(buffer, 0, read);
                }
                expanded.Position = 0;
                using var reader = new StreamReader(expanded);
                csvText = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            // Persist raw CSV for audit and replay.
            var storeKey = FormattableString.Invariant($"fama-french/{request.DatasetName}/{request.Range.From:yyyyMMdd}-{request.Range.To:yyyyMMdd}");
            await _rawStore.SaveAsync(storeKey, csvText, cancellationToken).ConfigureAwait(false);

            // Parse the CSV content.
            var lines = csvText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

            // Find the header row: the first line that starts with a comma.
            string[]? factorNames = null;
            var headerIndex = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(','))
                {
                    var parts = lines[i].Split(',');
                    // Skip the first empty element; remaining elements are factor names.
                    factorNames = parts[1..].Select(n => n.Trim()).ToArray();
                    headerIndex = i;
                    break;
                }
            }

            if (factorNames is null || headerIndex < 0)
            {
                return BuildErrorEnvelope(
                    $"No header row found in CSV for Fama-French dataset {request.DatasetName}.");
            }

            var observations = new List<FactorObservation>();

            for (var i = headerIndex + 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                // Stop at blank lines or lines that start with non-digit characters
                // (marks the annual section or end of daily data).
                if (string.IsNullOrWhiteSpace(line) || !char.IsDigit(line[0]))
                {
                    break;
                }

                var fields = line.Split(',');
                var dateStr = fields[0].Trim();
                DateOnly date;

                if (dateStr.Length == 8)
                {
                    // YYYYMMDD format (daily).
                    date = DateOnly.ParseExact(dateStr, "yyyyMMdd", CultureInfo.InvariantCulture);
                }
                else if (dateStr.Length == 6)
                {
                    // YYYYMM format (monthly) — use the last day of the month.
                    var year = int.Parse(dateStr[..4], CultureInfo.InvariantCulture);
                    var month = int.Parse(dateStr[4..], CultureInfo.InvariantCulture);
                    date = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
                }
                else
                {
                    continue;
                }

                // Filter to requested date range.
                if (date < request.Range.From || date > request.Range.To)
                {
                    continue;
                }

                if (fields.Length != factorNames.Length + 1)
                {
                    return BuildErrorEnvelope("Malformed factor row in Fama-French dataset.");
                }

                var factors = new Dictionary<string, decimal>(StringComparer.Ordinal);
                for (var j = 1; j < fields.Length; j++)
                {
                    var valueStr = fields[j].Trim();
                    if (!decimal.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        return BuildErrorEnvelope("Malformed factor value in Fama-French dataset.");
                    }

                    // Factor values are in percentage form; convert to decimal.
                    factors[factorNames[j - 1]] = value / 100m;
                }

                foreach (var (factorName, value) in factors)
                {
                    observations.Add(new FactorObservation(date, factorName, value));
                }
            }

            _logger.LogInformation(
                "Fetched {Count} factor observations from Fama-French dataset {DatasetName}",
                observations.Count,
                request.DatasetName);

            var issues = new List<DataIssue>();
            if (observations.Count == 0 && CoverageComputation.CountExpectedPoints(_calendar, request.Range, request.Frequency) > 0)
            {
                issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Error,
                    $"No factor observations found within the requested date range for dataset {request.DatasetName}."));
            }

            var returnedDates = observations.Select(o => o.Date).ToHashSet();
            var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
                _calendar, request.Range, request.Frequency, returnedDates);
            issues.AddRange(gapIssues);

            var provenance = new DataProvenance(
                Provider: new ProviderCode("fama-french"),
                Dataset: request.DatasetName.Value,
                LicenseFlag: LicenseType.Free,
                RetrievalMode: RetrievalMode.Api,
                Freshness: FreshnessClass.EndOfDay,
                RetrievedAtUtc: _clock.UtcNow,
                SourceUrl: url,
                DataDate: observations.Count > 0 ? observations.Max(o => o.Date) : null);

            return new DataEnvelope<IReadOnlyList<FactorObservation>>(
                Payload: observations,
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
            _logger.LogError(ex, "Failed to fetch Fama-French dataset {DatasetName}", request.DatasetName);

            return BuildErrorEnvelope(
                $"Failed to fetch Fama-French dataset {request.DatasetName}.");
        }
    }

    /// <summary>
    /// Builds an error envelope with an empty payload and a single error issue.
    /// </summary>
    /// <param name="message">The error message to include in the issue.</param>
    /// <returns>A <see cref="DataEnvelope{TPayload}"/> representing the error state.</returns>
    private static DataEnvelope<IReadOnlyList<FactorObservation>> BuildErrorEnvelope(string message) =>
        new(
            Payload: [],
            Coverage: new DataCoverage(
                RequestedPoints: 0,
                ReturnedPoints: 0,
                MissingPoints: 0,
                CoverageRatio: 0m),
            Issues: [new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Error, message)],
            Provenance: []);
}
