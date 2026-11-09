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

using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Exceptions;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Tests.Shared;
using FluentAssertions;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit;

public sealed class PriceBasisDeclarationTests
{
    [Theory]
    [InlineData(true, "all.provider.none")]
    [InlineData(false, "all.computed.splits")]
    public async Task Every_price_provenance_entry_declares_the_static_basis(bool tiingo, string token)
    {
        var fixture = new PriceBasisFixture();
        var adapter = fixture.Adapter(tiingo);
        var basis = PriceBasisFixture.Basis(tiingo);
        ((IPriceBasisAdapter)adapter).DeliveredBasis.Should().Be(basis);
        basis.Token.Should().Be(token);
        var result = await adapter.FetchAsync(PriceBasisFixture.Request(basis));
        result.Payload.Should().ContainSingle();
        result.Provenance.Should().NotBeEmpty().And.OnlyContain(p => p.PriceBasis == basis);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Routing_accepts_only_its_declared_basis(bool tiingo)
    {
        var adapter = new PriceBasisFixture().Adapter(tiingo);
        adapter.CanHandle(PriceBasisFixture.Request(PriceBasisFixture.Basis(tiingo))).Should().BeTrue();
        adapter.CanHandle(PriceBasisFixture.Request(PriceBasis.Raw)).Should().BeFalse();
        adapter.CanHandle(PriceBasisFixture.Request(PriceBasisFixture.Basis(!tiingo))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unsupported_raw_basis_is_refused_before_any_transport(bool tiingo)
    {
        var fixture = new PriceBasisFixture();
        Func<Task> fetch = () => fixture.Adapter(tiingo).FetchAsync(PriceBasisFixture.Request(PriceBasis.Raw));
        var failure = await fetch.Should().ThrowExactlyAsync<PriceBasisNotSupportedException>();
        failure.Which.Requested.Should().Be(PriceBasis.Raw);
        fixture.TiingoTransport.RequestedUris.Should().BeEmpty();
        fixture.TwelvePrices.RequestedUris.Should().BeEmpty();
        fixture.TwelveTransport.FundamentalsUris.Should().BeEmpty();
    }
}

public sealed class TiingoIsolatedInputControls
{
    [Fact]
    public async Task Impossible_OHLC_is_rejected_with_valid_adjusted_close()
    {
        var fixture = new PriceBasisFixture();
        fixture.TiingoTransport.RespondTo("/prices", """[{"date":"2024-01-08T00:00:00Z","open":100,"high":98,"low":99,"close":100,"adjClose":90,"volume":10}]""");
        var result = await fixture.Tiingo.FetchAsync(PriceBasisFixture.Request(PriceBasisFixture.Basis(true)));
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code.Value == "FETCH_FAILED");
    }

    [Fact]
    public async Task Invalid_ISO_date_is_rejected_with_valid_adjusted_close()
    {
        var fixture = new PriceBasisFixture();
        fixture.TiingoTransport.RespondTo("/prices", """[{"date":"01/08/2024T00:00:00Z","open":100,"high":101,"low":99,"close":100,"adjClose":90,"volume":10}]""");
        var result = await fixture.Tiingo.FetchAsync(PriceBasisFixture.Request(PriceBasisFixture.Basis(true)));
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code.Value == "FETCH_FAILED");
    }

    [Fact]
    public async Task Negative_volume_is_rejected_with_valid_adjusted_close()
    {
        var fixture = new PriceBasisFixture();
        fixture.TiingoTransport.RespondTo("/prices", """[{"date":"2024-01-08T00:00:00Z","open":100,"high":101,"low":99,"close":100,"adjClose":90,"volume":-1}]""");
        var result = await fixture.Tiingo.FetchAsync(PriceBasisFixture.Request(PriceBasisFixture.Basis(true)));
        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Code.Value == "FETCH_FAILED");
    }
}

public sealed class TwelveDataBasisRequestShapeTests
{
    [Fact]
    public async Task Delivered_basis_keeps_split_prices_and_unadjusted_dividends()
    {
        var fixture = new PriceBasisFixture();
        var result = await fixture.Twelve.FetchAsync(PriceBasisFixture.Request(PriceBasisFixture.Basis(false)));
        result.Payload.Should().ContainSingle();
        fixture.TwelvePrices.RequestedUris.Should().ContainSingle().Which.Query.Split('&').Count(p => p == "adjust=splits").Should().Be(1);
        var dividends = fixture.TwelveTransport.FundamentalsUris.Single(u => u.AbsolutePath.EndsWith("/dividends", StringComparison.Ordinal));
        dividends.Query.Split('&').Count(p => p == "adjust=false").Should().Be(1);
        fixture.TwelvePrices.RequestedUris.Concat(fixture.TwelveTransport.FundamentalsUris)
            .SelectMany(u => u.Query.TrimStart('?').Split('&')).Where(p => p.StartsWith("adjust=", StringComparison.Ordinal))
            .Should().OnlyContain(p => p == "adjust=splits" || p == "adjust=false");
    }

    [Fact]
    public async Task Other_adjustment_basis_is_refused_without_mapping_or_transport()
    {
        var fixture = new PriceBasisFixture();
        Func<Task> fetch = () => fixture.Twelve.FetchAsync(PriceBasisFixture.Request(PriceBasisFixture.Basis(true)));
        await fetch.Should().ThrowExactlyAsync<PriceBasisNotSupportedException>();
        fixture.TwelvePrices.RequestedUris.Should().BeEmpty();
        fixture.TwelveTransport.FundamentalsUris.Should().BeEmpty();
    }
}
