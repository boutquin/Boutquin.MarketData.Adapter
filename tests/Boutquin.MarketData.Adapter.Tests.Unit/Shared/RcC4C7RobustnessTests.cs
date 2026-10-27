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
using System.IO.Compression;
using System.Text;
using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.Cme;
using Boutquin.MarketData.Adapter.Ecb;
using Boutquin.MarketData.Adapter.FamaFrench;
using Boutquin.MarketData.Adapter.Fred;
using Boutquin.MarketData.Adapter.NewYorkFed;
using Boutquin.MarketData.Adapter.Shared;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.UsTreasury;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Transport.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.Shared;

public sealed class RcC4C7RobustnessTests
{
    private static readonly DateOnly s_day = new(2024, 1, 10);
    private static readonly DateRange s_range = new(s_day, s_day);
    private static readonly IBusinessCalendar s_calendar = new WeekendOnlyCalendar("TEST");

    [Theory]
    [InlineData("nyfed")]
    [InlineData("fred")]
    public async Task Range_contains_only_requested_dates_and_provenance(string provider)
    {
        var transport = new FakeHttpDataTransport();
        if (provider == "nyfed")
        {
            transport.RespondTo("sofr", """{"refRates":[{"effectiveDate":"2024-01-09","percentRate":5},{"effectiveDate":"2024-01-10","percentRate":5.1},{"effectiveDate":"2024-01-11","percentRate":5.2}]}""");
            var result = await NyFed(transport).FetchAsync(new OvernightFixingRequest(new BenchmarkName("SOFR"), s_range));
            result.Payload.Should().ContainSingle().Which.Date.Should().Be(s_day);
            result.Provenance[0].DataDate.Should().Be(s_day);
        }
        else
        {
            transport.RespondTo("fred", """{"observations":[{"date":"2024-01-09","value":"5"},{"date":"2024-01-10","value":"5.1"},{"date":"2024-01-11","value":"5.2"}]}""");
            var result = await Fred(transport).FetchAsync(new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), s_range));
            result.Payload.Should().ContainSingle().Which.Date.Should().Be(s_day);
            result.Provenance[0].DataDate.Should().Be(s_day);
        }
    }

    [Fact]
    public async Task Fred_provenance_never_contains_key_and_wire_dates_are_invariant()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", """{"observations":[{"date":"2024-01-10","value":"5.1"}]}""");
        var store = new FakeRawDocumentStore();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var result = await Fred(transport, store).FetchAsync(new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), s_range));
            transport.RequestedUris.Single().Query.Should().Contain("observation_start=2024-01-10");
            result.Provenance.Single().SourceUrl.Should().NotContain("rc-secret");
            store.SavedKeys.Single().Should().EndWith("20240110-20240110");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Fred_success_provenance_excludes_api_credentials()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", """{"observations":[{"date":"2024-01-10","value":"5.1"}]}""");
        var result = await Fred(transport).FetchAsync(new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), s_range));
        result.Provenance.Single().SourceUrl.Should().NotContain("rc-secret");
    }

    [Fact]
    public async Task Fred_refuses_plaintext_credential_transport()
    {
        var transport = new FakeHttpDataTransport();
        var adapter = new FredEconomicSeriesAdapter(transport, new FakeRawDocumentStore(), new FakeClock(), s_calendar,
            Options.Create(new FredOptions { BaseUrl = "http://provider.example", ApiKey = "rc-secret" }), NullLogger<FredEconomicSeriesAdapter>.Instance);
        Func<Task> act = () => adapter.FetchAsync(new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), s_range));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("FRED BaseUrl must be an HTTPS origin without credentials, query, or fragment.*");
        transport.RequestedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task Fred_diagnostics_exclude_credential_bearing_exception()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new IOException("https://provider.example?api_key=rc-secret"));
        var logger = new CapturingLogger<FredEconomicSeriesAdapter>();
        var adapter = new FredEconomicSeriesAdapter(transport, new FakeRawDocumentStore(), new FakeClock(), s_calendar,
            Options.Create(new FredOptions { ApiKey = "rc-secret" }), logger);
        var result = await adapter.FetchAsync(new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), s_range));
        result.Issues.Should().ContainSingle();
        logger.Messages.Should().ContainSingle().Which.Should().NotContain("rc-secret");
    }

    [Theory]
    [InlineData("{\"effectiveDate\":\"BAD\",\"percentRate\":5}")]
    [InlineData("{\"effectiveDate\":\"2024-01-10\"}")]
    public async Task NyFed_malformed_rows_are_diagnostic(string badRow)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("sofr", "{\"refRates\":[{\"effectiveDate\":\"2024-01-10\",\"percentRate\":5}," + badRow + "]}");
        var result = await NyFed(transport).FetchAsync(new OvernightFixingRequest(new BenchmarkName("SOFR"), s_range));
        result.Payload.Should().ContainSingle();
        result.Issues.Should().Contain(issue => issue.Code == new IssueCode("MALFORMED_ROW"));
    }

    [Fact]
    public async Task FamaFrench_stops_at_blank_section_and_disposes_transport_stream()
    {
        var stream = new TrackedStream(Zip(",Mkt-RF\n20240110,1\n\n20240110,99\n"));
        var result = await Fama(new StreamTransport(stream)).FetchAsync(new FactorSeriesRequest(new FactorDatasetId("factors"), s_range));
        result.Payload.Should().ContainSingle().Which.Value.Should().Be(0.01m);
        stream.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task FamaFrench_disposes_stream_on_corrupt_zip()
    {
        var stream = new TrackedStream(Encoding.UTF8.GetBytes("invalid zip"));
        var result = await Fama(new StreamTransport(stream)).FetchAsync(new FactorSeriesRequest(new FactorDatasetId("factors"), s_range));
        result.Issues.Should().Contain(issue => issue.Severity == IssueSeverity.Error);
        stream.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task FamaFrench_rejects_expansion_beyond_sixty_four_mebibytes()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondToWithBytes("zip", Zip(",Mkt-RF\n20240110,1\n\n" + new string('x', 64 * 1024 * 1024)));
        var store = new FakeRawDocumentStore();
        var adapter = new FamaFrenchFactorAdapter(transport, store, new FakeClock(), s_calendar,
            Options.Create(new FamaFrenchOptions()), NullLogger<FamaFrenchFactorAdapter>.Instance);
        var result = await adapter.FetchAsync(new FactorSeriesRequest(new FactorDatasetId("factors"), s_range));
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(issue => issue.Severity == IssueSeverity.Error);
        store.SavedKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Cme_does_not_open_out_of_range_files_or_external_symlinks()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        try
        {
            var outside = Path.Combine(root, "outside.txt");
            await File.WriteAllTextAsync(outside, "Month,Settle\nJUN 25,97");
            File.CreateSymbolicLink(Path.Combine(root, "20240110-SR3.csv"), outside);
            var directory = Path.Combine(root, "data");
            Directory.CreateDirectory(directory);
            File.CreateSymbolicLink(Path.Combine(directory, "20240110-SR3.csv"), outside);
            // An out-of-range dangling link must never be opened or persisted.
            File.CreateSymbolicLink(Path.Combine(directory, "20240109-SR3.csv"), Path.Combine(root, "missing"));
            var store = new FakeRawDocumentStore();
            var adapter = new CmeSettlementAdapter(store, new FakeClock(), s_calendar,
                Options.Create(new CmeOptions { SettlementDirectory = directory }), NullLogger<CmeSettlementAdapter>.Instance);
            var result = await adapter.FetchAsync(new FuturesSettlementRequest(new FuturesProductCode("SR3"), s_range));
            result.Payload.Should().BeEmpty();
            store.SavedKeys.Should().BeEmpty();
            result.Issues.Should().NotContain(issue => issue.Code == new IssueCode("FETCH_FAILED"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Invalid_requests_are_rejected_at_adapter_boundary()
    {
        var transport = new FakeHttpDataTransport();
        Action ecbNull = () => Ecb(transport).CanHandle(null!);
        ecbNull.Should().ThrowExactly<ArgumentNullException>();
        Action nyfedNull = () => NyFed(transport).CanHandle(null!);
        nyfedNull.Should().ThrowExactly<ArgumentNullException>();
        Func<Task> fredDefault = () => Fred(transport).FetchAsync(new EconomicSeriesRequest(default, s_range));
        await fredDefault.Should().ThrowAsync<ArgumentException>();
        Func<Task> famaUnsupported = () => Fama(transport).FetchAsync(new FactorSeriesRequest(new FactorDatasetId("factors"), s_range, DataFrequency.Intraday));
        await famaUnsupported.Should().ThrowAsync<ArgumentException>();
        var cme = new CmeSettlementAdapter(new FakeRawDocumentStore(), new FakeClock(), s_calendar,
            Options.Create(new CmeOptions()), NullLogger<CmeSettlementAdapter>.Instance);
        Action cmeDefault = () => cme.CanHandle(new FuturesSettlementRequest(default, s_range));
        cmeDefault.Should().Throw<ArgumentException>();
        transport.RequestedUris.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2019, 12, 31, 2020, 1, 1, true)]
    [InlineData(2030, 12, 31, 2031, 1, 1, true)]
    [InlineData(2024, 1, 10, 2024, 1, 10, false)]
    public void Explicitly_bounded_calendars_emit_one_range_warning(int fy, int fm, int fd, int ty, int tm, int td, bool expected)
    {
        // Reflection keeps this test executable against the pre-fix production assembly.
        var type = typeof(AdapterCoverageHelper).Assembly.GetType("Boutquin.MarketData.Adapter.Shared.AdapterCalendarFactory");
        type.Should().NotBeNull("built-in calendar bounds need explicit provenance metadata");
        var calendar = (IBusinessCalendar)type!.GetMethod("Create")!.Invoke(null, ["USSF"])!;
        var range = new DateRange(new DateOnly(fy, fm, fd), new DateOnly(ty, tm, td));
        var (_, issues) = AdapterCoverageHelper.Compute(calendar, range, DataFrequency.Daily, new HashSet<DateOnly>());
        issues.Count(issue => issue.Code == new IssueCode("CALENDAR_RANGE_EXCEEDED")).Should().Be(expected ? 1 : 0);
        var custom = new HolidayCalendar("USSF", []);
        GapClassifier.Classify(custom, range, new HashSet<DateOnly>()).Should().NotContain(issue => issue.Code == new IssueCode("CALENDAR_RANGE_EXCEEDED"));
        var algorithmic = AdapterCalendarFactory.Create("USNY");
        GapClassifier.Classify(algorithmic, range, new HashSet<DateOnly>()).Should().NotContain(issue => issue.Code == new IssueCode("CALENDAR_RANGE_EXCEEDED"));
    }

    [Theory]
    [InlineData("nyfed", "nyfed-sofr", "USSOFR")]
    [InlineData("treasury", "us-treasury", "USSF")]
    public void Publication_calendars_follow_the_source(string source, string key, string expectedCode)
    {
        var services = new ServiceCollection();
        if (source == "nyfed")
        {
            services.AddMarketDataNewYorkFed();
        }
        else
        {
            services.AddMarketDataUsTreasury();
        }
        using var provider = services.BuildServiceProvider();
        var calendar = provider.GetRequiredKeyedService<IBusinessCalendar>(key);
        calendar.Code.Should().Be(expectedCode);
        calendar.IsBusinessDay(new DateOnly(2024, 10, 14)).Should().BeFalse("publication calendars close on Columbus Day, while NYSE equities trade");
    }

    [Fact]
    public async Task Treasury_reports_malformed_tenor_and_preserves_valid_tenor()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("treasury", """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices">
            <entry><content><m:properties><d:NEW_DATE>2024-01-10T00:00:00</d:NEW_DATE><d:BC_1MONTH>5.5</d:BC_1MONTH><d:BC_3MONTH>bad</d:BC_3MONTH></m:properties></content></entry></feed>
            """);
        var result = await Treasury(transport).FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), s_day));
        result.Payload.Should().ContainSingle().Which.Rate.Should().Be(0.055m);
        result.Issues.Should().Contain(issue => issue.Code == new IssueCode("MALFORMED_ROW"));
    }

    [Fact]
    public async Task Treasury_newest_empty_curve_fails_closed_instead_of_silently_selecting_stale_curve()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("treasury", """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices">
            <entry><content><m:properties><d:NEW_DATE>2024-01-09T00:00:00</d:NEW_DATE><d:BC_1MONTH>5.5</d:BC_1MONTH></m:properties></content></entry>
            <entry><content><m:properties><d:NEW_DATE>2024-01-10T00:00:00</d:NEW_DATE><d:BC_1MONTH /></m:properties></content></entry></feed>
            """);
        var result = await Treasury(transport).FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), s_day));
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(issue => issue.Code == new IssueCode("NO_QUOTES") && issue.Severity == IssueSeverity.Error);
        transport.RequestedUris.Should().ContainSingle();
    }

    [Fact]
    public async Task FamaFrench_monthly_leap_day_and_raw_persistence_are_pinned()
    {
        var csv = ",Mkt-RF\n202402,1.5\n";
        var transport = new FakeHttpDataTransport();
        transport.RespondToWithBytes("zip", Zip(csv));
        var store = new FakeRawDocumentStore();
        var adapter = new FamaFrenchFactorAdapter(transport, store, new FakeClock(), s_calendar,
            Options.Create(new FamaFrenchOptions()), NullLogger<FamaFrenchFactorAdapter>.Instance);
        var result = await adapter.FetchAsync(new FactorSeriesRequest(new FactorDatasetId("monthly"),
            new DateRange(new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29)), DataFrequency.Monthly));
        result.Payload.Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 2, 29));
        result.Payload[0].Value.Should().Be(0.015m);
        store.Store.Should().ContainSingle().Which.Value.Should().Be(csv);
    }

    [Theory]
    [InlineData(DataFrequency.Weekly, "2024-01-08", "2024-01-14")]
    [InlineData(DataFrequency.Monthly, "2024-01-01", "2024-01-31")]
    public async Task Fred_accepts_native_nondaily_frequency_and_persists_raw_response(DataFrequency frequency, string from, string to)
    {
        const string json = """{"observations":[{"date":"2024-01-10","value":"5.1"},{"date":"2024-01-11","value":"."}]}""";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("fred", json);
        var store = new FakeRawDocumentStore();
        var result = await Fred(transport, store).FetchAsync(new EconomicSeriesRequest(new EconomicSeriesId("NATIVE"),
            new DateRange(DateOnly.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateOnly.ParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture)), frequency));
        result.Payload.Should().ContainSingle().Which.Value.Should().Be(5.1m);
        result.Issues.Should().Contain(issue => issue.Code == new IssueCode("MISSING_VALUES"));
        result.Coverage.RequestedPoints.Should().Be(1);
        store.Store.Should().ContainSingle().Which.Value.Should().Be(json);
    }

    [Fact]
    public async Task Test_transport_honors_cancellation_and_copies_fixture_bytes()
    {
        var transport = new FakeHttpDataTransport();
        byte[] fixture = [65];
        transport.RespondToWithBytes("example.test", fixture);
        fixture[0] = 66;
        using var response = await transport.GetAsync(new Uri("https://example.test"));
        response.ReadByte().Should().Be(65);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Func<Task> act = () => transport.GetAsync(new Uri("https://example.test"), cancellationToken: cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    public async Task Test_raw_store_observes_production_key_rejection(string key)
    {
        var store = new FakeRawDocumentStore();
        Func<Task> act = () => store.SaveAsync(key, "payload");
        await act.Should().ThrowAsync<ArgumentException>();
        store.SavedKeys.Should().BeEmpty();
    }

    [Fact]
    public void Range_notice_does_not_change_complete_coverage()
    {
        var date = new DateOnly(2031, 1, 2);
        var (coverage, issues) = AdapterCoverageHelper.Compute(AdapterCalendarFactory.Create("USSF"),
            new DateRange(date, date), DataFrequency.Daily, new HashSet<DateOnly> { date });
        coverage.CoverageRatio.Should().Be(1m);
        issues.Should().ContainSingle().Which.Severity.Should().Be(IssueSeverity.Warning);
    }

    [Theory]
    [InlineData("nyfed")]
    [InlineData("fama")]
    [InlineData("treasury")]
    [InlineData("cme")]
    public async Task Raw_key_dates_use_Gregorian_invariant_culture(string source)
    {
        var oldCulture = CultureInfo.CurrentCulture;
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var transport = new FakeHttpDataTransport();
            var store = new FakeRawDocumentStore();
            if (source == "nyfed")
            {
                transport.RespondTo("sofr", """{"refRates":[{"effectiveDate":"2024-01-10","percentRate":5.1}]}""");
                var adapter = new NewYorkFedSofrAdapter(transport, store, new FakeClock(), s_calendar, Options.Create(new NewYorkFedOptions()), NullLogger<NewYorkFedSofrAdapter>.Instance);
                await adapter.FetchAsync(new OvernightFixingRequest(new BenchmarkName("SOFR"), s_range));
            }
            else if (source == "fama")
            {
                transport.RespondToWithBytes("zip", Zip(",Mkt-RF\n20240110,1\n"));
                var adapter = new FamaFrenchFactorAdapter(transport, store, new FakeClock(), s_calendar, Options.Create(new FamaFrenchOptions()), NullLogger<FamaFrenchFactorAdapter>.Instance);
                await adapter.FetchAsync(new FactorSeriesRequest(new FactorDatasetId("factors"), s_range));
            }
            else if (source == "treasury")
            {
                transport.RespondTo("treasury", """
                    <feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices">
                    <entry><content><m:properties><d:NEW_DATE>2024-01-10T00:00:00</d:NEW_DATE><d:BC_1MONTH>5.5</d:BC_1MONTH></m:properties></content></entry></feed>
                    """);
                var adapter = new UsTreasuryYieldAdapter(transport, store, new FakeClock(), s_calendar, Options.Create(new UsTreasuryOptions()), NullLogger<UsTreasuryYieldAdapter>.Instance);
                await adapter.FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), s_day));
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "20240110-SR3.csv"), "Month,Settle\nJUN 25,97");
                var adapter = new CmeSettlementAdapter(store, new FakeClock(), s_calendar, Options.Create(new CmeOptions { SettlementDirectory = directory }), NullLogger<CmeSettlementAdapter>.Instance);
                await adapter.FetchAsync(new FuturesSettlementRequest(new FuturesProductCode("SR3"), s_range));
            }
            store.SavedKeys.Should().ContainSingle().Which.Should().Contain("20240110").And.NotContain("2567");
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Treasury_competing_dates_exclude_future_and_choose_latest_earlier_curve()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("treasury", """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices">
            <entry><content><m:properties><d:NEW_DATE>2024-01-11T00:00:00</d:NEW_DATE><d:BC_1MONTH>9</d:BC_1MONTH></m:properties></content></entry>
            <entry><content><m:properties><d:NEW_DATE>2024-01-08T00:00:00</d:NEW_DATE><d:BC_1MONTH>1</d:BC_1MONTH></m:properties></content></entry>
            <entry><content><m:properties><d:NEW_DATE>2024-01-09T00:00:00</d:NEW_DATE><d:BC_1MONTH>5.5</d:BC_1MONTH></m:properties></content></entry></feed>
            """);
        var result = await Treasury(transport).FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), s_day));
        result.Payload.Should().ContainSingle().Which.Should().Be(new YieldCurveQuote(new DateOnly(2024, 1, 9), "1M", 0.055m));
        result.Provenance.Single().DataDate.Should().Be(new DateOnly(2024, 1, 9));
        result.Issues.Should().ContainSingle().Which.Code.Should().Be(new IssueCode("DATE_ROLLBACK"));
    }

    [Fact]
    public async Task Treasury_business_date_is_not_shifted_by_source_offset()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("treasury", """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices">
            <entry><content><m:properties><d:NEW_DATE>2024-01-10T00:00:00+14:00</d:NEW_DATE><d:BC_1MONTH>5.5</d:BC_1MONTH></m:properties></content></entry></feed>
            """);
        var result = await Treasury(transport).FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), s_day));
        result.Payload.Should().ContainSingle().Which.Date.Should().Be(s_day);
    }

    [Fact]
    public async Task NyFed_string_and_numeric_rates_have_identical_decimal_contract()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("sofr", """{"refRates":[{"effectiveDate":"2024-01-10","percentRate":"5.1"},{"effectiveDate":"2024-01-10","percentRate":5.1}]}""");
        var result = await NyFed(transport).FetchAsync(new OvernightFixingRequest(new BenchmarkName("SOFR"), s_range));
        result.Payload.Should().HaveCount(2).And.OnlyContain(row => row.Date == s_day && row.Value == 0.051m && row.Units == "decimal-rate");
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task Ecb_incomplete_rows_are_reported_and_raw_payload_is_saved()
    {
        const string csv = "KEY,TIME_PERIOD,OBS_VALUE\nEST.B.EU000A2X2A25.WT,2024-01-10,3.25\nEST.B.EU000A2X2A25.WT,2024-01-10,\nEST.B.EU000A2X2A25.WT\n";
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", csv);
        var store = new FakeRawDocumentStore();
        var adapter = new EcbEstrAdapter(transport, store, new FakeClock(), s_calendar, Options.Create(new EcbOptions()), NullLogger<EcbEstrAdapter>.Instance);
        var result = await adapter.FetchAsync(new OvernightFixingRequest(new BenchmarkName("ESTR"), s_range));
        result.Payload.Should().ContainSingle().Which.Value.Should().Be(0.0325m);
        var issue = result.Issues.Should().ContainSingle().Which;
        issue.Code.Should().Be(new IssueCode("MISSING_VALUES"));
        issue.Message.Should().Contain("2").And.Contain("incomplete");
        store.Store.Should().ContainSingle().Which.Value.Should().Be(csv);
    }

    [Fact]
    public async Task Ecb_forwards_actual_caller_token_to_transport()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new TokenTransport("KEY,TIME_PERIOD,OBS_VALUE\nEST.B.EU000A2X2A25.WT,2024-01-10,3.25");
        var result = await Ecb(transport).FetchAsync(new OvernightFixingRequest(new BenchmarkName("ESTR"), s_range), cancellation.Token);
        result.Payload.Should().ContainSingle();
        transport.ReceivedToken.Should().Be(cancellation.Token);
    }

    [Theory]
    [InlineData("nyfed")]
    [InlineData("treasury")]
    public async Task Source_provenance_pins_all_contract_fields(string source)
    {
        var transport = new FakeHttpDataTransport();
        IReadOnlyList<DataProvenance> provenance;
        if (source == "nyfed")
        {
            transport.RespondTo("sofr", """{"refRates":[{"effectiveDate":"2024-01-10","percentRate":5.1}]}""");
            provenance = (await NyFed(transport).FetchAsync(new OvernightFixingRequest(new BenchmarkName("SOFR"), s_range))).Provenance;
        }
        else
        {
            transport.RespondTo("treasury", """
                <feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices">
                <entry><content><m:properties><d:NEW_DATE>2024-01-10T00:00:00</d:NEW_DATE><d:BC_1MONTH>5.5</d:BC_1MONTH></m:properties></content></entry></feed>
                """);
            provenance = (await Treasury(transport).FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), s_day))).Provenance;
        }
        provenance.Should().ContainSingle().Which.Should().Be(new DataProvenance(
            new ProviderCode(source == "nyfed" ? "nyfed-sofr" : "us-treasury"),
            source == "nyfed" ? "SOFR" : "USD-TREASURY", LicenseType.Free, RetrievalMode.Api,
            FreshnessClass.EndOfDay, new FakeClock().UtcNow, transport.RequestedUris.Single().OriginalString, s_day));
    }

    [Theory]
    [InlineData("nyfed")]
    [InlineData("fred")]
    [InlineData("fama")]
    [InlineData("shared")]
    public async Task Missing_gap_dates_are_exact_and_unique(string source)
    {
        var range = new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12));
        var transport = new FakeHttpDataTransport();
        IReadOnlyList<DataIssue> issues;
        if (source == "nyfed")
        {
            transport.RespondTo("sofr", """{"refRates":[{"effectiveDate":"2024-01-08","percentRate":5},{"effectiveDate":"2024-01-09","percentRate":5},{"effectiveDate":"2024-01-10","percentRate":5}]}""");
            issues = (await NyFed(transport).FetchAsync(new OvernightFixingRequest(new BenchmarkName("SOFR"), range))).Issues;
        }
        else if (source == "fred")
        {
            transport.RespondTo("fred", """{"observations":[{"date":"2024-01-08","value":"5"},{"date":"2024-01-09","value":"5"},{"date":"2024-01-10","value":"5"}]}""");
            issues = (await Fred(transport).FetchAsync(new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), range))).Issues;
        }
        else if (source == "fama")
        {
            transport.RespondToWithBytes("zip", Zip(",Mkt-RF\n20240108,1\n20240109,1\n20240110,1\n"));
            issues = (await Fama(transport).FetchAsync(new FactorSeriesRequest(new FactorDatasetId("factors"), range))).Issues;
        }
        else
        {
            issues = GapClassifier.Classify(s_calendar, range, new HashSet<DateOnly> { new(2024, 1, 8), new(2024, 1, 9), new(2024, 1, 10) });
        }
        issues.Where(issue => issue.Code == new IssueCode("UNEXPECTED_GAP"))
            .Select(issue => System.Text.RegularExpressions.Regex.Match(issue.Message, @"\d{4}-\d{2}-\d{2}").Value)
            .Should().Equal("2024-01-11", "2024-01-12");
    }

    [Fact]
    public async Task Treasury_refuses_internal_dtd_before_parsing_values()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("treasury", """
            <!DOCTYPE feed [<!ENTITY rate "5.5">]>
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices">
            <entry><content><m:properties><d:NEW_DATE>2024-01-10T00:00:00</d:NEW_DATE><d:BC_1MONTH>&rate;</d:BC_1MONTH></m:properties></content></entry></feed>
            """);
        var result = await Treasury(transport).FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), s_day));
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(issue => issue.Code == new IssueCode("FETCH_FAILED"));
    }

    [Theory]
    [InlineData("ecb")]
    [InlineData("nyfed")]
    [InlineData("treasury")]
    [InlineData("fama")]
    public void Default_registration_supplies_own_options_and_preserves_calendar_override(string source)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpDataTransport>(new FakeHttpDataTransport());
        services.AddSingleton<IRawDocumentStore>(new FakeRawDocumentStore());
        services.AddSingleton<IClock>(new FakeClock());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var key = source switch { "ecb" => "ecb-estr", "nyfed" => "nyfed-sofr", "treasury" => "us-treasury", _ => "fama-french" };
        var calendar = new WeekendOnlyCalendar("OVERRIDE");
        services.AddKeyedSingleton<IBusinessCalendar>(key, calendar);
        switch (source)
        {
            case "ecb": services.AddMarketDataEcb(); break;
            case "nyfed": services.AddMarketDataNewYorkFed(); break;
            case "treasury": services.AddMarketDataUsTreasury(); break;
            default: services.AddMarketDataFamaFrench(); break;
        }
        using var provider = services.BuildServiceProvider();
        if (source == "fama")
        {
            provider.GetServices<IDataSourceAdapter<FactorSeriesRequest, FactorObservation>>().Should().ContainSingle();
        }
        else if (source == "treasury")
        {
            provider.GetServices<IDataSourceAdapter<YieldCurveQuoteRequest, YieldCurveQuote>>().Should().ContainSingle();
        }
        else
        {
            provider.GetServices<IDataSourceAdapter<OvernightFixingRequest, ScalarObservation>>().Should().ContainSingle();
        }
        provider.GetKeyedService<IBusinessCalendar>(key).Should().BeSameAs(calendar);
    }

    [Fact]
    public async Task Ecb_accepts_quoted_newline_and_rejects_unterminated_quote()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("ecb", "KEY,COMMENT,TIME_PERIOD,OBS_VALUE\nEST.B.EU000A2X2A25.WT,\"one\ntwo\",2024-01-10,3.25\nEST.B.EU000A2X2A25.WT,comment,2024-01-10,\"9.99");
        var result = await Ecb(transport).FetchAsync(new OvernightFixingRequest(new BenchmarkName("ESTR"), s_range));
        result.Payload.Should().ContainSingle().Which.Value.Should().Be(0.0325m);
        result.Issues.Should().Contain(issue => issue.Code == new IssueCode("MALFORMED_ROW"));
    }

    [Theory]
    [InlineData("nyfed")]
    [InlineData("ecb")]
    [InlineData("fred")]
    [InlineData("fama")]
    public async Task Expected_empty_weekend_does_not_report_no_data_error(string source)
    {
        var transport = new FakeHttpDataTransport();
        var range = new DateRange(new DateOnly(2024, 1, 6), new DateOnly(2024, 1, 7));
        if (source == "nyfed")
        {
            transport.RespondTo("sofr", "{\"refRates\":[]}");
            var result = await NyFed(transport).FetchAsync(new OvernightFixingRequest(new BenchmarkName("SOFR"), range));
            result.Issues.Should().NotContain(issue => issue.Severity == IssueSeverity.Error);
        }
        else if (source == "ecb")
        {
            transport.RespondTo("ecb", "KEY,TIME_PERIOD,OBS_VALUE\n");
            var result = await Ecb(transport).FetchAsync(new OvernightFixingRequest(new BenchmarkName("ESTR"), range));
            result.Issues.Should().NotContain(issue => issue.Severity == IssueSeverity.Error);
        }
        else if (source == "fred")
        {
            transport.RespondTo("fred", "{\"observations\":[]}");
            var result = await Fred(transport).FetchAsync(new EconomicSeriesRequest(new EconomicSeriesId("DGS10"), range));
            result.Issues.Should().NotContain(issue => issue.Severity == IssueSeverity.Error);
        }
        else
        {
            transport.RespondToWithBytes("zip", Zip(",Mkt-RF\n"));
            var result = await Fama(transport).FetchAsync(new FactorSeriesRequest(new FactorDatasetId("factors"), range));
            result.Issues.Should().NotContain(issue => issue.Severity == IssueSeverity.Error);
        }
    }

    private static NewYorkFedSofrAdapter NyFed(IHttpDataTransport transport) => new(transport, new FakeRawDocumentStore(), new FakeClock(), s_calendar, Options.Create(new NewYorkFedOptions()), NullLogger<NewYorkFedSofrAdapter>.Instance);
    private static EcbEstrAdapter Ecb(IHttpDataTransport transport) => new(transport, new FakeRawDocumentStore(), new FakeClock(), s_calendar, Options.Create(new EcbOptions()), NullLogger<EcbEstrAdapter>.Instance);
    private static FredEconomicSeriesAdapter Fred(IHttpDataTransport transport, FakeRawDocumentStore? store = null) => new(transport, store ?? new FakeRawDocumentStore(), new FakeClock(), s_calendar, Options.Create(new FredOptions { ApiKey = "rc-secret" }), NullLogger<FredEconomicSeriesAdapter>.Instance);
    private static FamaFrenchFactorAdapter Fama(IHttpDataTransport transport) => new(transport, new FakeRawDocumentStore(), new FakeClock(), s_calendar, Options.Create(new FamaFrenchOptions()), NullLogger<FamaFrenchFactorAdapter>.Instance);
    private static UsTreasuryYieldAdapter Treasury(IHttpDataTransport transport) => new(transport, new FakeRawDocumentStore(), new FakeClock(), s_calendar, Options.Create(new UsTreasuryOptions()), NullLogger<UsTreasuryYieldAdapter>.Instance);

    private static byte[] Zip(string csv)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("factors.CSV").Open());
            writer.Write(csv);
        }
        return memory.ToArray();
    }

    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class StreamTransport(Stream stream) : IHttpDataTransport
    {
        public Task<Stream> GetAsync(Uri uri, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) => Task.FromResult(stream);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception) + exception);
    }

    private sealed class TokenTransport(string csv) : IHttpDataTransport
    {
        public CancellationToken ReceivedToken { get; private set; }
        public Task<Stream> GetAsync(Uri uri, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
        {
            ReceivedToken = cancellationToken;
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        }
    }
}
