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
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Exceptions;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Integration;

public sealed class FallbackChainBasisTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Real_kernel_routes_and_refuses_without_crossing_basis(int scenario)
    {
        var fixture = new PriceBasisFixture();
        if (scenario == 2)
        {
            fixture.TiingoTransport.ThrowOnNext(new HttpRequestException("fixture unavailable"));
        }

        var root = Path.Combine(Path.GetTempPath(), $"adapter-basis-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddMarketDataKernel(snapshotDirectory: Path.Combine(root, "snapshots"),
                rawDocumentDirectory: Path.Combine(root, "raw"), checkpointDirectory: Path.Combine(root, "checkpoints"),
                eventLogDirectory: Path.Combine(root, "events"));
            services.AddSingleton<IDataSourceAdapter<PriceHistoryRequest, Bar>>(fixture.Tiingo);
            services.AddSingleton<IDataSourceAdapter<PriceHistoryRequest, Bar>>(fixture.Twelve);
            using var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<IDataPipeline>();
            var basis = scenario == 5 ? PriceBasis.Raw : PriceBasisFixture.Basis(scenario is 2 or 4 or 6);
            var request = PriceBasisFixture.Request(basis);
            if (scenario is 3 or 4)
            {
                request = request with { Symbols = [new Symbol("XIC")], Exchange = ExchangeCode.XTSE };
            }

            if (scenario == 5)
            {
                Func<Task> fetch = () => pipeline.ExecuteAsync<PriceHistoryRequest, Bar>(request);
                var refusal = await fetch.Should().ThrowExactlyAsync<PriceBasisNotSupportedException>();
                refusal.Which.Message.Should().Be("No registered price adapter declares price basis 'none.provider.none'. Declared: all.computed.splits, all.provider.none. Price adapters must implement IPriceBasisAdapter; see docs/migration-3.0.md.");
            }
            else
            {
                var result = await pipeline.ExecuteAsync<PriceHistoryRequest, Bar>(request);
                if (scenario is 2 or 4)
                {
                    result.Payload.Should().BeEmpty();
                    result.Issues.Should().ContainSingle(i => i.Code == IssueCode.PartitionFailed);
                }
                else
                {
                    result.Payload.Should().ContainSingle();
                    result.Provenance.Should().NotBeEmpty().And.OnlyContain(p => p.PriceBasis == basis);
                }
            }

            if (scenario is 2 or 6)
            {
                fixture.TiingoTransport.RequestedUris.Should().NotBeEmpty();
            }
            else
            {
                fixture.TiingoTransport.RequestedUris.Should().BeEmpty();
            }

            if (scenario is 1 or 3)
            {
                fixture.TwelvePrices.RequestedUris.Should().NotBeEmpty();
                fixture.TwelveTransport.FundamentalsUris.Should().NotBeEmpty();
            }
            else
            {
                fixture.TwelvePrices.RequestedUris.Should().BeEmpty();
                fixture.TwelveTransport.FundamentalsUris.Should().BeEmpty();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
