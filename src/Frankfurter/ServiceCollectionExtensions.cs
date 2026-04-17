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

namespace Boutquin.MarketData.Adapter.Frankfurter;

/// <summary>
/// Extension methods for registering the Frankfurter FX adapter in a dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="FrankfurterFxAdapter"/> as an <see cref="IDataSourceAdapter{TRequest, TRecord}"/>
    /// for <see cref="FxHistoryRequest"/> and <see cref="FxRate"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure <see cref="FrankfurterOptions"/>.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddMarketDataFrankfurter(
        this IServiceCollection services,
        Action<FrankfurterOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddKeyedSingleton<IBusinessCalendar>("frankfurter", (_, _) => HolidayCalendarFactory.Create("TARGET"));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDataSourceAdapter<FxHistoryRequest, FxRate>, FrankfurterFxAdapter>());

        return services;
    }
}
