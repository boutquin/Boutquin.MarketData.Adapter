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

namespace Boutquin.MarketData.Adapter.UsTreasury;

/// <summary>
/// Configuration options for the US Treasury yield curve adapter.
/// </summary>
/// <remarks>
/// Bind this class to a configuration section (e.g., "UsTreasury") or configure inline
/// via <see cref="ServiceCollectionExtensions.AddMarketDataUsTreasury"/>. The default
/// <see cref="BaseUrl"/> points to the Treasury XML feed for daily par yield curve rates.
/// </remarks>
public sealed class UsTreasuryOptions
{
    /// <summary>
    /// Base URL for the US Treasury interest rates XML feed.
    /// </summary>
    public string BaseUrl { get; set; } = "https://home.treasury.gov/resource-center/data-chart-center/interest-rates/pages/xml";
}
