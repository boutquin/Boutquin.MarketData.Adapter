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
/// Fetches instrument reference metadata from the Twelve Data <c>/etfs</c> endpoint
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

    /// <summary>The stable <c>twelvedata</c> provider identity attached to this adapter's provenance.</summary>
    public ProviderCode ProviderKey => new("twelvedata");

    /// <summary>The fallback rank, 50; adapters with higher ranks are attempted first.</summary>
    public int Priority => 50;

    /// <summary>
    /// Listing venues TwelveData's metadata endpoint covers for this adapter's role:
    /// CA primary (XTSE, NEOE) and US fallback (XNYS, XNAS) — the four venues the
    /// etfwealthiq universe declares. See <see cref="TwelveDataPriceBarAdapter"/> for
    /// the broader opt-in rationale.
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
    /// <param name="request">Metadata requests with symbols on a supported venue, or with no venue hint.</param>
    /// <returns><see langword="true"/> when the request matches the adapter's declared support; this does not guarantee provider availability.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public bool CanHandle(InstrumentMetadataRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Symbols.Count > 0
            && (request.Exchange is null || s_supportedExchanges.Contains(request.Exchange.Value));
    }

    /// <summary>Retrieves ETF catalogue metadata for the requested symbols from Twelve Data.</summary>
    /// <param name="request">Metadata requests with symbols on a supported venue, or with no venue hint.</param>
    /// <param name="cancellationToken">Cancels transport, raw-payload persistence and parsing where those operations support cancellation.</param>
    /// <returns>A task yielding canonical records together with provider provenance, coverage and data-quality issues. Empty or partial responses retain their failure or coverage information.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="OperationCanceledException">The caller or a dependency cancels the operation.</exception>
    /// <remarks>Successful fetches include raw-payload persistence and normalization. Inspect the envelope's issues and coverage before using its records; a returned envelope alone does not certify complete data.</remarks>
    public async Task<DataEnvelope<IReadOnlyList<InstrumentMetadata>>> FetchAsync(
        InstrumentMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanHandle(request))
        {
            throw new ArgumentException("Twelve Data metadata fetches require a supported venue.", nameof(request));
        }

        _options.ValidateEndpoint();
        var results = new List<InstrumentMetadata>();
        var issues = new List<DataIssue>();
        var provenanceList = new List<DataProvenance>();

        foreach (var symbol in request.Symbols)
        {
            // Use /etfs rather than /stocks — TwelveData's /stocks endpoint returns only
            // common-stock listings and silently filters ETF rows out, which produced the
            // 2026-04-19 CA research regression (bare XIC returned "Scandinavian Energies
            // Inc." on XFRA/XMUN, a US stock that happens to share the 3-letter symbol).
            // See https://twelvedata.com/docs/asset-catalogs/etf-list.
            var micCodeParam = request.Exchange is null
                ? string.Empty
                : $"&mic_code={request.Exchange.Value}";
            var sourceUrl = $"{_options.BaseUrl.TrimEnd('/')}/etfs" +
                      $"?symbol={Uri.EscapeDataString(symbol.Ticker)}" +
                      $"{micCodeParam}";
            var url = $"{sourceUrl}&apikey={Uri.EscapeDataString(_options.ApiKey)}";

            try
            {
                var uri = new Uri(url);
                using var stream = await _transport.GetAsync(uri, new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                var rawKey = $"twelvedata/meta/{symbol}/{request.Exchange?.ToString() ?? "any"}";
                await _rawStore.SaveAsync(rawKey, json, cancellationToken).ConfigureAwait(false);

                var meta = ParseMetadata(json, symbol, request.Exchange);
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
                    SourceUrl: sourceUrl));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to fetch metadata for {Symbol} from TwelveData ({ExceptionType})", symbol, ex.GetType().Name);
                issues.Add(new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Warning,
                    $"Failed to fetch metadata for symbol '{symbol}'."));
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
    /// Parses the TwelveData <c>/etfs</c> response. The response contains a
    /// <c>"data"</c> array; we extract the first row matching the requested symbol and,
    /// when specified, exchange. Provider-side filtering is not trusted for identity.
    /// </summary>
    private static InstrumentMetadata? ParseMetadata(string json, Symbol symbol, ExchangeCode? exchangeHint)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var dataArray) ||
            dataArray.ValueKind != JsonValueKind.Array ||
            dataArray.GetArrayLength() == 0)
        {
            return null;
        }

        foreach (var row in dataArray.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("symbol", out var ticker) ||
                ticker.ValueKind != JsonValueKind.String ||
                !string.Equals(ticker.GetString(), symbol.Ticker, StringComparison.OrdinalIgnoreCase) ||
                !row.TryGetProperty("mic_code", out var mic) ||
                mic.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<ExchangeCode>(mic.GetString(), ignoreCase: true, out var exchange) ||
                !Enum.IsDefined(exchange) ||
                !string.Equals(mic.GetString(), exchange.ToString(), StringComparison.OrdinalIgnoreCase) ||
                (exchangeHint is not null && exchange != exchangeHint.Value))
            {
                continue;
            }

            var name = row.TryGetProperty("name", out var n) ? n.GetString() ?? symbol.Ticker : symbol.Ticker;
            return new InstrumentMetadata(
                new Symbol(symbol.Ticker.ToUpperInvariant()),
                name,
                exchange,
                AssetClassCode.Equities,
                null,
                null,
                "Metadata sourced from TwelveData (fallback)");
        }

        return null;
    }

}
