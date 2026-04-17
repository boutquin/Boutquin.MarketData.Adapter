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

namespace Boutquin.MarketData.Adapter.Ecb;

/// <summary>
/// Configuration options for the ECB (European Central Bank) data adapter.
/// </summary>
public sealed class EcbOptions
{
    /// <summary>
    /// Base URL for the ECB Data API. Defaults to the official ECB SDMX data service endpoint.
    /// </summary>
    public string BaseUrl { get; set; } = "https://data-api.ecb.europa.eu/service/data";

    /// <summary>
    /// The SDMX series key used to retrieve the Euro Short-Term Rate (EUR/STR) fixing data.
    /// </summary>
    public string EstrSeriesKey { get; set; } = "FM/B.U2.EUR.4F.KR.DFR.LEV";
}
