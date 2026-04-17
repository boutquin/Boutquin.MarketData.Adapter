# Changelog

All notable changes to `Boutquin.MarketData.Adapter` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Versions are produced by [MinVer](https://github.com/adamralph/minver) from git tags on the public release repository.

## [Unreleased]

## [1.0.2] — 2026-04-17

### Fixed

- `Boutquin.MarketData.Adapter.Shared` was missing from the solution file and therefore not included in the 1.0.1 NuGet publish. Consumers of `Boutquin.MarketData.Adapter.NewYorkFed` or `Boutquin.MarketData.Adapter.UsTreasury` received a restore error (`NU1101`) because those packages declare a transitive dependency on `Boutquin.MarketData.Adapter.Shared >= 1.0.1` which did not exist on NuGet.org. Added `src/Shared/Shared.csproj` to `Boutquin.MarketData.Adapter.slnx`; all packages republish at 1.0.2 with `--skip-duplicate` handling the already-published ones.

## [1.0.1] — 2026-04-17

## [1.0.0] — 2026-04-17

First public release. Boutquin.MarketData.Adapter ships eleven standalone NuGet packages — one per data source — each implementing `IDataSourceAdapter<TRequest, TRecord>` from `Boutquin.MarketData.Abstractions` and self-registering via an `AddMarketData*()` extension on `IServiceCollection`. The kernel (`Boutquin.MarketData`) handles transport, caching, normalization, and pipeline orchestration; the adapters handle provider-specific parsing, authentication, and URL construction only.

### Highlights

- **Eleven standalone packages, one dependency each.** Every adapter depends only on the kernel packages (`Abstractions`, `Transport`, `Storage`). No adapter depends on another adapter. Install only the packages you need; the DI composition assembles them at runtime via `IPrioritizedAdapter` ordering.
- **`DataDate` discipline.** Every adapter sets `DataProvenance.DataDate` to the actual business date of data served — not the wall-clock date of the fetch. When the actual date lags the requested date (T+1 publication, weekend gap, holiday), the adapter emits a `DataIssue` with `IssueCode.StaleData` so callers can distinguish a fresh observation from a carried-forward one.
- **Calendar-aware coverage.** `GapClassifier` and `AdapterCoverageHelper` in the shared package classify date-series gaps as expected (holidays, weekends, known publication lag) or unexpected (genuine missing data), feeding coverage metadata back into `DataProvenance` without false `StaleData` alerts on market-closed days.
- **Canonical-first.** Adapters emit typed records, not dictionaries. Provider-specific field names, casing quirks, and percent-vs-decimal conventions are resolved inside the adapter; the pipeline sees only `Bar`, `FxRate`, `ScalarObservation`, `YieldCurveQuote`, `FuturesSettlement`, `FactorObservation`, and `InstrumentMetadata`.
- **SSRF is a kernel concern, not an adapter concern.** Adapters never construct URLs from unvalidated external input; all HTTP dispatch flows through `ResilientHttpDataTransport`, which validates every URI before dispatch and blocks loopback, private-range, and cloud-metadata targets at the transport layer.

### Added

#### Shared Utilities (`Boutquin.MarketData.Adapter.Shared`)

- **`GapClassifier`** — classifies date-series gaps as expected (weekend, market holiday, known T+N publication lag) or unexpected (genuine missing observation). Uses kernel business-day calendars to determine which dates are expected to have no publication.
- **`AdapterCoverageHelper`** — aggregates per-partition gap classifications into a `CoverageComputation` instance and surfaces the result in `DataProvenance`, enabling pipelines to distinguish a full-coverage fetch from a partial one without inspecting individual records.

#### Equity and Instrument Data

- **`Boutquin.MarketData.Adapter.Tiingo`** — `TiingoPriceBarAdapter` producing split- and dividend-adjusted daily `Bar` records from the [Tiingo REST API](https://www.tiingo.com/documentation/general/overview); `TiingoInstrumentMetadataAdapter` producing `InstrumentMetadata`. Requires `TiingoOptions.ApiToken`; requests without a valid token receive HTTP 401. `AddMarketDataTiingo(opts => ...)` DI extension.

- **`Boutquin.MarketData.Adapter.TwelveData`** — `TwelveDataPriceBarAdapter` producing daily `Bar` records from the [Twelve Data API](https://twelvedata.com/docs) covering global equities, ETFs, and indices; `TwelveDataInstrumentMetadataAdapter` producing `InstrumentMetadata`. Requires `TwelveDataOptions.ApiKey`; requests without a valid key receive HTTP 401. Suitable as a fallback source alongside Tiingo via `IPrioritizedAdapter` ordering. `AddMarketDataTwelveData(opts => ...)` DI extension.

#### FX Rates

- **`Boutquin.MarketData.Adapter.Frankfurter`** — `FrankfurterFxAdapter` producing `FxRate` records from the [Frankfurter API](https://www.frankfurter.dev/) (ECB reference rates). Anonymous — no authentication required. Rates are published on TARGET business days only; requesting a date when the ECB did not publish returns the most recent prior rate with a `DataIssue`. `AddMarketDataFrankfurter()` DI extension.

#### Yield Curves

- **`Boutquin.MarketData.Adapter.UsTreasury`** — `UsTreasuryYieldAdapter` producing constant-maturity Treasury `YieldCurveQuote` records from the [US Treasury H.15 XML feed](https://home.treasury.gov/resource-center/data-chart-center/interest-rates/). Anonymous. Published on US federal government business days; weekend and holiday dates emit `DataIssue`. `AddMarketDataUsTreasury()` DI extension.

- **`Boutquin.MarketData.Adapter.BankOfCanada`** — three adapters sharing `BankOfCanadaOptions`: `BankOfCanadaYieldCurveAdapter` producing Canadian zero-coupon `YieldCurveQuote` records from the [Valet API](https://www.bankofcanada.ca/valet/docs) (configurable `GroupName`, default `BD.CDN.ZERO`); `BankOfCanadaFxAdapter` producing `FxRate`; `BankOfCanadaCorraAdapter` producing `ScalarObservation` for the CORRA overnight rate fixing. Anonymous. `AddMarketDataBankOfCanada()` DI extension.

#### Overnight Rate Fixings

- **`Boutquin.MarketData.Adapter.NewYorkFed`** — `NewYorkFedSofrAdapter` producing `ScalarObservation` for the SOFR overnight rate from the [NY Fed Markets API](https://markets.newyorkfed.org/static/docs/markets-api.html). Anonymous. SOFR is published T+1; requesting today's rate before publication returns the prior fixing with a `DataIssue`. Corresponds to `StandardBenchmarks.UsdSofr` in the kernel. `AddMarketDataNewYorkFed()` DI extension.

- **`Boutquin.MarketData.Adapter.BankOfEngland`** — `BankOfEnglandSoniaAdapter` producing `ScalarObservation` for the SONIA overnight rate from the [Bank of England IADB](https://www.bankofengland.co.uk/boeapps/database/). Anonymous. Default series code `IUDSNKY`. Corresponds to `StandardBenchmarks.GbpSonia`. `AddMarketDataBankOfEngland()` DI extension.

- **`Boutquin.MarketData.Adapter.Ecb`** — `EcbEstrAdapter` producing `ScalarObservation` for the Euro Short-Term Rate (ESTR / €STR) from the [ECB SDMX Data Service](https://data.ecb.europa.eu/help/api/data). Anonymous. Default SDMX series key `FM/B.U2.EUR.4F.KR.DFR.LEV`. Published on TARGET business days. Corresponds to `StandardBenchmarks.EurEstr`. `AddMarketDataEcb()` DI extension.

#### Factor Data

- **`Boutquin.MarketData.Adapter.FamaFrench`** — `FamaFrenchFactorAdapter` producing `FactorObservation` records from the [Ken French Data Library](https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/data_library.html) (Dartmouth FTP endpoint). Anonymous. Downloads compressed CSV files and handles the canonical current file format for three-factor, five-factor, and momentum datasets. `AddMarketDataFamaFrench()` DI extension.

#### Futures Settlements

- **`Boutquin.MarketData.Adapter.Cme`** — `CmeSettlementAdapter` producing `FuturesSettlement` records by parsing CME Group daily settlement CSV exports from a local directory (no network call). `CmeOptions.SettlementDirectory` and `CmeOptions.FilePattern` (default `*.csv`) configure the source path. CME settlement files must be obtained separately from the CME Group or a licensed data vendor. `AddMarketDataCme(opts => ...)` DI extension.

### Documentation

- [`docs/adapters.md`](docs/adapters.md) — per-adapter reference covering provider API documentation URLs, records emitted, authentication model, configuration options, publication lag behaviour, and data-quality notes for all eleven adapters.
- `README.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md` — repository scaffolding.
