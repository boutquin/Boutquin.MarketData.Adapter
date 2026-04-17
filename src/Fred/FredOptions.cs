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

namespace Boutquin.MarketData.Adapter.Fred;

/// <summary>
/// Configuration options for the FRED (Federal Reserve Economic Data) adapter.
/// </summary>
public sealed class FredOptions
{
    /// <summary>
    /// Base URL for the FRED API. Defaults to the official St. Louis Fed endpoint.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.stlouisfed.org";

    /// <summary>
    /// API key issued by FRED for authenticating requests.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// When <see langword="true"/>, divides percent-denominated values by 100 to produce decimal form
    /// (e.g., 5.25% becomes 0.0525). Defaults to <see langword="true"/>.
    /// </summary>
    public bool NormalizePercentToDecimal { get; set; } = true;
}
