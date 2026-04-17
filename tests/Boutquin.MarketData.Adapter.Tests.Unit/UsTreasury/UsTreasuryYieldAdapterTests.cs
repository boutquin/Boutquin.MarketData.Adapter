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
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.UsTreasury;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.UsTreasury;

public sealed class UsTreasuryYieldAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static UsTreasuryYieldAdapter CreateAdapter(
        FakeHttpDataTransport transport,
        FakeRawDocumentStore? rawStore = null,
        UsTreasuryOptions? options = null,
        IBusinessCalendar? calendar = null)
    {
        return new UsTreasuryYieldAdapter(
            transport,
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(options ?? new UsTreasuryOptions()),
            NullLogger<UsTreasuryYieldAdapter>.Instance);
    }

    [Fact]
    public void ProviderKey_returns_us_treasury() =>
        CreateAdapter(new FakeHttpDataTransport()).ProviderKey.Should().Be(new ProviderCode("us-treasury"));

    [Fact]
    public void Priority_returns_100() =>
        CreateAdapter(new FakeHttpDataTransport()).Priority.Should().Be(100);

    [Fact]
    public void CanHandle_returns_true()
    {
        var request = new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), new DateOnly(2024, 1, 15));
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public async Task FetchAsync_parses_xml_into_yield_curve_quotes()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices">
              <entry>
                <content type="application/xml">
                  <m:properties>
                    <d:NEW_DATE>2024-01-15T00:00:00</d:NEW_DATE>
                    <d:BC_1MONTH>5.50</d:BC_1MONTH>
                    <d:BC_3MONTH>5.40</d:BC_3MONTH>
                    <d:BC_1YEAR>4.80</d:BC_1YEAR>
                  </m:properties>
                </content>
              </entry>
            </feed>
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("treasury", xml);
        var adapter = CreateAdapter(transport);

        var request = new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), new DateOnly(2024, 1, 15));
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().HaveCountGreaterThanOrEqualTo(3);
        result.Payload.Should().Contain(new YieldCurveQuote("1M", 0.055m));
        result.Payload.Should().Contain(new YieldCurveQuote("3M", 0.054m));
        result.Payload.Should().Contain(new YieldCurveQuote("1Y", 0.048m));
    }

    [Fact]
    public async Task FetchAsync_populates_provenance()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices">
              <entry>
                <content type="application/xml">
                  <m:properties>
                    <d:NEW_DATE>2024-01-15T00:00:00</d:NEW_DATE>
                    <d:BC_1MONTH>5.50</d:BC_1MONTH>
                  </m:properties>
                </content>
              </entry>
            </feed>
            """;

        var transport = new FakeHttpDataTransport();
        transport.RespondTo("treasury", xml);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), new DateOnly(2024, 1, 15)), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        result.Provenance[0].Provider.Should().Be(new ProviderCode("us-treasury"));
    }
}
