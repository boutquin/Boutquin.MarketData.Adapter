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

namespace Boutquin.MarketData.Adapter.BankOfCanada;

/// <summary>
/// Fetches CORRA (Canadian Overnight Repo Rate Average) fixings from the Bank of Canada
/// Valet API and returns canonical <see cref="ScalarObservation"/> records with provenance metadata.
/// </summary>
/// <remarks>
/// <para>
/// The adapter calls the <c>/observations/group/{CorraGroupName}/json?start_date={from}&amp;end_date={to}</c>
/// endpoint, which returns a JSON response containing an <c>observations</c> array. Each element
/// has a date property <c>"d"</c> and one or more series properties whose values are objects
/// with a <c>"v"</c> sub-property containing the rate as a string percentage. Rates are converted
/// from percentage to decimal form (divided by 100) before being returned.
/// </para>
/// <para>
/// This adapter only handles requests where the <see cref="OvernightFixingRequest.BenchmarkId"/>
/// is "CORRA" (case-insensitive comparison). CORRA data is freely available from the Bank of
/// Canada without authentication.
/// </para>
/// </remarks>
public sealed class BankOfCanadaCorraAdapter : IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly BankOfCanadaOptions _options;
    private readonly ILogger<BankOfCanadaCorraAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BankOfCanadaCorraAdapter"/> class.
    /// </summary>
    /// <param name="transport">The HTTP transport used to fetch data from the Valet API.</param>
    /// <param name="rawStore">The raw document store for persisting API responses before parsing.</param>
    /// <param name="clock">The clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">The business calendar for computing coverage against expected trading days.</param>
    /// <param name="options">Configuration options for the Bank of Canada adapter.</param>
    /// <param name="logger">The logger instance for diagnostic output.</param>
    public BankOfCanadaCorraAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("boc-corra")] IBusinessCalendar calendar,
        IOptions<BankOfCanadaOptions> options,
        ILogger<BankOfCanadaCorraAdapter> logger)
    {
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProviderCode ProviderKey => new("boc-corra");

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanHandle(OvernightFixingRequest request) =>
        string.Equals(request.BenchmarkId.Value, "CORRA", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<DataEnvelope<IReadOnlyList<ScalarObservation>>> FetchAsync(
        OvernightFixingRequest request,
        CancellationToken cancellationToken = default)
    {
        var fromDate = request.Range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toDate = request.Range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var url = $"{_options.BaseUrl}/observations/group/{_options.CorraGroupName}/json?start_date={fromDate}&end_date={toDate}";

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
                $"boc/corra/{request.Range.From:yyyyMMdd}-{request.Range.To:yyyyMMdd}",
                json,
                cancellationToken).ConfigureAwait(false);

            var issues = new List<DataIssue>();
            var observations = new List<ScalarObservation>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("observations", out var obsArray))
            {
                foreach (var element in obsArray.EnumerateArray())
                {
                    if (!element.TryGetProperty("d", out var dateElement))
                    {
                        continue;
                    }

                    var dateString = dateElement.GetString();
                    if (dateString is null ||
                        !DateOnly.TryParseExact(dateString, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        continue;
                    }

                    // Iterate properties, skip "d", take the first property with a "v" sub-property.
                    decimal? rateDecimal = null;
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name == "d")
                        {
                            continue;
                        }

                        if (property.Value.ValueKind == JsonValueKind.Object &&
                            property.Value.TryGetProperty("v", out var vElement) &&
                            vElement.ValueKind != JsonValueKind.Null)
                        {
                            var rateString = vElement.GetString();
                            if (rateString is not null &&
                                decimal.TryParse(rateString, NumberStyles.Number, CultureInfo.InvariantCulture, out var percentRate))
                            {
                                // Convert from percentage to decimal form (e.g., 4.25 -> 0.0425).
                                rateDecimal = percentRate / 100m;
                            }

                            break;
                        }
                    }

                    if (rateDecimal.HasValue)
                    {
                        observations.Add(new ScalarObservation(date, rateDecimal.Value, "decimal-rate"));
                    }
                }
            }

            _logger.LogInformation(
                "Fetched {Count} CORRA fixings from Bank of Canada",
                observations.Count);

            if (observations.Count == 0)
            {
                issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Error,
                    "Bank of Canada returned no CORRA fixings for the requested date range."));
            }

            var returnedDates = observations.Select(o => o.Date).ToHashSet();
            var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
                _calendar, request.Range, request.Frequency, returnedDates);
            issues.AddRange(gapIssues);

            var provenance = new DataProvenance(
                Provider: new ProviderCode("boc-corra"),
                Dataset: "CORRA",
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
            _logger.LogError(ex, "Failed to fetch CORRA fixings from Bank of Canada");

            return new DataEnvelope<IReadOnlyList<ScalarObservation>>(
                Payload: Array.Empty<ScalarObservation>(),
                Coverage: new DataCoverage(
                    RequestedPoints: 0,
                    ReturnedPoints: 0,
                    MissingPoints: 0,
                    CoverageRatio: 0m),
                Issues: [new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Error, $"Bank of Canada CORRA fetch failed: {ex.Message}")],
                Provenance: [new DataProvenance(
                    Provider: new ProviderCode("boc-corra"),
                    Dataset: "CORRA",
                    LicenseFlag: LicenseType.Free,
                    RetrievalMode: RetrievalMode.Api,
                    Freshness: FreshnessClass.Unknown,
                    RetrievedAtUtc: _clock.UtcNow,
                    SourceUrl: url)]);
        }
    }
}
