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
/// Tiingo's <c>startDate</c> describes price-history availability, so it does not populate
/// <see cref="InstrumentMetadata.InceptionDate"/> (the date the instrument began trading).
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

    /// <summary>The stable <c>tiingo</c> provider identity attached to this adapter's provenance.</summary>
    public ProviderCode ProviderKey => new("tiingo");

    /// <summary>The fallback rank, 100; adapters with higher ranks are attempted first.</summary>
    public int Priority => 100;

    /// <summary>
    /// Listing venues Tiingo's metadata endpoint covers for this adapter's role in the
    /// orchestrator: US equities and ETFs only. See <see cref="TiingoPriceBarAdapter"/>
    /// for the CA research regression this filter prevents.
    /// </summary>
    private static readonly IReadOnlySet<ExchangeCode> s_supportedExchanges =
        new HashSet<ExchangeCode> { ExchangeCode.XNYS, ExchangeCode.XNAS };

    /// <summary>Checks the request against this provider's supported routing constraints without performing I/O.</summary>
    /// <param name="request">Metadata requests with symbols on a supported US venue, or with no venue hint.</param>
    /// <returns><see langword="true"/> when the request matches the adapter's declared support; this does not guarantee provider availability.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public bool CanHandle(InstrumentMetadataRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Symbols.Count > 0
            && (request.Exchange is null || s_supportedExchanges.Contains(request.Exchange.Value));
    }

    /// <summary>Retrieves instrument metadata for the requested symbols from Tiingo.</summary>
    /// <param name="request">Metadata requests with symbols on a supported US venue, or with no venue hint.</param>
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
            throw new ArgumentException("Tiingo metadata fetches require a supported venue.", nameof(request));
        }

        TiingoTickerValidator.ThrowIfInvalid(request.Symbols, nameof(request));
        _options.ValidateEndpoint();

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
                if (meta is not null && !string.Equals(meta.Symbol.Ticker, symbol.Ticker, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new DataIssue(new IssueCode("TICKER_MISMATCH"), IssueSeverity.Warning,
                        $"Tiingo returned metadata for a different symbol than '{symbol}'."));
                }
                else if (meta is not null && request.Exchange is not null && meta.Exchange != request.Exchange.Value)
                {
                    issues.Add(new DataIssue(new IssueCode("EXCHANGE_MISMATCH"), IssueSeverity.Warning,
                        $"Tiingo returned metadata for a different exchange than '{request.Exchange.Value}'."));
                }
                else if (meta is not null)
                {
                    results.Add(meta);
                    provenanceList.Add(new DataProvenance(
                        Provider: new ProviderCode("tiingo"),
                        Dataset: symbol.Ticker,
                        LicenseFlag: LicenseType.Free,
                        RetrievalMode: RetrievalMode.Api,
                        Freshness: FreshnessClass.EndOfDay,
                        RetrievedAtUtc: _clock.UtcNow,
                        SourceUrl: url));
                }
                else
                {
                    issues.Add(new DataIssue(new IssueCode("NO_DATA"), IssueSeverity.Warning,
                        $"Tiingo returned unparseable metadata for symbol '{symbol}'."));
                }

                _logger.LogInformation("Fetched metadata for {Symbol} from Tiingo", symbol);

            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Tiingo returned 404 for {Symbol}", symbol);
                issues.Add(new DataIssue(new IssueCode("NOT_FOUND"), IssueSeverity.Warning,
                    $"Tiingo returned 404 for symbol '{symbol}'."));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to fetch metadata for {Symbol} from Tiingo ({ExceptionType})", symbol, ex.GetType().Name);
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

    private static InstrumentMetadata? ParseMetadata(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var ticker = root.TryGetProperty("ticker", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(ticker))
        {
            return null;
        }

        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var exchange = root.TryGetProperty("exchangeCode", out var e) ? e.GetString() ?? "" : "";
        if (!Enum.TryParse<ExchangeCode>(exchange, ignoreCase: true, out var exchangeCode) ||
            !Enum.IsDefined(exchangeCode) ||
            !string.Equals(exchange, exchangeCode.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var description = root.TryGetProperty("description", out var d) ? d.GetString() : null;

        return new InstrumentMetadata(
            new Symbol(ticker),
            name,
            exchangeCode,
            AssetClassCode.Equities,
            null,
            null,
            description);
    }
}
