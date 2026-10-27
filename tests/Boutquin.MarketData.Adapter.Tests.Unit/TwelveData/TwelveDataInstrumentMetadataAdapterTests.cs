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

using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.TwelveData;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.TwelveData;

public sealed class TwelveDataInstrumentMetadataAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static TwelveDataInstrumentMetadataAdapter CreateAdapter(FakeHttpDataTransport? transport = null)
    {
        return new TwelveDataInstrumentMetadataAdapter(
            transport ?? new FakeHttpDataTransport(),
            new FakeRawDocumentStore(),
            s_clock,
            Options.Create(new TwelveDataOptions { ApiKey = "test-key" }),
            NullLogger<TwelveDataInstrumentMetadataAdapter>.Instance);
    }

    [Fact]
    public void ProviderKey_returns_twelvedata() =>
        CreateAdapter().ProviderKey.Should().Be(new ProviderCode("twelvedata"));

    [Fact]
    public void Priority_returns_50() =>
        CreateAdapter().Priority.Should().Be(50);

    [Fact]
    public void CanHandle_returns_true_for_non_empty_symbols()
    {
        var request = new InstrumentMetadataRequest([new Symbol("XIC")]);
        CreateAdapter().CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public void Empty_symbols_are_rejected_at_the_request_boundary()
    {
        Action act = () => new InstrumentMetadataRequest([]);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("symbols.Count");
    }

    // ---------- Exchange-hint filtering ----------

    [Fact]
    public void CanHandle_returns_true_when_Exchange_is_null_preserving_back_compat()
    {
        var request = new InstrumentMetadataRequest([new Symbol("AAPL")]);
        CreateAdapter().CanHandle(request).Should().BeTrue();
    }

    [Theory]
    [InlineData(ExchangeCode.XTSE)]
    [InlineData(ExchangeCode.NEOE)]
    [InlineData(ExchangeCode.XNYS)]
    [InlineData(ExchangeCode.XNAS)]
    public void CanHandle_returns_true_for_supported_exchanges(ExchangeCode exchange)
    {
        var request = new InstrumentMetadataRequest([new Symbol("XIC")]) { Exchange = exchange };
        CreateAdapter().CanHandle(request).Should().BeTrue();
    }

    [Theory]
    [InlineData(ExchangeCode.XLON)]
    [InlineData(ExchangeCode.XPAR)]
    [InlineData(ExchangeCode.XETR)]
    public void CanHandle_returns_false_for_unsupported_exchanges(ExchangeCode exchange)
    {
        var request = new InstrumentMetadataRequest([new Symbol("BARC")]) { Exchange = exchange };
        CreateAdapter().CanHandle(request).Should().BeFalse();
    }

    // ---------- URL construction: /etfs endpoint + mic_code query parameter ----------

    private const string EtfsSingleHit = """
        {"data":[{"symbol":"XIC","name":"iShares Core S&P/TSX Capped Composite Index ETF","currency":"CAD","exchange":"TSX","mic_code":"XTSE","country":"Canada"}],"count":1,"status":"ok"}
        """;

    [Fact]
    public async Task FetchAsync_hits_the_etfs_endpoint_not_the_stocks_endpoint()
    {
        // Per TwelveData's API: /stocks returns common-stock results only; for ETFs we
        // must call /etfs. The 2026-04-19 CA research regression produced "Scandinavian
        // Energies Inc." for bare XIC because /stocks does not surface ETF listings.
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("/etfs", EtfsSingleHit);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new InstrumentMetadataRequest([new Symbol("XIC")]), CancellationToken.None);

        transport.RequestedUris.Should().ContainSingle();
        transport.RequestedUris[0].AbsolutePath.Should().EndWith("/etfs");
        result.Payload.Should().ContainSingle().Which.Exchange.Should().Be(ExchangeCode.XTSE);
    }

    [Fact]
    public async Task FetchAsync_selects_only_the_requested_symbol_and_venue()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("/etfs", """
            {"data":[{"symbol":"OTHER","name":"Wrong","mic_code":"XNAS"},
                     {"symbol":"XIC","name":"Wrong venue","mic_code":"NEOE"},
                     {"symbol":"XIC","name":"Correct","mic_code":"XTSE"}]}
            """);
        var result = await CreateAdapter(transport).FetchAsync(
            new InstrumentMetadataRequest([new Symbol("XIC")]) { Exchange = ExchangeCode.XTSE },
            CancellationToken.None);

        result.Payload.Should().ContainSingle().Which.Name.Should().Be("Correct");
        result.Payload[0].Exchange.Should().Be(ExchangeCode.XTSE);
    }

    [Fact]
    public async Task FetchAsync_rejects_wrong_symbol_on_requested_venue()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("/etfs", """{"data":[{"symbol":"OTHER","name":"Wrong","mic_code":"XTSE"}]}""");
        var result = await CreateAdapter(transport).FetchAsync(
            new InstrumentMetadataRequest([new Symbol("XIC")]) { Exchange = ExchangeCode.XTSE },
            CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new Boutquin.MarketData.Abstractions.Diagnostics.IssueCode("NO_DATA"));
    }

    [Fact]
    public async Task FetchAsync_rejects_numeric_mic_in_metadata_response()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("/etfs", """{"data":[{"symbol":"XIC","name":"Wrong venue","mic_code":"1"}]}""");
        var result = await CreateAdapter(transport).FetchAsync(
            new InstrumentMetadataRequest([new Symbol("XIC")]) { Exchange = ExchangeCode.XNAS },
            CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code == new Boutquin.MarketData.Abstractions.Diagnostics.IssueCode("NO_DATA"));
    }

    [Fact]
    public async Task FetchAsync_metadata_raw_keys_distinguish_venues()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("/etfs", EtfsSingleHit);
        var rawStore = new FakeRawDocumentStore();
        var adapter = new TwelveDataInstrumentMetadataAdapter(
            transport, rawStore, s_clock,
            Options.Create(new TwelveDataOptions { ApiKey = "test-key" }),
            NullLogger<TwelveDataInstrumentMetadataAdapter>.Instance);

        await adapter.FetchAsync(new InstrumentMetadataRequest([new Symbol("XIC")]) { Exchange = ExchangeCode.XTSE });
        await adapter.FetchAsync(new InstrumentMetadataRequest([new Symbol("XIC")]) { Exchange = ExchangeCode.NEOE });

        rawStore.SavedKeys.Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task FetchAsync_metadata_propagates_caller_cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(source.Token));
        var adapter = CreateAdapter(transport);

        Func<Task> act = () => adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol("XIC")]), source.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_metadata_provenance_does_not_expose_api_key()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("/etfs", EtfsSingleHit);
        var result = await CreateAdapter(transport).FetchAsync(
            new InstrumentMetadataRequest([new Symbol("XIC")]), CancellationToken.None);

        transport.RequestedUris[0].Query.Should().Contain("apikey=test-key");
        result.Provenance.Should().ContainSingle().Which.SourceUrl.Should().NotContain("test-key");
    }

    [Fact]
    public async Task FetchAsync_appends_mic_code_query_param_when_Exchange_is_provided()
    {
        // XIC is listed on both TSX (XTSE) and NEO (NEOE); without mic_code the adapter
        // gets both rows and arbitrarily picks the first. With mic_code=XTSE, TwelveData
        // returns only the TSX row — unambiguous.
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("/etfs", EtfsSingleHit);
        var adapter = CreateAdapter(transport);

        await adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol("XIC")]) { Exchange = ExchangeCode.XTSE },
            CancellationToken.None);

        var url = transport.RequestedUris[0].Query;
        url.Should().Contain("mic_code=XTSE");
    }

    [Fact]
    public async Task FetchAsync_omits_mic_code_when_Exchange_is_null_preserving_venue_neutral_semantics()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("/etfs", EtfsSingleHit);
        var adapter = CreateAdapter(transport);

        await adapter.FetchAsync(new InstrumentMetadataRequest([new Symbol("XIC")]), CancellationToken.None);

        transport.RequestedUris[0].Query.Should().NotContain("mic_code");
    }

    [Theory]
    [InlineData(ExchangeCode.XTSE, "XTSE")]
    [InlineData(ExchangeCode.NEOE, "NEOE")]
    [InlineData(ExchangeCode.XNAS, "XNAS")]
    [InlineData(ExchangeCode.XNYS, "XNYS")]
    public async Task FetchAsync_renders_mic_code_param_as_ISO_MIC_matching_enum_member_name(
        ExchangeCode exchange, string expectedMicCode)
    {
        // Post-2026-04-20 rename: every ExchangeCode member name IS the ISO 10383 MIC, so
        // the adapter passes the enum name directly to TwelveData's mic_code parameter
        // without any translation layer. This test pins that assumption.
        var transport = new FakeHttpDataTransport();
        transport.RespondTo("/etfs", EtfsSingleHit);
        var adapter = CreateAdapter(transport);

        await adapter.FetchAsync(
            new InstrumentMetadataRequest([new Symbol("XIC")]) { Exchange = exchange },
            CancellationToken.None);

        transport.RequestedUris[0].Query.Should().Contain($"mic_code={expectedMicCode}");
    }
}
