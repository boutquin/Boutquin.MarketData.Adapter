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
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Adapter.UsTreasury;
using Boutquin.MarketData.Calendars;
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

    [Theory]
    [InlineData("CAD-ZERO")]
    [InlineData("BOC-ZERO")]
    [InlineData("USD-OTHER")]
    public void CanHandle_rejects_foreign_curves(string curveId)
    {
        var request = new YieldCurveQuoteRequest(new YieldCurveId(curveId), new DateOnly(2024, 1, 15));
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
    }

    [Fact]
    public void CanHandle_rejects_foreign_venue()
    {
        var request = new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), new DateOnly(2024, 1, 15))
        {
            Exchange = ExchangeCode.XTSE,
        };
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
    }

    [Fact]
    public async Task FetchAsync_rejects_foreign_curve_before_transport()
    {
        var transport = new FakeHttpDataTransport();
        var request = new YieldCurveQuoteRequest(new YieldCurveId("CAD-ZERO"), new DateOnly(2024, 1, 15));

        Func<Task> act = () => CreateAdapter(transport).FetchAsync(request);

        await act.Should().ThrowAsync<ArgumentException>();
        transport.RequestedUris.Should().BeEmpty();
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
        result.Payload.Should().Contain(q => q.Tenor == "1M" && q.Rate == 0.055m);
        result.Payload.Should().Contain(q => q.Tenor == "3M" && q.Rate == 0.054m);
        result.Payload.Should().Contain(q => q.Tenor == "1Y" && q.Rate == 0.048m);
    }

    [Fact]
    public async Task FetchAsync_rolls_back_across_New_Year_when_current_year_has_no_quote()
    {
        const string emptyFeed = """
            <feed xmlns="http://www.w3.org/2005/Atom" />
            """;
        const string priorFeed = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices">
              <entry><content><m:properties><d:NEW_DATE>2023-12-29T00:00:00</d:NEW_DATE><d:BC_1MONTH>5.42</d:BC_1MONTH></m:properties></content></entry>
            </feed>
            """;
        var transport = new FakeHttpDataTransport();
        transport.RespondDynamic(_ => true, uri => uri.Query.Contains("field_tdr_date_value=2024", StringComparison.Ordinal)
            ? emptyFeed : priorFeed);
        var request = new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), new DateOnly(2024, 1, 1));

        var result = await CreateAdapter(transport).FetchAsync(request);

        result.Payload.Should().ContainSingle(q => q.Date == new DateOnly(2023, 12, 29) && q.Rate == 0.0542m);
        result.Issues.Should().ContainSingle(i => i.Code == new Boutquin.MarketData.Abstractions.Diagnostics.IssueCode("DATE_ROLLBACK"));
        transport.RequestedUris.Select(uri => uri.Query).Should().Equal(
            "?data=daily_treasury_yield_curve&field_tdr_date_value=2024",
            "?data=daily_treasury_yield_curve&field_tdr_date_value=2023");
        result.Provenance.Should().ContainSingle().Which.SourceUrl.Should().Contain("field_tdr_date_value=2023");
    }

    [Fact]
    public async Task FetchAsync_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(cancellation.Token));
        var request = new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), new DateOnly(2024, 1, 15));

        Func<Task> act = () => CreateAdapter(transport).FetchAsync(request, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_failure_issue_does_not_disclose_exception_detail()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new IOException("credential file /private/secret/api-key.txt failed"));
        var request = new YieldCurveQuoteRequest(new YieldCurveId("USD-TREASURY"), new DateOnly(2024, 1, 15));

        var result = await CreateAdapter(transport).FetchAsync(request);

        result.Issues.Should().ContainSingle().Which.Message.Should().Be("US Treasury fetch failed.");
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
