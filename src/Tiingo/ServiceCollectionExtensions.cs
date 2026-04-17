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

using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Calendars.Holidays;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Boutquin.MarketData.Adapter.Tiingo;

/// <summary>
/// Extension methods for registering the Tiingo adapter in a dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Tiingo daily price bar adapter and configures <see cref="TiingoOptions"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">A delegate to configure <see cref="TiingoOptions"/>.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// The adapter is registered via <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable(IServiceCollection, ServiceDescriptor)"/>
    /// so that multiple adapters for the same contract can coexist in the fallback chain.
    /// Call <c>AddMarketDataKernel</c> before this method to ensure shared kernel services
    /// (transport, clock, raw document store) are available.
    /// </remarks>
    public static IServiceCollection AddMarketDataTiingo(
        this IServiceCollection services,
        Action<TiingoOptions> configure)
    {
        services.Configure(configure);
        services.TryAddKeyedSingleton<IBusinessCalendar>("tiingo", (_, _) => HolidayCalendarFactory.Create("USNY"));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDataSourceAdapter<PriceHistoryRequest, Bar>, TiingoPriceBarAdapter>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDataSourceAdapter<InstrumentMetadataRequest, InstrumentMetadata>, TiingoInstrumentMetadataAdapter>());
        return services;
    }
}
