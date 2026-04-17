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
using System.Xml.Linq;

using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Transport.Http;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boutquin.MarketData.Adapter.UsTreasury;

/// <summary>
/// Fetches US Treasury par yield curve data from the Treasury Department's XML feed
/// and returns canonical <see cref="YieldCurveQuote"/> records with provenance metadata.
/// </summary>
/// <remarks>
/// <para>
/// The adapter calls the Treasury daily yield curve XML endpoint, which returns an RSS-like
/// feed containing entries with OData-style properties. Each entry includes a date
/// (<c>d:NEW_DATE</c>) and rate fields such as <c>d:BC_1MONTH</c>, <c>d:BC_10YEAR</c>, etc.
/// The API returns data for an entire year; the adapter filters to the closest date on or
/// before the requested <see cref="YieldCurveQuoteRequest.AsOfDate"/>.
/// </para>
/// <para>
/// Rates are published as percentages and are converted to decimal form (divided by 100)
/// before being returned. Empty rate fields (e.g., tenors not published on certain dates)
/// are silently skipped.
/// </para>
/// </remarks>
public sealed class UsTreasuryYieldAdapter : IDataSourceAdapter<YieldCurveQuoteRequest, YieldCurveQuote>, IPrioritizedAdapter
{
    private static readonly XNamespace s_atomNs = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace s_metadataNs = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";
    private static readonly XNamespace s_dataNs = "http://schemas.microsoft.com/ado/2007/08/dataservices";

    /// <summary>
    /// Maps Treasury XML field names to canonical tenor labels.
    /// </summary>
    private static readonly Dictionary<string, string> s_tenorMap = new(StringComparer.Ordinal)
    {
        ["BC_1MONTH"] = "1M",
        ["BC_2MONTH"] = "2M",
        ["BC_3MONTH"] = "3M",
        ["BC_4MONTH"] = "4M",
        ["BC_6MONTH"] = "6M",
        ["BC_1YEAR"] = "1Y",
        ["BC_2YEAR"] = "2Y",
        ["BC_3YEAR"] = "3Y",
        ["BC_5YEAR"] = "5Y",
        ["BC_7YEAR"] = "7Y",
        ["BC_10YEAR"] = "10Y",
        ["BC_20YEAR"] = "20Y",
        ["BC_30YEAR"] = "30Y",
    };

