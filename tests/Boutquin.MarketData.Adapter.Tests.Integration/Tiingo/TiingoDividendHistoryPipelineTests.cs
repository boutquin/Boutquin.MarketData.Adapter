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

using Boutquin.Domain.Enumerations;
using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Keys;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.Tiingo;
using Boutquin.MarketData.Cache.TimeSeries;
using Boutquin.MarketData.DependencyInjection;
using Boutquin.MarketData.Transport.Http;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Integration.Tiingo;

/// <summary>
/// End-to-end proof that a real host composition reaches the Tiingo dividend adapter: the real
/// kernel (<c>AddMarketDataKernel</c>) and the real provider registrations
/// (<c>AddMarketDataTiingo</c>) are composed together, and dividends are fetched by asking
/// <see cref="IDataPipeline.ExecuteAsync{TRequest, TRecord}"/>. Nothing in the assertion path
/// constructs an adapter — the pipeline selects one.
/// </summary>
/// <remarks>
/// <para>
/// The ONLY substituted collaborator is the network: <see cref="IHttpDataTransport"/> is replaced
/// with a canned-payload double. Everything between the request and the records — adapter
/// selection over the registered enumeration, the fallback orchestrator, response evaluation,
/// coverage and envelope assembly — is production code.
/// </para>
/// <para>
/// This test complements, rather than duplicates, the kernel repo's own dispatch tests. Those run
/// against kernel sources with a fake adapter and prove the kernel's plumbing; this one runs the
/// real Tiingo adapter against the kernel as published, and so is the only place the two halves
/// are proven to fit together. It therefore pins the kernel at the version this repo references,
/// and picks up later kernel changes only when that pin moves.
/// </para>
/// </remarks>
public sealed class TiingoDividendHistoryPipelineTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"tiingo-dividend-pipeline-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    /// <summary>
    /// A SCHD-shaped daily payload: two ex-dates carrying cash, interleaved with ordinary trading
    /// days whose <c>divCash</c> is zero. The <c>2011-12-19 → 0.1217</c> bar is the regression
    /// anchor observed in a live provider payload.
    /// </summary>
    private const string SchdDailyPayload = """
        [
            {"date":"2011-10-20T00:00:00.000Z","open":25.05,"high":25.20,"low":24.90,"close":25.10,"adjClose":21.40,"volume":120000,"divCash":0.0,"splitFactor":1.0},
            {"date":"2011-12-19T00:00:00.000Z","open":24.80,"high":25.00,"low":24.70,"close":24.95,"adjClose":21.60,"volume":210000,"divCash":0.1217,"splitFactor":1.0},
            {"date":"2012-01-10T00:00:00.000Z","open":25.30,"high":25.50,"low":25.20,"close":25.45,"adjClose":22.10,"volume":150000,"divCash":0.0,"splitFactor":1.0},
            {"date":"2012-03-23T00:00:00.000Z","open":26.10,"high":26.30,"low":26.00,"close":26.20,"adjClose":22.90,"volume":175000,"divCash":0.1350,"splitFactor":1.0}
        ]
        """;

    private const string OverPrecisionDividendPayload = """
        [
            {"date":"2025-09-26T00:00:00.000Z","divCash":0.98765}
        ]
        """;

    /// <param name="transport">The canned-payload double standing in for the network.</param>
    /// <param name="slot">
    /// Per-host storage discriminator. Two hosts in one test must not share a snapshot directory,
    /// or the first host's persisted result would answer the second host's request.
    /// </param>
    /// <param name="registerTiingo">
    /// Whether to call <c>AddMarketDataTiingo</c>. Passing <see langword="false"/> composes a host
    /// that has the kernel but no provider — the counterfactual that makes a green here meaningful.
    /// </param>
    /// <param name="useTimeSeriesStore">Whether the host enables the opt-in row-store path.</param>
    /// <param name="registerDefaultNormalizers">
    /// Whether the host opts into the kernel's default record normalizers.
    /// </param>
    private ServiceProvider BuildRealHost(
        FakeHttpDataTransport transport,
        string slot,
        bool registerTiingo = true,
        bool useTimeSeriesStore = false,
        bool registerDefaultNormalizers = true)
    {
        var root = Path.Combine(_tempDir, slot);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // The production composition roots, exactly as a host calls them.
        services.AddMarketDataKernel(
            snapshotDirectory: Path.Combine(root, "snapshots"),
            rawDocumentDirectory: Path.Combine(root, "raw"),
            checkpointDirectory: Path.Combine(root, "checkpoints"),
            eventLogDirectory: Path.Combine(root, "events"),
            timeSeriesDirectory: Path.Combine(root, "timeseries"),
            configurePipeline: options =>
            {
                options.UseTimeSeriesStore = useTimeSeriesStore;
                options.RegisterDefaultNormalizers = registerDefaultNormalizers;
            });

        if (registerTiingo)
        {
            services.AddMarketDataTiingo(o => o.ApiToken = "test-token");
        }

        // Cut the network, and only the network. Registered last so it wins resolution over the
        // kernel's real HTTP transport.
        services.AddSingleton<IHttpDataTransport>(transport);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ExecuteAsync_fetches_dividends_through_the_real_kernel_and_Tiingo_composition()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD/prices", SchdDailyPayload);

        using var provider = BuildRealHost(transport, "dispatch");
        var pipeline = provider.GetRequiredService<IDataPipeline>();

        var result = await pipeline.ExecuteAsync<DividendHistoryRequest, DividendPayment>(
            new DividendHistoryRequest(
                [new Symbol("SCHD")],
                new DateRange(new DateOnly(2011, 10, 1), new DateOnly(2012, 3, 31))));

        transport.RequestedUris.Should().ContainSingle(
            "the pipeline reached the provider through the adapter it selected — the records below "
            + "are the result of a real dispatch, not of anything this test constructed");

        result.Payload.Should().HaveCount(2,
            "only the two bars carrying a positive divCash are ex-dates");

        result.Payload[0].ExDate.Should().Be(new DateOnly(2011, 12, 19));
        result.Payload[0].Amount.Should().Be(0.1217m);
        result.Payload[0].Currency.Should().Be(Currency.USD);
        result.Payload[0].Type.Should().Be(DividendType.Unclassified);

        result.Payload[1].ExDate.Should().Be(new DateOnly(2012, 3, 23));
        result.Payload[1].Amount.Should().Be(0.1350m);
        result.Payload[1].Currency.Should().Be(Currency.USD);
        result.Payload[1].Type.Should().Be(DividendType.Unclassified);
    }

    [Fact]
    public async Task ExecuteAsync_splits_default_tier_multi_symbol_dividends_and_replays_aggregate_from_cache()
    {
        const string SchdPayload = """
            [
                {"date":"2024-03-20T00:00:00.000Z","divCash":0.50}
            ]
            """;
        const string VigPayload = """
            [
                {"date":"2024-03-21T00:00:00.000Z","divCash":0.75}
            ]
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD/prices", SchdPayload);
        transport.RespondTo("tiingo/daily/VIG/prices", VigPayload);

        using var provider = BuildRealHost(transport, "default-multi-symbol");
        var pipeline = provider.GetRequiredService<IDataPipeline>();
        var request = new DividendHistoryRequest(
            [new Symbol("SCHD"), new Symbol("VIG")],
            new DateRange(new DateOnly(2024, 1, 1), new DateOnly(2024, 3, 31)));

        var live = await pipeline.ExecuteAsync<DividendHistoryRequest, DividendPayment>(request);

        transport.RequestedUris.Should().SatisfyRespectively(
            uri => uri.AbsoluteUri.Should().Contain("/tiingo/daily/SCHD/prices"),
            uri => uri.AbsoluteUri.Should().Contain("/tiingo/daily/VIG/prices"));
        live.Payload.Should().Equal(
            new DividendPayment(
                new DateOnly(2024, 3, 20), 0.50m, Currency.USD, DividendType.Unclassified),
            new DividendPayment(
                new DateOnly(2024, 3, 21), 0.75m, Currency.USD, DividendType.Unclassified));
        live.Provenance.Select(provenance => provenance.Dataset)
            .Should().Equal(["SCHD", "VIG"],
                "each partition's provenance must retain ownership of its payment");
        live.Issues.Should().NotContain(issue => issue.Code == IssueCode.PartitionFailed);

        var cached = await pipeline.ExecuteAsync<DividendHistoryRequest, DividendPayment>(request);

        transport.RequestedUris.Should().HaveCount(2,
            "the second public pipeline call must replay the owned aggregate without another provider request");
        cached.Payload.Should().Equal(live.Payload);
        cached.Issues.Should().NotContain(issue => issue.Code == IssueCode.PartitionFailed);
        cached.Provenance.Select(provenance => provenance.Dataset)
            .Should().Equal("SCHD", "VIG");
        cached.Provenance.Should().OnlyContain(provenance =>
            provenance.RetrievalMode == RetrievalMode.Cache && provenance.SourceUrl == null);
    }

    [Fact]
    public async Task Time_series_host_normalizes_persists_and_replays_real_Tiingo_dividends()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD/prices", OverPrecisionDividendPayload);

        using var provider = BuildRealHost(
            transport, "normalized-time-series", useTimeSeriesStore: true);
        provider.GetService<INormalizer<DividendPayment, DividendPayment>>()
            .Should().NotBeNull("the real host opted into the kernel dividend normalizer");
        var pipeline = provider.GetRequiredService<IDataPipeline>();
        var request = new DividendHistoryRequest(
            [new Symbol("SCHD")],
            new DateRange(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)));

        var live = await pipeline.ExecuteAsync<DividendHistoryRequest, DividendPayment>(request);
        var cached = await pipeline.ExecuteAsync<DividendHistoryRequest, DividendPayment>(request);

        live.Payload.Should().ContainSingle().Which.Amount.Should().Be(0.9877m,
            "the host's normalizer rounds the deliberately over-precision provider amount");
        cached.Payload.Should().ContainSingle().Which.Amount.Should().Be(0.9877m);
        transport.RequestedUris.Should().ContainSingle("the second production call is a cache replay");

        var store = provider.GetRequiredService<ITimeSeriesStoreRegistry>()
            .Resolve<DividendPaymentDatasetKey, DividendPayment>();
        store.Should().NotBeNull();
        var persisted = new List<DividendPayment>();
        await foreach (var payment in store!.GetRangeAsync(
            new DividendPaymentDatasetKey("tiingo", "SCHD"), request.Range.From, request.Range.To))
        {
            persisted.Add(payment);
        }

        persisted.Should().ContainSingle().Which.Amount.Should().Be(0.9877m,
            "normalization occurs before the provider-keyed append");
    }

    [Fact]
    public async Task ExecutePartitionedAsync_preserves_multi_symbol_payment_ownership_by_dataset_key()
    {
        const string EqualPaymentPayload = """
            [
                {"date":"2024-03-20T00:00:00.000Z","divCash":0.50}
            ]
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD/prices", EqualPaymentPayload);
        transport.RespondTo("tiingo/daily/VIG/prices", EqualPaymentPayload);

        using var provider = BuildRealHost(transport, "partitioned-ownership");
        var pipeline = provider.GetRequiredService<IDataPipeline>();
        var request = new DividendHistoryRequest(
            [new Symbol("SCHD"), new Symbol("VIG")],
            new DateRange(new DateOnly(2024, 1, 1), new DateOnly(2024, 3, 31)));

        var results = await pipeline.ExecutePartitionedAsync<
            DividendHistoryRequest,
            DividendPayment,
            Symbol,
            string>(
                request,
                static (batch, symbol) => new DividendHistoryRequest([symbol], batch.Range)
                {
                    Exchange = batch.Exchange,
                },
                static (_, symbol) => symbol.Ticker);

        results.Keys.Should().BeEquivalentTo("SCHD", "VIG");
        results["SCHD"].Payload.Should().ContainSingle().Which.Should().Be(
            new DividendPayment(new DateOnly(2024, 3, 20), 0.50m, Currency.USD, DividendType.Unclassified));
        results["VIG"].Payload.Should().ContainSingle().Which.Should().Be(
            new DividendPayment(new DateOnly(2024, 3, 20), 0.50m, Currency.USD, DividendType.Unclassified));
        transport.RequestedUris.Should().HaveCount(2,
            "the production partitioned entry point must dispatch one single-symbol fetch per owned result envelope");
    }

    /// <summary>
    /// The venue guard, observed through the pipeline rather than by calling <c>CanHandle</c>
    /// directly. A foreign listing must not be served USD-denominated distributions, so the
    /// adapter self-excludes and — with no other adapter able to serve it — the provider is never
    /// contacted at all.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_does_not_serve_a_non_US_listing_from_the_Tiingo_dividend_adapter()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD/prices", SchdDailyPayload);

        using var provider = BuildRealHost(transport, "foreign-venue", useTimeSeriesStore: true);
        var pipeline = provider.GetRequiredService<IDataPipeline>();

        var result = await pipeline.ExecuteAsync<DividendHistoryRequest, DividendPayment>(
            new DividendHistoryRequest(
                [new Symbol("SCHD")],
                new DateRange(new DateOnly(2011, 10, 1), new DateOnly(2012, 3, 31)))
            {
                Exchange = ExchangeCode.XTSE,
            });

        transport.RequestedUris.Should().BeEmpty(
            "the adapter excluded itself on the venue hint, so no provider call was made");
        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(issue => issue.Code == IssueCode.PartitionFailed,
            "the established no-handler contract is an issue-bearing empty envelope");
    }

    /// <summary>
    /// The discriminator. Same kernel, same transport, same request — the only thing removed is
    /// the provider registration, and the capability disappears. Without this, a green above could
    /// not distinguish "the Tiingo registration makes dividends reachable" from "dividends would
    /// have been returned anyway".
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_returns_no_dividends_when_the_Tiingo_provider_is_not_registered()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD/prices", SchdDailyPayload);

        using var provider = BuildRealHost(transport, "no-provider", registerTiingo: false);
        var pipeline = provider.GetRequiredService<IDataPipeline>();

        var result = await pipeline.ExecuteAsync<DividendHistoryRequest, DividendPayment>(
            new DividendHistoryRequest(
                [new Symbol("SCHD")],
                new DateRange(new DateOnly(2011, 10, 1), new DateOnly(2012, 3, 31))));

        transport.RequestedUris.Should().BeEmpty(
            "no adapter was registered to serve the request, so nothing called the provider");
        result.Payload.Should().BeEmpty();
    }

    /// <summary>
    /// A window in which the symbol distributed nothing — every bar carries <c>divCash: 0.0</c>,
    /// the ordinary shape for a non-paying ETF and for any payer between its ex-dates.
    /// </summary>
    private const string NonPayerDailyPayload = """
        [
            {"date":"2011-10-20T00:00:00.000Z","open":25.05,"high":25.20,"low":24.90,"close":25.10,"adjClose":25.10,"volume":120000,"divCash":0.0,"splitFactor":1.0},
            {"date":"2011-11-15T00:00:00.000Z","open":25.30,"high":25.45,"low":25.15,"close":25.40,"adjClose":25.40,"volume":132000,"divCash":0.0,"splitFactor":1.0},
            {"date":"2012-01-10T00:00:00.000Z","open":25.30,"high":25.50,"low":25.20,"close":25.45,"adjClose":25.45,"volume":150000,"divCash":0.0,"splitFactor":1.0}
        ]
        """;

    /// <summary>
    /// An empty dividend window must reach the caller as an empty SUCCESS — not as a failed
    /// partition — when the request is served by the REAL Tiingo adapter over the kernel as
    /// published to the local feed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the seam no other test covers. The kernel's own empty-window test drives a fake
    /// adapter, so it proves the evaluator handles the envelope shape it is handed; this one
    /// proves the real adapter actually PRODUCES that shape — an empty payload whose coverage is
    /// not <see cref="CompletenessStatus.Missing"/> — and that the two fit together end to end.
    /// </para>
    /// <para>
    /// It is also the regression guard on a real defect: before the kernel registered a
    /// dividend-specific response evaluator, the pipeline's default evaluator tested the empty
    /// payload BEFORE it examined coverage, so an ordinary non-payer surfaced as
    /// <c>Missing</c> coverage plus an error-severity <c>PARTITION_FAILED</c> issue —
    /// indistinguishable from a provider outage, and the common case for a consumer computing a
    /// trailing income yield across a universe. Because this project consumes the kernel as a
    /// package, the assertion only exercises that fix once the pin moves to a kernel that carries
    /// it; against an older pin it fails, which is exactly the signal wanted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_reports_a_dividend_free_window_as_an_empty_success()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/SCHD/prices", NonPayerDailyPayload);

        using var provider = BuildRealHost(transport, "empty-window");
        var pipeline = provider.GetRequiredService<IDataPipeline>();

        var result = await pipeline.ExecuteAsync<DividendHistoryRequest, DividendPayment>(
            new DividendHistoryRequest(
                [new Symbol("SCHD")],
                new DateRange(new DateOnly(2011, 10, 1), new DateOnly(2012, 3, 31))));

        transport.RequestedUris.Should().ContainSingle(
            "the provider was actually reached and answered — this is a real fetch that found no "
            + "ex-dates, not an unservable partition");

        result.Payload.Should().BeEmpty("the symbol distributed nothing in this window");

        result.Coverage.Status.Should().Be(CompletenessStatus.Complete,
            "the adapter reached the provider and parsed the window, so the data is not missing — "
            + "it is a complete answer that happens to contain no payments");

        result.Issues.Should().NotContain(i => i.Severity == IssueSeverity.Error,
            "a non-paying symbol is a legitimate answer; reporting it as an error would make "
            + "\"this ETF pays nothing\" indistinguishable from \"the provider is down\"");

        result.Issues.Should().NotContain(i => i.Code == IssueCode.PartitionFailed,
            "PARTITION_FAILED is the specific symptom the dividend response evaluator exists to "
            + "prevent for an empty-but-complete window");
    }

    [Theory]
    [InlineData("price")]
    [InlineData("metadata")]
    [InlineData("dividend")]
    public async Task ExecuteAsync_rejects_hostile_Tiingo_ticker_before_transport(string kind)
    {
        var transport = new FakeHttpDataTransport();
        using var provider = BuildRealHost(transport, $"hostile-{kind}");
        var pipeline = provider.GetRequiredService<IDataPipeline>();
        Symbol[] symbols = [new Symbol("../iex/price")];
        var range = new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 9));
        IReadOnlyList<DataIssue> issues;
        if (kind == "price")
        {
            var result = await pipeline.ExecuteAsync<PriceHistoryRequest, Bar>(new PriceHistoryRequest(symbols, range));
            result.Payload.Should().BeEmpty();
            issues = result.Issues;
        }
        else if (kind == "metadata")
        {
            var result = await pipeline.ExecuteAsync<InstrumentMetadataRequest, InstrumentMetadata>(new InstrumentMetadataRequest(symbols));
            result.Payload.Should().BeEmpty();
            issues = result.Issues;
        }
        else
        {
            var result = await pipeline.ExecuteAsync<DividendHistoryRequest, DividendPayment>(new DividendHistoryRequest(symbols, range));
            result.Payload.Should().BeEmpty();
            issues = result.Issues;
        }

        issues.Should().Contain(issue => issue.Code == IssueCode.PartitionFailed);
        transport.RequestedUris.Should().BeEmpty();
        Directory.EnumerateFiles(Path.Combine(_tempDir, $"hostile-{kind}", "raw"), "*", SearchOption.AllDirectories).Should().BeEmpty();
    }
}
