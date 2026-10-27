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
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.Shared;
using Boutquin.MarketData.Calendars.Holidays;
using Boutquin.MarketData.Transport.Http;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boutquin.MarketData.Adapter.TwelveData;

/// <summary>
/// Fetches daily OHLCAV price bars from the Twelve Data REST API and persists the raw JSON
/// response before parsing into canonical <see cref="Bar"/> records.
/// </summary>
/// <remarks>
/// <para>
/// This adapter supports only <see cref="DataFrequency.Daily"/> requests. Intraday and
/// other frequencies are not handled and will cause <see cref="CanHandle"/> to return
/// <see langword="false"/>.
/// </para>
/// <para>
/// Twelve Data's basic <c>time_series</c> endpoint does not provide an adjusted close price.
/// The unadjusted <c>close</c> value is used for both the <see cref="Bar.Close"/> and
/// <see cref="Bar.AdjustedClose"/> fields.
/// </para>
/// <para>
/// Exactly one symbol is accepted because <see cref="Bar"/> does not carry symbol ownership.
/// Partition batches before dispatch. Fetch failures are logged and recorded as
/// <see cref="DataIssue"/> entries; caller cancellation propagates.
/// </para>
/// </remarks>
public sealed class TwelveDataPriceBarAdapter : IDataSourceAdapter<PriceHistoryRequest, Bar>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly TwelveDataOptions _options;
    private readonly ILogger<TwelveDataPriceBarAdapter> _logger;
    private static readonly IBusinessCalendar s_canadianCalendar = HolidayCalendarFactory.Create("CATO");

    /// <summary>
    /// Initializes a new instance of the <see cref="TwelveDataPriceBarAdapter"/> class.
    /// </summary>
    /// <param name="transport">HTTP transport for making API requests.</param>
    /// <param name="rawStore">Store for persisting raw API responses before parsing.</param>
    /// <param name="clock">Clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">Business calendar for computing calendar-aware coverage.</param>
    /// <param name="options">Twelve Data-specific configuration (base URL and API key).</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public TwelveDataPriceBarAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("twelvedata")] IBusinessCalendar calendar,
        IOptions<TwelveDataOptions> options,
        ILogger<TwelveDataPriceBarAdapter> logger)
    {
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The stable <c>twelvedata</c> provider identity attached to this adapter's provenance.</summary>
    public ProviderCode ProviderKey => new("twelvedata");

    /// <summary>The fallback rank, 50; adapters with higher ranks are attempted first.</summary>
    public int Priority => 50;

    /// <summary>
    /// Listing venues TwelveData covers for this adapter's role in the orchestrator:
    /// CA primary (XTSE, NEOE) and US fallback (XNYS, XNAS) — the four venues the
    /// etfwealthiq universe declares. A request whose <see cref="PriceHistoryRequest.Exchange"/>
    /// names a venue outside this set falls through to the next prioritized adapter.
    /// TwelveData covers additional global venues commercially; extending this set is
    /// an opt-in change rather than a silent broadening.
    /// </summary>
    private static readonly IReadOnlySet<ExchangeCode> s_supportedExchanges =
        new HashSet<ExchangeCode>
        {
            ExchangeCode.XTSE,
            ExchangeCode.NEOE,
            ExchangeCode.XNYS,
            ExchangeCode.XNAS,
        };

    /// <summary>Checks the request against this provider's supported routing constraints without performing I/O.</summary>
    /// <param name="request">Daily requests for exactly one symbol on a supported venue, or with no venue hint.</param>
    /// <returns><see langword="true"/> when the request matches the adapter's declared support; this does not guarantee provider availability.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public bool CanHandle(PriceHistoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Frequency == DataFrequency.Daily
            && request.Symbols.Count == 1
            && (request.Exchange is null || s_supportedExchanges.Contains(request.Exchange.Value));
    }

    /// <summary>Retrieves unadjusted daily price bars for one symbol from Twelve Data.</summary>
    /// <param name="request">Daily requests for exactly one symbol on a supported venue, or with no venue hint.</param>
    /// <param name="cancellationToken">Cancels transport, raw-payload persistence and parsing where those operations support cancellation.</param>
    /// <returns>A task yielding canonical records together with provider provenance, coverage and data-quality issues. Empty or partial responses retain their failure or coverage information.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="OperationCanceledException">The caller or a dependency cancels the operation.</exception>
    /// <remarks>Successful fetches include raw-payload persistence and normalization. Inspect the envelope's issues and coverage before using its records; a returned envelope alone does not certify complete data.</remarks>
    public async Task<DataEnvelope<IReadOnlyList<Bar>>> FetchAsync(
        PriceHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanHandle(request))
        {
            throw new ArgumentException("Twelve Data price fetches require one symbol, daily frequency, and a supported venue. Partition batches before dispatch.", nameof(request));
        }

        _options.ValidateEndpoint();
        var allBars = new List<Bar>();
        var issues = new List<DataIssue>();
        var provenanceList = new List<DataProvenance>();
        var coverages = new List<DataCoverage>();
        var symbolsWithData = 0;
        var calendar = request.Exchange is ExchangeCode.XTSE or ExchangeCode.NEOE
            ? s_canadianCalendar
            : _calendar;

        foreach (var symbol in request.Symbols)
        {
            List<Bar> barsForSymbol = [];
            try
            {
                var (bars, provenanceForSymbol) = await FetchPaginatedAsync(
                    symbol, request, cancellationToken).ConfigureAwait(false);
                barsForSymbol = bars;
                allBars.AddRange(barsForSymbol);
                provenanceList.AddRange(provenanceForSymbol);

                if (barsForSymbol.Count > 0)
                {
                    symbolsWithData++;
                }
                else
                {
                    issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Warning,
                        $"Twelve Data returned 0 bars for symbol '{symbol}'."));
                }

                _logger.LogInformation("Fetched {Count} bars for {Symbol} from Twelve Data", barsForSymbol.Count, symbol);
            }
            catch (ApiErrorResponseException apiEx)
            {
                _logger.LogWarning("Twelve Data returned an error response for {Symbol}: {Message}", symbol, apiEx.Message);
                issues.Add(new DataIssue(new IssueCode("API_ERROR"), IssueSeverity.Warning,
                    $"Twelve Data returned an error for symbol '{symbol}'."));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to fetch bars for {Symbol} from Twelve Data ({ExceptionType})", symbol, ex.GetType().Name);
                issues.Add(new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Warning,
                    $"Failed to fetch data for symbol '{symbol}'."));
            }

            var (symbolCoverage, symbolGapIssues) = AdapterCoverageHelper.Compute(
                calendar, request.Range, request.Frequency, barsForSymbol.Select(b => b.Date).ToHashSet());
            coverages.Add(symbolCoverage);
            issues.AddRange(symbolGapIssues);
        }

        // Twelve Data returns values in DESCENDING date order (newest first). When multiple
        // pagination pages are concatenated the combined list stays descending across the
        // whole range. Sort ascending so downstream consumers that expect chronological
        // order (including the `bars[^1]` = last-bar convention) work correctly — otherwise
        // `GetPriceHistoryAsync` callers treating `bars[^1].Date` as "latest" silently
        // picked up the OLDEST date instead, capping backtest end dates at inception.
        allBars.Sort((a, b) => a.Date.CompareTo(b.Date));

        var coverage = CoverageComputation.Aggregate(coverages);

        return new DataEnvelope<IReadOnlyList<Bar>>(
            allBars,
            coverage,
            issues,
            provenanceList);
    }

    /// <summary>
    /// Checks whether the Twelve Data response indicates an error via the <c>status</c> field.
    /// </summary>
    private static bool IsErrorResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("status", out var statusProp) &&
               statusProp.ValueKind == JsonValueKind.String &&
               string.Equals(statusProp.GetString(), "error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the <c>values</c> array from a Twelve Data time_series response into <see cref="Bar"/> records.
    /// </summary>
    private static List<Bar> ParseBars(string json)
    {
        var bars = new List<Bar>();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("values", out var valuesElement) ||
            valuesElement.ValueKind != JsonValueKind.Array)
        {
            return bars;
        }

        foreach (var element in valuesElement.EnumerateArray())
        {
            var dateStr = element.GetProperty("datetime").GetString()!;
            var date = DateOnly.ParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture);

            var open = decimal.Parse(element.GetProperty("open").GetString()!, CultureInfo.InvariantCulture);
            var high = decimal.Parse(element.GetProperty("high").GetString()!, CultureInfo.InvariantCulture);
            var low = decimal.Parse(element.GetProperty("low").GetString()!, CultureInfo.InvariantCulture);
            var close = decimal.Parse(element.GetProperty("close").GetString()!, CultureInfo.InvariantCulture);
            var volume = long.Parse(element.GetProperty("volume").GetString()!, CultureInfo.InvariantCulture);

            if (volume < 0 || open < 0 || high < 0 || low < 0 || close < 0
                || low > high || open < low || open > high || close < low || close > high)
            {
                throw new FormatException("Twelve Data returned an invalid equity price bar.");
            }

            bars.Add(new Bar(date, open, high, low, close, close, volume));
        }

        return bars;
    }

    /// <summary>
    /// TwelveData's <c>/time_series</c> endpoint hard-caps responses at 5000 bars per call
    /// (per https://twelvedata.com/docs/market-data/time-series — the <c>outputsize</c>
    /// parameter is validated in [1, 5000] regardless of subscription tier). Ranges that
    /// need more than 5000 bars (e.g. a 25-year CA backtest for a TSX ETF) must paginate
    /// or the consumer receives only the most-recent 5000 bars and a partial-coverage
    /// rejection downstream. This helper pages backward from <c>request.Range.To</c>,
    /// shrinking <c>end_date</c> to one day before the previous page's oldest bar, until
    /// either (a) a page comes back with fewer than 5000 bars (data exhausted), (b) the
    /// oldest bar reaches <c>request.Range.From</c>, or (c) the safety cap is hit.
    /// </summary>
    /// <remarks>
    /// Safety cap: 20 pages × 5000 bars = 100 000 bars ≈ 395 trading years. No legitimate
    /// backtest approaches this. The cap prevents an infinite loop if TwelveData ever
    /// returns overlapping pages or a malformed response that doesn't advance the cursor.
    /// </remarks>
    private const int MaxPages = 20;
    private const int TwelveDataOutputSizeCap = 5000;

    private async Task<(List<Bar> Bars, List<DataProvenance> Provenance)> FetchPaginatedAsync(
        Symbol symbol,
        PriceHistoryRequest request,
        CancellationToken cancellationToken)
    {
        var perSymbolBars = new List<Bar>();
        var perSymbolProvenance = new List<DataProvenance>();
        var barsByDate = new Dictionary<DateOnly, Bar>();
        var pageEnd = request.Range.To;

        // mic_code disambiguates symbols that trade on multiple venues (e.g. XIC on both
        // TSX/XTSE and NEO/NEOE). Post the 2026-04-20 ExchangeCode ISO-realignment rename,
        // the enum member name IS the ISO 10383 MIC, so we pass `request.Exchange.Value.ToString()`
        // directly without a translation layer.
        var micCodeParam = request.Exchange is null
            ? string.Empty
            : $"&mic_code={request.Exchange.Value}";

        for (var page = 0; page < MaxPages; page++)
        {
            // end_date carries the end of the day. A bare date is read as that day's midnight, and
            // TwelveData's daily bars since about 2016 are stamped later in the day, so a bare
            // end_date silently dropped the range's last bar and, when paging, each page's last bar.
            var sourceUrl = $"{_options.BaseUrl.TrimEnd('/')}/time_series" +
                      $"?symbol={Uri.EscapeDataString(symbol.Ticker)}" +
                      $"&interval=1day" +
                      $"&adjust=none" +
                      $"&start_date={request.Range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
                      $"&end_date={pageEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}%2023:59:59" +
                      $"&outputsize={TwelveDataOutputSizeCap}" +
                      $"{micCodeParam}";
            var url = $"{sourceUrl}&apikey={Uri.EscapeDataString(_options.ApiKey)}&format=JSON";

            var uri = new Uri(url);
            using var stream = await _transport.GetAsync(uri, null, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            var rawKey = $"twelvedata/daily/{symbol}/{request.Exchange?.ToString() ?? "any"}/{request.Range.From.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{pageEnd.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
            await _rawStore.SaveAsync(rawKey, json, cancellationToken).ConfigureAwait(false);

            if (IsErrorResponse(json))
            {
                throw new ApiErrorResponseException(
                    $"Twelve Data /time_series returned status=error for {symbol} (page {page + 1}).");
            }

            ValidateResponseIdentity(json, symbol, request.Exchange);
            var pageBars = ParseBars(json);

            perSymbolProvenance.Add(new DataProvenance(
                Provider: new ProviderCode("twelvedata"),
                Dataset: symbol.Ticker,
                LicenseFlag: LicenseType.Free,
                RetrievalMode: RetrievalMode.Api,
                Freshness: FreshnessClass.EndOfDay,
                RetrievedAtUtc: _clock.UtcNow,
                SourceUrl: $"{sourceUrl}&format=JSON",
                DataDate: pageBars.Count > 0 ? pageBars.Max(b => b.Date) : null));

            if (pageBars.Count == 0)
            {
                // Server has no more data for this range — stop.
                break;
            }

            // Equal repeats are harmless. Conflicting unversioned rows cannot be resolved
            // by provider enumeration order without silently choosing an arbitrary value.
            foreach (var bar in pageBars)
            {
                if (bar.Date < request.Range.From || bar.Date > request.Range.To)
                {
                    continue;
                }

                if (barsByDate.TryGetValue(bar.Date, out var existing))
                {
                    if (existing != bar)
                    {
                        throw new FormatException("Twelve Data returned conflicting bars for one date.");
                    }

                    continue;
                }

                barsByDate.Add(bar.Date, bar);
                perSymbolBars.Add(bar);
            }

            // TwelveData returns values in descending date order. The OLDEST bar in the
            // page is the last element.
            var oldestBar = pageBars[^1];

            // Stop conditions:
            //   (1) we've reached the caller's requested start date — no more work.
            //   (2) the page came back short (<5000 bars) — server has nothing older.
            if (oldestBar.Date <= request.Range.From || pageBars.Count < TwelveDataOutputSizeCap)
            {
                break;
            }

            // Advance cursor: next page ends the day before this page's oldest bar.
            pageEnd = oldestBar.Date.AddDays(-1);
            if (pageEnd < request.Range.From)
            {
                break;
            }
        }

        return (perSymbolBars, perSymbolProvenance);
    }

    // Older snapshots omit meta, so absence alone does not disqualify a response. Supplied
    // identity fields must agree with the request; sending mic_code is not proof of identity.
    private static void ValidateResponseIdentity(string json, Symbol symbol, ExchangeCode? exchange)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("meta", out var meta))
        {
            return;
        }

        if (meta.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Twelve Data time-series metadata must be an object.");
        }

        if (meta.TryGetProperty("symbol", out var responseSymbol)
            && (responseSymbol.ValueKind != JsonValueKind.String
                || !string.Equals(responseSymbol.GetString(), symbol.Ticker, StringComparison.OrdinalIgnoreCase)))
        {
            throw new FormatException("Twelve Data returned a different time-series symbol.");
        }

        if (meta.TryGetProperty("interval", out var interval)
            && (interval.ValueKind != JsonValueKind.String || interval.GetString() != "1day"))
        {
            throw new FormatException("Twelve Data returned a different time-series interval.");
        }

        if (meta.TryGetProperty("mic_code", out var mic)
            && (mic.ValueKind != JsonValueKind.String
                || !Enum.TryParse<ExchangeCode>(mic.GetString(), true, out var returnedExchange)
                || !Enum.IsDefined(returnedExchange)
                || !string.Equals(mic.GetString(), returnedExchange.ToString(), StringComparison.OrdinalIgnoreCase)
                || (exchange is not null && returnedExchange != exchange.Value)))
        {
            throw new FormatException("Twelve Data returned an invalid or conflicting time-series MIC.");
        }

        // Older provider snapshots have only the display exchange name. Recognize the
        // four venues this adapter declares instead of treating a contradictory label as proof.
        if (exchange is not null && !meta.TryGetProperty("mic_code", out _)
            && meta.TryGetProperty("exchange", out var exchangeName))
        {
            var legacyExchange = exchangeName.ValueKind == JsonValueKind.String
                ? exchangeName.GetString()?.ToUpperInvariant() switch
                {
                    "NASDAQ" or "XNAS" => ExchangeCode.XNAS,
                    "NYSE" or "XNYS" => ExchangeCode.XNYS,
                    "TSX" or "XTSE" => ExchangeCode.XTSE,
                    "NEO" or "NEO EXCHANGE" or "NEOE" => ExchangeCode.NEOE,
                    _ => (ExchangeCode?)null,
                }
                : null;
            if (legacyExchange != exchange)
            {
                throw new FormatException("Twelve Data returned an unrecognized or conflicting exchange label.");
            }
        }
    }

    /// <summary>
    /// Signals that Twelve Data returned a <c>status=error</c> JSON body. Caught at the
    /// single-symbol boundary and returned as an issue-bearing failed fetch.
    /// </summary>
    private sealed class ApiErrorResponseException : Exception
    {
        public ApiErrorResponseException(string message) : base(message) { }
    }
}
