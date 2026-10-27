# Boutquin.MarketData.Adapter

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://github.com/boutquin/Boutquin.MarketData.Adapter/blob/v2.0.0-rc.20261027.4/LICENSE.txt)
[![Build](https://github.com/boutquin/Boutquin.MarketData.Adapter/actions/workflows/pr-verify.yml/badge.svg)](https://github.com/boutquin/Boutquin.MarketData.Adapter/actions/workflows/pr-verify.yml)

> **2.0 RC:** uses published MarketData `2.0.0-rc.20261027.4` and Storage `2.0.0` from NuGet.org. See [migration](https://github.com/boutquin/Boutquin.MarketData.Adapter/blob/v2.0.0-rc.20261027.4/docs/migration-2.0.md) and [build verification](https://github.com/boutquin/Boutquin.MarketData.Adapter/blob/v2.0.0-rc.20261027.4/docs/runner-dependencies.md).

Concrete data source adapters for the [Boutquin.MarketData](https://github.com/boutquin/Boutquin.MarketData) kernel. Each adapter implements `IDataSourceAdapter<TRequest, TRecord>` from `Boutquin.MarketData.Abstractions` and self-registers via an `AddMarketData*()` extension method on `IServiceCollection`. Adapters are standalone NuGet packages — install only the ones you need.

## Adapters

| Package | Provider | Records | Auth |
|---------|----------|---------|------|
| `Boutquin.MarketData.Adapter.Tiingo` | [Tiingo](https://www.tiingo.com/documentation/general/overview) | `Bar`, `InstrumentMetadata`, `DividendPayment` | API token |
| `Boutquin.MarketData.Adapter.TwelveData` | [Twelve Data](https://twelvedata.com/docs) | `Bar`, `InstrumentMetadata` | API key |
| `Boutquin.MarketData.Adapter.Frankfurter` | [Frankfurter](https://www.frankfurter.dev/) (ECB FX) | `FxRate` | Anonymous |
| `Boutquin.MarketData.Adapter.Fred` | [FRED](https://fred.stlouisfed.org/docs/api/fred/) (St. Louis Fed) | `ScalarObservation` | API key |
| `Boutquin.MarketData.Adapter.BankOfCanada` | [Bank of Canada](https://www.bankofcanada.ca/valet/docs) | `YieldCurveQuote`, `FxRate`, `ScalarObservation` (CORRA) | Anonymous |
| `Boutquin.MarketData.Adapter.NewYorkFed` | [NY Fed Markets API](https://markets.newyorkfed.org/static/docs/markets-api.html) | `ScalarObservation` (SOFR) | Anonymous |
| `Boutquin.MarketData.Adapter.UsTreasury` | [US Treasury H.15](https://home.treasury.gov/resource-center/data-chart-center/interest-rates/) | `YieldCurveQuote` | Anonymous |
| `Boutquin.MarketData.Adapter.BankOfEngland` | [BoE IADB](https://www.bankofengland.co.uk/boeapps/database/) | `ScalarObservation` (SONIA) | Anonymous |
| `Boutquin.MarketData.Adapter.Ecb` | [ECB SDMX](https://data.ecb.europa.eu/help/api/data) | `ScalarObservation` (ESTR) | Anonymous |
| `Boutquin.MarketData.Adapter.FamaFrench` | [Ken French Data Library](https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/data_library.html) | `FactorObservation` | Anonymous |
| `Boutquin.MarketData.Adapter.Cme` | CME Group settlement CSV files | `FuturesSettlement` | File-based |

See [docs/adapters.md](https://github.com/boutquin/Boutquin.MarketData.Adapter/blob/v2.0.0-rc.20261027.4/docs/adapters.md) for per-adapter configuration details, rate limits, publication lag notes, and data-quality caveats.

## Design Principles

- Each provider adapter is a standalone NuGet package; it may use Shared utilities but does not depend on another provider adapter
- Self-registering DI: each adapter provides a single `AddMarketData*()` extension method
- Canonical-first: adapters emit typed records, not dictionaries
- `DataDate` on `DataProvenance` is the actual business date of data served, which may differ from the requested date for sources with publication lag
- `DATE_ROLLBACK` `DataIssue` emitted when actual data date precedes the requested date (e.g., requesting Monday SOFR before the NY Fed publishes returns Friday's fixing)
- Source adapters may depend on the shared Adapter utilities; no source adapter depends on another provider adapter or on domain packages (`Boutquin.Analytics`, `Boutquin.Trading`, `Boutquin.OptionPricing`)

## Quick Start

```bash
dotnet add package Boutquin.MarketData.Adapter.Tiingo --version 2.0.0-rc.20261027.4
dotnet add package Boutquin.MarketData.DependencyInjection --version 2.0.0-rc.20261027.4
```

Install Fred and NewYorkFed at the same Adapter RC version to run the full example below.


```csharp
// Add the kernel from Boutquin.MarketData.DependencyInjection,
// then attach whichever adapters you need.
services
    .AddMarketDataKernel()
    .AddMarketDataTiingo(opts =>
    {
        opts.ApiToken = configuration["Tiingo:ApiToken"]!;
    })
    .AddMarketDataFred(opts =>
    {
        opts.ApiKey = configuration["Fred:ApiKey"]!;
    })
    .AddMarketDataNewYorkFed();
```

Then fetch one symbol via `IDataPipeline` (the records do not contain a ticker):

```csharp
var symbol = new Symbol("AAPL");
var result = await pipeline.ExecuteAsync<PriceHistoryRequest, Bar>(
    new PriceHistoryRequest([symbol], dateRange),
    cancellationToken);

foreach (var bar in result.Payload)
{
    Console.WriteLine($"{symbol.Ticker} {bar.Date}: close={bar.Close}");
}
```

## Building

```bash
dotnet build Boutquin.MarketData.Adapter.slnx --configuration Release
dotnet test Boutquin.MarketData.Adapter.slnx --configuration Release
dotnet format Boutquin.MarketData.Adapter.slnx --verify-no-changes
```

### Qualified dependency setup

Use the SDK selected by `global.json` and [runner dependencies](https://github.com/boutquin/Boutquin.MarketData.Adapter/blob/v2.0.0-rc.20261027.4/docs/runner-dependencies.md). All packages restore from NuGet.org. `Directory.Packages.props` records the exact kernel version and public source commit; `dependency-source.json` pins signed upstream archives. Never rebuild an accepted package identity.

## Documentation

- [Adapter Reference](https://github.com/boutquin/Boutquin.MarketData.Adapter/blob/v2.0.0-rc.20261027.4/docs/adapters.md) — per-adapter configuration, auth, publication lag, and data-quality notes

## Disclaimer

Boutquin.MarketData.Adapter is open-source software provided under the Apache 2.0 License. It is a general-purpose library intended for educational and research purposes.

**This software does not constitute financial advice.** The data retrieval and normalization tools are provided as-is for research and development. Before using any market data or derived calculations in production, consult with qualified professionals who understand your specific requirements and regulatory obligations.

## License

Licensed under the [Apache License, Version 2.0](https://github.com/boutquin/Boutquin.MarketData.Adapter/blob/v2.0.0-rc.20261027.4/LICENSE.txt).

Copyright (c) 2026 Pierre G. Boutquin. All rights reserved.
