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

namespace Boutquin.MarketData.Adapter.BankOfCanada;

/// <summary>
/// Configuration options for the Bank of Canada adapters.
/// </summary>
/// <remarks>
/// Bind this class to a configuration section (e.g., "BankOfCanada") or configure inline
/// via <see cref="ServiceCollectionExtensions.AddMarketDataBankOfCanada"/>. The FX and CORRA
/// adapters read <see cref="BaseUrl"/> (the Valet API); the zero-coupon yield-curve adapter
/// reads <see cref="YieldCurveLookupUrl"/>, because the Bank does not publish that curve
/// through Valet.
/// </remarks>
public sealed class BankOfCanadaOptions
{
    /// <summary>
    /// Base URL for the Bank of Canada Valet API.
    /// </summary>
    public string BaseUrl { get; set; } = "https://www.bankofcanada.ca/valet";

    /// <summary>
    /// URL of the Bank of Canada's zero-coupon yield-curve CSV lookup — the download behind
    /// the "Yield curves for zero-coupon bonds" page. Defaults to
    /// <c>https://www.bankofcanada.ca/stats/results/csv</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="BankOfCanadaYieldCurveAdapter"/> appends
    /// <c>?lookupPage=lookup_yield_curve.php&amp;dFrom={from}&amp;dTo={to}</c> to this value.
    /// The response is a CSV with a <c>Date</c> column followed by 120 <c>ZCnnnYR</c> columns
    /// (terms from 0.25 to 30 years in 0.25-year steps), rates already in decimal form, and
    /// <c>na</c> for dates with no curve. The Bank refreshes the file weekly with a lag of
    /// about two weeks. This endpoint is not part of the documented Valet API.
    /// </remarks>
    public string YieldCurveLookupUrl { get; set; } = "https://www.bankofcanada.ca/stats/results/csv";

    /// <summary>
    /// No longer used. Earlier versions fetched the yield curve from a Valet observation group
    /// of this name, but Valet has no zero-coupon yield-curve group (the default,
    /// <c>BD.CDN.ZERO</c>, answers HTTP 404), so the adapter now reads the Bank's CSV lookup
    /// at <see cref="YieldCurveLookupUrl"/> instead. Retained so existing configuration
    /// bindings keep compiling; the value has no effect.
    /// </summary>
    [Obsolete("Valet has no zero-coupon yield-curve group; the adapter reads YieldCurveLookupUrl instead. This property has no effect.")]
    public string GroupName { get; set; } = "BD.CDN.ZERO";

    /// <summary>
    /// The currency code of the supported zero-coupon curve. Only "CAD" is served;
    /// another value makes the yield-curve adapter ineligible.
    /// </summary>
    public string Currency { get; set; } = "CAD";

    /// <summary>
    /// The observation group name identifying the CORRA series (e.g., "CORRA_GRAPH").
    /// </summary>
    public string CorraGroupName { get; set; } = "CORRA_GRAPH";

    /// <summary>
    /// Valet series code containing the CORRA rate within the configured observation group.
    /// </summary>
    /// <value><c>AVG.INTWO</c>, the Bank of Canada's published CORRA rate series.</value>
    /// <remarks>
    /// A Valet group may contain both a percentage rate and unrelated trading-volume series.
    /// This explicit code selects the rate; it is independent of <see cref="CorraGroupName"/>.
    /// Configure both values when using a custom group. Empty codes are rejected before fetching.
    /// </remarks>
    public string CorraSeriesCode { get; set; } = "AVG.INTWO";
}
