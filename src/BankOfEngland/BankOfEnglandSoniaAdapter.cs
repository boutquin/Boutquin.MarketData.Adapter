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
/// The adapter calls the IADB CSV export endpoint, which returns a header line followed by
/// rows of <c>"DD MMM YY",rate</c>. Rates are published as percentages (e.g., 5.19 for 5.19%)
/// and are converted to decimal form (divided by 100) before being returned. Lines with empty
/// or unparseable values are silently skipped, as the Bank of England omits fixings on
/// non-business days.
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

    /// <inheritdoc />
    public ProviderCode ProviderKey => new("boe-sonia");

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanHandle(OvernightFixingRequest request) =>
        string.Equals(request.BenchmarkId.Value, "SONIA", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<DataEnvelope<IReadOnlyList<ScalarObservation>>> FetchAsync(
        OvernightFixingRequest request,
        CancellationToken cancellationToken = default)
    {
        var fromDate = request.Range.From.ToString("dd/MMM/yyyy", CultureInfo.InvariantCulture);
        var toDate = request.Range.To.ToString("dd/MMM/yyyy", CultureInfo.InvariantCulture);
        var url = $"{_options.BaseUrl}/_iadb-fromshowcolumns.asp?csv.x=yes&SeriesCodes={_options.SoniaSeriesCode}&CSVF=TN&Datefrom={fromDate}&Dateto={toDate}";

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
                $"boe/sonia/{request.Range.From:yyyyMMdd}-{request.Range.To:yyyyMMdd}",
                csv,
                cancellationToken).ConfigureAwait(false);

            var issues = new List<DataIssue>();
            var observations = new List<ScalarObservation>();

            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // Skip the header line (e.g., "DATE,IUDSNKY").
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                var parts = line.Split(',');
                if (parts.Length < 2)
                {
                    continue;
                }

                // Date is quoted: "02 Jan 24"
                var dateStr = parts[0].Trim('"', ' ');
                var valueStr = parts[1].Trim('"', ' ');

                if (string.IsNullOrEmpty(valueStr) ||
                    !decimal.TryParse(valueStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var ratePercent))
                {
                    continue;
                }

                if (!DateOnly.TryParseExact(dateStr, "dd MMM yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
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

            if (observations.Count == 0 && issues.Count == 0)
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch SONIA fixings from Bank of England");

            return new DataEnvelope<IReadOnlyList<ScalarObservation>>(
                Payload: Array.Empty<ScalarObservation>(),
                Coverage: new DataCoverage(
                    RequestedPoints: 1,
                    ReturnedPoints: 0,
                    MissingPoints: 1,
                    CoverageRatio: 0m),
                Issues: [new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Error, $"Bank of England SONIA fetch failed: {ex.Message}")],
                Provenance: [new DataProvenance(
                    Provider: new ProviderCode("boe-sonia"),
                    Dataset: "SONIA",
                    LicenseFlag: LicenseType.Free,
                    RetrievalMode: RetrievalMode.Api,
                    Freshness: FreshnessClass.Unknown,
                    RetrievedAtUtc: _clock.UtcNow,
                    SourceUrl: url)]);
        }
    }
}
