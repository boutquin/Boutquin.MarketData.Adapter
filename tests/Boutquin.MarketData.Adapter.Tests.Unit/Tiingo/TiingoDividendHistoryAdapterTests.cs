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

using System.Xml.Linq;
using Boutquin.Domain.Enumerations;
using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.Tiingo;
using Boutquin.MarketData.Transport.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.Tiingo;

public sealed class TiingoDividendHistoryAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static TiingoDividendHistoryAdapter CreateAdapter(
        FakeHttpDataTransport? transport = null,
        FakeRawDocumentStore? rawStore = null,
        ILogger<TiingoDividendHistoryAdapter>? logger = null,
        TiingoOptions? options = null) =>
        new(
            transport ?? new FakeHttpDataTransport(),
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            Options.Create(options ?? new TiingoOptions { ApiToken = "test-token" }),
            logger ?? NullLogger<TiingoDividendHistoryAdapter>.Instance);

    private static DateRange MakeRange(int year = 2024, int fromMonth = 1, int fromDay = 1, int toMonth = 1, int toDay = 31) =>
        new(new DateOnly(year, fromMonth, fromDay), new DateOnly(year, toMonth, toDay));

    /// <summary>
    /// Contract pin for the adapter's identity: fully-qualified name, owning assembly, and the
    /// two interfaces the pipeline's adapter dispatch and priority ordering resolve it through.
    /// Written reflectively on purpose — a strongly-typed reference to a type that does not exist
    /// yet fails at the compiler, which is not an assertion failure.
    /// </summary>
    [Fact]
    public void Adapter_type_exists_in_the_Tiingo_assembly_and_implements_the_dividend_contracts()
    {
        object? found = typeof(TiingoPriceBarAdapter).Assembly
            .GetType("Boutquin.MarketData.Adapter.Tiingo.TiingoDividendHistoryAdapter");

        found.Should().NotBeNull(
            "M-3 ships TiingoDividendHistoryAdapter alongside the price adapter in the Tiingo assembly");

        var adapterType = (Type)found!;
        adapterType.GetInterfaces().Should().Contain(
            typeof(IDataSourceAdapter<DividendHistoryRequest, DividendPayment>),
            "the pipeline resolves dividend adapters through IDataSourceAdapter<DividendHistoryRequest, DividendPayment>");
        adapterType.GetInterfaces().Should().Contain(
            typeof(IPrioritizedAdapter),
            "the fallback orchestrator orders candidate adapters by IPrioritizedAdapter.Priority");
    }

    [Fact]
    public void ProviderKey_returns_tiingo() =>
        CreateAdapter().ProviderKey.Should().Be(new ProviderCode("tiingo"));

    [Fact]
    public void Priority_returns_100() =>
        CreateAdapter().Priority.Should().Be(100);

    [Theory]
    [InlineData("transport")]
    [InlineData("rawStore")]
    [InlineData("clock")]
    [InlineData("options")]
    [InlineData("logger")]
    public void Constructor_rejects_each_null_required_collaborator(string parameterName)
    {
        var transport = new FakeHttpDataTransport();
        var rawStore = new FakeRawDocumentStore();
        var clock = new FakeClock();
        var options = Options.Create(new TiingoOptions { ApiToken = "test-token" });
        var logger = NullLogger<TiingoDividendHistoryAdapter>.Instance;

        Action construct = parameterName switch
        {
            "transport" => () => _ = new TiingoDividendHistoryAdapter(null!, rawStore, clock, options, logger),
            "rawStore" => () => _ = new TiingoDividendHistoryAdapter(transport, null!, clock, options, logger),
            "clock" => () => _ = new TiingoDividendHistoryAdapter(transport, rawStore, null!, options, logger),
            "options" => () => _ = new TiingoDividendHistoryAdapter(transport, rawStore, clock, null!, logger),
            "logger" => () => _ = new TiingoDividendHistoryAdapter(transport, rawStore, clock, options, null!),
            _ => throw new InvalidOperationException($"Unknown constructor parameter '{parameterName}'."),
        };

        construct.Should().ThrowExactly<ArgumentNullException>()
            .WithParameterName(parameterName);
    }

    // ---------- CanHandle: US-venue guard, mirroring TiingoPriceBarAdapter ----------

    [Fact]
    public void Empty_symbol_requests_are_rejected_before_adapter_routing()
    {
        Action construct = () => _ = new DividendHistoryRequest([], MakeRange());

        construct.Should().ThrowExactly<ArgumentOutOfRangeException>(
            "the current kernel request contract rejects an identity-free request before CanHandle");
    }

    [Fact]
    public void CanHandle_returns_true_when_Exchange_is_null_preserving_venue_neutral_callers()
    {
        var request = new DividendHistoryRequest([new Symbol("SCHD")], MakeRange());
        CreateAdapter().CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_rejects_a_null_request_at_the_public_boundary()
    {
        Action handle = () => CreateAdapter().CanHandle(null!);

        handle.Should().ThrowExactly<ArgumentNullException>()
            .WithParameterName("request");
    }

    [Fact]
    public void CanHandle_returns_false_for_multi_symbol_requests()
    {
        var request = new DividendHistoryRequest(
            [new Symbol("SCHD"), new Symbol("VIG")],
            MakeRange());

        CreateAdapter().CanHandle(request).Should().BeFalse(
            "DividendPayment has no symbol field, so a flat multi-symbol response cannot preserve payment ownership");
    }

    [Theory]
    [InlineData(ExchangeCode.XNYS)]
    [InlineData(ExchangeCode.XNAS)]
    public void CanHandle_returns_true_for_supported_US_exchanges(ExchangeCode exchange)
    {
        var request = new DividendHistoryRequest([new Symbol("SCHD")], MakeRange()) { Exchange = exchange };
        CreateAdapter().CanHandle(request).Should().BeTrue();
    }

    [Theory]
    [InlineData(ExchangeCode.XTSE)]
    [InlineData(ExchangeCode.NEOE)]
    [InlineData(ExchangeCode.XLON)]
    [InlineData(ExchangeCode.XPAR)]
    public void CanHandle_returns_false_for_non_US_exchanges(ExchangeCode exchange)
    {
        // This adapter stamps Currency.USD on every payment it emits, which is only correct for
        // a US listing. A foreign venue hint must therefore make the adapter self-exclude so the
        // pipeline falls through to a venue-appropriate source rather than returning
        // USD-denominated distributions for a non-US listing — the same guard, on the same
        // exchange set, that the sibling price adapter applies.
        var request = new DividendHistoryRequest([new Symbol("XIC")], MakeRange()) { Exchange = exchange };
        CreateAdapter().CanHandle(request).Should().BeFalse();
    }

    [Fact]
    public async Task FetchAsync_rejects_an_unsupported_exchange_before_transport_or_raw_persistence()
    {
        var transport = new FakeHttpDataTransport();
        var rawStore = new FakeRawDocumentStore();
        var adapter = CreateAdapter(transport, rawStore);
        var request = new DividendHistoryRequest([new Symbol("XIC")], MakeRange())
        {
            Exchange = ExchangeCode.XTSE,
        };

        Func<Task> fetch = () => adapter.FetchAsync(request, CancellationToken.None);

        await fetch.Should().ThrowExactlyAsync<ArgumentException>()
            .WithParameterName("request");
        transport.RequestedUris.Should().BeEmpty(
            "an unsupported venue must be refused before the authenticated transport is called");
        rawStore.SavedKeys.Should().BeEmpty(
            "an unsupported venue must be refused before a raw document can be persisted");
    }

    [Theory]
    [InlineData(ExchangeCode.XNYS)]
    [InlineData(ExchangeCode.XNAS)]
    public async Task FetchAsync_accepts_each_supported_US_exchange(ExchangeCode exchange)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", "[]");
        var adapter = CreateAdapter(transport);
        var request = new DividendHistoryRequest([new Symbol("SCHD")], MakeRange())
        {
            Exchange = exchange,
        };

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Issues.Should().BeEmpty();
        transport.RequestedUris.Should().ContainSingle();
    }

    // ---------- Extraction from the shared daily-price payload ----------

    /// <summary>
    /// A SCHD-shaped slice of Tiingo's daily-price payload — the same endpoint the price adapter
    /// reads. It deliberately interleaves every negative shape with the two ex-dates:
    /// an explicit <c>divCash</c> of zero (2011-10-20), a bar with no <c>divCash</c> property at
    /// all (2011-11-15), and a bar whose <c>divCash</c> is JSON <c>null</c> (2012-01-10).
    /// The 2011-12-19 → 0.1217 pair is the regression anchor taken from the observed feed.
    /// </summary>
    private const string SchdShapedPayload = """
        [
            {"date":"2011-10-20T00:00:00.000Z","open":25.05,"high":25.20,"low":24.90,"close":25.10,"adjClose":21.40,"volume":120000,"divCash":0.0,"splitFactor":1.0},
            {"date":"2011-11-15T00:00:00.000Z","open":25.60,"high":25.80,"low":25.40,"close":25.70,"adjClose":21.95,"volume":98000,"splitFactor":1.0},
            {"date":"2011-12-19T00:00:00.000Z","open":24.80,"high":25.00,"low":24.70,"close":24.95,"adjClose":21.60,"volume":210000,"divCash":0.1217,"splitFactor":1.0},
            {"date":"2012-01-10T00:00:00.000Z","open":25.30,"high":25.50,"low":25.20,"close":25.45,"adjClose":22.10,"volume":150000,"divCash":null,"splitFactor":1.0},
            {"date":"2012-03-23T00:00:00.000Z","open":26.10,"high":26.30,"low":26.00,"close":26.20,"adjClose":22.90,"volume":175000,"divCash":0.1350,"splitFactor":1.0}
        ]
        """;

    private static DividendHistoryRequest SchdRequest() =>
        new(
            [new Symbol("SCHD")],
            new DateRange(new DateOnly(2011, 10, 1), new DateOnly(2012, 3, 31)));

    [Fact]
    public async Task FetchAsync_emits_exactly_one_payment_per_positive_divCash_bar()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", SchdShapedPayload);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(SchdRequest(), CancellationToken.None);

        result.Payload.Should().HaveCount(2,
            "only the two bars carrying a positive divCash are ex-dates — the zero, absent and null divCash bars are ordinary trading days and must yield no record at all");
        result.Payload[0].Should().Be(
            new DividendPayment(new DateOnly(2011, 12, 19), 0.1217m, Currency.USD, DividendType.Unclassified));
        result.Payload[1].Should().Be(
            new DividendPayment(new DateOnly(2012, 3, 23), 0.1350m, Currency.USD, DividendType.Unclassified));
    }

    [Fact]
    public async Task FetchAsync_emits_no_payment_for_a_negative_divCash_value()
    {
        const string NegativeDividendPayload = """
            [{"date":"2024-03-20T00:00:00.000Z","divCash":-0.50}]
            """;
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", NegativeDividendPayload);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new DividendHistoryRequest([new Symbol("SCHD")], MakeRange(2024, 3, 1, 3, 31)),
            CancellationToken.None);

        result.Payload.Should().BeEmpty(
            "only positive divCash values represent dividend payments");
        result.Issues.Should().BeEmpty(
            "a numeric non-positive divCash is an ordinary non-payment row, not malformed provider data");
    }

    [Theory]
    [InlineData("\"0.50\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task FetchAsync_routes_present_non_numeric_divCash_through_malformed_payload_behavior(
        string divCash)
    {
        var payload = $$"""
            [{"date":"2024-03-20T00:00:00.000Z","divCash":{{divCash}}}]
            """;
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", payload);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new DividendHistoryRequest([new Symbol("SCHD")], MakeRange(2024, 3, 1, 3, 31)),
            CancellationToken.None);

        result.Payload.Should().BeEmpty("malformed provider data must not produce a dividend payment");
        result.Issues.Should().ContainSingle(
            "a present non-null divCash of the wrong JSON kind must use the existing malformed-provider contract");
        result.Issues[0].Code.Should().Be(new IssueCode("FETCH_FAILED"));
    }

    [Fact]
    public async Task FetchAsync_collapses_duplicate_provider_bars_to_one_payment_per_ex_date()
    {
        const string DuplicateExDatePayload = """
            [
                {"date":"2024-03-20T00:00:00.000Z","divCash":0.50},
                {"date":"2024-03-20T00:00:00.000Z","divCash":0.50}
            ]
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", DuplicateExDatePayload);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new DividendHistoryRequest([new Symbol("SCHD")], MakeRange(2024, 3, 1, 3, 31)),
            CancellationToken.None);

        result.Payload.Should().ContainSingle(
            "Tiingo divCash is already a per-date aggregate, so repeated bars for one ex-date must not double-count a distribution");
        result.Payload[0].Should().Be(
            new DividendPayment(new DateOnly(2024, 3, 20), 0.50m, Currency.USD, DividendType.Unclassified));
    }

    [Theory]
    [InlineData("0.50", "0.75")]
    [InlineData("0.75", "0.50")]
    public async Task FetchAsync_reports_conflicting_duplicate_ex_dates_independently_of_response_order(
        string firstAmount,
        string secondAmount)
    {
        var payload = $$"""
            [
                {"date":"2024-03-20T00:00:00.000Z","divCash":{{firstAmount}}},
                {"date":"2024-03-20T00:00:00.000Z","divCash":{{secondAmount}}}
            ]
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", payload);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new DividendHistoryRequest([new Symbol("SCHD")], MakeRange(2024, 3, 1, 3, 31)),
            CancellationToken.None);

        result.Payload.Should().BeEmpty(
            "conflicting values for one ex-date are malformed provider data, not an order-dependent payment");
        result.Issues.Should().ContainSingle().Which.Code.Should().Be(new IssueCode("FETCH_FAILED"));
    }

    [Fact]
    public async Task FetchAsync_emits_only_rows_inside_the_inclusive_requested_range()
    {
        const string Payload = """
            [
                {"date":"2024-02-29T00:00:00.000Z","divCash":0.10},
                {"date":"2024-03-01T00:00:00.000Z","divCash":0.20},
                {"date":"2024-03-31T00:00:00.000Z","divCash":0.30},
                {"date":"2024-04-01T00:00:00.000Z","divCash":0.40}
            ]
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", Payload);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new DividendHistoryRequest([new Symbol("SCHD")], MakeRange(2024, 3, 1, 3, 31)),
            CancellationToken.None);

        result.Payload.Select(payment => payment.ExDate).Should().Equal(
            new DateOnly(2024, 3, 1),
            new DateOnly(2024, 3, 31));
    }

    [Theory]
    [InlineData("../iex/price")]
    [InlineData("AAPL/MSFT")]
    [InlineData("AAPL?token=attacker")]
    public async Task FetchAsync_rejects_provider_invalid_ticker_before_transport_or_raw_persistence(string ticker)
    {
        var transport = new FakeHttpDataTransport();
        var rawStore = new FakeRawDocumentStore();
        var adapter = CreateAdapter(transport, rawStore);
        var request = new DividendHistoryRequest(
            [new Symbol(ticker)],
            MakeRange(2024, 3, 1, 3, 31));

        Func<Task> act = () => adapter.FetchAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("request");
        transport.RequestedUris.Should().BeEmpty(
            "invalid ticker text must be refused before the authenticated transport is called");
        rawStore.SavedKeys.Should().BeEmpty(
            "invalid ticker text must be refused before a raw-document key is constructed");
    }

    [Fact]
    public async Task FetchAsync_rejects_ticker_exceeding_the_raw_store_segment_boundary()
    {
        var transport = new FakeHttpDataTransport();
        var rawStore = new FakeRawDocumentStore();
        var adapter = CreateAdapter(transport, rawStore);
        var request = new DividendHistoryRequest(
            [new Symbol(new string('A', 251))],
            MakeRange(2024, 3, 1, 3, 31));

        Func<Task> act = () => adapter.FetchAsync(request, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<ArgumentException>()
            .WithParameterName("request");
        transport.RequestedUris.Should().BeEmpty();
        rawStore.SavedKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_accepts_ticker_at_the_raw_store_segment_boundary()
    {
        var ticker = new string('A', 250);
        var transport = new FakeHttpDataTransport();
        transport.RespondTo($"tiingo/daily/{ticker}/prices", "[]");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new DividendHistoryRequest([new Symbol(ticker)], MakeRange()),
            CancellationToken.None);

        transport.RequestedUris.Should().ContainSingle();
        result.Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("BRK-B")]
    [InlineData("BF.B")]
    public async Task FetchAsync_preserves_legitimate_Tiingo_ticker_forms(string ticker)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo($"tiingo/daily/{ticker}/prices", "[]");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new DividendHistoryRequest([new Symbol(ticker)], MakeRange()),
            CancellationToken.None);

        result.Issues.Should().BeEmpty();
        transport.RequestedUris.Should().ContainSingle()
            .Which.AbsolutePath.Should().Be($"/tiingo/daily/{ticker}/prices");
    }

    [Theory]
    [InlineData("2024/03/20T00:00:00.000Z")]
    [InlineData("03/20/2024T00:00:00.000Z")]
    public async Task FetchAsync_routes_non_ISO_provider_dates_through_malformed_payload_behavior(string date)
    {
        var payload = $$"""
            [{"date":"{{date}}","divCash":0.50}]
            """;
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", payload);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new DividendHistoryRequest([new Symbol("SCHD")], MakeRange(2024, 3, 1, 3, 31)),
            CancellationToken.None);

        result.Payload.Should().BeEmpty("non-ISO provider dates are malformed payloads");
        result.Issues.Should().ContainSingle().Which.Code.Should().Be(new IssueCode("FETCH_FAILED"));
    }

    [Fact]
    public void Generated_XML_contains_concrete_contracts_for_public_dividend_adapter_members()
    {
        var xmlPath = Path.Combine(AppContext.BaseDirectory, "Boutquin.MarketData.Adapter.Tiingo.xml");
        var document = XDocument.Load(xmlPath);
        var memberNames = new[]
        {
            "P:Boutquin.MarketData.Adapter.Tiingo.TiingoDividendHistoryAdapter.ProviderKey",
            "P:Boutquin.MarketData.Adapter.Tiingo.TiingoDividendHistoryAdapter.Priority",
            "M:Boutquin.MarketData.Adapter.Tiingo.TiingoDividendHistoryAdapter.CanHandle(Boutquin.MarketData.Abstractions.Requests.DividendHistoryRequest)",
            "M:Boutquin.MarketData.Adapter.Tiingo.TiingoDividendHistoryAdapter.FetchAsync(Boutquin.MarketData.Abstractions.Requests.DividendHistoryRequest,System.Threading.CancellationToken)",
        };

        foreach (var memberName in memberNames)
        {
            var member = document.Descendants("member")
                .Single(element => string.Equals((string?)element.Attribute("name"), memberName, StringComparison.Ordinal));

            member.Element("inheritdoc").Should().BeNull($"{memberName} must ship a concrete consumer contract");
            member.Element("summary")?.Value.Should().NotBeNullOrWhiteSpace(
                $"{memberName} must explain its contract in the packed XML documentation");
        }
    }

    [Fact]
    public async Task FetchAsync_propagates_cancellation_requested_by_the_caller()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(cancellation.Token));
        var adapter = CreateAdapter(transport);

        Func<Task> act = () => adapter.FetchAsync(SchdRequest(), cancellation.Token);

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>(
            "the caller's cancellation is control flow and must not become a FETCH_FAILED warning envelope");
        thrown.Which.CancellationToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task FetchAsync_rejects_a_null_request_at_the_public_boundary()
    {
        Func<Task> fetch = () => CreateAdapter().FetchAsync(null!, CancellationToken.None);

        await fetch.Should().ThrowExactlyAsync<ArgumentNullException>()
            .WithParameterName("request");
    }

    // ---------- The Unclassified inference is audited, never silent ----------

    /// <summary>
    /// Minimal <see cref="ILogger{TCategoryName}"/> double that keeps every formatted entry so a
    /// test can assert on captured log state rather than on a call merely not throwing.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public async Task FetchAsync_logs_the_Unclassified_inference_for_every_emitted_payment()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", SchdShapedPayload);
        var logger = new CapturingLogger<TiingoDividendHistoryAdapter>();
        var adapter = CreateAdapter(transport, logger: logger);

        var result = await adapter.FetchAsync(SchdRequest(), CancellationToken.None);

        // Pinned literally, not as result.Payload.Count: an assertion that compared two numbers
        // which could both be zero would stay green if extraction silently stopped emitting.
        result.Payload.Should().HaveCount(2);

        var inferences = logger.Entries
            .Where(e => e.Message.Contains("Unclassified", StringComparison.Ordinal))
            .ToList();

        inferences.Should().HaveCount(2,
            "the classification is inferred rather than reported by the source, so every emitted payment must leave an audit trail — a silent fallback is what this assertion exists to prevent");
        inferences.Should().OnlyContain(e => e.Message.Contains("SCHD", StringComparison.Ordinal),
            "the entry must identify which symbol the inference was made for");
        inferences.Should().OnlyContain(e => e.Level >= LogLevel.Information,
            "an inference logged below Information would be invisible at normal host verbosity");
    }

    // ---------- Raw persistence, provenance, event-sparse coverage ----------

    [Fact]
    public async Task FetchAsync_persists_the_raw_payload_under_a_dividend_scoped_key()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", SchdShapedPayload);
        var rawStore = new FakeRawDocumentStore();
        var adapter = CreateAdapter(transport, rawStore);

        await adapter.FetchAsync(SchdRequest(), CancellationToken.None);

        rawStore.SavedKeys.Should().ContainSingle();
        rawStore.SavedKeys[0].Should().Be("tiingo/dividends/SCHD/20111001-20120331");
        rawStore.SavedKeys[0].Should().NotBe("tiingo/daily/SCHD/20111001-20120331",
            "the dividend projection is scoped separately from the price adapter's raw key so that fetching one never overwrites the other's stored payload for the same symbol and range");
        rawStore.Store[rawStore.SavedKeys[0]].Should().Be(SchdShapedPayload,
            "the raw document must be the untouched upstream response, persisted before parsing");
    }

    [Fact]
    public async Task FetchAsync_emits_provenance_for_the_fetched_symbol()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", SchdShapedPayload);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(SchdRequest(), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        var provenance = result.Provenance[0];
        provenance.Provider.Should().Be(new ProviderCode("tiingo"));
        provenance.Dataset.Should().Be("SCHD");
        provenance.LicenseFlag.Should().Be(LicenseType.Free);
        provenance.RetrievalMode.Should().Be(RetrievalMode.Api);
        provenance.Freshness.Should().Be(FreshnessClass.EndOfDay);
        provenance.RetrievedAtUtc.Should().Be(s_clock.UtcNow);
        provenance.SourceUrl.Should().Contain("/tiingo/daily/SCHD/prices",
            "the dividend projection is read off the daily-price endpoint, and provenance must name the URL actually called");
        provenance.DataDate.Should().Be(new DateOnly(2012, 3, 23),
            "the latest ex-date observed is how far forward this payload is known to be good");
    }

    [Fact]
    public async Task FetchAsync_reports_complete_coverage_and_no_gaps_across_an_event_sparse_range()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", SchdShapedPayload);
        var adapter = CreateAdapter(transport);

        // Six months of calendar time containing exactly two distributions. Under the price
        // adapter's trading-day coverage model this range would look ~98% empty and raise a gap
        // issue for every non-payment day; for an event series those days are the normal case.
        var result = await adapter.FetchAsync(SchdRequest(), CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(2,
            "coverage for an event series is measured against the ex-dates the payload actually carries, not against the trading days in the range");
        result.Coverage.ReturnedPoints.Should().Be(2);
        result.Coverage.MissingPoints.Should().Be(0);
        result.Coverage.CoverageRatio.Should().Be(1m);
        result.Coverage.Status.Should().Be(CompletenessStatus.Complete);
    }

    [Fact]
    public async Task FetchAsync_raises_no_gap_issues_for_non_payment_days()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", SchdShapedPayload);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(SchdRequest(), CancellationToken.None);

        // Asserted in its own test rather than after the coverage assertions above, so that a
        // regression to trading-day coverage fails *this* pin directly instead of being masked by
        // an earlier assertion short-circuiting the run.
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"),
            "a day with no distribution is not a missing observation, and flagging one would drown real data-quality signals in false positives");
        result.Issues.Should().BeEmpty(
            "a successful fetch over an event-sparse range is a clean result, not a degraded one");
    }

    // ---------- Batch and degraded-path behaviour ----------

    [Fact]
    public async Task FetchAsync_rejects_multi_symbol_requests_before_contacting_Tiingo()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", SchdShapedPayload);
        var adapter = CreateAdapter(transport);

        var request = new DividendHistoryRequest(
            [new Symbol("SCHD"), new Symbol("VIG")],
            new DateRange(new DateOnly(2011, 10, 1), new DateOnly(2012, 6, 30)));

        Func<Task> act = () => adapter.FetchAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>(
            "DividendPayment carries no symbol identity, so a flat batch result would make ownership ambiguous");
        transport.RequestedUris.Should().BeEmpty(
            "an unsupported batch must be refused before any provider work or raw persistence occurs");
    }

    [Fact]
    public async Task FetchAsync_records_a_non_cancellation_transport_failure_as_an_issue()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new HttpRequestException("provider unavailable"));
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(SchdRequest(), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle().Which.Code.Should().Be(new IssueCode("FETCH_FAILED"));
        result.Coverage.Status.Should().Be(CompletenessStatus.Missing);
    }

    [Fact]
    public async Task FetchAsync_sanitizes_consumer_issues_and_credential_bearing_logs()
    {
        const string HostileDetail =
            "credential=secret-token; path=/srv/private/tenant-42/raw/provider.json; connection=db.internal:5432";
        var failure = new IOException(HostileDetail);
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(failure);
        var logger = new CapturingLogger<TiingoDividendHistoryAdapter>();
        var adapter = CreateAdapter(transport, logger: logger);

        var result = await adapter.FetchAsync(SchdRequest(), CancellationToken.None);

        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.Code.Should().Be(new IssueCode("FETCH_FAILED"));
        issue.Message.Should().Be("Tiingo dividend fetch failed.");
        issue.Message.Should().NotContain("secret-token");
        issue.Message.Should().NotContain("/srv/private");
        issue.Message.Should().NotContain("db.internal");

        var warning = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Subject;
        warning.Exception.Should().BeNull("exceptions can contain credential-bearing request URLs");
        warning.Message.Should().Contain(nameof(IOException)).And.NotContain("secret-token").And.NotContain("/srv/private").And.NotContain("db.internal");
    }

    [Fact]
    public async Task FetchAsync_reports_complete_coverage_when_the_range_contains_no_distributions()
    {
        const string NoDistributionsPayload = """
            [
                {"date":"2024-01-08T00:00:00.000Z","open":100,"high":101,"low":99,"close":100.5,"adjClose":100.5,"volume":1000,"divCash":0.0,"splitFactor":1.0},
                {"date":"2024-01-09T00:00:00.000Z","open":100,"high":101,"low":99,"close":100.5,"adjClose":100.5,"volume":1000,"divCash":0.0,"splitFactor":1.0}
            ]
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD", NoDistributionsPayload);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new DividendHistoryRequest([new Symbol("SCHD")], MakeRange()), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Coverage.Status.Should().Be(CompletenessStatus.Complete,
            "a symbol that simply paid nothing in the window is fully covered, not missing data");
        result.Issues.Should().BeEmpty();
        result.Provenance.Should().ContainSingle()
            .Which.DataDate.Should().BeNull("there is no ex-date to date the payload by");
    }

    // ---------- Reachability through the production composition root ----------

    [Fact]
    public void AddMarketDataTiingo_registers_the_dividend_adapter_for_pipeline_dispatch()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IHttpDataTransport>(new FakeHttpDataTransport());
        services.AddSingleton<IRawDocumentStore>(new FakeRawDocumentStore());
        services.AddSingleton<IClock>(new FakeClock());
        services.AddMarketDataTiingo(o => o.ApiToken = "test-token");

        using var provider = services.BuildServiceProvider();

        // Resolved exactly the way the pipeline resolves candidate adapters before dispatch, so
        // this fails if the type exists but nothing in the production wiring can reach it.
        provider.GetServices<IDataSourceAdapter<DividendHistoryRequest, DividendPayment>>()
            .Should().ContainSingle()
            .Which.Should().BeOfType<TiingoDividendHistoryAdapter>();
    }

    [Fact]
    public void AddMarketDataTiingo_leaves_the_existing_adapter_registrations_intact()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IHttpDataTransport>(new FakeHttpDataTransport());
        services.AddSingleton<IRawDocumentStore>(new FakeRawDocumentStore());
        services.AddSingleton<IClock>(new FakeClock());
        services.AddMarketDataTiingo(o => o.ApiToken = "test-token");

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IDataSourceAdapter<PriceHistoryRequest, Bar>>()
            .Should().ContainSingle().Which.Should().BeOfType<TiingoPriceBarAdapter>();
        provider.GetServices<IDataSourceAdapter<InstrumentMetadataRequest, InstrumentMetadata>>()
            .Should().ContainSingle().Which.Should().BeOfType<TiingoInstrumentMetadataAdapter>();
    }
}