    private readonly IHttpDataTransport _transport;
    private readonly IRawDocumentStore _rawStore;
    private readonly IClock _clock;
    private readonly IBusinessCalendar _calendar;
    private readonly UsTreasuryOptions _options;
    private readonly ILogger<UsTreasuryYieldAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UsTreasuryYieldAdapter"/> class.
    /// </summary>
    /// <param name="transport">The HTTP transport used to fetch data from the Treasury XML feed.</param>
    /// <param name="rawStore">The raw document store for persisting API responses before parsing.</param>
    /// <param name="clock">The clock abstraction for timestamping provenance records.</param>
    /// <param name="calendar">Business calendar for computing calendar-aware coverage.</param>
    /// <param name="options">Configuration options for the US Treasury adapter.</param>
    /// <param name="logger">The logger instance for diagnostic output.</param>
    public UsTreasuryYieldAdapter(
        IHttpDataTransport transport,
        IRawDocumentStore rawStore,
        IClock clock,
        [FromKeyedServices("us-treasury")] IBusinessCalendar calendar,
        IOptions<UsTreasuryOptions> options,
        ILogger<UsTreasuryYieldAdapter> logger)
    {
        _transport = transport;
        _rawStore = rawStore;
        _clock = clock;
        _calendar = calendar;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public ProviderCode ProviderKey => new("us-treasury");

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanHandle(YieldCurveQuoteRequest request) => true;

    /// <inheritdoc />
    public async Task<DataEnvelope<IReadOnlyList<YieldCurveQuote>>> FetchAsync(
        YieldCurveQuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_options.BaseUrl}?data=daily_treasury_yield_curve&field_tdr_date_value={request.AsOfDate.Year}";

        try
        {
            var uri = new Uri(url);
            var stream = await _transport.GetAsync(uri, null, cancellationToken).ConfigureAwait(false);

            string xml;
            using (var reader = new StreamReader(stream))
            {
                xml = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            await _rawStore.SaveAsync(
                $"us-treasury/yield-curve/{request.CurveId}/{request.AsOfDate:yyyyMMdd}",
                xml,
                cancellationToken).ConfigureAwait(false);

            var issues = new List<DataIssue>();
            var quotes = new List<YieldCurveQuote>();

            var doc = XDocument.Parse(xml);

            // Navigate: feed/entry/content/m:properties
            var entries = doc.Descendants(s_atomNs + "entry");

            // Parse all entries and find the closest date <= AsOfDate.
            XElement? bestProperties = null;
            DateOnly bestDate = DateOnly.MinValue;

            foreach (var entry in entries)
            {
                var content = entry.Element(s_atomNs + "content");
                var properties = content?.Element(s_metadataNs + "properties");
                if (properties is null)
                {
                    continue;
                }

                var dateElement = properties.Element(s_dataNs + "NEW_DATE");
                if (dateElement is null)
                {
                    continue;
                }

                var dateText = dateElement.Value.Trim();
                if (!DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDateTime))
                {
                    continue;
                }

                var entryDate = DateOnly.FromDateTime(parsedDateTime);
                if (entryDate <= request.AsOfDate && entryDate > bestDate)
                {
                    bestDate = entryDate;
                    bestProperties = properties;
                }
            }

            if (bestProperties is null)
            {
                issues.Add(new DataIssue(new IssueCode("NO_MATCHING_DATE"), IssueSeverity.Error,
                    $"No Treasury yield curve entry found on or before {request.AsOfDate:yyyy-MM-dd}."));
            }
            else
            {
                if (bestDate != request.AsOfDate)
                {
                    issues.Add(new DataIssue(new IssueCode("DATE_ROLLBACK"), IssueSeverity.Warning,
                        $"{request.CurveId}: no data for {request.AsOfDate:yyyy-MM-dd}; using {bestDate:yyyy-MM-dd}."));
                }
                foreach (var (fieldName, tenor) in s_tenorMap)
                {
                    var element = bestProperties.Element(s_dataNs + fieldName);
                    if (element is null)
                    {
                        continue;
                    }

                    var rateText = element.Value.Trim();
                    if (string.IsNullOrEmpty(rateText))
                    {
                        continue;
                    }

                    if (!decimal.TryParse(rateText, NumberStyles.Number, CultureInfo.InvariantCulture, out var ratePercent))
                    {
                        continue;
                    }

                    // Convert from percentage to decimal form (e.g., 4.5 -> 0.045).
                    var rate = ratePercent / 100m;
                    quotes.Add(new YieldCurveQuote(tenor, rate));
                }
            }

            _logger.LogInformation(
                "Fetched {Count} US Treasury par yield quotes for {AsOfDate}",
                quotes.Count,
                request.AsOfDate);

            if (quotes.Count == 0 && issues.Count == 0)
            {
                issues.Add(new DataIssue(new IssueCode("NO_QUOTES"), IssueSeverity.Error,
                    $"No parseable yield curve quotes found for {request.CurveId}."));
            }

            var returnedPoints = quotes.Count > 0 ? 1 : 0;
            var coverage = new DataCoverage(
                RequestedPoints: 1,
                ReturnedPoints: returnedPoints,
                MissingPoints: 1 - returnedPoints,
                CoverageRatio: returnedPoints);

            var provenance = new DataProvenance(
                Provider: new ProviderCode("us-treasury"),
                Dataset: request.CurveId.Value,
                LicenseFlag: LicenseType.Free,
                RetrievalMode: RetrievalMode.Api,
                Freshness: FreshnessClass.EndOfDay,
                RetrievedAtUtc: _clock.UtcNow,
                SourceUrl: url,
                DataDate: bestDate != DateOnly.MinValue ? bestDate : null);

            return new DataEnvelope<IReadOnlyList<YieldCurveQuote>>(
                Payload: quotes,
                Coverage: coverage,
                Issues: issues,
                Provenance: [provenance]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch yield curve data for {CurveId} from US Treasury", request.CurveId);

            return new DataEnvelope<IReadOnlyList<YieldCurveQuote>>(
                Payload: Array.Empty<YieldCurveQuote>(),
                Coverage: new DataCoverage(
                    RequestedPoints: 1,
                    ReturnedPoints: 0,
                    MissingPoints: 1,
                    CoverageRatio: 0m),
                Issues: [new DataIssue(new IssueCode("FETCH_FAILED"), IssueSeverity.Error, $"US Treasury fetch failed: {ex.Message}")],
                Provenance: [new DataProvenance(
                    Provider: new ProviderCode("us-treasury"),
                    Dataset: request.CurveId.Value,
                    LicenseFlag: LicenseType.Free,
                    RetrievalMode: RetrievalMode.Api,
                    Freshness: FreshnessClass.Unknown,
                    RetrievedAtUtc: _clock.UtcNow,
                    SourceUrl: url)]);
        }
    }
}
