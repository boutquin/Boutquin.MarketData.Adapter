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
using System.Text;

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

namespace Boutquin.MarketData.Adapter.Ecb;

/// <summary>
/// Fetches Euro Short-Term Rate (EUR/STR) fixing history from the ECB Data API and returns
/// canonical <see cref="ScalarObservation"/> records with full provenance and coverage metadata.
/// </summary>
/// <remarks>
/// <para>
/// The adapter calls the ECB SDMX data service using CSV format (<c>format=csvdata</c>),
/// persists the raw CSV response via <see cref="IRawDocumentStore"/>, then parses observations
/// into <see cref="ScalarObservation"/> records. The relevant columns are <c>TIME_PERIOD</c>
/// (date as YYYY-MM-DD) and <c>OBS_VALUE</c> (decimal rate in percent form). Rows with empty
/// <c>OBS_VALUE</c> are skipped and counted as missing points.
/// </para>
/// <para>
/// The ECB reports rates in percent form (e.g., 3.90 for 3.90%); this adapter divides by 100
/// to produce decimal form (e.g., 0.039) for consistency with the canonical record contract.
/// </para>
/// </remarks>
public sealed class EcbEstrAdapter : IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly EcbOptions _options;
    private readonly ILogger<EcbEstrAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EcbEstrAdapter"/> class.
    /// </summary>
    /// <param name="transport">HTTP transport for making API requests.</param>
    /// <param name="rawStore">Store for persisting raw API responses before parsing.</param>
    /// <param name="clock">Clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">The business calendar for computing coverage against expected trading days.</param>
    /// <param name="options">ECB adapter configuration options.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public EcbEstrAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("ecb-estr")] IBusinessCalendar calendar,
        IOptions<EcbOptions> options,
        ILogger<EcbEstrAdapter> logger)
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

    /// <summary>The stable <c>ecb-estr</c> provider identity attached to this adapter's provenance.</summary>
    public ProviderCode ProviderKey => new("ecb-estr");

    /// <summary>The fallback rank, 100; adapters with higher ranks are attempted first.</summary>
    public int Priority => 100;

    /// <summary>Checks the request against this provider's supported routing constraints without performing I/O.</summary>
    /// <param name="request">Daily requests for the ESTR benchmark.</param>
    /// <returns><see langword="true"/> when the request matches the adapter's declared support; this does not guarantee provider availability.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public bool CanHandle(OvernightFixingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.Equals(request.BenchmarkId.Value, "ESTR", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Retrieves euro short-term rate observations within the requested dates from ECB.</summary>
    /// <param name="request">Daily requests for the ESTR benchmark.</param>
    /// <param name="cancellationToken">Cancels transport, raw-payload persistence and parsing where those operations support cancellation.</param>
    /// <returns>A task yielding canonical records together with provider provenance, coverage and data-quality issues. Empty or partial responses retain their failure or coverage information.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="OperationCanceledException">The caller or a dependency cancels the operation.</exception>
    /// <remarks>Successful fetches include raw-payload persistence and normalization. Inspect the envelope's issues and coverage before using its records; a returned envelope alone does not certify complete data.</remarks>
    public async Task<DataEnvelope<IReadOnlyList<ScalarObservation>>> FetchAsync(
        OvernightFixingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanHandle(request))
        {
            return BuildErrorEnvelope("UNSUPPORTED_BENCHMARK", "ECB adapter only supports ESTR requests.");
        }

        if (!string.Equals(_options.EstrSeriesKey, "EST/B.EU000A2X2A25.WT", StringComparison.OrdinalIgnoreCase))
        {
            return BuildErrorEnvelope("UNSUPPORTED_SERIES", "ECB series must identify the Euro short-term rate.");
        }

        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/{1}?startPeriod={2:yyyy-MM-dd}&endPeriod={3:yyyy-MM-dd}&format=csvdata",
            _options.BaseUrl,
            _options.EstrSeriesKey,
            request.Range.From,
            request.Range.To);

        try
        {
            var uri = new Uri(url);
            var headers = new Dictionary<string, string> { ["Accept"] = "text/csv" };
            var stream = await _transport.GetAsync(uri, headers, cancellationToken).ConfigureAwait(false);

            string csv;
            using (var reader = new StreamReader(stream))
            {
                csv = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var storeKey = string.Format(
                CultureInfo.InvariantCulture,
                "ecb/estr/{0:yyyyMMdd}-{1:yyyyMMdd}",
                request.Range.From,
                request.Range.To);
            await _rawStore.SaveAsync(storeKey, csv, cancellationToken).ConfigureAwait(false);

            var lines = SplitCsvRecords(csv);
            if (lines.Length == 0)
            {
                return BuildErrorEnvelope("EMPTY_RESPONSE", "ECB returned an empty CSV response for ESTR.");
            }

            // Parse header to locate column indices.
            if (!TryParseCsvLine(lines[0], out var headerColumns))
            {
                return BuildErrorEnvelope("SCHEMA_MISMATCH", "ECB CSV has an invalid header.");
            }
            var keyIndex = Array.FindIndex(headerColumns, c =>
                string.Equals(c.Trim(), "KEY", StringComparison.OrdinalIgnoreCase));
            var timePeriodIndex = Array.FindIndex(headerColumns, c =>
                string.Equals(c.Trim(), "TIME_PERIOD", StringComparison.OrdinalIgnoreCase));
            var obsValueIndex = Array.FindIndex(headerColumns, c =>
                string.Equals(c.Trim(), "OBS_VALUE", StringComparison.OrdinalIgnoreCase));

            if (keyIndex < 0 || timePeriodIndex < 0 || obsValueIndex < 0)
            {
                return BuildErrorEnvelope(
                    "SCHEMA_MISMATCH",
                    "ECB CSV response is missing required KEY, TIME_PERIOD or OBS_VALUE columns.");
            }

            var observations = new List<ScalarObservation>();
            var skipped = 0;
            var malformed = 0;
            var mismatched = 0;
            var totalDataRows = lines.Length - 1;

            for (var i = 1; i < lines.Length; i++)
            {
                if (!TryParseCsvLine(lines[i], out var fields))
                {
                    malformed++;
                    continue;
                }
                if (fields.Length <= Math.Max(keyIndex, Math.Max(timePeriodIndex, obsValueIndex)))
                {
                    skipped++;
                    continue;
                }

                if (!string.Equals(fields[keyIndex].Trim(), "EST.B.EU000A2X2A25.WT", StringComparison.OrdinalIgnoreCase))
                {
                    mismatched++;
                    continue;
                }

                var obsValueStr = fields[obsValueIndex].Trim();
                if (string.IsNullOrWhiteSpace(obsValueStr))
                {
                    skipped++;
                    continue;
                }

                if (!DateOnly.TryParseExact(fields[timePeriodIndex].Trim(), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    !decimal.TryParse(obsValueStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var percentRate))
                {
                    malformed++;
                    continue;
                }

                var rate = percentRate / 100m;

                if (date < request.Range.From || date > request.Range.To)
                {
                    continue;
                }

                observations.Add(new ScalarObservation(date, rate, "decimal-rate"));
            }

            _logger.LogInformation(
                "Fetched {Count} \u20acSTR fixings from ECB",
                observations.Count);

            var issues = new List<DataIssue>();
            if (observations.Count == 0 && CoverageComputation.CountExpectedPoints(_calendar, request.Range, request.Frequency) > 0)
            {
                issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Error,
                    "ECB returned 0 usable ESTR observations."));
            }
            else if (skipped > 0)
            {
                issues.Add(new DataIssue(new IssueCode("MISSING_VALUES"), IssueSeverity.Warning,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Skipped {0} incomplete row(s) in ECB ESTR response.",
                        skipped)));
            }

            if (malformed > 0)
            {
                issues.Add(new DataIssue(new IssueCode("MALFORMED_ROW"), IssueSeverity.Warning,
                    string.Format(CultureInfo.InvariantCulture,
                        "Skipped {0} malformed row(s) in ECB ESTR response.", malformed)));
            }
            if (mismatched > 0)
            {
                issues.Add(new DataIssue(new IssueCode("SERIES_MISMATCH"), IssueSeverity.Warning,
                    string.Format(CultureInfo.InvariantCulture,
                        "Skipped {0} row(s) for a different ECB series.", mismatched)));
            }

            var returnedDates = observations.Select(o => o.Date).ToHashSet();
            var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
                _calendar, request.Range, request.Frequency, returnedDates);
            issues.AddRange(gapIssues);

            var provenance = new DataProvenance(
                Provider: new ProviderCode("ecb-estr"),
                Dataset: "ESTR",
                LicenseFlag: LicenseType.Free,
                RetrievalMode: RetrievalMode.Api,
                Freshness: FreshnessClass.EndOfDay,
                RetrievedAtUtc: _clock.UtcNow,
                SourceUrl: url,
                DataDate: observations.Count > 0 ? observations.Max(o => o.Date) : null);

            return new DataEnvelope<IReadOnlyList<ScalarObservation>>(
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
            _logger.LogError(ex, "Failed to fetch ESTR fixings from ECB");

            return BuildErrorEnvelope("FETCH_FAILED", "Failed to fetch ESTR fixings from ECB.");
        }
    }

    private static DataEnvelope<IReadOnlyList<ScalarObservation>> BuildErrorEnvelope(string code, string message)
    {
        var errorCoverage = new DataCoverage(
            RequestedPoints: 0,
            ReturnedPoints: 0,
            MissingPoints: 0,
            CoverageRatio: 0m);

        var errorIssue = new DataIssue(new IssueCode(code), IssueSeverity.Error, message);

        return new DataEnvelope<IReadOnlyList<ScalarObservation>>(
            Payload: [],
            Coverage: errorCoverage,
            Issues: [errorIssue],
            Provenance: []);
    }

    private static string[] SplitCsvRecords(string csv)
    {
        var records = new List<string>();
        var record = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var current = csv[i];
            if (current == '"')
            {
                record.Append(current);
                if (quoted && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    record.Append(csv[++i]);
                    continue;
                }
                quoted = !quoted;
            }
            else if ((current == '\n' || current == '\r') && !quoted)
            {
                if (record.Length > 0)
                {
                    records.Add(record.ToString());
                    record.Clear();
                }
            }
            else
            {
                record.Append(current);
            }
        }
        if (record.Length > 0)
        {
            records.Add(record.ToString());
        }
        return [.. records];
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

}
