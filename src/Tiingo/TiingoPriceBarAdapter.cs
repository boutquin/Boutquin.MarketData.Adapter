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

namespace Boutquin.MarketData.Adapter.Tiingo;

/// <summary>
/// Fetches daily OHLCAV price bars from the Tiingo REST API and persists the raw JSON
/// response before parsing into canonical <see cref="Bar"/> records.
/// </summary>
/// <remarks>
/// <para>
/// This adapter supports only <see cref="DataFrequency.Daily"/> requests. Intraday and
/// other frequencies are not handled and will cause <see cref="CanHandle"/> to return
/// <see langword="false"/>.
/// </para>
/// <para>
/// Each symbol is fetched individually. Per-symbol failures are caught, logged as warnings,
/// and recorded as <see cref="DataIssue"/> entries — the adapter continues with the remaining
/// symbols rather than failing the entire batch.
/// </para>
/// </remarks>
public sealed class TiingoPriceBarAdapter : IDataSourceAdapter<PriceHistoryRequest, Bar>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly TiingoOptions _options;
    private readonly ILogger<TiingoPriceBarAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TiingoPriceBarAdapter"/> class.
    /// </summary>
    /// <param name="transport">HTTP transport for making API requests.</param>
    /// <param name="rawStore">Store for persisting raw API responses before parsing.</param>
    /// <param name="clock">Clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">Business calendar for computing calendar-aware coverage.</param>
    /// <param name="options">Tiingo-specific configuration (base URL and API token).</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public TiingoPriceBarAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("tiingo")] IBusinessCalendar calendar,
        IOptions<TiingoOptions> options,
        ILogger<TiingoPriceBarAdapter> logger)
    {
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProviderCode ProviderKey => new("tiingo");

    /// <inheritdoc />
    public int Priority => 100;

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

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Token {_options.ApiToken}"
        };

        foreach (var symbol in request.Symbols)
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/tiingo/daily/{symbol}/prices" +
                      $"?startDate={request.Range.From:yyyy-MM-dd}" +
                      $"&endDate={request.Range.To:yyyy-MM-dd}" +
                      $"&format=json";

            try
            {
                var uri = new Uri(url);
                using var stream = await _transport.GetAsync(uri, headers, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                var rawKey = $"tiingo/daily/{symbol}/{request.Range.From:yyyyMMdd}-{request.Range.To:yyyyMMdd}";
                await _rawStore.SaveAsync(rawKey, json, cancellationToken).ConfigureAwait(false);

                var bars = ParseBars(json);
                allBars.AddRange(bars);

                if (bars.Count > 0)
                {
                    symbolsWithData++;
                }
                else
                {
                    issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Warning,
                        $"Tiingo returned 0 bars for symbol '{symbol}'."));
                }

                _logger.LogInformation("Fetched {Count} bars for {Symbol} from Tiingo", bars.Count, symbol);

                provenanceList.Add(new DataProvenance(
                    Provider: new ProviderCode("tiingo"),
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
                _logger.LogWarning(ex, "Failed to fetch bars for {Symbol} from Tiingo", symbol);
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

    private static List<Bar> ParseBars(string json)
    {
        var bars = new List<Bar>();
        using var doc = JsonDocument.Parse(json);

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var dateStr = element.GetProperty("date").GetString()!;
            var date = DateOnly.Parse(dateStr.AsSpan(0, 10), CultureInfo.InvariantCulture);

            var open = element.GetProperty("open").GetDecimal();
            var high = element.GetProperty("high").GetDecimal();
            var low = element.GetProperty("low").GetDecimal();
            var close = element.GetProperty("close").GetDecimal();

            var adjClose = element.TryGetProperty("adjClose", out var adjProp) && adjProp.ValueKind == JsonValueKind.Number
                ? adjProp.GetDecimal()
                : close;

            var volume = element.TryGetProperty("volume", out var volProp) && volProp.ValueKind == JsonValueKind.Number
                ? volProp.GetInt64()
                : 0L;

            bars.Add(new Bar(date, open, high, low, close, adjClose, volume));
        }

        return bars;
    }
}
