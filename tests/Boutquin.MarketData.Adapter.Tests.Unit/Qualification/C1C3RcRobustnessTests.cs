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
using System.Text;
using Boutquin.Domain.Enumerations;
using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.BankOfCanada;
using Boutquin.MarketData.Adapter.BankOfEngland;
using Boutquin.MarketData.Adapter.Frankfurter;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.Tiingo;
using Boutquin.MarketData.Adapter.TwelveData;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Transport.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.Qualification;

public sealed class C1C3RcRobustnessTests
{
    private static readonly DateRange s_range = new(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 9));
    private static readonly FakeClock s_clock = new();
    private const string BarValues = "\"open\":\"100\",\"high\":\"101\",\"low\":\"99\",\"close\":\"100\",\"volume\":\"10\"";

    private sealed class RecordingTransport(string response) : IHttpDataTransport
    {
        public CancellationToken? ObservedToken { get; private set; }
        public Uri? ObservedUri { get; private set; }

        public Task<Stream> GetAsync(Uri uri, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            ObservedUri = uri;
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(response)));
        }
    }

    private sealed class CapturingLogger<T>(List<string> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Add(formatter(state, exception) + " " + exception);
    }

    private static IDataSourceAdapter<PriceHistoryRequest, Bar> Price(string provider, IHttpDataTransport transport) => provider switch
    {
        "tiingo" => new TiingoPriceBarAdapter(transport, new FakeRawDocumentStore(), s_clock, new WeekendOnlyCalendar("TEST"),
            Options.Create(new TiingoOptions()), NullLogger<TiingoPriceBarAdapter>.Instance),
        "twelvedata" => new TwelveDataPriceBarAdapter(transport, new FakeRawDocumentStore(), s_clock, new WeekendOnlyCalendar("TEST"),
            Options.Create(new TwelveDataOptions()), NullLogger<TwelveDataPriceBarAdapter>.Instance),
        _ => throw new ArgumentException("Unknown test provider.", nameof(provider)),
    };

    private static FrankfurterFxAdapter Fx(IHttpDataTransport transport) => new(transport, new FakeRawDocumentStore(), s_clock,
        new WeekendOnlyCalendar("TEST"), Options.Create(new FrankfurterOptions()), NullLogger<FrankfurterFxAdapter>.Instance);

    private static BankOfCanadaFxAdapter BocFx(IHttpDataTransport transport) => new(transport, new FakeRawDocumentStore(), s_clock,
        new WeekendOnlyCalendar("TEST"), Options.Create(new BankOfCanadaOptions()), NullLogger<BankOfCanadaFxAdapter>.Instance);

    private static IDataSourceAdapter<OvernightFixingRequest, ScalarObservation> Fixing(string provider, IHttpDataTransport transport) => provider switch
    {
        "CORRA" => new BankOfCanadaCorraAdapter(transport, new FakeRawDocumentStore(), s_clock, new WeekendOnlyCalendar("TEST"),
            Options.Create(new BankOfCanadaOptions()), NullLogger<BankOfCanadaCorraAdapter>.Instance),
        "SONIA" => new BankOfEnglandSoniaAdapter(transport, new FakeRawDocumentStore(), s_clock, new WeekendOnlyCalendar("TEST"),
            Options.Create(new BankOfEnglandOptions()), NullLogger<BankOfEnglandSoniaAdapter>.Instance),
        _ => throw new ArgumentException("Unknown test provider.", nameof(provider)),
    };

    private static BankOfCanadaYieldCurveAdapter Curve(IHttpDataTransport transport) => new(transport, new FakeRawDocumentStore(), s_clock,
        new WeekendOnlyCalendar("TEST"), Options.Create(new BankOfCanadaOptions()), NullLogger<BankOfCanadaYieldCurveAdapter>.Instance);

    [Theory]
    [InlineData("tiingo")]
    [InlineData("twelvedata")]
    public async Task Price_rejects_ambiguous_multi_symbol_envelopes_before_transport(string provider)
    {
        var transport = new RecordingTransport("[]");
        var adapter = Price(provider, transport);
        var request = new PriceHistoryRequest([new Symbol("AAPL"), new Symbol("MSFT")], s_range);
        adapter.CanHandle(request).Should().BeFalse();
        Func<Task> fetch = () => adapter.FetchAsync(request);
        await fetch.Should().ThrowAsync<ArgumentException>();
        transport.ObservedToken.Should().BeNull();
    }

    [Fact]
    public async Task Frankfurter_rejects_ambiguous_multi_pair_envelopes_before_transport()
    {
        var transport = new RecordingTransport("{}");
        var adapter = Fx(transport);
        var request = new FxHistoryRequest([new FxPair(Currency.EUR, Currency.USD), new FxPair(Currency.EUR, Currency.GBP)], s_range);
        adapter.CanHandle(request).Should().BeFalse();
        Func<Task> fetch = () => adapter.FetchAsync(request);
        await fetch.Should().ThrowAsync<ArgumentException>();
        transport.ObservedToken.Should().BeNull();
    }

    [Fact]
    public async Task Frankfurter_rejects_identity_pair_before_transport()
    {
        var transport = new RecordingTransport("{}");
        var adapter = Fx(transport);
        var request = new FxHistoryRequest([new FxPair(Currency.USD, Currency.USD)], s_range);
        adapter.CanHandle(request).Should().BeFalse();
        Func<Task> fetch = () => adapter.FetchAsync(request);
        await fetch.Should().ThrowAsync<ArgumentException>();
        transport.ObservedToken.Should().BeNull();
    }

    [Theory]
    [InlineData("tiingo")]
    [InlineData("twelvedata")]
    public async Task Direct_price_fetch_rejects_unsupported_frequency_and_venue(string provider)
    {
        var transport = new RecordingTransport("{}");
        var adapter = Price(provider, transport);
        foreach (var request in new[]
        {
            new PriceHistoryRequest([new Symbol("AAPL")], s_range, DataFrequency.Weekly),
            new PriceHistoryRequest([new Symbol("AAPL")], s_range) { Exchange = ExchangeCode.XLON },
        })
        {
            Func<Task> fetch = () => adapter.FetchAsync(request);
            await fetch.Should().ThrowAsync<ArgumentException>();
        }
        transport.ObservedToken.Should().BeNull();
    }

    [Theory]
    [InlineData("CORRA")]
    [InlineData("SONIA")]
    public async Task Direct_fixing_fetch_rejects_another_benchmark_before_transport(string provider)
    {
        var transport = new RecordingTransport("{}");
        Func<Task> fetch = () => Fixing(provider, transport).FetchAsync(new OvernightFixingRequest(new BenchmarkName("SOFR"), s_range));
        await fetch.Should().ThrowAsync<ArgumentException>();
        transport.ObservedToken.Should().BeNull();
    }

    [Theory]
    [InlineData("GBP")]
    [InlineData("eur")]
    public async Task Frankfurter_checks_supplied_base_identity(string responseBase)
    {
        var transport = new RecordingTransport("{\"base\":\"" + responseBase + "\",\"rates\":{\"2024-01-08\":{\"USD\":1.25}}}");
        var result = await Fx(transport).FetchAsync(new FxHistoryRequest([new FxPair(Currency.EUR, Currency.USD)], s_range));
        if (responseBase == "GBP")
        {
            result.Payload.Should().BeEmpty();
            result.Issues.Should().Contain(i => i.Code == new IssueCode("FETCH_FAILED"));
        }
        else
        {
            result.Payload.Should().ContainSingle().Which.Rate.Should().Be(1.25m);
        }
    }

    [Theory]
    [InlineData("CORRA")]
    [InlineData("SONIA")]
    public async Task Fixings_clip_provider_rows_to_the_inclusive_request_range(string provider)
    {
        var response = provider == "CORRA"
            ? """{"observations":[{"d":"2024-01-07","AVG.INTWO":{"v":"9"}},{"d":"2024-01-08","AVG.INTWO":{"v":"5"}},{"d":"2024-01-10","AVG.INTWO":{"v":"8"}}]}"""
            : "DATE,IUDSOIA\n07 Jan 2024,9\n08 Jan 2024,5\n10 Jan 2024,8\n";
        var result = await Fixing(provider, new RecordingTransport(response)).FetchAsync(new OvernightFixingRequest(new BenchmarkName(provider), s_range));
        result.Payload.Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 1, 8));
        result.Payload[0].Value.Should().Be(0.05m);
        result.Provenance.Should().ContainSingle().Which.DataDate.Should().Be(new DateOnly(2024, 1, 8));
        result.Coverage.ReturnedPoints.Should().Be(1);
    }

    [Fact]
    public async Task Boc_fx_clips_provider_rows_to_the_inclusive_request_range()
    {
        var response = """{"observations":[{"d":"2024-01-07","FXUSDCAD":{"v":"9"}},{"d":"2024-01-08","FXUSDCAD":{"v":"1.3"}},{"d":"2024-01-10","FXUSDCAD":{"v":"8"}}]}""";
        var result = await BocFx(new RecordingTransport(response)).FetchAsync(new FxHistoryRequest([new FxPair(Currency.USD, Currency.CAD)], s_range));
        result.Payload.Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 1, 8));
        result.Provenance.Should().ContainSingle().Which.DataDate.Should().Be(new DateOnly(2024, 1, 8));
    }

    [Theory]
    [InlineData("CORRA")]
    [InlineData("SONIA")]
    public async Task Empty_weekend_fixings_are_successful_without_an_error(string provider)
    {
        var response = provider == "CORRA" ? "{\"observations\":[]}" : "DATE,IUDSOIA\n";
        var weekend = new DateRange(new DateOnly(2024, 1, 6), new DateOnly(2024, 1, 7));
        var result = await Fixing(provider, new RecordingTransport(response)).FetchAsync(new OvernightFixingRequest(new BenchmarkName(provider), weekend));
        result.Coverage.CoverageRatio.Should().Be(1m);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_weekend_boc_fx_has_no_no_rates_warning()
    {
        var weekend = new DateRange(new DateOnly(2024, 1, 6), new DateOnly(2024, 1, 7));
        var result = await BocFx(new RecordingTransport("{\"observations\":[]}")).FetchAsync(new FxHistoryRequest([new FxPair(Currency.USD, Currency.CAD)], weekend));
        result.Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Curve_selects_latest_in_window_without_trusting_row_order(bool reverse)
    {
        string[] rows = ["2024-01-12,0.05", "2024-01-16,0.99", "2024-01-10,0.04", "2023-11-01,0.03"];
        if (reverse)
        {
            Array.Reverse(rows);
        }
        var result = await Curve(new RecordingTransport("Date,ZC100YR\n" + string.Join('\n', rows)))
            .FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("BOC-ZERO"), new DateOnly(2024, 1, 15)));
        result.Payload.Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 1, 12));
        result.Payload[0].Rate.Should().Be(0.05m);
        result.Provenance.Should().ContainSingle().Which.DataDate.Should().Be(new DateOnly(2024, 1, 12));
    }

    [Theory]
    [InlineData("2024-01-16")]
    [InlineData("2023-11-01")]
    public async Task Curve_refuses_a_row_outside_its_lookup_window(string date)
    {
        var result = await Curve(new RecordingTransport($"Date,ZC100YR\n{date},0.99"))
            .FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("BOC-ZERO"), new DateOnly(2024, 1, 15)));
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new IssueCode("EMPTY_OBSERVATIONS"));
    }

    [Fact]
    public async Task Curve_minimum_date_clamps_lookback_to_the_representable_boundary()
    {
        var result = await Curve(new RecordingTransport("Date,ZC100YR\n0001-01-01,0.05"))
            .FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("BOC-ZERO"), DateOnly.MinValue));
        result.Payload.Should().ContainSingle().Which.Date.Should().Be(DateOnly.MinValue);
    }

    [Fact]
    public async Task Curve_rejects_a_term_that_overflows_int_multiplication()
    {
        var result = await Curve(new RecordingTransport("Date,ZC178956983YR\n2024-01-15,0.05"))
            .FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("BOC-ZERO"), new DateOnly(2024, 1, 15)));
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new IssueCode("SCHEMA_MISMATCH"));
    }

    [Theory]
    [InlineData("frankfurter")]
    [InlineData("twelvedata-price")]
    [InlineData("twelvedata-meta")]
    public async Task Public_failure_issues_do_not_repeat_private_exception_detail(string provider)
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new IOException("credential=private-sentinel; path=/private/key; ?apikey=private-sentinel"));
        IReadOnlyList<DataIssue> issues;
        if (provider == "frankfurter")
        {
            issues = (await Fx(transport).FetchAsync(new FxHistoryRequest([new FxPair(Currency.EUR, Currency.USD)], s_range))).Issues;
        }
        else if (provider == "twelvedata-price")
        {
            issues = (await Price("twelvedata", transport).FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range))).Issues;
        }
        else
        {
            var adapter = new TwelveDataInstrumentMetadataAdapter(transport, new FakeRawDocumentStore(), s_clock,
                Options.Create(new TwelveDataOptions()), NullLogger<TwelveDataInstrumentMetadataAdapter>.Instance);
            issues = (await adapter.FetchAsync(new InstrumentMetadataRequest([new Symbol("AAPL")]))).Issues;
        }
        issues.Should().Contain(i => i.Code == new IssueCode("FETCH_FAILED"));
        issues.Should().OnlyContain(i => !i.Message.Contains("private-sentinel", StringComparison.Ordinal) && !i.Message.Contains("/private/key", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"symbol\":\"MSFT\"")]
    [InlineData("\"symbol\":\"AAPL\",\"mic_code\":\"XTSE\"")]
    [InlineData("\"symbol\":\"AAPL\",\"interval\":\"1week\"")]
    [InlineData("\"symbol\":\"AAPL\",\"exchange\":\"TSX\"")]
    public async Task TwelveData_refuses_conflicting_supplied_time_series_identity(string metadata)
    {
        var response = $$"""{"meta":{ {{metadata}} },"values":[{"datetime":"2024-01-08",{{BarValues}}}]}""";
        var result = await Price("twelvedata", new RecordingTransport(response)).FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range) { Exchange = ExchangeCode.XNAS });
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new IssueCode("FETCH_FAILED"));
    }

    [Fact]
    public async Task TwelveData_matching_supplied_identity_remains_usable()
    {
        var response = $$"""{"meta":{"symbol":"aapl","interval":"1day","mic_code":"XNAS","exchange":"NASDAQ"},"values":[{"datetime":"2024-01-08",{{BarValues}}}]}""";
        var result = await Price("twelvedata", new RecordingTransport(response)).FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range) { Exchange = ExchangeCode.XNAS });
        result.Payload.Should().ContainSingle().Which.Close.Should().Be(100m);
    }

    [Fact]
    public async Task Frankfurter_rejects_a_non_unit_response_amount()
    {
        var result = await Fx(new RecordingTransport("""{"base":"EUR","amount":2,"rates":{"2024-01-08":{"USD":2.5}}}"""))
            .FetchAsync(new FxHistoryRequest([new FxPair(Currency.EUR, Currency.USD)], s_range));
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new IssueCode("FETCH_FAILED"));
    }

    [Fact]
    public async Task TwelveData_explicitly_requests_unadjusted_OHLC_prices()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("time_series", "{\"values\":[]}");
        await Price("twelvedata", transport).FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range));
        System.Web.HttpUtility.ParseQueryString(transport.RequestedUris.Single().Query)["adjust"].Should().Be("none");
    }

    [Theory]
    [InlineData("tiingo", false)]
    [InlineData("tiingo", true)]
    [InlineData("twelvedata", false)]
    [InlineData("twelvedata", true)]
    public async Task Prices_reject_impossible_OHLC_or_negative_volume(string provider, bool negativeVolume)
    {
        var volume = negativeVolume ? -1 : 1;
        var high = negativeVolume ? 101 : 98;
        var response = provider == "tiingo"
            ? $$"""[{"date":"2024-01-08T00:00:00Z","open":100,"high":{{high}},"low":99,"close":100,"volume":{{volume}}}]"""
            : $$"""{"values":[{"datetime":"2024-01-08","open":"100","high":"{{high}}","low":"99","close":"100","volume":"{{volume}}"}]}""";
        var result = await Price(provider, new RecordingTransport(response)).FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range));
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new IssueCode("FETCH_FAILED"));
    }

    [Theory]
    [InlineData("tiingo")]
    [InlineData("twelvedata")]
    public async Task Price_dates_require_the_provider_ISO_grammar(string provider)
    {
        var response = provider == "tiingo"
            ? """[{"date":"01/08/2024T00:00:00Z","open":100,"high":101,"low":99,"close":100,"volume":10}]"""
            : $$"""{"values":[{"datetime":"01/08/2024",{{BarValues}}}]}""";
        var result = await Price(provider, new RecordingTransport(response)).FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range));
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new IssueCode("FETCH_FAILED"));
    }

    [Fact]
    public async Task Tiingo_price_coverage_start_is_not_instrument_inception()
    {
        var adapter = new TiingoInstrumentMetadataAdapter(new RecordingTransport("""{"ticker":"AAPL","name":"Apple","exchangeCode":"XNAS","startDate":"1980-12-12"}"""),
            new FakeRawDocumentStore(), s_clock, Options.Create(new TiingoOptions()), NullLogger<TiingoInstrumentMetadataAdapter>.Instance);
        var result = await adapter.FetchAsync(new InstrumentMetadataRequest([new Symbol("AAPL")]));
        result.Payload.Should().ContainSingle().Which.InceptionDate.Should().BeNull();
    }

    [Theory]
    [InlineData("tiingo")]
    [InlineData("twelvedata")]
    public async Task Metadata_rejects_unsupported_venue_before_transport(string provider)
    {
        var transport = new RecordingTransport("{}");
        IDataSourceAdapter<InstrumentMetadataRequest, InstrumentMetadata> adapter = provider == "tiingo"
            ? new TiingoInstrumentMetadataAdapter(transport, new FakeRawDocumentStore(), s_clock, Options.Create(new TiingoOptions()), NullLogger<TiingoInstrumentMetadataAdapter>.Instance)
            : new TwelveDataInstrumentMetadataAdapter(transport, new FakeRawDocumentStore(), s_clock, Options.Create(new TwelveDataOptions()), NullLogger<TwelveDataInstrumentMetadataAdapter>.Instance);
        Func<Task> fetch = () => adapter.FetchAsync(new InstrumentMetadataRequest([new Symbol("AAPL")]) { Exchange = ExchangeCode.XLON });
        await fetch.Should().ThrowAsync<ArgumentException>();
        transport.ObservedToken.Should().BeNull();
    }

    [Fact]
    public void Tiingo_routing_rejects_null_requests_with_the_documented_argument_exception()
    {
        var transport = new RecordingTransport("{}");
        Action price = () => Price("tiingo", transport).CanHandle(null!);
        Action metadata = () => new TiingoInstrumentMetadataAdapter(transport, new FakeRawDocumentStore(), s_clock,
            Options.Create(new TiingoOptions()), NullLogger<TiingoInstrumentMetadataAdapter>.Instance).CanHandle(null!);
        price.Should().ThrowExactly<ArgumentNullException>().WithParameterName("request");
        metadata.Should().ThrowExactly<ArgumentNullException>().WithParameterName("request");
    }

    [Fact]
    public void Default_calendar_registrations_preserve_caller_overrides()
    {
        var calendar = new WeekendOnlyCalendar("CALLER");
        string[] keys = ["tiingo", "twelvedata", "frankfurter", "bankofcanada", "boc-corra", "boe-sonia"];
        var services = new ServiceCollection();
        foreach (var key in keys)
        {
            services.AddKeyedSingleton<IBusinessCalendar>(key, calendar);
        }

        services.AddMarketDataTiingo(_ => { });
        services.AddMarketDataTwelveData(_ => { });
        services.AddMarketDataFrankfurter();
        services.AddMarketDataBankOfCanada();
        services.AddMarketDataBankOfEngland();
        using var provider = services.BuildServiceProvider();
        foreach (var key in keys)
        {
            provider.GetRequiredKeyedService<IBusinessCalendar>(key).Should().BeSameAs(calendar);
        }
    }

    [Theory]
    [InlineData("tiingo-price", false)]
    [InlineData("tiingo-meta", false)]
    [InlineData("tiingo-dividend", false)]
    [InlineData("twelvedata-price", false)]
    [InlineData("twelvedata-meta", false)]
    [InlineData("tiingo-price", true)]
    [InlineData("tiingo-meta", true)]
    [InlineData("tiingo-dividend", true)]
    [InlineData("twelvedata-price", true)]
    [InlineData("twelvedata-meta", true)]
    public async Task Credential_bearing_endpoints_require_https_before_transport(string provider, bool secure)
    {
        var transport = new RecordingTransport("{}");
        var baseUrl = secure ? "https://provider.example.test" : "http://provider.example.test";
        var tiingo = Options.Create(new TiingoOptions { BaseUrl = baseUrl, ApiToken = "private-token" });
        var twelve = Options.Create(new TwelveDataOptions { BaseUrl = baseUrl, ApiKey = "private-key" });
        var raw = new FakeRawDocumentStore();
        var calendar = new WeekendOnlyCalendar("TEST");
        Func<Task> fetch = provider switch
        {
            "tiingo-price" => () => new TiingoPriceBarAdapter(transport, raw, s_clock, calendar, tiingo, NullLogger<TiingoPriceBarAdapter>.Instance)
                .FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range)),
            "tiingo-meta" => () => new TiingoInstrumentMetadataAdapter(transport, raw, s_clock, tiingo, NullLogger<TiingoInstrumentMetadataAdapter>.Instance)
                .FetchAsync(new InstrumentMetadataRequest([new Symbol("AAPL")])),
            "tiingo-dividend" => () => new TiingoDividendHistoryAdapter(transport, raw, s_clock, tiingo, NullLogger<TiingoDividendHistoryAdapter>.Instance)
                .FetchAsync(new DividendHistoryRequest([new Symbol("AAPL")], s_range)),
            "twelvedata-price" => () => new TwelveDataPriceBarAdapter(transport, raw, s_clock, calendar, twelve, NullLogger<TwelveDataPriceBarAdapter>.Instance)
                .FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range)),
            "twelvedata-meta" => () => new TwelveDataInstrumentMetadataAdapter(transport, raw, s_clock, twelve, NullLogger<TwelveDataInstrumentMetadataAdapter>.Instance)
                .FetchAsync(new InstrumentMetadataRequest([new Symbol("AAPL")])),
            _ => throw new ArgumentException("Unknown test provider.", nameof(provider)),
        };
        if (secure)
        {
            await fetch();
            transport.ObservedToken.Should().NotBeNull();
        }
        else
        {
            await fetch.Should().ThrowAsync<ArgumentException>().WithParameterName("BaseUrl");
            transport.ObservedToken.Should().BeNull();
            raw.SavedKeys.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData("tiingo-price")]
    [InlineData("tiingo-meta")]
    [InlineData("tiingo-dividend")]
    [InlineData("twelvedata-price")]
    [InlineData("twelvedata-meta")]
    public async Task Credential_bearing_failures_do_not_log_exception_secrets(string provider)
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new IOException("https://provider.example?apikey=secret-log-sentinel"));
        var entries = new List<string>();
        var tiingo = Options.Create(new TiingoOptions { ApiToken = "secret-log-sentinel" });
        var twelve = Options.Create(new TwelveDataOptions { ApiKey = "secret-log-sentinel" });
        var raw = new FakeRawDocumentStore();
        var calendar = new WeekendOnlyCalendar("TEST");
        Func<Task> fetch = provider switch
        {
            "tiingo-price" => () => new TiingoPriceBarAdapter(transport, raw, s_clock, calendar, tiingo, new CapturingLogger<TiingoPriceBarAdapter>(entries))
                .FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range)),
            "tiingo-meta" => () => new TiingoInstrumentMetadataAdapter(transport, raw, s_clock, tiingo, new CapturingLogger<TiingoInstrumentMetadataAdapter>(entries))
                .FetchAsync(new InstrumentMetadataRequest([new Symbol("AAPL")])),
            "tiingo-dividend" => () => new TiingoDividendHistoryAdapter(transport, raw, s_clock, tiingo, new CapturingLogger<TiingoDividendHistoryAdapter>(entries))
                .FetchAsync(new DividendHistoryRequest([new Symbol("AAPL")], s_range)),
            "twelvedata-price" => () => new TwelveDataPriceBarAdapter(transport, raw, s_clock, calendar, twelve, new CapturingLogger<TwelveDataPriceBarAdapter>(entries))
                .FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range)),
            "twelvedata-meta" => () => new TwelveDataInstrumentMetadataAdapter(transport, raw, s_clock, twelve, new CapturingLogger<TwelveDataInstrumentMetadataAdapter>(entries))
                .FetchAsync(new InstrumentMetadataRequest([new Symbol("AAPL")])),
            _ => throw new ArgumentException("Unknown test provider.", nameof(provider)),
        };
        await fetch();
        entries.Should().ContainSingle().Which.Should().NotContain("secret-log-sentinel").And.Contain(nameof(IOException));
    }

    [Theory]
    [InlineData("tiingo-price")]
    [InlineData("tiingo-dividend")]
    [InlineData("tiingo-meta")]
    [InlineData("twelvedata")]
    [InlineData("twelvedata-meta")]
    [InlineData("frankfurter")]
    [InlineData("CORRA")]
    [InlineData("SONIA")]
    [InlineData("boc-fx")]
    [InlineData("boc-curve")]
    public async Task Caller_token_reaches_each_transport_boundary(string provider)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new RecordingTransport("{}");
        var token = cancellation.Token;
        switch (provider)
        {
            case "tiingo-price":
            case "twelvedata":
                await Price(provider == "tiingo-price" ? "tiingo" : provider, transport).FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range), token);
                break;
            case "tiingo-meta":
                await new TiingoInstrumentMetadataAdapter(transport, new FakeRawDocumentStore(), s_clock, Options.Create(new TiingoOptions()), NullLogger<TiingoInstrumentMetadataAdapter>.Instance)
                    .FetchAsync(new InstrumentMetadataRequest([new Symbol("AAPL")]), token);
                break;
            case "tiingo-dividend":
                await new TiingoDividendHistoryAdapter(transport, new FakeRawDocumentStore(), s_clock, Options.Create(new TiingoOptions()), NullLogger<TiingoDividendHistoryAdapter>.Instance)
                    .FetchAsync(new DividendHistoryRequest([new Symbol("AAPL")], s_range), token);
                break;
            case "twelvedata-meta":
                await new TwelveDataInstrumentMetadataAdapter(transport, new FakeRawDocumentStore(), s_clock, Options.Create(new TwelveDataOptions()), NullLogger<TwelveDataInstrumentMetadataAdapter>.Instance)
                    .FetchAsync(new InstrumentMetadataRequest([new Symbol("AAPL")]), token);
                break;
            case "frankfurter":
                await Fx(transport).FetchAsync(new FxHistoryRequest([new FxPair(Currency.EUR, Currency.USD)], s_range), token);
                break;
            case "CORRA":
            case "SONIA":
                await Fixing(provider, transport).FetchAsync(new OvernightFixingRequest(new BenchmarkName(provider), s_range), token);
                break;
            case "boc-fx":
                await BocFx(transport).FetchAsync(new FxHistoryRequest([new FxPair(Currency.USD, Currency.CAD)], s_range), token);
                break;
            case "boc-curve":
                await Curve(transport).FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("BOC-ZERO"), s_range.To), token);
                break;
        }
        transport.ObservedToken.Should().Be(token);
    }

    [Theory]
    [InlineData("tiingo-price")]
    [InlineData("tiingo-dividend")]
    [InlineData("tiingo-meta")]
    [InlineData("twelvedata")]
    [InlineData("twelvedata-meta")]
    [InlineData("frankfurter")]
    [InlineData("CORRA")]
    [InlineData("SONIA")]
    [InlineData("boc-fx")]
    [InlineData("boc-curve")]
    public async Task Cancellation_identity_is_preserved_even_when_caller_token_is_not_signaled(string provider)
    {
        var transport = new FakeHttpDataTransport();
        var cancellation = new OperationCanceledException("dependency cancellation");
        transport.ThrowOnNext(cancellation);
        Func<Task> fetch = provider switch
        {
            "tiingo-price" => () => Price("tiingo", transport).FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range)),
            "twelvedata" => () => Price("twelvedata", transport).FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range)),
            "tiingo-meta" => () => new TiingoInstrumentMetadataAdapter(transport, new FakeRawDocumentStore(), s_clock, Options.Create(new TiingoOptions()), NullLogger<TiingoInstrumentMetadataAdapter>.Instance)
                .FetchAsync(new InstrumentMetadataRequest([new Symbol("AAPL")])),
            "tiingo-dividend" => () => new TiingoDividendHistoryAdapter(transport, new FakeRawDocumentStore(), s_clock, Options.Create(new TiingoOptions()), NullLogger<TiingoDividendHistoryAdapter>.Instance)
                .FetchAsync(new DividendHistoryRequest([new Symbol("AAPL")], s_range)),
            "twelvedata-meta" => () => new TwelveDataInstrumentMetadataAdapter(transport, new FakeRawDocumentStore(), s_clock, Options.Create(new TwelveDataOptions()), NullLogger<TwelveDataInstrumentMetadataAdapter>.Instance)
                .FetchAsync(new InstrumentMetadataRequest([new Symbol("AAPL")])),
            "frankfurter" => () => Fx(transport).FetchAsync(new FxHistoryRequest([new FxPair(Currency.EUR, Currency.USD)], s_range)),
            "CORRA" or "SONIA" => () => Fixing(provider, transport).FetchAsync(new OvernightFixingRequest(new BenchmarkName(provider), s_range)),
            "boc-fx" => () => BocFx(transport).FetchAsync(new FxHistoryRequest([new FxPair(Currency.USD, Currency.CAD)], s_range)),
            "boc-curve" => () => Curve(transport).FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("BOC-ZERO"), s_range.To)),
            _ => throw new ArgumentException("Unknown test provider.", nameof(provider)),
        };
        var thrown = await fetch.Should().ThrowExactlyAsync<OperationCanceledException>();
        thrown.Which.Should().BeSameAs(cancellation);
    }

    [Theory]
    [InlineData("tiingo-price")]
    [InlineData("tiingo-dividend")]
    [InlineData("twelvedata")]
    [InlineData("frankfurter")]
    [InlineData("CORRA")]
    [InlineData("SONIA")]
    [InlineData("boc-fx")]
    [InlineData("boc-curve")]
    public async Task Wire_dates_and_raw_keys_are_invariant_under_a_non_Gregorian_culture(string provider)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
        try
        {
            var transport = new RecordingTransport("{}");
            var raw = new FakeRawDocumentStore();
            var calendar = new WeekendOnlyCalendar("TEST");
            Func<Task> fetch = provider switch
            {
                "tiingo-price" => () => new TiingoPriceBarAdapter(transport, raw, s_clock, calendar, Options.Create(new TiingoOptions()), NullLogger<TiingoPriceBarAdapter>.Instance)
                    .FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range)),
                "tiingo-dividend" => () => new TiingoDividendHistoryAdapter(transport, raw, s_clock, Options.Create(new TiingoOptions()), NullLogger<TiingoDividendHistoryAdapter>.Instance)
                    .FetchAsync(new DividendHistoryRequest([new Symbol("AAPL")], s_range)),
                "twelvedata" => () => new TwelveDataPriceBarAdapter(transport, raw, s_clock, calendar, Options.Create(new TwelveDataOptions()), NullLogger<TwelveDataPriceBarAdapter>.Instance)
                    .FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range)),
                "frankfurter" => () => new FrankfurterFxAdapter(transport, raw, s_clock, calendar, Options.Create(new FrankfurterOptions()), NullLogger<FrankfurterFxAdapter>.Instance)
                    .FetchAsync(new FxHistoryRequest([new FxPair(Currency.EUR, Currency.USD)], s_range)),
                "CORRA" => () => new BankOfCanadaCorraAdapter(transport, raw, s_clock, calendar, Options.Create(new BankOfCanadaOptions()), NullLogger<BankOfCanadaCorraAdapter>.Instance)
                    .FetchAsync(new OvernightFixingRequest(new BenchmarkName(provider), s_range)),
                "SONIA" => () => new BankOfEnglandSoniaAdapter(transport, raw, s_clock, calendar, Options.Create(new BankOfEnglandOptions()), NullLogger<BankOfEnglandSoniaAdapter>.Instance)
                    .FetchAsync(new OvernightFixingRequest(new BenchmarkName(provider), s_range)),
                "boc-fx" => () => new BankOfCanadaFxAdapter(transport, raw, s_clock, calendar, Options.Create(new BankOfCanadaOptions()), NullLogger<BankOfCanadaFxAdapter>.Instance)
                    .FetchAsync(new FxHistoryRequest([new FxPair(Currency.USD, Currency.CAD)], s_range)),
                "boc-curve" => () => new BankOfCanadaYieldCurveAdapter(transport, raw, s_clock, calendar, Options.Create(new BankOfCanadaOptions()), NullLogger<BankOfCanadaYieldCurveAdapter>.Instance)
                    .FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("BOC-ZERO"), s_range.To)),
                _ => throw new ArgumentException("Unknown test provider.", nameof(provider)),
            };
            await fetch();
            transport.ObservedUri.Should().NotBeNull();
            transport.ObservedUri!.OriginalString.Should().Contain("2024").And.NotContain("2567");
            raw.SavedKeys.Should().ContainSingle().Which.Should().Contain("2024").And.NotContain("2567");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("tiingo", "https://user:private-token@provider.example.test")]
    [InlineData("tiingo", "https://provider.example.test?apikey=private-token")]
    [InlineData("tiingo", "https://provider.example.test#fragment")]
    [InlineData("twelvedata", "https://user:private-token@provider.example.test")]
    [InlineData("twelvedata", "https://provider.example.test?apikey=private-token")]
    [InlineData("twelvedata", "https://provider.example.test#fragment")]
    public async Task Credential_base_endpoints_reject_userinfo_query_and_fragment(string provider, string endpoint)
    {
        var transport = new RecordingTransport("{}");
        IDataSourceAdapter<PriceHistoryRequest, Bar> adapter = provider == "tiingo"
            ? new TiingoPriceBarAdapter(transport, new FakeRawDocumentStore(), s_clock, new WeekendOnlyCalendar("TEST"),
                Options.Create(new TiingoOptions { BaseUrl = endpoint }), NullLogger<TiingoPriceBarAdapter>.Instance)
            : new TwelveDataPriceBarAdapter(transport, new FakeRawDocumentStore(), s_clock, new WeekendOnlyCalendar("TEST"),
                Options.Create(new TwelveDataOptions { BaseUrl = endpoint }), NullLogger<TwelveDataPriceBarAdapter>.Instance);
        Func<Task> fetch = () => adapter.FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range));
        await fetch.Should().ThrowAsync<ArgumentException>().WithParameterName("BaseUrl");
        transport.ObservedToken.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwelveData_rejects_conflicting_duplicate_dates_independently_of_response_order(bool reverse)
    {
        var first = reverse ? "100" : "100.5";
        var second = reverse ? "100.5" : "100";
        var response = $$"""{"values":[{"datetime":"2024-01-08","open":"100","high":"101","low":"99","close":"{{first}}","volume":"10"},{"datetime":"2024-01-08","open":"100","high":"101","low":"99","close":"{{second}}","volume":"10"}]}""";
        var result = await Price("twelvedata", new RecordingTransport(response)).FetchAsync(new PriceHistoryRequest([new Symbol("AAPL")], s_range));
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new IssueCode("FETCH_FAILED"));
    }
}
