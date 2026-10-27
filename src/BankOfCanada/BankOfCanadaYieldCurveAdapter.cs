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

using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Transport.Http;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boutquin.MarketData.Adapter.BankOfCanada;

/// <summary>
/// Fetches the Bank of Canada's zero-coupon Government of Canada yield curve and returns
/// canonical <see cref="YieldCurveQuote"/> records with provenance metadata.
/// </summary>
/// <remarks>
/// <para>
/// The Bank publishes this curve as a CSV download behind its "Yield curves for zero-coupon
/// bonds" page, not through the Valet API. The adapter calls
/// <c>{YieldCurveLookupUrl}?lookupPage=lookup_yield_curve.php&amp;dFrom={from}&amp;dTo={asOf}</c>
/// with a <see cref="LookbackDays"/>-day window ending on the requested date, persists the raw
/// CSV, and takes the latest row that carries at least one numeric value. Rows for holidays
/// and other days without a curve are published as <c>na</c> throughout and are skipped.
/// </para>
/// <para>
/// Each <c>ZCnnnYR</c> column is a term to maturity in hundredths of a year (<c>ZC025YR</c> is
/// 0.25 years, <c>ZC3000YR</c> is 30 years). Terms are relabelled as months when they are not
/// a whole number of years and as years otherwise — <c>3M</c>, <c>6M</c>, <c>9M</c>, <c>1Y</c>,
/// <c>15M</c>, …, <c>30Y</c> — so consumers can select nodes by the same tenor strings other
/// curve sources use. Rates are already in decimal form (0.05 = 5%) and are returned as-is.
/// </para>
/// <para>
/// The file is refreshed weekly with a lag of about two weeks, so a request for a recent
/// <see cref="YieldCurveQuoteRequest.AsOfDate"/> normally resolves to an earlier curve and
/// carries a <c>DATE_ROLLBACK</c> warning naming the date actually served.
/// </para>
/// </remarks>
public sealed class BankOfCanadaYieldCurveAdapter : IDataSourceAdapter<YieldCurveQuoteRequest, YieldCurveQuote>, IPrioritizedAdapter
{
    /// <summary>
    /// Calendar days before the requested date included in the lookup window. The Bank's
    /// publication lag is about two weeks; the window is wide enough to find a published curve
    /// behind that lag plus a holiday run.
    /// </summary>
    public const int LookbackDays = 30;

