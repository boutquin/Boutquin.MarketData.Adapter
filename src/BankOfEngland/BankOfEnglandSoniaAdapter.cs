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
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.Shared;
using Boutquin.MarketData.Transport.Http;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boutquin.MarketData.Adapter.BankOfEngland;

/// <summary>
/// Fetches SONIA (Sterling Overnight Index Average) fixing rates from the Bank of England
/// Interactive Analytical Database and returns canonical <see cref="ScalarObservation"/> records
/// with provenance metadata.
/// </summary>
/// <remarks>
/// <para>
/// The adapter calls the IADB CSV export
/// (<c>_iadb-fromshowcolumns.asp?csv.x=yes&amp;SeriesCodes={code}&amp;CSVF=TN&amp;UsingCodes=Y&amp;Datefrom=…&amp;Dateto=…</c>),
/// which answers with a <c>DATE,{code}</c> header followed by one <c>DD MMM YYYY,rate</c> row per
/// business day (CRLF line endings, no quoting). Non-business days are simply absent. Rates are
/// published as percentages (e.g., 5.1863 for 5.1863%) and are converted to decimal form
/// (divided by 100) before being returned; rows with an empty or unparseable value are skipped.
/// </para>
/// <para>
/// <c>UsingCodes=Y</c> is load-bearing: without it the IADB does not read <c>SeriesCodes</c> as
/// series codes and redirects to an HTML error page served as HTTP 200. The adapter therefore
/// checks that the body starts with the <c>DATE,</c> header and reports anything else as a
/// <c>SCHEMA_MISMATCH</c> error rather than as an empty series.
/// </para>
/// </remarks>
public sealed class BankOfEnglandSoniaAdapter : IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>, IPrioritizedAdapter
{
    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly BankOfEnglandOptions _options;
    private readonly ILogger<BankOfEnglandSoniaAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BankOfEnglandSoniaAdapter"/> class.
    /// </summary>
    /// <param name="transport">The HTTP transport used to fetch data from the IADB.</param>
    /// <param name="rawStore">The raw document store for persisting CSV responses before parsing.</param>
    /// <param name="clock">The clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">The business calendar for computing coverage against expected trading days.</param>
    /// <param name="options">Configuration options for the Bank of England adapter.</param>
    /// <param name="logger">The logger instance for diagnostic output.</param>
    public BankOfEnglandSoniaAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("boe-sonia")] IBusinessCalendar calendar,
        IOptions<BankOfEnglandOptions> options,
        ILogger<BankOfEnglandSoniaAdapter> logger)
    {
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The stable <c>boe-sonia</c> provider identity attached to this adapter's provenance.</summary>
    public ProviderCode ProviderKey => new("boe-sonia");

    /// <summary>The fallback rank, 100; adapters with higher ranks are attempted first.</summary>
    public int Priority => 100;

    /// <summary>Checks the request against this provider's supported routing constraints without performing I/O.</summary>
    /// <param name="request">Daily requests for the SONIA benchmark.</param>
    /// <returns><see langword="true"/> when the request matches the adapter's declared support; this does not guarantee provider availability.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public bool CanHandle(OvernightFixingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.Equals(request.BenchmarkId.Value, "SONIA", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Retrieves SONIA overnight fixings within the requested dates from Bank of England.</summary>
    /// <param name="request">Daily requests for the SONIA benchmark.</param>
    /// <param name="cancellationToken">Cancels transport, raw-payload persistence and parsing where those operations support cancellation.</param>
    /// <returns>A task yielding canonical records together with provider provenance, coverage and data-quality issues. Empty or partial responses retain their failure or coverage information.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="OperationCanceledException">The caller or a dependency cancels the operation.</exception>
    /// <remarks>Successful fetches include raw-payload persistence and normalization. Inspect the envelope's issues and coverage before using its records; a returned envelope alone does not certify complete data.</remarks>
    public async Task<DataEnvelope<IReadOnlyList<ScalarObservation>>> FetchAsync(
        OvernightFixingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanHandle(request))
        {
            throw new ArgumentException("The Bank of England SONIA adapter requires a SONIA request.", nameof(request));
        }

        var fromDate = request.Range.From.ToString("dd/MMM/yyyy", CultureInfo.InvariantCulture);
        var toDate = request.Range.To.ToString("dd/MMM/yyyy", CultureInfo.InvariantCulture);
        var url = $"{_options.BaseUrl.TrimEnd('/')}/_iadb-fromshowcolumns.asp?csv.x=yes&SeriesCodes={_options.SoniaSeriesCode}&CSVF=TN&UsingCodes=Y&Datefrom={fromDate}&Dateto={toDate}";

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
                $"boe/sonia/{request.Range.From.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{request.Range.To.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}",
                csv,
                cancellationToken).ConfigureAwait(false);

            var issues = new List<DataIssue>();
            var observations = new List<ScalarObservation>();

            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // The IADB serves its error page (bad series code, missing UsingCodes=Y) as HTTP 200
            // HTML, so a non-CSV body must be reported as a schema failure, not as an empty series.
            if (lines.Length == 0 ||
                !string.Equals(lines[0], $"DATE,{_options.SoniaSeriesCode}", StringComparison.OrdinalIgnoreCase))
            {
                return BuildErrorEnvelope(
                    "SCHEMA_MISMATCH",
                    "Bank of England response is not the IADB CSV export (expected a DATE,{series} header).",
                    url);
            }

            // Skip the header line ("DATE,IUDSOIA").
            for (var i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Split(',');
                if (parts.Length < 2)
                {
                    continue;
                }

                // Rows are unquoted: 02 Jan 2024,5.1863
                var dateStr = parts[0].Trim('"', ' ');
                var valueStr = parts[1].Trim('"', ' ');

                if (string.IsNullOrEmpty(valueStr) ||
                    !decimal.TryParse(valueStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var ratePercent))
                {
                    continue;
                }

                if (!DateOnly.TryParseExact(dateStr, "dd MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    continue;
                }

                if (date < request.Range.From || date > request.Range.To)
                {
                    continue;
                }

                // Convert from percentage to decimal form (e.g., 5.19 -> 0.0519).
                var rate = ratePercent / 100m;
                observations.Add(new ScalarObservation(date, rate, "decimal-rate"));
            }

            _logger.LogInformation(
                "Fetched {Count} SONIA fixings from Bank of England",
                observations.Count);

            if (observations.Count == 0 && issues.Count == 0
                && CoverageComputation.CountExpectedPoints(_calendar, request.Range, request.Frequency) > 0)
            {
                issues.Add(new DataIssue(new IssueCode("NO_OBSERVATIONS"), IssueSeverity.Error,
                    "No parseable SONIA fixings found in the Bank of England response."));
            }

            var returnedDates = observations.Select(o => o.Date).ToHashSet();
            var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
                _calendar, request.Range, request.Frequency, returnedDates);
            issues.AddRange(gapIssues);

            var provenance = new DataProvenance(
                Provider: new ProviderCode("boe-sonia"),
                Dataset: "SONIA",
                LicenseFlag: LicenseType.Free,
                RetrievalMode: RetrievalMode.Api,
                Freshness: FreshnessClass.EndOfDay,
                RetrievedAtUtc: _clock.UtcNow,
                SourceUrl: url,
                DataDate: observations.Count > 0 ? observations.Max(o => o.Date) : null);

            return new DataEnvelope<IReadOnlyList<ScalarObservation>>(
                Payload: observations,
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
            _logger.LogError(ex, "Failed to fetch SONIA fixings from Bank of England");

            return BuildErrorEnvelope("FETCH_FAILED", "Bank of England SONIA fetch failed.", url);
        }
    }

    private DataEnvelope<IReadOnlyList<ScalarObservation>> BuildErrorEnvelope(string code, string message, string url) =>
        new(
            Payload: Array.Empty<ScalarObservation>(),
            Coverage: new DataCoverage(
                RequestedPoints: 1,
                ReturnedPoints: 0,
                MissingPoints: 1,
                CoverageRatio: 0m),
            Issues: [new DataIssue(new IssueCode(code), IssueSeverity.Error, message)],
            Provenance: [new DataProvenance(
                Provider: new ProviderCode("boe-sonia"),
                Dataset: "SONIA",
                LicenseFlag: LicenseType.Free,
                RetrievalMode: RetrievalMode.Api,
                Freshness: FreshnessClass.Unknown,
                RetrievedAtUtc: _clock.UtcNow,
                SourceUrl: url)]);
}
