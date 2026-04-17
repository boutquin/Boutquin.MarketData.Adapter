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
using System.Text.Json;

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

namespace Boutquin.MarketData.Adapter.NewYorkFed;

/// <summary>
/// Fetches SOFR (Secured Overnight Financing Rate) fixings from the New York Federal Reserve
/// Markets API and returns canonical <see cref="ScalarObservation"/> records with provenance metadata.
/// </summary>
/// <remarks>
/// <para>
/// The adapter calls the <c>/rates/secured/sofr/search.json</c> endpoint with a date range,
/// which returns a JSON response containing a <c>refRates</c> array. Each element provides
/// an <c>effectiveDate</c> (YYYY-MM-DD) and a <c>percentRate</c> (decimal percentage).
/// Rates are converted from percentage to decimal form (divided by 100) before being returned.
/// </para>
/// <para>
/// This adapter only handles requests where the <see cref="OvernightFixingRequest.BenchmarkId"/>
/// is "SOFR" (case-insensitive comparison). SOFR data is freely available from the NY Fed
/// without authentication.
/// </para>
/// </remarks>
public sealed class NewYorkFedSofrAdapter : IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly NewYorkFedOptions _options;
    private readonly ILogger<NewYorkFedSofrAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewYorkFedSofrAdapter"/> class.
    /// </summary>
    /// <param name="transport">The HTTP transport used to fetch data from the NY Fed Markets API.</param>
    /// <param name="rawStore">The raw document store for persisting API responses before parsing.</param>
    /// <param name="clock">The clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">The business calendar for computing coverage against expected trading days.</param>
    /// <param name="options">Configuration options for the New York Fed adapter.</param>
    /// <param name="logger">The logger instance for diagnostic output.</param>
    public NewYorkFedSofrAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("nyfed-sofr")] IBusinessCalendar calendar,
        IOptions<NewYorkFedOptions> options,
        ILogger<NewYorkFedSofrAdapter> logger)
    {
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProviderCode ProviderKey => new("nyfed-sofr");

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanHandle(OvernightFixingRequest request) =>
        string.Equals(request.BenchmarkId.Value, "SOFR", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<DataEnvelope<IReadOnlyList<ScalarObservation>>> FetchAsync(
        OvernightFixingRequest request,
        CancellationToken cancellationToken = default)
    {
        var fromDate = request.Range.From.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
        var toDate = request.Range.To.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
        var url = $"{_options.BaseUrl}/rates/secured/sofr/search.json?startDate={fromDate}&endDate={toDate}&type=rate";

        try
        {
            var uri = new Uri(url);
            var stream = await _transport.GetAsync(uri, null, cancellationToken).ConfigureAwait(false);

            string json;
            using (var reader = new StreamReader(stream))
            {
                json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            await _rawStore.SaveAsync(
                $"nyfed/sofr/{request.Range.From:yyyyMMdd}-{request.Range.To:yyyyMMdd}",
                json,
                cancellationToken).ConfigureAwait(false);

            var issues = new List<DataIssue>();
            var observations = new List<ScalarObservation>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("refRates", out var refRates))
            {
                foreach (var element in refRates.EnumerateArray())
                {
                    if (!element.TryGetProperty("effectiveDate", out var dateElement) ||
                        !element.TryGetProperty("percentRate", out var rateElement))
                    {
                        continue;
                    }

                    var dateString = dateElement.GetString();
                    if (dateString is null ||
                        !DateOnly.TryParseExact(dateString, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        continue;
                    }

                    decimal percentRate;
                    if (rateElement.ValueKind == JsonValueKind.Number)
                    {
                        percentRate = rateElement.GetDecimal();
                    }
                    else
                    {
                        var rateString = rateElement.GetString();
                        if (rateString is null ||
                            !decimal.TryParse(rateString, NumberStyles.Number, CultureInfo.InvariantCulture, out percentRate))
                        {
                            continue;
                        }
                    }

                    // Convert from percentage to decimal form (e.g., 5.31 -> 0.0531).
                    var decimalRate = percentRate / 100m;
                    observations.Add(new ScalarObservation(date, decimalRate, "decimal-rate"));
                }
            }

            _logger.LogInformation(
                "Fetched {Count} SOFR fixings from NY Fed",
                observations.Count);

            if (observations.Count == 0)
            {
                issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Error,
                    "NY Fed returned no SOFR fixings for the requested date range."));
            }

            var returnedDates = observations.Select(o => o.Date).ToHashSet();
            var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
                _calendar, request.Range, request.Frequency, returnedDates);
            issues.AddRange(gapIssues);

            var provenance = new DataProvenance(
                Provider: new ProviderCode("nyfed-sofr"),
                Dataset: "SOFR",
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
            _logger.LogError(ex, "Failed to fetch SOFR fixings from NY Fed");

            return new DataEnvelope<IReadOnlyList<ScalarObservation>>(
                Payload: Array.Empty<ScalarObservation>(),
                Coverage: new DataCoverage(
                    RequestedPoints: 0,
                    ReturnedPoints: 0,
                    MissingPoints: 0,
                    CoverageRatio: 0m),
                Issues: [new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Error, $"NY Fed SOFR fetch failed: {ex.Message}")],
                Provenance: [new DataProvenance(
                    Provider: new ProviderCode("nyfed-sofr"),
                    Dataset: "SOFR",
                    LicenseFlag: LicenseType.Free,
                    RetrievalMode: RetrievalMode.Api,
                    Freshness: FreshnessClass.Unknown,
                    RetrievedAtUtc: _clock.UtcNow,
                    SourceUrl: url)]);
        }
    }
}
