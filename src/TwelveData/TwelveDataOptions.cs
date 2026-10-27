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

namespace Boutquin.MarketData.Adapter.TwelveData;

/// <summary>
/// Configuration options for the Twelve Data adapter.
/// </summary>
/// <remarks>
/// Bind this class to a configuration section (e.g., "TwelveData") or configure inline
/// via <see cref="ServiceCollectionExtensions.AddMarketDataTwelveData"/>. The
/// <see cref="ApiKey"/> is required for all Twelve Data API calls; requests without
/// a valid key will receive HTTP 401.
/// </remarks>
public sealed class TwelveDataOptions
{
    /// <summary>
    /// Absolute HTTPS base URL for the Twelve Data REST API. Fetches reject other schemes before sending credentials.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.twelvedata.com";

    /// <summary>
    /// API authentication key issued by Twelve Data.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    internal void ValidateEndpoint()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("Twelve Data BaseUrl must be a clean absolute HTTPS endpoint without userinfo, query, or fragment because requests carry an API key.", nameof(BaseUrl));
        }
    }
}
