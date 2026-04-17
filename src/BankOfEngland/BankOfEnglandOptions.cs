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

namespace Boutquin.MarketData.Adapter.BankOfEngland;

/// <summary>
/// Configuration options for the Bank of England SONIA adapter.
/// </summary>
/// <remarks>
/// Bind this class to a configuration section (e.g., "BankOfEngland") or configure inline
/// via <see cref="ServiceCollectionExtensions.AddMarketDataBankOfEngland"/>. All properties
/// have sensible defaults for fetching SONIA overnight fixing rates.
/// </remarks>
public sealed class BankOfEnglandOptions
{
    /// <summary>
    /// Base URL for the Bank of England Interactive Analytical Database (IADB).
    /// </summary>
    public string BaseUrl { get; set; } = "https://www.bankofengland.co.uk/boeapps/database";

    /// <summary>
    /// The IADB series code for the SONIA overnight rate.
    /// </summary>
    public string SoniaSeriesCode { get; set; } = "IUDSNKY";
}
