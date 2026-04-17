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
/// Each symbol is fetched individually. Per-symbol failures are caught, logged as warnings,
/// and recorded as <see cref="DataIssue"/> entries — the adapter continues with the remaining
/// symbols rather than failing the entire batch.
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

    /// <inheritdoc />
    public ProviderCode ProviderKey => new("twelvedata");

    /// <inheritdoc />
    public int Priority => 50;

    /// <inheritdoc />
    public bool CanHandle(PriceHistoryRequest request) =>
        request.Frequency == DataFrequency.Daily && request.Symbols.Count > 0;

    /// <inheritdoc />
    public async Task<DataEnvelope<IReadOnlyList<Bar>>> FetchAsync(
        PriceHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var allBars = new List<Bar>();
        var issues = new List<DataIssue>();
        var provenanceList = new List<DataProvenance>();
        var symbolsWithData = 0;

        foreach (var symbol in request.Symbols)
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/time_series" +
                      $"?symbol={symbol}" +
                      $"&interval=1day" +
                      $"&start_date={request.Range.From:yyyy-MM-dd}" +
                      $"&end_date={request.Range.To:yyyy-MM-dd}" +
                      $"&apikey={_options.ApiKey}" +
                      $"&format=JSON";

            try
            {
                var uri = new Uri(url);
                using var stream = await _transport.GetAsync(uri, null, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                var rawKey = $"twelvedata/daily/{symbol}/{request.Range.From:yyyyMMdd}-{request.Range.To:yyyyMMdd}";
                await _rawStore.SaveAsync(rawKey, json, cancellationToken).ConfigureAwait(false);

                if (IsErrorResponse(json))
                {
                    _logger.LogWarning("Twelve Data returned an error response for {Symbol}", symbol);
                    issues.Add(new DataIssue(new IssueCode("API_ERROR"), IssueSeverity.Warning,
                        $"Twelve Data returned an error for symbol '{symbol}'."));
                    continue;
                }

                var bars = ParseBars(json);
                allBars.AddRange(bars);

                if (bars.Count > 0)
                {
                    symbolsWithData++;
                }
                else
                {
                    issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Warning,
                        $"Twelve Data returned 0 bars for symbol '{symbol}'."));
                }

                _logger.LogInformation("Fetched {Count} bars for {Symbol} from Twelve Data", bars.Count, symbol);

                provenanceList.Add(new DataProvenance(
                    Provider: new ProviderCode("twelvedata"),
                    Dataset: symbol.Ticker,
                    LicenseFlag: LicenseType.Free,
                    RetrievalMode: RetrievalMode.Api,
                    Freshness: FreshnessClass.EndOfDay,
                    RetrievedAtUtc: _clock.UtcNow,
                    SourceUrl: url,
                    DataDate: bars.Count > 0 ? bars.Max(b => b.Date) : null));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch bars for {Symbol} from Twelve Data", symbol);
                issues.Add(new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Warning,
                    $"Failed to fetch data for symbol '{symbol}': {ex.Message}"));
            }
        }

        var returnedDates = allBars.Select(b => b.Date).ToHashSet();
        var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
            _calendar, request.Range, request.Frequency, returnedDates);
        issues.AddRange(gapIssues);

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
            var date = DateOnly.Parse(dateStr, CultureInfo.InvariantCulture);

            var open = decimal.Parse(element.GetProperty("open").GetString()!, CultureInfo.InvariantCulture);
            var high = decimal.Parse(element.GetProperty("high").GetString()!, CultureInfo.InvariantCulture);
            var low = decimal.Parse(element.GetProperty("low").GetString()!, CultureInfo.InvariantCulture);
            var close = decimal.Parse(element.GetProperty("close").GetString()!, CultureInfo.InvariantCulture);
            var volume = long.Parse(element.GetProperty("volume").GetString()!, CultureInfo.InvariantCulture);

            bars.Add(new Bar(date, open, high, low, close, close, volume));
        }

        return bars;
    }
}