    private const string ZeroCouponColumnPrefix = "ZC";
    private const string ZeroCouponColumnSuffix = "YR";

    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly BankOfCanadaOptions _options;
    private readonly ILogger<BankOfCanadaYieldCurveAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BankOfCanadaYieldCurveAdapter"/> class.
    /// </summary>
    /// <param name="transport">The HTTP transport used to fetch the CSV from the Bank of Canada.</param>
    /// <param name="rawStore">The raw document store for persisting responses before parsing.</param>
    /// <param name="clock">The clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">Retained for constructor compatibility. Coverage counts one as-of snapshot.</param>
    /// <param name="options">Configuration options for the Bank of Canada adapter.</param>
    /// <param name="logger">The logger instance for diagnostic output.</param>
    public BankOfCanadaYieldCurveAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("bankofcanada")] IBusinessCalendar calendar,
        IOptions<BankOfCanadaOptions> options,
        ILogger<BankOfCanadaYieldCurveAdapter> logger)
    {
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The stable <c>bankofcanada</c> provider identity attached to this adapter's provenance.</summary>
    public ProviderCode ProviderKey => new("bankofcanada");

    /// <summary>The fallback rank, 100; adapters with higher ranks are attempted first.</summary>
    public int Priority => 100;

    /// <summary>Checks the request against this provider's supported routing constraints without performing I/O.</summary>
    /// <param name="request">CAD-denominated BOC-ZERO or CAD-ZERO curve requests.</param>
    /// <returns><see langword="true"/> when the request matches the adapter's declared support; this does not guarantee provider availability.</returns>
    public bool CanHandle(YieldCurveQuoteRequest request) =>
        string.Equals(_options.Currency, "CAD", StringComparison.OrdinalIgnoreCase)
        &&
        (string.Equals(request.CurveId.Value, "BOC-ZERO", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.CurveId.Value, "CAD-ZERO", StringComparison.OrdinalIgnoreCase))
        && (request.Exchange is null or ExchangeCode.XTSE or ExchangeCode.NEOE);

    /// <summary>Retrieves the latest usable zero-coupon curve inside the as-of lookback from Bank of Canada.</summary>
    /// <param name="request">CAD-denominated BOC-ZERO or CAD-ZERO curve requests.</param>
    /// <param name="cancellationToken">Cancels transport, raw-payload persistence and parsing where those operations support cancellation.</param>
    /// <returns>A task yielding canonical records together with provider provenance, coverage and data-quality issues. Empty or partial responses retain their failure or coverage information.</returns>
    /// <exception cref="ArgumentException">The request does not match this adapter's supported routing constraints.</exception>
    /// <exception cref="OperationCanceledException">The caller or a dependency cancels the operation.</exception>
    /// <remarks>Successful fetches include raw-payload persistence and normalization. Inspect the envelope's issues and coverage before using its records; a returned envelope alone does not certify complete data.</remarks>
    public async Task<DataEnvelope<IReadOnlyList<YieldCurveQuote>>> FetchAsync(
        YieldCurveQuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CanHandle(request))
        {
            throw new ArgumentException("The Bank of Canada adapter cannot serve this curve or venue.", nameof(request));
        }

        var from = DateOnly.FromDayNumber(Math.Max(0, request.AsOfDate.DayNumber - LookbackDays));
        var url = $"{_options.YieldCurveLookupUrl.TrimEnd('/')}?lookupPage=lookup_yield_curve.php" +
                  $"&dFrom={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&dTo={request.AsOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

        try
        {
            var uri = new Uri(url);
            var stream = await _transport.GetAsync(uri, null, cancellationToken).ConfigureAwait(false);

            string csv;
            using (var reader = new StreamReader(stream))
            {
                csv = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            await _rawStore.SaveAsync(
                $"bankofcanada/yield-curve/{request.CurveId}/{request.AsOfDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}",
                csv,
                cancellationToken).ConfigureAwait(false);

            var issues = new List<DataIssue>();
            var quotes = new List<YieldCurveQuote>();
            DateOnly? observationDate = null;

            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var tenorByColumn = lines.Length > 0 ? ParseHeader(lines[0]) : null;

            if (tenorByColumn is null)
            {
                issues.Add(new DataIssue(new IssueCode("SCHEMA_MISMATCH"), IssueSeverity.Error,
                    "Bank of Canada yield-curve CSV is missing the Date column or the ZCnnnYR term columns."));
            }
            else
            {
                // Select by business date, independently of response ordering, within the
                // requested lookup window. A later provider row must not become look-ahead data.
                for (var i = 1; i < lines.Length; i++)
                {
                    var (date, rowQuotes) = ParseRow(lines[i], tenorByColumn);
                    if (date is null || rowQuotes.Count == 0 || date < from || date > request.AsOfDate
                        || (observationDate is not null && date <= observationDate))
                    {
                        continue;
                    }

                    observationDate = date;
                    quotes.Clear();
                    quotes.AddRange(rowQuotes);
                }

                if (observationDate is null)
                {
                    issues.Add(new DataIssue(new IssueCode("EMPTY_OBSERVATIONS"), IssueSeverity.Error,
                        $"Bank of Canada published no zero-coupon curve between {from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} and {request.AsOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}."));
                }
                else if (observationDate != request.AsOfDate)
                {
                    issues.Add(new DataIssue(new IssueCode("DATE_ROLLBACK"), IssueSeverity.Warning,
                        $"{request.CurveId}: no data for {request.AsOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}; using {observationDate:yyyy-MM-dd}."));
                }
            }

            _logger.LogInformation(
                "Fetched {Count} yield curve quotes for {CurveId} from Bank of Canada",
                quotes.Count,
                request.CurveId);

            var returnedPoints = quotes.Count > 0 ? 1 : 0;
            var coverage = new DataCoverage(
                RequestedPoints: 1,
                ReturnedPoints: returnedPoints,
                MissingPoints: 1 - returnedPoints,
                CoverageRatio: returnedPoints);

            var provenance = new DataProvenance(
                Provider: new ProviderCode("bankofcanada"),
                Dataset: request.CurveId.Value,
                LicenseFlag: LicenseType.Free,
                RetrievalMode: RetrievalMode.Api,
                Freshness: FreshnessClass.Delayed,
                RetrievedAtUtc: _clock.UtcNow,
                SourceUrl: url,
                DataDate: observationDate);

            return new DataEnvelope<IReadOnlyList<YieldCurveQuote>>(
                Payload: quotes,
                Coverage: coverage,
                Issues: issues,
                Provenance: [provenance]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch yield curve data for {CurveId} from Bank of Canada", request.CurveId);

            return new DataEnvelope<IReadOnlyList<YieldCurveQuote>>(
                Payload: Array.Empty<YieldCurveQuote>(),
                Coverage: new DataCoverage(
                    RequestedPoints: 1,
                    ReturnedPoints: 0,
                    MissingPoints: 1,
                    CoverageRatio: 0m),
                Issues: [new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Error, "Bank of Canada yield-curve fetch failed.")],
                Provenance: [new DataProvenance(
                    Provider: new ProviderCode("bankofcanada"),
                    Dataset: request.CurveId.Value,
                    LicenseFlag: LicenseType.Free,
                    RetrievalMode: RetrievalMode.Api,
                    Freshness: FreshnessClass.Unknown,
                    RetrievedAtUtc: _clock.UtcNow,
                    SourceUrl: url)]);
        }
    }

    /// <summary>
    /// Maps each column index to its tenor label, or returns <see langword="null"/> when the
    /// header is not <c>Date</c> followed by at least one <c>ZCnnnYR</c> column. A trailing
    /// empty cell (the file ends every line with a comma) is ignored.
    /// </summary>
    private static Dictionary<int, string>? ParseHeader(string headerLine)
    {
        var cells = headerLine.Split(',', StringSplitOptions.TrimEntries);
        if (cells.Length < 2 || !string.Equals(cells[0], "Date", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tenorByColumn = new Dictionary<int, string>();
        for (var i = 1; i < cells.Length; i++)
        {
            if (TryParseTermColumn(cells[i], out var tenor))
            {
                tenorByColumn[i] = tenor;
            }
        }

        return tenorByColumn.Count > 0 ? tenorByColumn : null;
    }

    /// <summary>
    /// Converts a <c>ZCnnnYR</c> column name (term in hundredths of a year) to a tenor label:
    /// whole years as <c>{n}Y</c>, anything else as <c>{n}M</c>.
    /// </summary>
    private static bool TryParseTermColumn(string column, out string tenor)
    {
        tenor = string.Empty;
        if (!column.StartsWith(ZeroCouponColumnPrefix, StringComparison.OrdinalIgnoreCase) ||
            !column.EndsWith(ZeroCouponColumnSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = column.AsSpan(ZeroCouponColumnPrefix.Length, column.Length - ZeroCouponColumnPrefix.Length - ZeroCouponColumnSuffix.Length);
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var hundredthsOfYear) ||
            hundredthsOfYear <= 0 ||
            (long)hundredthsOfYear * 12 % 100 != 0)
        {
            return false;
        }

        var months = (long)hundredthsOfYear * 12 / 100;
        tenor = months % 12 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{months / 12}Y")
            : string.Create(CultureInfo.InvariantCulture, $"{months}M");
        return true;
    }

    /// <summary>
    /// Parses one data row into its date and the quotes for every term column that holds a
    /// numeric value. <c>na</c> cells (and anything else that is not a decimal) are skipped.
    /// </summary>
    private static (DateOnly? Date, List<YieldCurveQuote> Quotes) ParseRow(string line, Dictionary<int, string> tenorByColumn)
    {
        var quotes = new List<YieldCurveQuote>();
        var cells = line.Split(',', StringSplitOptions.TrimEntries);

        if (cells.Length == 0 ||
            !DateOnly.TryParseExact(cells[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return (null, quotes);
        }

        foreach (var (column, tenor) in tenorByColumn)
        {
            if (column < cells.Length &&
                decimal.TryParse(cells[column], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
            {
                quotes.Add(new YieldCurveQuote(date, tenor, rate));
            }
        }

        return (date, quotes);
    }
}
