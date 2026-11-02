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
/// <b>Price basis.</b> Prices are requested split-adjusted (<c>adjust=splits</c>, sent explicitly and part of the
/// raw-payload key <c>twelvedata/daily-splits/...</c>). <see cref="Bar.Open"/>, <see cref="Bar.High"/>,
/// <see cref="Bar.Low"/> and <see cref="Bar.Close"/> are those split-adjusted prices: a price-return series with
/// no dividend adjustment. Unlike the kernel's general wording for <see cref="Bar.Close"/>, this close is
/// restated for later splits, so it is not the price printed on the day.
/// </para>
/// <para>
/// <b>Total return.</b> <see cref="Bar.AdjustedClose"/> is computed by this adapter from the split-adjusted closes
/// and the cash dividends listed by Twelve Data's <c>/dividends</c> endpoint (raw key
/// <c>twelvedata/dividends/...</c>). For a dividend <c>D</c> whose ex-date <c>E</c> falls after the first bar and on
/// or before the last bar, the factor is <c>C_E / (C_E + D)</c>, where <c>C_E</c> is the close of the first bar
/// dated on or after <c>E</c>; every bar dated before <c>E</c> is multiplied by it. The one-day adjusted return
/// into that bar is therefore exactly <c>(C_E + D) / C_prev - 1</c>, the holding-period return with the dividend
/// received in cash. When several ex-dates are credited to the same bar (non-trading days, or a provider gap), their
/// amounts are summed first, so <c>D</c> is the sum and the return is exactly <c>(C_E + sum D) / C_prev - 1</c>. The last bar of the fetched window is the anchor: its <see cref="Bar.AdjustedClose"/> equals
/// its <see cref="Bar.Close"/>. Adjusted levels are therefore relative to the request's end, not to today;
/// adjusted returns are exact. Chain returns, not levels, when combining windows fetched separately.
/// </para>
/// <para>
/// Twelve Data's own <c>adjust=all</c> series is deliberately not used. Live comparison against its cash
/// dividend list showed it applying a wrong-signed extra adjustment on some distribution dates (for example
/// Canadian ETF year-end distributions that were reinvested and consolidated, which are price-neutral), so
/// its one-day returns on those dates were wrong by up to four percent. Only cash dividends listed by
/// <c>/dividends</c> adjust this adapter's series; distributions not listed there do not.
/// </para>
/// <para>
/// <b>Listed events only.</b> The total return is exact for the events Twelve Data lists and no better. Three
/// provider-list failure modes are not detected: (1) splits the provider does not list; (2) cash distributions the
/// provider omits, because an empty or short <c>/dividends</c> response is read as "no dividends" (for example
/// VCN on XTSE for 2020 returned three rows, none between 2020-06-22 and 2020-12-30); and (3) a listed row that was
/// in fact a reinvested or notional distribution, which is credited as cash. Verify against an independent source
/// where the exact total return matters.
/// </para>
/// <para>
/// <b>Fail-closed rules.</b> A failed or error-bearing dividend request yields a <c>FETCH_FAILED</c> issue and
/// no bars, never price-return data presented as total return; an empty dividend list from a successful
/// request means the window had no cash dividends. Dividend amounts are requested unadjusted
/// (<c>adjust=false</c>) because the provider's split restatement of dividends was found inconsistent across
/// listings. When any applicable dividend precedes a split recorded by the <c>/splits</c> endpoint (raw key
/// <c>twelvedata/splits/...</c>), the dividend's split basis cannot be verified and the symbol returns a
/// <c>DIVIDEND_SPLIT_BASIS_UNVERIFIED</c> issue and no bars. Negative amounts, and conflicting amounts for one
/// ex-date, fail the symbol; identical repeated rows count once and zero amounts are ignored. A dividend
/// dated on or before the first bar, or after the last bar, cannot be represented inside the window and is
/// reported as a <c>DIVIDEND_OUTSIDE_BARS</c> warning. A split missing from the provider's split list cannot be
/// detected here.
/// </para>
/// <para>
/// <b>Provider cost.</b> Each symbol fetch adds one <c>/dividends</c> call, plus one <c>/splits</c> call when the
/// window contains an applicable dividend. Twelve Data prices both as fundamentals calls that need a plan
/// including fundamentals data, so they consume more credits than a price page.
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

    /// <summary>Retrieves split-adjusted daily price bars for one symbol from Twelve Data, with a total-return <see cref="Bar.AdjustedClose"/> computed from its cash dividends.</summary>
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
                bars.Sort((a, b) => a.Date.CompareTo(b.Date));

                var symbolIssues = new List<DataIssue>();
                if (bars.Count > 0)
                {
                    bars = await ApplyTotalReturnAsync(
                        symbol, request, bars, provenanceForSymbol, symbolIssues, cancellationToken).ConfigureAwait(false);
                }

                // Nothing is published for the symbol until its total return is complete.
                barsForSymbol = bars;
                allBars.AddRange(barsForSymbol);
                provenanceList.AddRange(provenanceForSymbol);
                issues.AddRange(symbolIssues);

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
            catch (DividendBasisException basisEx)
            {
                _logger.LogWarning("Twelve Data dividend split basis is unverifiable for {Symbol}: {Message}", symbol, basisEx.Message);
                issues.Add(new DataIssue(new IssueCode("DIVIDEND_SPLIT_BASIS_UNVERIFIED"), IssueSeverity.Warning,
                    $"Twelve Data lists a split after a dividend ex-date for symbol '{symbol}', so the dividend's split basis cannot be verified; no total-return bars were produced."));
            }
            catch (DividendDataException dividendEx)
            {
                _logger.LogWarning("Twelve Data dividend data failed for {Symbol} ({ExceptionType})", symbol, dividendEx.InnerException?.GetType().Name ?? nameof(DividendDataException));
                issues.Add(new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Warning,
                    $"Twelve Data dividend or split data for symbol '{symbol}' could not be used; no total-return bars were produced."));
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
                      $"&adjust=splits" +
                      $"&start_date={request.Range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
                      $"&end_date={pageEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}%2023:59:59" +
                      $"&outputsize={TwelveDataOutputSizeCap}" +
                      $"{micCodeParam}";
            var url = $"{sourceUrl}&apikey={Uri.EscapeDataString(_options.ApiKey)}&format=JSON";

            var uri = new Uri(url);
            using var stream = await _transport.GetAsync(uri, null, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            var rawKey = $"twelvedata/daily-splits/{symbol}/{request.Exchange?.ToString() ?? "any"}/{request.Range.From.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{pageEnd.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
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

    /// <summary>
    /// Fetches the window's cash dividends (and, when one applies, the symbol's splits) and returns the bars
    /// with a computed total-return <see cref="Bar.AdjustedClose"/>. <paramref name="bars"/> must be ascending
    /// and non-empty. Any dividend or split failure becomes a <see cref="DividendDataException"/>; an
    /// unverifiable split basis becomes a <see cref="DividendBasisException"/>.
    /// </summary>
    private async Task<List<Bar>> ApplyTotalReturnAsync(
        Symbol symbol,
        PriceHistoryRequest request,
        List<Bar> bars,
        List<DataProvenance> provenance,
        List<DataIssue> issues,
        CancellationToken cancellationToken)
    {
        var firstDate = bars[0].Date;
        var lastDate = bars[^1].Date;
        List<CashDividend> applicable;
        try
        {
            var dividends = await FetchDividendsAsync(symbol, request, provenance, cancellationToken).ConfigureAwait(false);
            applicable = [];
            foreach (var dividend in dividends)
            {
                if (dividend.ExDate > firstDate && dividend.ExDate <= lastDate)
                {
                    applicable.Add(dividend);
                }
                else
                {
                    issues.Add(new DataIssue(new IssueCode("DIVIDEND_OUTSIDE_BARS"), IssueSeverity.Warning,
                        $"Twelve Data lists a cash dividend for symbol '{symbol}' with ex-date {dividend.ExDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, which no return between two returned bars spans; it is not reflected in this window's adjusted closes."));
                }
            }

            if (applicable.Count > 0)
            {
                var earliestExDate = applicable.Min(d => d.ExDate);
                var splitDates = await FetchSplitDatesAsync(symbol, request, earliestExDate, provenance, cancellationToken).ConfigureAwait(false);
                if (splitDates.Any(splitDate => splitDate >= earliestExDate))
                {
                    throw new DividendBasisException(
                        $"A split on or after {earliestExDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} makes the dividend split basis ambiguous.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DividendBasisException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DividendDataException(ex);
        }

        return ComputeTotalReturn(bars, applicable);
    }

    /// <summary>
    /// Applies the cumulative dividend factors. Each applicable dividend multiplies every bar dated before its
    /// ex-date by <c>C_E / (C_E + D)</c>, where <c>C_E</c> is the close of the first bar on or after the ex-date,
    /// so the one-day adjusted return into that bar is exactly <c>(C_E + D) / C_prev - 1</c> and the last bar
    /// keeps <c>AdjustedClose == Close</c>.
    /// </summary>
    private static List<Bar> ComputeTotalReturn(List<Bar> bars, List<CashDividend> applicable)
    {
        if (applicable.Count == 0)
        {
            return bars;
        }

        // Sum the dividends credited to each bar (several ex-dates can map to one bar: non-trading days or a
        // provider gap), so the factor is C_E / (C_E + sum D) and the bar's return is exactly (C_E + sum D) / C_prev.
        var sumsByBar = new SortedDictionary<int, decimal>();
        foreach (var dividend in applicable)
        {
            var index = bars.FindIndex(b => b.Date >= dividend.ExDate);
            sumsByBar[index] = sumsByBar.GetValueOrDefault(index) + dividend.Amount;
        }

        var factors = new List<(int BarIndex, decimal Factor)>(sumsByBar.Count);
        foreach (var (index, amount) in sumsByBar)
        {
            var exClose = bars[index].Close;
            if (exClose <= 0m)
            {
                throw new FormatException("Twelve Data returned a non-positive close on a dividend ex-date.");
            }

            factors.Add((index, exClose / (exClose + amount)));
        }

        var result = new List<Bar>(bars.Count);
        var multiplier = 1m;
        for (var i = bars.Count - 1; i >= 0; i--)
        {
            // Bars before index k receive the factor of the dividend whose first ex-dated bar is k.
            foreach (var (barIndex, factor) in factors)
            {
                if (barIndex == i + 1)
                {
                    multiplier *= factor;
                }
            }

            result.Add(bars[i] with { AdjustedClose = bars[i].Close * multiplier });
        }

        result.Reverse();
        return result;
    }

    private async Task<List<CashDividend>> FetchDividendsAsync(
        Symbol symbol,
        PriceHistoryRequest request,
        List<DataProvenance> provenance,
        CancellationToken cancellationToken)
    {
        // adjust=false: amounts as the provider stores them. Its split restatement of dividends was found
        // inconsistent across listings, so splits are checked separately instead of trusted.
        var sourceUrl = $"{_options.BaseUrl.TrimEnd('/')}/dividends" +
                        $"?symbol={Uri.EscapeDataString(symbol.Ticker)}" +
                        MicCodeParam(request.Exchange) +
                        $"&start_date={request.Range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
                        $"&end_date={request.Range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
                        "&adjust=false";
        var rawKey = $"twelvedata/dividends/{symbol}/{request.Exchange?.ToString() ?? "any"}/{request.Range.From.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{request.Range.To.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
        using var document = await FetchFundamentalsAsync(sourceUrl, rawKey, symbol, request.Exchange, cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("dividends", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Twelve Data /dividends response has no dividends array.");
        }

        var byDate = new Dictionary<DateOnly, decimal>();
        foreach (var element in list.EnumerateArray())
        {
            var exDate = DateOnly.ParseExact(element.GetProperty("ex_date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var amountElement = element.GetProperty("amount");
            var amount = amountElement.ValueKind == JsonValueKind.String
                ? decimal.Parse(amountElement.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)
                : amountElement.GetDecimal();
            if (amount < 0m)
            {
                throw new FormatException("Twelve Data returned a negative dividend amount.");
            }

            if (exDate < request.Range.From || exDate > request.Range.To || amount == 0m)
            {
                continue;
            }

            if (byDate.TryGetValue(exDate, out var existing))
            {
                if (existing != amount)
                {
                    throw new FormatException("Twelve Data returned conflicting dividend amounts for one ex-date.");
                }

                continue;
            }

            byDate.Add(exDate, amount);
        }

        var dividends = byDate.Select(kv => new CashDividend(kv.Key, kv.Value)).OrderBy(d => d.ExDate).ToList();
        provenance.Add(CreateProvenance(symbol, sourceUrl, dividends.Count > 0 ? dividends[^1].ExDate : null));
        return dividends;
    }

    private async Task<List<DateOnly>> FetchSplitDatesAsync(
        Symbol symbol,
        PriceHistoryRequest request,
        DateOnly from,
        List<DataProvenance> provenance,
        CancellationToken cancellationToken)
    {
        // Split-adjusted prices are restated to the present, so splits after the window matter too.
        var asOf = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        if (asOf < request.Range.To)
        {
            asOf = request.Range.To;
        }

        var sourceUrl = $"{_options.BaseUrl.TrimEnd('/')}/splits" +
                        $"?symbol={Uri.EscapeDataString(symbol.Ticker)}" +
                        MicCodeParam(request.Exchange) +
                        $"&start_date={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
                        $"&end_date={asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        var rawKey = $"twelvedata/splits/{symbol}/{request.Exchange?.ToString() ?? "any"}/{from.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{asOf.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
        using var document = await FetchFundamentalsAsync(sourceUrl, rawKey, symbol, request.Exchange, cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("splits", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Twelve Data /splits response has no splits array.");
        }

        var dates = list.EnumerateArray()
            .Select(e => DateOnly.ParseExact(e.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToList();
        provenance.Add(CreateProvenance(symbol, sourceUrl, dates.Count > 0 ? dates.Max() : null));
        return dates;
    }

    /// <summary>Fetches, persists and identity-checks one fundamentals response; throws on a <c>status=error</c> body.</summary>
    private async Task<JsonDocument> FetchFundamentalsAsync(
        string sourceUrl,
        string rawKey,
        Symbol symbol,
        ExchangeCode? exchange,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"{sourceUrl}&apikey={Uri.EscapeDataString(_options.ApiKey)}&format=JSON");
        using var stream = await _transport.GetAsync(uri, null, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await _rawStore.SaveAsync(rawKey, json, cancellationToken).ConfigureAwait(false);

        if (IsErrorResponse(json))
        {
            throw new ApiErrorResponseException("Twelve Data fundamentals request returned status=error.");
        }

        ValidateResponseIdentity(json, symbol, exchange);
        return JsonDocument.Parse(json);
    }

    private DataProvenance CreateProvenance(Symbol symbol, string sourceUrl, DateOnly? dataDate) => new(
        Provider: new ProviderCode("twelvedata"),
        Dataset: symbol.Ticker,
        LicenseFlag: LicenseType.Free,
        RetrievalMode: RetrievalMode.Api,
        Freshness: FreshnessClass.EndOfDay,
        RetrievedAtUtc: _clock.UtcNow,
        SourceUrl: $"{sourceUrl}&format=JSON",
        DataDate: dataDate);

    private static string MicCodeParam(ExchangeCode? exchange) =>
        exchange is null ? string.Empty : $"&mic_code={exchange.Value}";

    /// <summary>One cash dividend: ex-date and amount per share in the listing's currency.</summary>
    private readonly record struct CashDividend(DateOnly ExDate, decimal Amount);

    /// <summary>Wraps any failure of the dividend or split requests so it cannot pass as price-return success.</summary>
    private sealed class DividendDataException : Exception
    {
        public DividendDataException(Exception inner) : base("Twelve Data dividend data could not be used.", inner) { }
    }

    /// <summary>Signals that a recorded split leaves an applicable dividend's split basis unverifiable.</summary>
    private sealed class DividendBasisException : Exception
    {
        public DividendBasisException(string message) : base(message) { }
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
