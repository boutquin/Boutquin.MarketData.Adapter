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

namespace Boutquin.MarketData.Adapter.Tiingo;

/// <summary>
/// Configuration options for the Tiingo data adapter.
/// </summary>
/// <remarks>
/// Bind this class to a configuration section (e.g., "Tiingo") or configure inline
/// via <see cref="ServiceCollectionExtensions.AddMarketDataTiingo"/>. The
/// <see cref="ApiToken"/> is required for all Tiingo API calls; requests without
/// a valid token will receive HTTP 401.
/// </remarks>
public sealed class TiingoOptions
{
    /// <summary>
    /// Absolute HTTPS base URL for the Tiingo REST API. Fetches reject other schemes before sending credentials.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.tiingo.com";

    /// <summary>
    /// API authentication token issued by Tiingo.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    internal void ValidateEndpoint()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("Tiingo BaseUrl must be a clean absolute HTTPS endpoint without userinfo, query, or fragment because requests carry an API token.", nameof(BaseUrl));
        }
    }
}
