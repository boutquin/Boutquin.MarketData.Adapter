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
/// Fetches cash-dividend history for one US-listed equity or ETF from the Tiingo REST API,
/// emitting one <see cref="DividendPayment"/> per ex-date on which cash was distributed.
/// </summary>
/// <remarks>
/// <para>
/// <b>No dedicated dividend endpoint.</b> This adapter reads the same daily-price resource
/// <see cref="TiingoPriceBarAdapter"/> uses, because every bar of that payload already carries a
/// <c>divCash</c> field. A bar whose <c>divCash</c> is positive is an ex-date; a bar whose
/// <c>divCash</c> is zero, absent, or <c>null</c> is an ordinary trading day and yields no record
/// at all. The raw response is persisted under its own dividend-scoped key so that fetching
/// distributions never overwrites the price adapter's stored payload for the same symbol and range.
/// </para>
/// <para>
/// <b>Coverage is event-sparse — the one deliberate departure from the price-adapter template.</b>
/// The shared trading-day coverage helper the price adapters use expects one observation per
/// trading day and reports every day without one as an unexpected gap. Distributions occur on a
/// handful of ex-dates per year, so applying that model here would report a normal six-month
/// window as roughly ninety-eight percent missing and raise a gap issue for every non-payment day
/// — false positives that would bury genuine data-quality signals. This adapter therefore builds
/// its own envelope: requested and returned counts are both the number of ex-dates the payload
/// carried, and no gap issues are raised for days without a distribution. The ratio still degrades
/// to the fraction of symbols the provider actually answered for, so a failed fetch reports
/// missing or partial coverage rather than a false <c>Complete</c>.
/// </para>
/// <para>
/// <b>Classification is inferred, and the inference is logged.</b> Every emitted payment is typed
/// <see cref="DividendType.Unclassified"/>. Tiingo's <c>divCash</c> is the total cash distributed
/// on a given ex-date, which for a fund routinely blends ordinary or qualified income with return
/// of capital and capital-gain distributions. Because no single category describes that blend,
/// assigning the jurisdictional default for a US listing would assert an income treatment that is
/// wrong for the return-of-capital and capital-gain portions — and downstream gross-up and
/// withholding computations would act on the assertion. Leaving the aggregate unclassified is the
/// honest value, and an <see cref="LogLevel.Information"/> entry is written for every payment so
/// the choice is auditable rather than silent.
/// </para>
/// <para>
/// <b>One record per ex-date; re-ingest is idempotent.</b> Since <c>divCash</c> is already the
/// per-date aggregate, identical duplicate rows collapse to one payment. Conflicting amounts for
/// one ex-date are treated as malformed provider data instead of resolving by response order.
/// Rows outside the inclusive request range are ignored, so a provider response cannot widen the
/// caller's requested window. Separating multiple payments made on the same day is not possible
/// from this source — that blend is the same property behind the classification decision above.
/// </para>
/// <para>
/// <b>Single-symbol boundary.</b> <see cref="DividendPayment"/> carries no symbol field, so a flat
/// multi-symbol envelope cannot preserve payment ownership. This adapter therefore accepts exactly
/// one symbol per call. Consumers with a batch request must partition it before dispatch and keep
/// each returned envelope keyed by its symbol.
/// </para>
/// </remarks>
public sealed class TiingoDividendHistoryAdapter
    : IDataSourceAdapter<DividendHistoryRequest, DividendPayment>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly TiingoOptions _options;
    private readonly ILogger<TiingoDividendHistoryAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TiingoDividendHistoryAdapter"/> class.
    /// </summary>
    /// <param name="transport">HTTP transport for making API requests.</param>
    /// <param name="rawStore">Store for persisting raw API responses before parsing.</param>
    /// <param name="clock">Clock abstraction for timestamping provenance records.</param>
    /// <param name="options">Tiingo-specific configuration (base URL and API token).</param>
    /// <param name="logger">Logger for diagnostic output and classification-inference events.</param>
    /// <exception cref="ArgumentNullException">
    /// Any required collaborator is <see langword="null"/>.
    /// </exception>
    public TiingoDividendHistoryAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        IOptions<TiingoOptions> options,
        ILogger<TiingoDividendHistoryAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(rawStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Identifies Tiingo in provenance records and adapter selection.
    /// </summary>
    /// <value>The provider code <c>tiingo</c>.</value>
    public ProviderCode ProviderKey => new("tiingo");

    /// <summary>
    /// Orders this adapter in the fallback chain.
    /// </summary>
    /// <value><c>100</c>.</value>
    public int Priority => 100;

    /// <summary>
    /// Listing venues this adapter covers: US equities and ETFs only. A request whose
    /// <see cref="DividendHistoryRequest.Exchange"/> is set but not contained here causes
    /// <see cref="CanHandle"/> to return <see langword="false"/>, so the pipeline falls through
    /// to the next prioritized adapter. The set is deliberately identical to the sibling price
    /// adapter's, and it is what makes the unconditional <see cref="Currency.USD"/> stamped on
    /// every emitted payment correct for supported US listings. Explicit foreign venue hints
    /// are refused; callers omitting the hint rely on Tiingo's US ticker namespace.
    /// </summary>
    private static readonly IReadOnlySet<ExchangeCode> s_supportedExchanges =
        new HashSet<ExchangeCode> { ExchangeCode.XNYS, ExchangeCode.XNAS };

    /// <summary>
    /// Determines whether this adapter can serve a single-symbol request for a supported US venue.
    /// </summary>
    /// <param name="request">The dividend-history request to evaluate.</param>
    /// <returns>
    /// <see langword="true"/> when the request contains exactly one symbol and its optional venue
    /// is <see cref="ExchangeCode.XNYS"/> or <see cref="ExchangeCode.XNAS"/>; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    public bool CanHandle(DividendHistoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Symbols.Count == 1
            && (request.Exchange is null || s_supportedExchanges.Contains(request.Exchange.Value));
    }

    /// <summary>
    /// Fetches Tiingo daily-price data for one symbol, persists the raw JSON, and projects each
    /// distinct positive <c>divCash</c> ex-date into one canonical <see cref="DividendPayment"/>.
    /// </summary>
    /// <param name="request">A dividend-history request containing exactly one symbol.</param>
    /// <param name="cancellationToken">A token that cancels transport, response reading, or raw persistence.</param>
    /// <returns>
    /// A task whose result contains the symbol's payments, event-sparse coverage, provider
    /// provenance, and a warning issue when a non-cancellation fetch failure occurs.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="request"/> does not contain exactly one symbol, or its ticker is not a
    /// valid Tiingo daily-endpoint path segment, or its venue is not supported. Partition a batch
    /// before calling this adapter because <see cref="DividendPayment"/> cannot carry symbol
    /// ownership.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is signaled while the operation is in flight.
    /// </exception>
    public async Task<DataEnvelope<IReadOnlyList<DividendPayment>>> FetchAsync(
        DividendHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Symbols.Count != 1)
        {
            throw new ArgumentException(
                "Tiingo dividend fetches require exactly one symbol so every payment retains ownership. "
                + "Partition multi-symbol requests before adapter dispatch.",
                nameof(request));
        }

        if (request.Exchange is not null
            && !s_supportedExchanges.Contains(request.Exchange.Value))
        {
            throw new ArgumentException(
                $"Tiingo dividend fetches support only XNYS and XNAS, not {request.Exchange.Value}.",
                nameof(request));
        }

        var requestedSymbol = request.Symbols[0];
        TiingoTickerValidator.ThrowIfInvalid(request.Symbols, nameof(request));
        _options.ValidateEndpoint();

        var allPayments = new List<DividendPayment>();
        var issues = new List<DataIssue>();
        var provenanceList = new List<DataProvenance>();
        var symbolsFetched = 0;

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Token {_options.ApiToken}"
        };

        foreach (var symbol in request.Symbols)
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/tiingo/daily/{symbol}/prices" +
                      $"?startDate={request.Range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
                      $"&endDate={request.Range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
                      $"&format=json";

            try
            {
                var uri = new Uri(url);
                using var stream = await _transport.GetAsync(uri, headers, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                var rawKey = $"tiingo/dividends/{symbol}/{request.Range.From.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{request.Range.To.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
                await _rawStore.SaveAsync(rawKey, json, cancellationToken).ConfigureAwait(false);

                var payments = ParsePayments(json, request.Range);

                foreach (var payment in payments)
                {
                    _logger.LogInformation(
                        "Tiingo reports {Symbol} {Amount} on ex-date {ExDate:yyyy-MM-dd} as a single per-date " +
                        "cash aggregate with no category breakdown, so the dividend category is inferred as " +
                        "{Classification} rather than assigned a jurisdictional default.",
                        symbol,
                        payment.Amount,
                        payment.ExDate,
                        nameof(DividendType.Unclassified));
                }

                allPayments.AddRange(payments);

                provenanceList.Add(new DataProvenance(
                    Provider: new ProviderCode("tiingo"),
                    Dataset: symbol.Ticker,
                    LicenseFlag: LicenseType.Free,
                    RetrievalMode: RetrievalMode.Api,
                    Freshness: FreshnessClass.EndOfDay,
                    RetrievedAtUtc: _clock.UtcNow,
                    SourceUrl: url,
                    DataDate: payments.Count > 0 ? payments.Max(p => p.ExDate) : null));

                symbolsFetched++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to fetch dividends for {Symbol} from Tiingo ({ExceptionType})", symbol, ex.GetType().Name);
                issues.Add(new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Warning,
                    "Tiingo dividend fetch failed."));
            }
        }

        return new DataEnvelope<IReadOnlyList<DividendPayment>>(
            allPayments,
            ComputeEventSparseCoverage(request.Symbols.Count, symbolsFetched, allPayments),
            issues,
            provenanceList);
    }

    /// <summary>
    /// Builds the coverage envelope for an event series. Deliberately does not use the shared
    /// trading-day coverage helper the price adapters use — see the class remarks for why.
    /// </summary>
    /// <param name="symbolsRequested">How many symbols the request asked for.</param>
    /// <param name="symbolsFetched">How many of them the provider answered for.</param>
    /// <param name="payments">The ex-date rows extracted across all answered symbols.</param>
    /// <returns>
    /// A coverage envelope whose requested and returned counts are both the number of ex-dates
    /// found, so a range containing few distributions never reads as incomplete. The ratio still
    /// degrades — to the fraction of symbols the provider answered for — when a fetch fails, so a
    /// total outage reports <see cref="CompletenessStatus.Missing"/> rather than a false
    /// <see cref="CompletenessStatus.Complete"/>.
    /// </returns>
    private static DataCoverage ComputeEventSparseCoverage(
        int symbolsRequested,
        int symbolsFetched,
        IReadOnlyCollection<DividendPayment> payments)
    {
        var exDatesFound = payments.Count;

        var ratio = symbolsRequested > 0 && symbolsFetched < symbolsRequested
            ? (decimal)symbolsFetched / symbolsRequested
            : 1m;

        return new DataCoverage(
            RequestedPoints: exDatesFound,
            ReturnedPoints: exDatesFound,
            MissingPoints: 0,
            CoverageRatio: ratio);
    }

    /// <summary>
    /// Projects the ex-date rows out of a Tiingo daily-price payload. Bars whose
    /// <c>divCash</c> is zero, absent, or JSON <c>null</c> are ordinary trading days and are
    /// skipped. Positive rows require an ISO <c>yyyy-MM-dd</c> date prefix, are clipped to the
    /// inclusive request range, and must agree when an ex-date is repeated.
    /// </summary>
    private static List<DividendPayment> ParsePayments(string json, DateRange range)
    {
        var payments = new List<DividendPayment>();
        var amountsByExDate = new Dictionary<DateOnly, decimal>();
        using var doc = JsonDocument.Parse(json);

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            decimal divCash;
            if (!element.TryGetProperty("divCash", out var divProp)
                || divProp.ValueKind == JsonValueKind.Null)
            {
                divCash = 0m;
            }
            else if (divProp.ValueKind == JsonValueKind.Number)
            {
                divCash = divProp.GetDecimal();
            }
            else
            {
                throw new FormatException(
                    $"Tiingo dividend divCash must be a JSON number or null, but was {divProp.ValueKind}.");
            }

            if (divCash <= 0m)
            {
                continue;
            }

            var dateText = element.GetProperty("date").GetString();
            if (dateText is null
                || dateText.Length < 10
                || !DateOnly.TryParseExact(
                    dateText.AsSpan(0, 10),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var exDate))
            {
                throw new FormatException($"Tiingo dividend date '{dateText}' is not an ISO yyyy-MM-dd date.");
            }

            if (exDate < range.From || exDate > range.To)
            {
                continue;
            }

            if (amountsByExDate.TryGetValue(exDate, out var existingAmount))
            {
                if (existingAmount != divCash)
                {
                    throw new FormatException(
                        $"Tiingo returned conflicting dividend amounts for ex-date {exDate:yyyy-MM-dd}: "
                        + $"{existingAmount} and {divCash}.");
                }

                continue;
            }

            amountsByExDate.Add(exDate, divCash);
            payments.Add(new DividendPayment(exDate, divCash, Currency.USD, DividendType.Unclassified));
        }

        return payments;
    }

}
