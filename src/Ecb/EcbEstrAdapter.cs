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
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProviderCode ProviderKey => new("ecb-estr");

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanHandle(OvernightFixingRequest request) =>
        string.Equals(request.BenchmarkId.Value, "ESTR", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<DataEnvelope<IReadOnlyList<ScalarObservation>>> FetchAsync(
        OvernightFixingRequest request,
        CancellationToken cancellationToken = default)
    {
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

            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return BuildErrorEnvelope("EMPTY_RESPONSE", "ECB returned an empty CSV response for ESTR.");
            }

            // Parse header to locate column indices.
            var headerColumns = lines[0].Split(',');
            var timePeriodIndex = Array.FindIndex(headerColumns, c =>
                string.Equals(c.Trim(), "TIME_PERIOD", StringComparison.OrdinalIgnoreCase));
            var obsValueIndex = Array.FindIndex(headerColumns, c =>
                string.Equals(c.Trim(), "OBS_VALUE", StringComparison.OrdinalIgnoreCase));

            if (timePeriodIndex < 0 || obsValueIndex < 0)
            {
                return BuildErrorEnvelope(
                    "SCHEMA_MISMATCH",
                    "ECB CSV response is missing required TIME_PERIOD or OBS_VALUE columns.");
            }

            var observations = new List<ScalarObservation>();
            var skipped = 0;
            var totalDataRows = lines.Length - 1;

            for (var i = 1; i < lines.Length; i++)
            {
                var fields = lines[i].Split(',');
                if (fields.Length <= Math.Max(timePeriodIndex, obsValueIndex))
                {
                    skipped++;
                    continue;
                }

                var obsValueStr = fields[obsValueIndex].Trim();
                if (string.IsNullOrWhiteSpace(obsValueStr))
                {
                    skipped++;
                    continue;
                }

                var date = DateOnly.ParseExact(
                    fields[timePeriodIndex].Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);

                var rate = decimal.Parse(obsValueStr, CultureInfo.InvariantCulture) / 100m;

                observations.Add(new ScalarObservation(date, rate, "decimal-rate"));
            }

            _logger.LogInformation(
                "Fetched {Count} \u20acSTR fixings from ECB",
                observations.Count);

            var issues = new List<DataIssue>();
            if (observations.Count == 0)
            {
                issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Error,
                    "ECB returned 0 usable ESTR observations."));
            }
            else if (skipped > 0)
            {
                issues.Add(new DataIssue(new IssueCode("MISSING_VALUES"), IssueSeverity.Warning,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Skipped {0} row(s) with missing OBS_VALUE in ECB ESTR response.",
                        skipped)));
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch ESTR fixings from ECB");

            return BuildErrorEnvelope("FETCH_FAILED", $"Failed to fetch ESTR fixings from ECB: {ex.Message}");
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
}
