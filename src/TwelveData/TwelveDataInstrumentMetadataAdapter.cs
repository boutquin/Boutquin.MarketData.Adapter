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

namespace Boutquin.MarketData.Adapter.TwelveData;

/// <summary>
/// Fetches instrument reference metadata from the Twelve Data <c>/stocks</c> endpoint
/// and reshapes it into canonical <see cref="InstrumentMetadata"/> records.
/// </summary>
/// <remarks>
/// <para>
/// Twelve Data does not provide an instrument inception date — the
/// <see cref="InstrumentMetadata.InceptionDate"/> field is always <see langword="null"/>.
/// Consumers should fall back to the earliest available price date when needed.
/// </para>
/// <para>
/// This adapter has <see cref="Priority"/> 50 (lower than Tiingo at 100), so it serves
/// as a fallback when Tiingo returns 404 for a symbol.
/// </para>
/// </remarks>
public sealed class TwelveDataInstrumentMetadataAdapter
    : IDataSourceAdapter<InstrumentMetadataRequest, InstrumentMetadata>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly TwelveDataOptions _options;
    private readonly ILogger<TwelveDataInstrumentMetadataAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TwelveDataInstrumentMetadataAdapter"/> class.
    /// </summary>
    public TwelveDataInstrumentMetadataAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        IOptions<TwelveDataOptions> options,
        ILogger<TwelveDataInstrumentMetadataAdapter> logger)
    {
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProviderCode ProviderKey => new("twelvedata");

    /// <inheritdoc />
    public int Priority => 50;

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

        foreach (var symbol in request.Symbols)
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/stocks" +
                      $"?symbol={Uri.EscapeDataString(symbol.Ticker)}" +
                      $"&apikey={_options.ApiKey}";

            try
            {
                var uri = new Uri(url);
                using var stream = await _transport.GetAsync(uri, new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                var rawKey = $"twelvedata/meta/{symbol}";
                await _rawStore.SaveAsync(rawKey, json, cancellationToken).ConfigureAwait(false);

                var meta = ParseMetadata(json, symbol);
                if (meta is not null)
                {
                    results.Add(meta);
                }
                else
                {
                    issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Warning,
                        $"TwelveData returned no matching data for symbol '{symbol}'."));
                }

                _logger.LogInformation("Fetched metadata for {Symbol} from TwelveData", symbol);

                provenanceList.Add(new DataProvenance(
                    Provider: new ProviderCode("twelvedata"),
                    Dataset: symbol.Ticker,
                    LicenseFlag: LicenseType.Free,
                    RetrievalMode: RetrievalMode.Api,
                    Freshness: FreshnessClass.EndOfDay,
                    RetrievedAtUtc: _clock.UtcNow,
                    SourceUrl: url));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch metadata for {Symbol} from TwelveData", symbol);
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

    /// <summary>
    /// Parses the TwelveData <c>/stocks</c> response. The response contains a
    /// <c>"data"</c> array; we extract the first match for the requested symbol.
    /// </summary>
    private static InstrumentMetadata? ParseMetadata(string json, Symbol symbol)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var dataArray) ||
            dataArray.ValueKind != JsonValueKind.Array ||
            dataArray.GetArrayLength() == 0)
        {
            return null;
        }

        var first = dataArray[0];
        var name = first.TryGetProperty("name", out var n) ? n.GetString() ?? symbol.Ticker : symbol.Ticker;
        var exchange = first.TryGetProperty("exchange", out var e) ? e.GetString() ?? "" : "";

        // TwelveData does not provide inception/startDate — leave null.
        return new InstrumentMetadata(
            new Symbol(symbol.Ticker.ToUpperInvariant()),
            name,
            ParseExchangeCode(exchange),
            AssetClassCode.Equities,
            null,
            null,
            $"Metadata sourced from TwelveData (fallback)");
    }

    private static ExchangeCode ParseExchangeCode(string exchange) =>
        Enum.TryParse<ExchangeCode>(exchange, ignoreCase: true, out var code) ? code : ExchangeCode.XNAS;
}
