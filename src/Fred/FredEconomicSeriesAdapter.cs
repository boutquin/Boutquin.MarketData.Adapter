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

namespace Boutquin.MarketData.Adapter.Fred;

/// <summary>
/// Fetches economic time series observations from the FRED API and returns canonical
/// <see cref="ScalarObservation"/> records with full provenance and coverage metadata.
/// </summary>
/// <remarks>
/// <para>
/// The adapter calls the <c>/fred/series/observations</c> endpoint, persists the raw JSON
/// response via <see cref="IRawDocumentStore"/>, then parses observations into
/// <see cref="ScalarObservation"/> records. Entries where FRED reports the value as
/// <c>"."</c> (its sentinel for missing data) are skipped and counted as missing points.
/// </para>
/// <para>
/// When <see cref="FredOptions.NormalizePercentToDecimal"/> is enabled for a known
/// percent-denominated series, values are divided by 100 (e.g., 5.25 becomes 0.0525).
/// Otherwise the published magnitude is preserved and the unit is marked unknown.
/// </para>
/// </remarks>
public sealed class FredEconomicSeriesAdapter : IDataSourceAdapter<EconomicSeriesRequest, ScalarObservation>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly FredOptions _options;
    private readonly ILogger<FredEconomicSeriesAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FredEconomicSeriesAdapter"/> class.
    /// </summary>
    /// <param name="transport">HTTP transport for making API requests.</param>
    /// <param name="rawStore">Store for persisting raw API responses before parsing.</param>
    /// <param name="clock">Clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">Business calendar for computing calendar-aware coverage.</param>
    /// <param name="options">FRED adapter configuration options.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public FredEconomicSeriesAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("fred")] IBusinessCalendar calendar,
        IOptions<FredOptions> options,
        ILogger<FredEconomicSeriesAdapter> logger)
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

    /// <summary>The stable <c>fred</c> provider identity attached to this adapter's provenance.</summary>
    public ProviderCode ProviderKey => new("fred");

    /// <summary>The fallback rank, 100; adapters with higher ranks are attempted first.</summary>
    public int Priority => 100;

    /// <summary>Checks the request against this provider's supported routing constraints without performing I/O.</summary>
    /// <param name="request">Requests with a series identifier and a supported observation frequency.</param>
    /// <returns><see langword="true"/> when the request matches the adapter's declared support; this does not guarantee provider availability.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public bool CanHandle(EconomicSeriesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return !string.IsNullOrWhiteSpace(request.SeriesId.Value) &&
        request.Frequency is DataFrequency.Daily or DataFrequency.Weekly or DataFrequency.Monthly;
    }

    /// <summary>Retrieves observations for the requested economic series from FRED.</summary>
    /// <param name="request">Requests with a series identifier and a supported observation frequency.</param>
    /// <param name="cancellationToken">Cancels transport, raw-payload persistence and parsing where those operations support cancellation.</param>
    /// <returns>A task yielding canonical records together with provider provenance, coverage and data-quality issues. Empty or partial responses retain their failure or coverage information.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="OperationCanceledException">The caller or a dependency cancels the operation.</exception>
    /// <remarks>Successful fetches include raw-payload persistence and normalization. Inspect the envelope's issues and coverage before using its records; a returned envelope alone does not certify complete data.</remarks>
    public async Task<DataEnvelope<IReadOnlyList<ScalarObservation>>> FetchAsync(
        EconomicSeriesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanHandle(request))
        {
            throw new ArgumentException("Unsupported FRED series or frequency.", nameof(request));
        }
        if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new InvalidOperationException("FRED BaseUrl must be an HTTPS origin without credentials, query, or fragment.");
        }
        var encodedSeriesId = Uri.EscapeDataString(request.SeriesId.Value);
        var sourceUrl = FormattableString.Invariant($"{_options.BaseUrl}/fred/series/observations?series_id={encodedSeriesId}&observation_start={request.Range.From:yyyy-MM-dd}&observation_end={request.Range.To:yyyy-MM-dd}&file_type=json");
        var url = sourceUrl + "&api_key=" + Uri.EscapeDataString(_options.ApiKey);

        try
        {
            var uri = new Uri(url);
            var stream = await _transport.GetAsync(uri, null, cancellationToken).ConfigureAwait(false);

            string json;
            using (var reader = new StreamReader(stream))
            {
                json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var storeKey = FormattableString.Invariant($"fred/series/{encodedSeriesId}/{request.Range.From:yyyyMMdd}-{request.Range.To:yyyyMMdd}");
            await _rawStore.SaveAsync(storeKey, json, cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var observationsElement = doc.RootElement.GetProperty("observations");

            var observations = new List<ScalarObservation>();
            var skipped = 0;

            foreach (var entry in observationsElement.EnumerateArray())
            {
                var valueStr = entry.GetProperty("value").GetString();
                if (valueStr is null or "." || string.IsNullOrWhiteSpace(valueStr))
                {
                    skipped++;
                    continue;
                }

                var date = DateOnly.ParseExact(
                    entry.GetProperty("date").GetString()!,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);

                if (date < request.Range.From || date > request.Range.To)
                {
                    continue;
                }
                var value = decimal.Parse(valueStr, CultureInfo.InvariantCulture);

                if (_options.NormalizePercentToDecimal)
                {
                    value /= 100m;
                }

                observations.Add(new ScalarObservation(
                    date, value, _options.NormalizePercentToDecimal ? "decimal" : "unknown"));
            }

            _logger.LogInformation(
                "Fetched {Count} observations for FRED series {SeriesId}",
                observations.Count,
                request.SeriesId);

            var issues = new List<DataIssue>();
            if (observations.Count == 0 && CoverageComputation.CountExpectedPoints(_calendar, request.Range, request.Frequency) > 0)
            {
                issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Error,
                    $"FRED returned 0 usable observations for series {request.SeriesId}."));
            }
            else if (skipped > 0)
            {
                issues.Add(new DataIssue(new IssueCode("MISSING_VALUES"), IssueSeverity.Warning,
                    $"Skipped {skipped} observation(s) with missing values ('.') for series {request.SeriesId}."));
            }

            var returnedDates = observations.Select(o => o.Date).ToHashSet();
            var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
                _calendar, request.Range, request.Frequency, returnedDates);
            issues.AddRange(gapIssues);

            var provenance = new DataProvenance(
                Provider: new ProviderCode("fred"),
                Dataset: request.SeriesId.Value,
                LicenseFlag: LicenseType.Free,
                RetrievalMode: RetrievalMode.Api,
                Freshness: FreshnessClass.EndOfDay,
                RetrievedAtUtc: _clock.UtcNow,
                SourceUrl: sourceUrl,
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
            _logger.LogError("Failed to fetch FRED series {SeriesId} ({ErrorType})", request.SeriesId, ex.GetType().Name);

            var errorCoverage = new DataCoverage(
                RequestedPoints: 0,
                ReturnedPoints: 0,
                MissingPoints: 0,
                CoverageRatio: 0m);

            var errorIssue = new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Error,
                $"Failed to fetch FRED series {request.SeriesId}.");

            return new DataEnvelope<IReadOnlyList<ScalarObservation>>(
                Payload: [],
                Coverage: errorCoverage,
                Issues: [errorIssue],
                Provenance: []);
        }
    }
}
