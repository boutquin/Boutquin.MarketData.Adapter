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

using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.Tiingo;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.Tiingo;

public sealed class TiingoInstrumentMetadataAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static TiingoInstrumentMetadataAdapter CreateAdapter(
        FakeHttpDataTransport? transport = null,
        FakeRawDocumentStore? rawStore = null)
    {
        return new TiingoInstrumentMetadataAdapter(
            transport ?? new FakeHttpDataTransport(),
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            Options.Create(new TiingoOptions { ApiToken = "test-token" }),
            NullLogger<TiingoInstrumentMetadataAdapter>.Instance);
    }

    [Fact]
    public void ProviderKey_returns_tiingo() =>
        CreateAdapter().ProviderKey.Should().Be(new ProviderCode("tiingo"));

    [Fact]
    public void Priority_returns_100() =>
        CreateAdapter().Priority.Should().Be(100);

    [Fact]
    public void CanHandle_returns_true_for_non_empty_symbols()
    {
        var request = new InstrumentMetadataRequest([new Symbol("AAPL")]);
        CreateAdapter().CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public void Empty_symbols_are_rejected_at_the_request_boundary()
    {
        Action act = () => new InstrumentMetadataRequest([]);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("symbols.Count");
    }

    // ---------- Exchange-hint filtering (CA research regression defense) ----------

    [Fact]
    public void CanHandle_returns_true_when_Exchange_is_null_preserving_back_compat()
    {
        var request = new InstrumentMetadataRequest([new Symbol("AAPL")]);
        CreateAdapter().CanHandle(request).Should().BeTrue();
    }

    [Theory]
    [InlineData(ExchangeCode.XNYS)]
    [InlineData(ExchangeCode.XNAS)]
    public void CanHandle_returns_true_for_supported_US_exchanges(ExchangeCode exchange)
    {
        var request = new InstrumentMetadataRequest([new Symbol("AAPL")]) { Exchange = exchange };
        CreateAdapter().CanHandle(request).Should().BeTrue();
    }

    [Theory]
    [InlineData(ExchangeCode.XTSE)]
    [InlineData(ExchangeCode.NEOE)]
    [InlineData(ExchangeCode.XLON)]
    public void CanHandle_returns_false_for_non_US_exchanges(ExchangeCode exchange)
    {
        // 2026-04-19 regression: a 22-ticker InstrumentMetadataRequest with bare CA
        // tickers (XIC/VCN/VFV/...) hit Tiingo and returned 22 records for entirely
        // unrelated US equities plus 13 NOT_FOUND. With the Exchange hint set to XTSE,
        // Tiingo's metadata adapter self-excludes and the orchestrator routes to
        // TwelveData, which has accurate TSX metadata.
        var request = new InstrumentMetadataRequest([new Symbol("XIC")]) { Exchange = exchange };
        CreateAdapter().CanHandle(request).Should().BeFalse();
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

        Func<Task> act = () => adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol(ticker)]),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("request");
        transport.RequestedUris.Should().BeEmpty();
        rawStore.SavedKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_rejects_ticker_exceeding_the_raw_store_segment_boundary()
    {
        var transport = new FakeHttpDataTransport();
        var rawStore = new FakeRawDocumentStore();
        var adapter = CreateAdapter(transport, rawStore);

        Func<Task> act = () => adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol(new string('A', 251))]),
            CancellationToken.None);

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
        transport.RespondTo($"tiingo/daily/{ticker}", $$"""{"ticker":"{{ticker}}","name":"Boundary instrument","exchangeCode":"XNAS"}""");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol(ticker)]),
            CancellationToken.None);

        transport.RequestedUris.Should().ContainSingle();
        result.Payload.Should().ContainSingle().Which.Symbol.Ticker.Should().Be(ticker);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(cancellation.Token));
        var adapter = CreateAdapter(transport);

        Func<Task> act = () => adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol("AAPL")]), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_keeps_transport_exception_detail_out_of_consumer_issues()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new IOException("credential=secret; path=/srv/private/raw.json"));
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol("AAPL")]), CancellationToken.None);

        result.Issues.Should().ContainSingle(issue => issue.Code == new IssueCode("FETCH_FAILED"))
            .Which.Message.Should().Be("Failed to fetch metadata for symbol 'AAPL'.");
    }

    [Fact]
    public async Task FetchAsync_rejects_metadata_without_provider_ticker()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/AAPL", "{}");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol("AAPL")]), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(issue => issue.Code == new IssueCode("NO_DATA"));
    }

    [Theory]
    [InlineData("\"NOT_A_MIC\"")]
    [InlineData("\"999\"")]
    [InlineData("null")]
    public async Task FetchAsync_rejects_metadata_without_a_known_exchange(string exchangeValue)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/AAPL",
            $$"""{"ticker":"AAPL","name":"Apple","exchangeCode":{{exchangeValue}}}""");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol("AAPL")]), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Provenance.Should().BeEmpty();
        result.Issues.Should().ContainSingle(issue => issue.Code == new IssueCode("NO_DATA"));
        result.Coverage.ReturnedPoints.Should().Be(0);
    }

    [Fact]
    public async Task FetchAsync_rejects_metadata_with_a_conflicting_exchange_hint()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/AAPL",
            """{"ticker":"AAPL","name":"Apple","exchangeCode":"XNAS"}""");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol("AAPL")]) { Exchange = ExchangeCode.XNYS },
            CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Provenance.Should().BeEmpty();
        result.Issues.Should().ContainSingle(issue => issue.Code == new IssueCode("EXCHANGE_MISMATCH"));
        result.Coverage.ReturnedPoints.Should().Be(0);
    }

    [Fact]
    public async Task FetchAsync_rejects_absent_provider_identity_when_request_is_UNKNOWN()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/UNKNOWN", "{}");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol("UNKNOWN")]), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Provenance.Should().BeEmpty();
        result.Issues.Should().ContainSingle(issue => issue.Code == new IssueCode("NO_DATA"));
    }

    [Fact]
    public async Task FetchAsync_accepts_explicit_provider_identity_when_request_is_UNKNOWN()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/UNKNOWN", """{"ticker":"UNKNOWN","name":"Named instrument","exchangeCode":"XNAS"}""");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol("UNKNOWN")]), CancellationToken.None);

        result.Payload.Should().ContainSingle().Which.Symbol.Ticker.Should().Be("UNKNOWN");
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_rejects_metadata_for_a_different_requested_ticker()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("tiingo/daily/AAPL", """{"ticker":"MSFT","name":"Microsoft","exchangeCode":"XNAS"}""");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol("AAPL")]), CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Provenance.Should().BeEmpty();
        result.Issues.Should().ContainSingle(issue => issue.Code == new IssueCode("TICKER_MISMATCH"));
    }

    [Theory]
    [InlineData("BRK-B")]
    [InlineData("BF.B")]
    public async Task FetchAsync_preserves_legitimate_Tiingo_ticker_endpoint(string ticker)
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo($"tiingo/daily/{ticker}", $$"""{"ticker":"{{ticker}}","name":"Named instrument","exchangeCode":"XNAS"}""");
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol(ticker)]),
            CancellationToken.None);

        transport.RequestedUris.Should().ContainSingle()
            .Which.AbsolutePath.Should().Be($"/tiingo/daily/{ticker}");
        result.Payload.Should().ContainSingle().Which.Symbol.Ticker.Should().Be(ticker);
        result.Payload[0].Name.Should().Be("Named instrument");
        result.Payload[0].Exchange.Should().Be(ExchangeCode.XNAS);
        result.Issues.Should().BeEmpty();
    }
}
