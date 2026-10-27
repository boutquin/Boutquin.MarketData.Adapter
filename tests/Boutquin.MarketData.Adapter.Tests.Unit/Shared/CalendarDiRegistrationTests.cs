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
using Boutquin.MarketData.Adapter.BankOfCanada;
using Boutquin.MarketData.Adapter.BankOfEngland;
using Boutquin.MarketData.Adapter.Cme;
using Boutquin.MarketData.Adapter.Ecb;
using Boutquin.MarketData.Adapter.FamaFrench;
using Boutquin.MarketData.Adapter.Frankfurter;
using Boutquin.MarketData.Adapter.Fred;
using Boutquin.MarketData.Adapter.NewYorkFed;
using Boutquin.MarketData.Adapter.Shared;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.Tiingo;
using Boutquin.MarketData.Adapter.TwelveData;
using Boutquin.MarketData.Adapter.UsTreasury;
using Boutquin.MarketData.Transport.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.Shared;

/// <summary>
/// AC #5 (feature-calendar-aware-coverage): each AddMarketData*() extension registers a
/// keyed <see cref="IBusinessCalendar"/> and the concrete adapter constructor receives a
/// non-null calendar when resolved from a <see cref="ServiceProvider"/>.
/// </summary>
public sealed class CalendarDiRegistrationTests
{
    [Theory]
    [InlineData("boe-sonia")]
    [InlineData("frankfurter")]
    [InlineData("bankofcanada")]
    [InlineData("boc-corra")]
    public void Default_table_calendars_retain_range_diagnostics_through_DI(string key)
    {
        var services = BaseServices();
        services.AddMarketDataBankOfEngland();
        services.AddMarketDataFrankfurter();
        services.AddMarketDataBankOfCanada();
        using var provider = services.BuildServiceProvider();
        var calendar = provider.GetRequiredKeyedService<IBusinessCalendar>(key);
        var range = new DateRange(new DateOnly(2031, 1, 6), new DateOnly(2031, 1, 6));
        var (_, issues) = AdapterCoverageHelper.Compute(calendar, range, DataFrequency.Daily, new HashSet<DateOnly> { range.From });
        issues.Should().ContainSingle(issue => issue.Code.Value == "CALENDAR_RANGE_EXCEEDED");
    }

    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IHttpDataTransport>(new FakeHttpDataTransport());
        services.AddSingleton<IRawDocumentStore>(new FakeRawDocumentStore());
        services.AddSingleton<IClock>(new FakeClock());
        return services;
    }

    [Fact]
    public void AddMarketDataBankOfEngland_registers_GBLO_calendar_and_resolves_adapter()
    {
        var services = BaseServices();
        services.AddMarketDataBankOfEngland();
        using var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IBusinessCalendar>("boe-sonia").Should().NotBeNull();
        var adapter = provider.GetServices<IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>>().Single();
        adapter.Should().BeOfType<BankOfEnglandSoniaAdapter>();
    }

    [Fact]
    public void AddMarketDataEcb_registers_TARGET_calendar_and_resolves_adapter()
    {
        var services = BaseServices();
        services.AddMarketDataEcb(_ => { });
        using var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IBusinessCalendar>("ecb-estr").Should().NotBeNull();
        provider.GetServices<IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>>()
            .Single().Should().BeOfType<EcbEstrAdapter>();
    }

    [Fact]
    public void AddMarketDataNewYorkFed_registers_USNY_calendar_and_resolves_adapter()
    {
        var services = BaseServices();
        services.AddMarketDataNewYorkFed(_ => { });
        using var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IBusinessCalendar>("nyfed-sofr").Should().NotBeNull();
        provider.GetServices<IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>>()
            .Single().Should().BeOfType<NewYorkFedSofrAdapter>();
    }

    [Fact]
    public void AddMarketDataFred_registers_USNY_calendar_and_resolves_adapter()
    {
        var services = BaseServices();
        services.AddMarketDataFred(o => o.ApiKey = "test-key");
        using var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IBusinessCalendar>("fred").Should().NotBeNull();
        provider.GetServices<IDataSourceAdapter<EconomicSeriesRequest, ScalarObservation>>()
            .Single().Should().BeOfType<FredEconomicSeriesAdapter>();
    }

    [Fact]
    public void AddMarketDataBankOfCanada_registers_CATO_calendars_and_resolves_three_adapters()
    {
        var services = BaseServices();
        services.AddMarketDataBankOfCanada(_ => { });
        using var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IBusinessCalendar>("bankofcanada").Should().NotBeNull();
        provider.GetKeyedService<IBusinessCalendar>("boc-corra").Should().NotBeNull();

        provider.GetServices<IDataSourceAdapter<YieldCurveQuoteRequest, YieldCurveQuote>>()
            .Single().Should().BeOfType<BankOfCanadaYieldCurveAdapter>();
        provider.GetServices<IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>>()
            .Single().Should().BeOfType<BankOfCanadaCorraAdapter>();
        provider.GetServices<IDataSourceAdapter<FxHistoryRequest, FxRate>>()
            .Single().Should().BeOfType<BankOfCanadaFxAdapter>();
    }

    [Fact]
    public void AddMarketDataUsTreasury_registers_USNY_calendar_and_resolves_adapter()
    {
        var services = BaseServices();
        services.AddMarketDataUsTreasury(_ => { });
        using var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IBusinessCalendar>("us-treasury").Should().NotBeNull();
        provider.GetServices<IDataSourceAdapter<YieldCurveQuoteRequest, YieldCurveQuote>>()
            .Single().Should().BeOfType<UsTreasuryYieldAdapter>();
    }

    [Fact]
    public void AddMarketDataTiingo_registers_USNY_calendar_and_resolves_adapter()
    {
        var services = BaseServices();
        services.AddMarketDataTiingo(o => o.ApiToken = "test-token");
        using var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IBusinessCalendar>("tiingo").Should().NotBeNull();
        provider.GetServices<IDataSourceAdapter<PriceHistoryRequest, Bar>>()
            .Single().Should().BeOfType<TiingoPriceBarAdapter>();
    }

    [Fact]
    public void AddMarketDataTwelveData_registers_USNY_calendar_and_resolves_adapter()
    {
        var services = BaseServices();
        services.AddMarketDataTwelveData(o => o.ApiKey = "test-key");
        using var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IBusinessCalendar>("twelvedata").Should().NotBeNull();
        provider.GetServices<IDataSourceAdapter<PriceHistoryRequest, Bar>>()
            .Single().Should().BeOfType<TwelveDataPriceBarAdapter>();
    }

    [Fact]
    public void AddMarketDataFrankfurter_registers_TARGET_calendar_and_resolves_adapter()
    {
        var services = BaseServices();
        services.AddMarketDataFrankfurter(_ => { });
        using var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IBusinessCalendar>("frankfurter").Should().NotBeNull();
        provider.GetServices<IDataSourceAdapter<FxHistoryRequest, FxRate>>()
            .Single().Should().BeOfType<FrankfurterFxAdapter>();
    }

    [Fact]
    public void AddMarketDataFamaFrench_registers_calendar_and_resolves_adapter()
    {
        var services = BaseServices();
        services.AddMarketDataFamaFrench(_ => { });
        using var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IBusinessCalendar>("fama-french").Should().NotBeNull();
        provider.GetServices<IDataSourceAdapter<FactorSeriesRequest, FactorObservation>>()
            .Single().Should().BeOfType<FamaFrenchFactorAdapter>();
    }

    [Fact]
    public void AddMarketDataCme_registers_XCME_calendar_and_resolves_adapter()
    {
        var services = BaseServices();
        services.AddMarketDataCme(o => o.SettlementDirectory = Path.GetTempPath());
        using var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IBusinessCalendar>("cme-eod").Should().NotBeNull();
        provider.GetServices<IDataSourceAdapter<FuturesSettlementRequest, FuturesSettlement>>()
            .Single().Should().BeOfType<CmeSettlementAdapter>();
    }

    [Fact]
    public void All_adapters_registered_together_resolve_each_to_non_null_calendar()
    {
        // Multiple adapters sharing IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>
        // must coexist behind the same DI container — this is the realistic deployment shape.
        var services = BaseServices();
        services.AddMarketDataBankOfEngland(_ => { });
        services.AddMarketDataEcb(_ => { });
        services.AddMarketDataNewYorkFed(_ => { });
        services.AddMarketDataFred(o => o.ApiKey = "test");
        services.AddMarketDataBankOfCanada(_ => { });
        services.AddMarketDataUsTreasury(_ => { });
        services.AddMarketDataTiingo(o => o.ApiToken = "test");
        services.AddMarketDataTwelveData(o => o.ApiKey = "test");
        services.AddMarketDataFrankfurter(_ => { });
        services.AddMarketDataFamaFrench(_ => { });
        services.AddMarketDataCme(o => o.SettlementDirectory = Path.GetTempPath());
        using var provider = services.BuildServiceProvider();

        // Every fixing adapter (4 of them) coexists behind one interface registration.
        var fixingAdapters = provider
            .GetServices<IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>>()
            .ToList();
        fixingAdapters.Should().HaveCount(4);
        fixingAdapters.Select(a => a.GetType().Name).Should().BeEquivalentTo(
            new[]
            {
                nameof(BankOfEnglandSoniaAdapter),
                nameof(EcbEstrAdapter),
                nameof(NewYorkFedSofrAdapter),
                nameof(BankOfCanadaCorraAdapter),
            });

        // Yield curve adapters (UsTreasury, BoC) coexist.
        var yieldAdapters = provider
            .GetServices<IDataSourceAdapter<YieldCurveQuoteRequest, YieldCurveQuote>>()
            .ToList();
        yieldAdapters.Should().HaveCount(2);

        // Price bar adapters (Tiingo, TwelveData) coexist.
        var priceAdapters = provider
            .GetServices<IDataSourceAdapter<PriceHistoryRequest, Bar>>()
            .ToList();
        priceAdapters.Should().HaveCount(2);

        // Per-adapter keyed calendars are all present.
        var keys = new[]
        {
            "boe-sonia", "ecb-estr", "nyfed-sofr", "fred",
            "bankofcanada", "boc-corra", "us-treasury",
            "tiingo", "twelvedata", "frankfurter", "fama-french", "cme-eod",
        };
        foreach (var key in keys)
        {
            provider.GetKeyedService<IBusinessCalendar>(key).Should()
                .NotBeNull($"keyed calendar '{key}' must be registered");
        }
    }
}
