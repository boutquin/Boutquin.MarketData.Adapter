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

namespace Boutquin.MarketData.Adapter.Frankfurter;

/// <summary>
/// Fetches historical FX spot rates from the Frankfurter API (backed by the European Central Bank).
/// </summary>
/// <remarks>
/// Frankfurter provides free, unauthenticated access to daily ECB reference rates.
/// This adapter iterates over each requested currency pair, fetches the time series
/// from the API, persists the raw JSON response, and parses it into canonical
/// <see cref="FxRate"/> records. Pairs that fail are logged as warnings and reported
/// as <see cref="DataIssue"/> entries without aborting the remaining pairs.
/// </remarks>
public sealed class FrankfurterFxAdapter : IDataSourceAdapter<FxHistoryRequest, FxRate>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly FrankfurterOptions _options;
    private readonly ILogger<FrankfurterFxAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FrankfurterFxAdapter"/> class.
    /// </summary>
    /// <param name="transport">HTTP transport for fetching data from the Frankfurter API.</param>
    /// <param name="rawStore">Store for persisting raw API responses before parsing.</param>
    /// <param name="clock">Clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">Business calendar for computing calendar-aware coverage.</param>
    /// <param name="options">Configuration options containing the API base URL.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public FrankfurterFxAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("frankfurter")] IBusinessCalendar calendar,
        IOptions<FrankfurterOptions> options,
        ILogger<FrankfurterFxAdapter> logger)
    {
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProviderCode ProviderKey => new("frankfurter");

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanHandle(FxHistoryRequest request) =>
        request.Frequency == DataFrequency.Daily && request.Pairs.Count > 0;

    /// <inheritdoc />
    public async Task<DataEnvelope<IReadOnlyList<FxRate>>> FetchAsync(
        FxHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var allRates = new List<FxRate>();
        var provenance = new List<DataProvenance>();
        var issues = new List<DataIssue>();
        var pairsWithData = 0;

        foreach (var pair in request.Pairs)
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/{request.Range.From:yyyy-MM-dd}..{request.Range.To:yyyy-MM-dd}" +
                      $"?base={pair.BaseCurrency}&symbols={pair.QuoteCurrency}";

            try
            {
                var uri = new Uri(url);
                using var stream = await _transport.GetAsync(uri, null, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                var rawKey = $"frankfurter/fx/{pair.BaseCurrency}-{pair.QuoteCurrency}/{request.Range.From:yyyyMMdd}-{request.Range.To:yyyyMMdd}";
                await _rawStore.SaveAsync(rawKey, json, cancellationToken).ConfigureAwait(false);

                var pairRates = ParseRates(json, pair);
                allRates.AddRange(pairRates);

                if (pairRates.Count > 0)
                {
                    pairsWithData++;
                }
                else
                {
                    issues.Add(new DataIssue(new IssueCode("NO_RATES"), IssueSeverity.Warning,
                        $"Frankfurter returned no rates for {pair}."));
                }

                _logger.LogInformation(
                    "Fetched {Count} FX rates for {Pair} from Frankfurter",
                    pairRates.Count,
                    pair);

                provenance.Add(new DataProvenance(
                    Provider: new ProviderCode("frankfurter"),
                    Dataset: pair.ToString(),
                    LicenseFlag: LicenseType.Free,
                    RetrievalMode: RetrievalMode.Api,
                    Freshness: FreshnessClass.EndOfDay,
                    RetrievedAtUtc: _clock.UtcNow,
                    SourceUrl: url,
                    DataDate: pairRates.Count > 0 ? pairRates.Max(r => r.Date) : null));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch FX rates for {Pair} from Frankfurter", pair);
                issues.Add(new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Error,
                    $"Failed to fetch rates for {pair}: {ex.Message}"));
            }
        }

        var returnedDates = allRates.Select(r => r.Date).ToHashSet();
        var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
            _calendar, request.Range, request.Frequency, returnedDates);
        issues.AddRange(gapIssues);

        return new DataEnvelope<IReadOnlyList<FxRate>>(
            Payload: allRates,
            Coverage: coverage,
            Issues: issues,
            Provenance: provenance);
    }

    /// <summary>
    /// Parses Frankfurter JSON into a list of <see cref="FxRate"/> records for the specified pair.
    /// </summary>
    private static List<FxRate> ParseRates(string json, FxPair pair)
    {
        var rates = new List<FxRate>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("rates", out var ratesElement) ||
            ratesElement.ValueKind != JsonValueKind.Object)
        {
            return rates;
        }

        foreach (var dateProperty in ratesElement.EnumerateObject())
        {
            if (!DateOnly.TryParseExact(dateProperty.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            if (dateProperty.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (dateProperty.Value.TryGetProperty(pair.QuoteCurrency.ToString(), out var rateElement) &&
                rateElement.TryGetDecimal(out var rate))
            {
                rates.Add(new FxRate(date, pair.BaseCurrency, pair.QuoteCurrency, rate));
            }
        }

        return rates;
    }
}
