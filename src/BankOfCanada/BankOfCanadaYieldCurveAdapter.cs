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
using Boutquin.MarketData.Transport.Http;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boutquin.MarketData.Adapter.BankOfCanada;

/// <summary>
/// Fetches Canadian zero-coupon yield curve data from the Bank of Canada Valet API
/// and returns canonical <see cref="YieldCurveQuote"/> records with provenance metadata.
/// </summary>
/// <remarks>
/// The adapter calls the <c>/observations/group/{GroupName}/json?recent=1</c> endpoint,
/// which returns the most recent observation across all series in the specified group.
/// Each series name encodes the tenor as the final dot-separated segment (e.g.,
/// "BD.CDN.ZERO.3M" yields tenor "3M"). Rates are published as percentages and are
/// converted to decimal form (divided by 100) before being returned.
/// </remarks>
public sealed class BankOfCanadaYieldCurveAdapter : IDataSourceAdapter<YieldCurveQuoteRequest, YieldCurveQuote>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly BankOfCanadaOptions _options;
    private readonly ILogger<BankOfCanadaYieldCurveAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BankOfCanadaYieldCurveAdapter"/> class.
    /// </summary>
    /// <param name="transport">The HTTP transport used to fetch data from the Valet API.</param>
    /// <param name="rawStore">The raw document store for persisting API responses before parsing.</param>
    /// <param name="clock">The clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">Business calendar for computing calendar-aware coverage.</param>
    /// <param name="options">Configuration options for the Bank of Canada adapter.</param>
    /// <param name="logger">The logger instance for diagnostic output.</param>
    public BankOfCanadaYieldCurveAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("bankofcanada")] IBusinessCalendar calendar,
        IOptions<BankOfCanadaOptions> options,
        ILogger<BankOfCanadaYieldCurveAdapter> logger)
    {
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProviderCode ProviderKey => new("bankofcanada");

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanHandle(YieldCurveQuoteRequest request) => true;

    /// <inheritdoc />
    public async Task<DataEnvelope<IReadOnlyList<YieldCurveQuote>>> FetchAsync(
        YieldCurveQuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_options.BaseUrl}/observations/group/{_options.GroupName}/json?recent=1";

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
                $"bankofcanada/yield-curve/{request.CurveId}/{request.AsOfDate:yyyyMMdd}",
                json,
                cancellationToken).ConfigureAwait(false);

            var issues = new List<DataIssue>();
            var quotes = new List<YieldCurveQuote>();
            DateOnly? observationDate = null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("observations", out var observations) ||
                observations.GetArrayLength() == 0)
            {
                issues.Add(new DataIssue(new IssueCode("EMPTY_OBSERVATIONS"), IssueSeverity.Error,
                    $"Bank of Canada returned no observations for group {_options.GroupName}."));
            }
            else
            {
                // Take the last element (most recent observation).
                var lastIndex = observations.GetArrayLength() - 1;
                var observation = observations[lastIndex];

                // Extract the actual observation date from the "d" property.
                if (observation.TryGetProperty("d", out var dateElement) &&
                    dateElement.GetString() is { } dateStr &&
                    DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                {
                    observationDate = parsedDate;
                    if (parsedDate != request.AsOfDate)
                    {
                        issues.Add(new DataIssue(new IssueCode("DATE_ROLLBACK"), IssueSeverity.Warning,
                            $"{request.CurveId}: no data for {request.AsOfDate:yyyy-MM-dd}; using {parsedDate:yyyy-MM-dd}."));
                    }
                }

                foreach (var property in observation.EnumerateObject())
                {
                    // Skip the date marker property.
                    if (property.Name == "d")
                    {
                        continue;
                    }

                    // Extract tenor from the last segment of the series name.
                    var segments = property.Name.Split('.');
                    var tenor = segments[^1];

                    // The value is an object with a "v" property containing the rate string.
                    if (property.Value.ValueKind != JsonValueKind.Object ||
                        !property.Value.TryGetProperty("v", out var vElement) ||
                        vElement.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    var rateString = vElement.GetString();
                    if (rateString is null ||
                        !decimal.TryParse(rateString, NumberStyles.Number, CultureInfo.InvariantCulture, out var ratePercent))
                    {
                        continue;
                    }

                    // Convert from percentage to decimal form (e.g., 2.5 -> 0.025).
                    var rate = ratePercent / 100m;
                    quotes.Add(new YieldCurveQuote(tenor, rate));
                }
            }

            _logger.LogInformation(
                "Fetched {Count} yield curve quotes for {CurveId} from Bank of Canada",
                quotes.Count,
                request.CurveId);

            if (quotes.Count == 0 && issues.Count == 0)
            {
                issues.Add(new DataIssue(new IssueCode("NO_QUOTES"), IssueSeverity.Error,
                    $"No parseable yield curve quotes found for {request.CurveId}."));
            }

            var returnedPoints = quotes.Count > 0 ? 1 : 0;
            var coverage = new DataCoverage(
                RequestedPoints: 1,
                ReturnedPoints: returnedPoints,
                MissingPoints: 1 - returnedPoints,
                CoverageRatio: returnedPoints);

            var provenance = new DataProvenance(
                Provider: new ProviderCode("bankofcanada"),
                Dataset: request.CurveId.Value,
                LicenseFlag: LicenseType.Free,
                RetrievalMode: RetrievalMode.Api,
                Freshness: FreshnessClass.EndOfDay,
                RetrievedAtUtc: _clock.UtcNow,
                SourceUrl: url,
                DataDate: observationDate);

            return new DataEnvelope<IReadOnlyList<YieldCurveQuote>>(
                Payload: quotes,
                Coverage: coverage,
                Issues: issues,
                Provenance: [provenance]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch yield curve data for {CurveId} from Bank of Canada", request.CurveId);

            return new DataEnvelope<IReadOnlyList<YieldCurveQuote>>(
                Payload: Array.Empty<YieldCurveQuote>(),
                Coverage: new DataCoverage(
                    RequestedPoints: 1,
                    ReturnedPoints: 0,
                    MissingPoints: 1,
                    CoverageRatio: 0m),
                Issues: [new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Error, $"Bank of Canada fetch failed: {ex.Message}")],
                Provenance: [new DataProvenance(
                    Provider: new ProviderCode("bankofcanada"),
                    Dataset: request.CurveId.Value,
                    LicenseFlag: LicenseType.Free,
                    RetrievalMode: RetrievalMode.Api,
                    Freshness: FreshnessClass.Unknown,
                    RetrievedAtUtc: _clock.UtcNow,
                    SourceUrl: url)]);
        }
    }
}
