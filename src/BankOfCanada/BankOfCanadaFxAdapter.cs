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

using Boutquin.Domain.Enumerations;

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
/// Fetches official Bank of Canada FX rates from the Valet API and returns canonical
/// <see cref="FxRate"/> records with provenance metadata.
/// </summary>
/// <remarks>
/// <para>
/// The adapter calls the <c>/observations/{series}/json?start_date={from}&amp;end_date={to}</c>
/// endpoint. Each observation element has a date property <c>"d"</c> and a series property
/// (e.g., <c>"FXUSDCAD"</c>) whose value is an object with a <c>"v"</c> sub-property
/// containing the rate as a string. Rates are already in decimal form (e.g., 1.36 means
/// 1 USD = 1.36 CAD) and are returned as-is.
/// </para>
/// <para>
/// Only USD/CAD and CAD/USD pairs are supported. The Bank of Canada publishes these as
/// official noon (benchmark) rates, which are the authoritative source for Canadian tax reporting.
/// </para>
/// </remarks>
public sealed class BankOfCanadaFxAdapter : IDataSourceAdapter<FxHistoryRequest, FxRate>, IPrioritizedAdapter
{
    private static readonly IReadOnlyDictionary<(Currency Base, Currency Quote), string> s_seriesMap =
        new Dictionary<(Currency, Currency), string>
        {
            [(Currency.USD, Currency.CAD)] = "FXUSDCAD",
            [(Currency.CAD, Currency.USD)] = "FXCADUSD",
        };

    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly BankOfCanadaOptions _options;
    private readonly ILogger<BankOfCanadaFxAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BankOfCanadaFxAdapter"/> class.
    /// </summary>
    /// <param name="transport">HTTP transport for fetching data from the Valet API.</param>
    /// <param name="rawStore">Store for persisting raw API responses before parsing.</param>
    /// <param name="clock">Clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">Business calendar for computing calendar-aware coverage.</param>
    /// <param name="options">Configuration options containing the API base URL.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public BankOfCanadaFxAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("bankofcanada")] IBusinessCalendar calendar,
        IOptions<BankOfCanadaOptions> options,
        ILogger<BankOfCanadaFxAdapter> logger)
    {
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The stable <c>boc-fx</c> provider identity attached to this adapter's provenance.</summary>
    public ProviderCode ProviderKey => new("boc-fx");

    /// <summary>The fallback rank, 100; adapters with higher ranks are attempted first.</summary>
    public int Priority => 100;

    /// <summary>Checks the request against this provider's supported routing constraints without performing I/O.</summary>
    /// <param name="request">Daily requests for exactly one supported CAD/USD currency pair.</param>
    /// <returns><see langword="true"/> when the request matches the adapter's declared support; this does not guarantee provider availability.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public bool CanHandle(FxHistoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Frequency == DataFrequency.Daily && request.Pairs.Count == 1
            && request.Pairs.All(p => s_seriesMap.ContainsKey((p.BaseCurrency, p.QuoteCurrency)));
    }

    /// <summary>Retrieves daily CAD/USD or USD/CAD reference FX observations from Bank of Canada.</summary>
    /// <param name="request">Daily requests for exactly one supported CAD/USD currency pair.</param>
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
            throw new ArgumentException("Bank of Canada FX fetches require one USD/CAD or CAD/USD pair and daily frequency because FxRate does not carry pair identity.", nameof(request));
        }

        var allRates = new List<FxRate>();
        var provenance = new List<DataProvenance>();
        var issues = new List<DataIssue>();

        foreach (var pair in request.Pairs)
        {
            var seriesId = s_seriesMap[(pair.BaseCurrency, pair.QuoteCurrency)];
            var fromDate = request.Range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var toDate = request.Range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var url = $"{_options.BaseUrl.TrimEnd('/')}/observations/{seriesId}/json?start_date={fromDate}&end_date={toDate}";

            try
            {
                var uri = new Uri(url);
                using var stream = await _transport.GetAsync(uri, null, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                var rawKey = $"boc/fx/{seriesId}/{request.Range.From.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{request.Range.To.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
                await _rawStore.SaveAsync(rawKey, json, cancellationToken).ConfigureAwait(false);

                var pairRates = ParseRates(json, seriesId)
                    .Where(rate => rate.Date >= request.Range.From && rate.Date <= request.Range.To)
                    .ToList();
                allRates.AddRange(pairRates);

                if (pairRates.Count > 0)
                {
                    _logger.LogInformation(
                        "Fetched {Count} FX rates for {Pair} from Bank of Canada",
                        pairRates.Count, pair);
                }
                else if (CoverageComputation.CountExpectedPoints(_calendar, request.Range, request.Frequency) > 0)
                {
                    issues.Add(new DataIssue(new IssueCode("NO_RATES"), IssueSeverity.Warning,
                        $"Bank of Canada returned no rates for {pair}."));
                }

                provenance.Add(new DataProvenance(
                    Provider: new ProviderCode("boc-fx"),
                    Dataset: seriesId,
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
                _logger.LogWarning(ex, "Failed to fetch FX rates for {Pair} from Bank of Canada", pair);
                issues.Add(new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Error,
                    $"Failed to fetch rates for {pair} from Bank of Canada."));
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

    private static List<FxRate> ParseRates(string json, string seriesId)
    {
        var rates = new List<FxRate>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("observations", out var observations))
        {
            return rates;
        }

        foreach (var obs in observations.EnumerateArray())
        {
            if (!obs.TryGetProperty("d", out var dateElement))
            {
                continue;
            }

            var dateStr = dateElement.GetString();
            if (dateStr is null ||
                !DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            if (!obs.TryGetProperty(seriesId, out var seriesObj) ||
                !seriesObj.TryGetProperty("v", out var vElement) ||
                vElement.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            var valueStr = vElement.GetString();
            if (string.IsNullOrEmpty(valueStr) ||
                !decimal.TryParse(valueStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate))
            {
                continue;
            }

            rates.Add(new FxRate(date, rate));
        }

        return rates;
    }
}
