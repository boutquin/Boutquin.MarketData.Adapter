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

using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Transport.Http;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boutquin.MarketData.Adapter.Tiingo;

/// <summary>
/// Fetches instrument reference metadata from the Tiingo daily meta endpoint
/// (<c>/tiingo/daily/{ticker}</c>) and parses it into canonical
/// <see cref="InstrumentMetadata"/> records.
/// </summary>
/// <remarks>
/// Each symbol is fetched individually. Per-symbol failures (including 404 Not Found)
/// are caught, logged as warnings, and recorded as <see cref="DataIssue"/> entries —
/// the adapter continues with the remaining symbols.
/// </remarks>
public sealed class TiingoInstrumentMetadataAdapter
    : IDataSourceAdapter<InstrumentMetadataRequest, InstrumentMetadata>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly TiingoOptions _options;
    private readonly ILogger<TiingoInstrumentMetadataAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TiingoInstrumentMetadataAdapter"/> class.
    /// </summary>
    public TiingoInstrumentMetadataAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        IOptions<TiingoOptions> options,
        ILogger<TiingoInstrumentMetadataAdapter> logger)
    {
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProviderCode ProviderKey => new("tiingo");

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanHandle(InstrumentMetadataRequest request) =>
        request.Symbols.Count > 0;

    /// <inheritdoc />
    public async Task<DataEnvelope<IReadOnlyList<InstrumentMetadata>>> FetchAsync(
        InstrumentMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        var results = new List<InstrumentMetadata>();
        var issues = new List<DataIssue>();
        var provenanceList = new List<DataProvenance>();

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Token {_options.ApiToken}"
        };

        foreach (var symbol in request.Symbols)
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/tiingo/daily/{symbol}";

            try
            {
                var uri = new Uri(url);
                using var stream = await _transport.GetAsync(uri, headers, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                var rawKey = $"tiingo/meta/{symbol}";
                await _rawStore.SaveAsync(rawKey, json, cancellationToken).ConfigureAwait(false);

                var meta = ParseMetadata(json);
                if (meta is not null)
                {
                    results.Add(meta);
                }
                else
                {
                    issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Warning,
                        $"Tiingo returned unparseable metadata for symbol '{symbol}'."));
                }

                _logger.LogInformation("Fetched metadata for {Symbol} from Tiingo", symbol);

                provenanceList.Add(new DataProvenance(
                    Provider: new ProviderCode("tiingo"),
                    Dataset: symbol.Ticker,
                    LicenseFlag: LicenseType.Free,
                    RetrievalMode: RetrievalMode.Api,
                    Freshness: FreshnessClass.EndOfDay,
                    RetrievedAtUtc: _clock.UtcNow,
                    SourceUrl: url));
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Tiingo returned 404 for {Symbol}", symbol);
                issues.Add(new DataIssue(new IssueCode("NOT_FOUND"), IssueSeverity.Warning,
                    $"Tiingo returned 404 for symbol '{symbol}'."));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch metadata for {Symbol} from Tiingo", symbol);
                issues.Add(new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Warning,
                    $"Failed to fetch metadata for symbol '{symbol}': {ex.Message}"));
            }
        }

        var coverage = new DataCoverage(
            RequestedPoints: request.Symbols.Count,
            ReturnedPoints: results.Count,
            MissingPoints: request.Symbols.Count - results.Count,
            CoverageRatio: request.Symbols.Count > 0
                ? (decimal)results.Count / request.Symbols.Count
                : 1m);

        return new DataEnvelope<IReadOnlyList<InstrumentMetadata>>(
            results,
            coverage,
            issues,
            provenanceList);
    }

    private static InstrumentMetadata? ParseMetadata(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var ticker = root.TryGetProperty("ticker", out var t) ? t.GetString() ?? "" : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var exchange = root.TryGetProperty("exchangeCode", out var e) ? e.GetString() ?? "" : "";
        var description = root.TryGetProperty("description", out var d) ? d.GetString() : null;

        DateOnly? inceptionDate = null;
        if (root.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.String)
        {
            var dateStr = sd.GetString();
            if (dateStr is not null && DateOnly.TryParse(dateStr.AsSpan(0, Math.Min(10, dateStr.Length)),
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                inceptionDate = parsed;
            }
        }

        return new InstrumentMetadata(
            new Symbol(string.IsNullOrWhiteSpace(ticker) ? "UNKNOWN" : ticker),
            name,
            ParseExchangeCode(exchange),
            AssetClassCode.Equities,
            null,
            inceptionDate,
            description);
    }

    private static ExchangeCode ParseExchangeCode(string exchange) =>
        Enum.TryParse<ExchangeCode>(exchange, ignoreCase: true, out var code) ? code : ExchangeCode.XNAS;
}
