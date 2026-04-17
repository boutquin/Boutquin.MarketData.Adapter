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
/// Configuration options for the Bank of Canada Valet API adapter.
/// </summary>
/// <remarks>
/// Bind this class to a configuration section (e.g., "BankOfCanada") or configure inline
/// via <see cref="ServiceCollectionExtensions.AddMarketDataBankOfCanada"/>. All properties
/// have sensible defaults for fetching the Canadian zero-coupon yield curve.
/// </remarks>
public sealed class BankOfCanadaOptions
{
    /// <summary>
    /// Base URL for the Bank of Canada Valet API.
    /// </summary>
    public string BaseUrl { get; set; } = "https://www.bankofcanada.ca/valet";

    /// <summary>
    /// The observation group name identifying the yield curve series (e.g., "BD.CDN.ZERO").
    /// </summary>
    public string GroupName { get; set; } = "BD.CDN.ZERO";

    /// <summary>
    /// The currency code for the yield curve (e.g., "CAD").
    /// </summary>
    public string Currency { get; set; } = "CAD";

    /// <summary>
    /// The observation group name identifying the CORRA series (e.g., "CORRA_GRAPH").
    /// </summary>
    public string CorraGroupName { get; set; } = "CORRA_GRAPH";
}
