# Boutquin.MarketData.Adapter

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE.txt)
[![Build](https://github.com/boutquin/Boutquin.MarketData.Adapter.Dev/actions/workflows/pr-verify.yml/badge.svg)](https://github.com/boutquin/Boutquin.MarketData.Adapter.Dev/actions/workflows/pr-verify.yml)

> **Note:** CI is currently disabled pending publication of `Boutquin.MarketData.*` kernel packages to nuget.org. Run `dotnet build` and `dotnet test` locally before submitting a PR.

Concrete data source adapters for the [Boutquin.MarketData](https://github.com/boutquin/Boutquin.MarketData) kernel. Each adapter implements `IDataSourceAdapter<TRequest, TRecord>` from `Boutquin.MarketData.Abstractions` and self-registers via an `AddMarketData*()` extension method on `IServiceCollection`. Adapters are standalone NuGet packages — install only the ones you need.

## Adapters

| Package | Provider | Records | Auth |
|---------|----------|---------|------|
| `Boutquin.MarketData.Adapter.Tiingo` | [Tiingo](https://www.tiingo.com/documentation/general/overview) | `Bar`, `InstrumentMetadata` | API token |
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

See [docs/adapters.md](docs/adapters.md) for per-adapter configuration details, rate limits, publication lag notes, and data-quality caveats.

## Design Principles

- Each adapter is a standalone NuGet package with no dependency on other adapters
- Self-registering DI: each adapter provides a single `AddMarketData*()` extension method
- Canonical-first: adapters emit typed records, not dictionaries
- `DataDate` on `DataProvenance` is the actual business date of data served, which may differ from the requested date for sources with publication lag
- `DATE_ROLLBACK` `DataIssue` emitted when actual data date precedes the requested date (e.g., requesting Monday SOFR before the NY Fed publishes returns Friday's fixing)
- No adapter depends on another adapter or on domain packages (`Boutquin.Analytics`, `Boutquin.Trading`, `Boutquin.OptionPricing`)

## Quick Start

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

Then fetch via `IDataPipeline`:

```csharp
var result = await pipeline.FetchAsync(
    new PriceHistoryRequest(symbols, dateRange),
    cancellationToken);

foreach (var bar in result.Records)
    Console.WriteLine($"{bar.Symbol} {bar.Date}: close={bar.Close}");
```

## Building

```bash
dotnet build Boutquin.MarketData.Adapter.slnx --configuration Release
dotnet test Boutquin.MarketData.Adapter.slnx --configuration Release
dotnet format Boutquin.MarketData.Adapter.slnx --verify-no-changes
```

### Local Development (Before Kernel Is on NuGet)

The `nuget.config` resolves `Boutquin.MarketData.*` packages from a local feed at `../Boutquin.MarketData/nupkg`. Pack the kernel locally first:

```bash
cd ../Boutquin.MarketData
dotnet pack --configuration Release /p:MinVerVersionOverride=0.1.0-local --output nupkg
```

## Documentation

- [Adapter Reference](docs/adapters.md) — per-adapter configuration, auth, publication lag, and data-quality notes

## Disclaimer

Boutquin.MarketData.Adapter is open-source software provided under the Apache 2.0 License. It is a general-purpose library intended for educational and research purposes.

**This software does not constitute financial advice.** The data retrieval and normalization tools are provided as-is for research and development. Before using any market data or derived calculations in production, consult with qualified professionals who understand your specific requirements and regulatory obligations.

## License

Licensed under the [Apache License, Version 2.0](LICENSE.txt).

Copyright (c) 2026 Pierre G. Boutquin. All rights reserved.
