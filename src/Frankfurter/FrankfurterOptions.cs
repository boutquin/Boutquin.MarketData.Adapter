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

namespace Boutquin.MarketData.Adapter.Frankfurter;

/// <summary>
/// Configuration options for the Frankfurter FX adapter.
/// </summary>
public sealed class FrankfurterOptions
{
    /// <summary>
    /// Base URL for the Frankfurter API, including the API version segment. Defaults to the
    /// public <c>v1</c> endpoint.
    /// </summary>
    /// <remarks>
    /// The adapter appends the time-series path (<c>/{from}..{to}?base=…&amp;symbols=…</c>) directly
    /// to this value, so an override must carry the version segment too. The service answers the
    /// unversioned host root with HTTP 404.
    /// </remarks>
    public string BaseUrl { get; set; } = "https://api.frankfurter.dev/v1";
}
