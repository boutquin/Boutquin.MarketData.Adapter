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

using System.Globalization;

using Boutquin.Domain.Enumerations;
using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.BankOfCanada;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Transport.Http;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.BankOfCanada;

public sealed class BankOfCanadaPublicationContractTests
{
    // Independent weekday nonpublication dates from the official CORRA AVG.INTWO and
    // FXUSDCAD histories, 2020-01-01..2025-12-31. Both histories contain the same 1,495 dates.
    // https://www.bankofcanada.ca/valet/observations/group/CORRA_GRAPH/json
    // https://www.bankofcanada.ca/valet/observations/FXUSDCAD/json
    private const string ObservedClosures = """
        2020-01-01,2020-02-17,2020-04-10,2020-05-18,2020-07-01,2020-08-03,2020-09-07,2020-10-12,2020-11-11,2020-12-25,2020-12-28,
        2021-01-01,2021-02-15,2021-04-02,2021-05-24,2021-07-01,2021-08-02,2021-09-06,2021-09-30,2021-10-11,2021-11-11,2021-12-27,2021-12-28,
        2022-01-03,2022-02-21,2022-04-15,2022-05-23,2022-07-01,2022-08-01,2022-09-05,2022-09-30,2022-10-10,2022-11-11,2022-12-26,2022-12-27,
        2023-01-02,2023-02-20,2023-04-07,2023-05-22,2023-07-03,2023-08-07,2023-09-04,2023-10-02,2023-10-09,2023-11-13,2023-12-25,2023-12-26,
        2024-01-01,2024-02-19,2024-03-29,2024-05-20,2024-07-01,2024-08-05,2024-09-02,2024-09-30,2024-10-14,2024-11-11,2024-12-25,2024-12-26,
        2025-01-01,2025-02-17,2025-04-18,2025-05-19,2025-07-01,2025-08-04,2025-09-01,2025-09-30,2025-10-13,2025-11-11,2025-12-25,2025-12-26
        """;

    private static ServiceProvider Build(FakeHttpDataTransport transport, Action<BankOfCanadaOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IHttpDataTransport>(transport);
        services.AddSingleton<IRawDocumentStore>(new FakeRawDocumentStore());
        services.AddSingleton<IClock>(new FakeClock());
        services.AddMarketDataBankOfCanada(configure);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("bankofcanada")]
    [InlineData("boc-corra")]
    public void Registered_calendars_match_every_observed_date_over_six_complete_years(string key)
    {
        using var provider = Build(new FakeHttpDataTransport());
        var calendar = provider.GetRequiredKeyedService<IBusinessCalendar>(key);
        var closures = ObservedClosures.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(date => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToHashSet();
        var from = new DateOnly(2020, 1, 1);
        var to = new DateOnly(2025, 12, 31);
        var dates = Enumerable.Range(from.DayNumber, to.DayNumber - from.DayNumber + 1).Select(DateOnly.FromDayNumber).ToArray();
        var expected = dates.Where(date => date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday && !closures.Contains(date)).ToHashSet();
        expected.Should().HaveCount(1495);
        dates.Where(date => calendar.IsBusinessDay(date) != expected.Contains(date)).Should().BeEmpty();
        calendar.Code.Should().Be("CAST");
    }

    [Theory]
    [InlineData("CORRA")]
    [InlineData("FX")]
    public async Task Default_registration_does_not_report_a_gap_on_Remembrance_Day(string dataset)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("bankofcanada", "{\"observations\":[]}");
        using var provider = Build(transport);
        var range = new DateRange(new DateOnly(2024, 11, 11), new DateOnly(2024, 11, 11));
        DataCoverage coverage;
        IReadOnlyList<DataIssue> issues;
        if (dataset == "CORRA")
        {
            var adapter = provider.GetServices<IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>>().Single();
            var result = await adapter.FetchAsync(new OvernightFixingRequest(new BenchmarkName("CORRA"), range));
            coverage = result.Coverage;
            issues = result.Issues;
        }
        else
        {
            var adapter = provider.GetServices<IDataSourceAdapter<FxHistoryRequest, FxRate>>().Single();
            var result = await adapter.FetchAsync(new FxHistoryRequest([new FxPair(Currency.USD, Currency.CAD)], range));
            coverage = result.Coverage;
            issues = result.Issues;
        }
        coverage.RequestedPoints.Should().Be(0);
        coverage.CoverageRatio.Should().Be(1m);
        issues.Should().BeEmpty();
    }

    [Fact]
    public async Task Corra_default_registration_parses_the_actual_primary_rate_series()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("CORRA_GRAPH", """
            {"observations":[
              {"d":"2024-01-02","AVG.INTWO":{"v":"5.0500"},"CORRA_TRIMMED_VOLUME":{"v":"14295473503"}},
              {"d":"2024-01-03","AVG.INTWO":{"v":"5.0500"},"CORRA_TRIMMED_VOLUME":{"v":"14392833854"}},
              {"d":"2024-01-04","AVG.INTWO":{"v":"5.0400"},"CORRA_TRIMMED_VOLUME":{"v":"13314418117"}}]}
            """);
        using var provider = Build(transport);
        var adapter = provider.GetServices<IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>>().Single();
        var result = await adapter.FetchAsync(new OvernightFixingRequest(new BenchmarkName("CORRA"),
            new DateRange(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4))));
        result.Payload.Select(row => row.Value).Should().Equal(0.0505m, 0.0505m, 0.0504m);
        result.Coverage.CoverageRatio.Should().Be(1m);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task Corra_custom_group_and_explicit_series_are_independently_consumed()
    {
        var seriesProperty = typeof(BankOfCanadaOptions).GetProperty("CorraSeriesCode");
        seriesProperty.Should().NotBeNull("a group may carry several series, so group-derived identity is unsound");
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("CUSTOM_GROUP", """{"observations":[{"d":"2024-01-08","OTHER":{"v":"99"},"CUSTOM_RATE":{"v":"4.25"}}]}""");
        using var provider = Build(transport, options =>
        {
            options.CorraGroupName = "CUSTOM_GROUP";
            seriesProperty!.SetValue(options, "CUSTOM_RATE");
        });
        var adapter = provider.GetServices<IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>>().Single();
        var result = await adapter.FetchAsync(new OvernightFixingRequest(new BenchmarkName("CORRA"),
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 8))));
        result.Payload.Should().ContainSingle().Which.Value.Should().Be(0.0425m);
        transport.RequestedUris.Should().ContainSingle().Which.AbsolutePath.Should().Contain("CUSTOM_GROUP");
    }
}
