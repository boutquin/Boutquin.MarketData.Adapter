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
/// This adapter accepts exactly one non-identity currency pair, fetches the time series
/// from the API, persists the raw JSON response, and parses it into canonical
/// <see cref="FxRate"/> records. Pairs that fail are logged as warnings and reported
/// as <see cref="DataIssue"/> entries. Partition batches before dispatch because
/// <see cref="FxRate"/> does not carry pair ownership.
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

    /// <summary>The stable <c>frankfurter</c> provider identity attached to this adapter's provenance.</summary>
    public ProviderCode ProviderKey => new("frankfurter");

    /// <summary>The fallback rank, 100; adapters with higher ranks are attempted first.</summary>
    public int Priority => 100;

    /// <summary>Checks the request against this provider's supported routing constraints without performing I/O.</summary>
    /// <param name="request">Daily requests for exactly one non-identity currency pair.</param>
    /// <returns><see langword="true"/> when the request matches the adapter's declared support; this does not guarantee provider availability.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public bool CanHandle(FxHistoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Frequency == DataFrequency.Daily && request.Pairs.Count == 1
            && request.Pairs[0].BaseCurrency != request.Pairs[0].QuoteCurrency;
    }

    /// <summary>Retrieves daily reference FX observations for one currency pair from Frankfurter.</summary>
    /// <param name="request">Daily requests for exactly one non-identity currency pair.</param>
    /// <param name="cancellationToken">Cancels transport, raw-payload persistence and parsing where those operations support cancellation.</param>
    /// <returns>A task yielding canonical records together with provider provenance, coverage and data-quality issues. Empty or partial responses retain their failure or coverage information.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="OperationCanceledException">The caller or a dependency cancels the operation.</exception>
    /// <remarks>Successful fetches include raw-payload persistence and normalization. Inspect the envelope's issues and coverage before using its records; a returned envelope alone does not certify complete data.</remarks>
    public async Task<DataEnvelope<IReadOnlyList<FxRate>>> FetchAsync(
        FxHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanHandle(request))
        {
            throw new ArgumentException("Frankfurter fetches require one non-identity currency pair and daily frequency. Partition batches before dispatch.", nameof(request));
        }

        var allRates = new List<FxRate>();
        var provenance = new List<DataProvenance>();
        var issues = new List<DataIssue>();
        var coverages = new List<DataCoverage>();
        var pairsWithData = 0;

        foreach (var pair in request.Pairs)
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/{request.Range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}..{request.Range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
                      $"?base={pair.BaseCurrency}&symbols={pair.QuoteCurrency}";

            List<FxRate> pairRates = [];
            try
            {
                var uri = new Uri(url);
                using var stream = await _transport.GetAsync(uri, null, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                var rawKey = $"frankfurter/fx/{pair.BaseCurrency}-{pair.QuoteCurrency}/{request.Range.From.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{request.Range.To.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
                await _rawStore.SaveAsync(rawKey, json, cancellationToken).ConfigureAwait(false);

                pairRates = ParseRates(json, pair)
                    .Where(rate => rate.Date >= request.Range.From && rate.Date <= request.Range.To)
                    .ToList();
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch FX rates for {Pair} from Frankfurter", pair);
                issues.Add(new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Error,
                    $"Failed to fetch rates for {pair}."));
            }

            var (pairCoverage, pairGapIssues) = AdapterCoverageHelper.Compute(
                _calendar, request.Range, request.Frequency, pairRates.Select(r => r.Date).ToHashSet());
            coverages.Add(pairCoverage);
            issues.AddRange(pairGapIssues);
        }

        var coverage = CoverageComputation.Aggregate(coverages);

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

        // Legacy snapshots can omit base; an explicitly supplied base may never contradict
        // the requested pair. The provider's v1 response defines base as the rate denominator.
        if (root.TryGetProperty("base", out var responseBase)
            && (responseBase.ValueKind != JsonValueKind.String
                || !string.Equals(responseBase.GetString(), pair.BaseCurrency.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new FormatException("Frankfurter returned a different base currency.");
        }

        if (root.TryGetProperty("amount", out var amount)
            && (amount.ValueKind != JsonValueKind.Number || !amount.TryGetDecimal(out var amountValue) || amountValue != 1m))
        {
            throw new FormatException("Frankfurter returned a non-unit rate amount.");
        }

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
                rates.Add(new FxRate(date, rate));
            }
        }

        return rates;
    }
}
