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
using Boutquin.MarketData.Abstractions.Contracts;
using Boutquin.MarketData.Abstractions.Records;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.Tiingo;
using Boutquin.MarketData.Adapter.TwelveData;
using Boutquin.MarketData.Calendars;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Boutquin.MarketData.Adapter.Tests.Shared;

internal sealed class PriceBasisFixture
{
    public FakeHttpDataTransport TiingoTransport { get; } = new();
    public FakeHttpDataTransport TwelvePrices { get; } = new();
    public TwelveDataFundamentalsStubTransport TwelveTransport { get; }
    public TiingoPriceBarAdapter Tiingo { get; }
    public TwelveDataPriceBarAdapter Twelve { get; }

    public PriceBasisFixture()
    {
        TiingoTransport.RespondTo("/prices", """[{"date":"2024-01-08T00:00:00Z","open":100,"high":101,"low":99,"close":100,"adjClose":90,"volume":10}]""");
        TwelvePrices.RespondTo("/time_series", """{"values":[{"datetime":"2024-01-08","open":"100","high":"101","low":"99","close":"100","volume":"10"}]}""");
        TwelveTransport = new TwelveDataFundamentalsStubTransport(TwelvePrices);
        IBusinessCalendar calendar = new WeekendOnlyCalendar("TEST");
        Tiingo = new TiingoPriceBarAdapter(TiingoTransport, new FakeRawDocumentStore(), new FakeClock(), calendar,
            Options.Create(new TiingoOptions { ApiToken = "fixture" }), NullLogger<TiingoPriceBarAdapter>.Instance);
        Twelve = new TwelveDataPriceBarAdapter(TwelveTransport, new FakeRawDocumentStore(), new FakeClock(), calendar,
            Options.Create(new TwelveDataOptions { ApiKey = "fixture" }), NullLogger<TwelveDataPriceBarAdapter>.Instance);
    }

    public IDataSourceAdapter<PriceHistoryRequest, Bar> Adapter(bool tiingo) => tiingo ? Tiingo : Twelve;
    public static PriceBasis Basis(bool tiingo) => tiingo ? TiingoPriceBarAdapter.DeliveredPriceBasis : TwelveDataPriceBarAdapter.DeliveredPriceBasis;
    public static PriceHistoryRequest Request(PriceBasis basis) => new([new Symbol("SPY")],
        new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 8)))
    { Basis = basis, Exchange = ExchangeCode.XNYS };
}
