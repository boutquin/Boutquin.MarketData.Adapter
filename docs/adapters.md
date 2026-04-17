# Adapter Reference

Each adapter in this repository implements `IDataSourceAdapter<TRequest, TRecord>` from `Boutquin.MarketData.Abstractions` and self-registers via an `AddMarketData*()` extension method on `IServiceCollection`. Adapters are standalone NuGet packages — install only the ones you need.

## Authentication Summary

| Adapter | Auth | Credentials field |
|---------|------|-------------------|
| Tiingo | API token required | `TiingoOptions.ApiToken` |
| Twelve Data | API key required | `TwelveDataOptions.ApiKey` |
| FRED | API key required | `FredOptions.ApiKey` |
| Frankfurter | Anonymous | — |
| NY Fed | Anonymous | — |
| Bank of Canada | Anonymous | — |
| US Treasury | Anonymous | — |
| Bank of England | Anonymous | — |
| ECB | Anonymous | — |
| Fama-French | Anonymous | — |
| CME | File-based (CSV) | `CmeOptions.SettlementDirectory` |

---

## Tiingo (`Boutquin.MarketData.Adapter.Tiingo`)

**Provider:** [Tiingo](https://www.tiingo.com/documentation/general/overview)
**Records:** `Bar` (daily OHLCAV price bars), `InstrumentMetadata`
**Auth:** API token — register at tiingo.com, then set `TiingoOptions.ApiToken`.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataTiingo(opts =>
    {
        opts.ApiToken = configuration["Tiingo:ApiToken"]!;
    });
```

**Notes:** Prices are split- and dividend-adjusted by default. `DataProvenance.DataDate` is set to the actual business date of the bar, which may differ from the request end date when markets were closed.

---

## Twelve Data (`Boutquin.MarketData.Adapter.TwelveData`)

**Provider:** [Twelve Data](https://twelvedata.com/docs)
**Records:** `Bar` (daily OHLCAV price bars), `InstrumentMetadata`
**Auth:** API key — register at twelvedata.com, then set `TwelveDataOptions.ApiKey`.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataTwelveData(opts =>
    {
        opts.ApiKey = configuration["TwelveData:ApiKey"]!;
    });
```

**Notes:** Supports global equity markets, ETFs, and indices. Use alongside Tiingo as a fallback source by registering both adapters — `IPrioritizedAdapter` ordering controls selection.

---

## Frankfurter (`Boutquin.MarketData.Adapter.Frankfurter`)

**Provider:** [Frankfurter API](https://www.frankfurter.dev/) (ECB reference rates)
**Records:** `FxRate`
**Auth:** Anonymous — no configuration required.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataFrankfurter();
```

**Notes:** Rates are ECB reference rates published on TARGET business days (not published on weekends or ECB holidays). Requesting a date when the ECB did not publish will return the most recent prior publication with a `DATE_ROLLBACK` `DataIssue`.

---

## FRED (`Boutquin.MarketData.Adapter.Fred`)

**Provider:** [Federal Reserve Economic Data](https://fred.stlouisfed.org/docs/api/fred/) (St. Louis Fed)
**Records:** `ScalarObservation`
**Auth:** API key — register at fred.stlouisfed.org, then set `FredOptions.ApiKey`.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataFred(opts =>
    {
        opts.ApiKey = configuration["Fred:ApiKey"]!;
        opts.NormalizePercentToDecimal = true; // default: divide percent values by 100
    });
```

**Notes:** `FredOptions.NormalizePercentToDecimal` (default `true`) divides percent-denominated series (e.g., `5.25%`) by 100 to produce decimal form (`0.0525`) for use in rate calculations. Set to `false` to preserve raw published values.

---

## Bank of Canada (`Boutquin.MarketData.Adapter.BankOfCanada`)

**Provider:** [Bank of Canada Valet API](https://www.bankofcanada.ca/valet/docs)
**Records:** `YieldCurveQuote` (Canadian zero-coupon yield curve), `FxRate` (CAD FX rates), `ScalarObservation` (CORRA fixing)
**Auth:** Anonymous.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataBankOfCanada();
```

**Notes:** Yield curve data uses the CORRA-linked zero-coupon series. The `DATE_ROLLBACK` warning fires when the requested date precedes the most recent available publication (typically T+1 for CORRA).

---

## New York Fed (`Boutquin.MarketData.Adapter.NewYorkFed`)

**Provider:** [NY Fed Markets API](https://markets.newyorkfed.org/static/docs/markets-api.html)
**Records:** `ScalarObservation` (SOFR fixing)
**Auth:** Anonymous.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataNewYorkFed();
```

**Notes:** SOFR is published on the next business day (T+1). Requesting today's SOFR before the NY Fed publishes will return the prior day's value with a `DATE_ROLLBACK` `DataIssue`. Corresponds to `StandardBenchmarks.UsdSofr` in the kernel.

---

## US Treasury (`Boutquin.MarketData.Adapter.UsTreasury`)

**Provider:** [US Treasury interest rate XML feed](https://home.treasury.gov/resource-center/data-chart-center/interest-rates/)
**Records:** `YieldCurveQuote`
**Auth:** Anonymous.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataUsTreasury();
```

**Notes:** Rates are constant-maturity Treasury (CMT) yields from the H.15 release. The feed publishes on US federal government business days; weekend and holiday dates return the prior publication with a `DATE_ROLLBACK` `DataIssue`.

---

## Bank of England (`Boutquin.MarketData.Adapter.BankOfEngland`)

**Provider:** [Bank of England IADB](https://www.bankofengland.co.uk/boeapps/database/)
**Records:** `ScalarObservation` (SONIA fixing)
**Auth:** Anonymous.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataBankOfEngland();
```

**Notes:** The default series code (`IUDSNKY`) retrieves SONIA overnight rates. Corresponds to `StandardBenchmarks.GbpSonia` in the kernel.

---

## ECB (`Boutquin.MarketData.Adapter.Ecb`)

**Provider:** [ECB SDMX Data Service](https://data.ecb.europa.eu/help/api/data)
**Records:** `ScalarObservation` (ESTR / EUR short-term rate fixing)
**Auth:** Anonymous.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataEcb();
```

**Notes:** The default SDMX series key retrieves the Euro Short-Term Rate (ESTR, also written €STR). Corresponds to `StandardBenchmarks.EurEstr` in the kernel. ESTR is published on TARGET business days.

---

## Fama-French (`Boutquin.MarketData.Adapter.FamaFrench`)

**Provider:** [Ken French Data Library](https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/data_library.html)
**Records:** `FactorObservation`
**Auth:** Anonymous.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataFamaFrench();
```

**Notes:** Downloads factor data (e.g., three-factor, five-factor, momentum) as compressed CSV files from the Dartmouth endpoint. File formats have changed across decades of the library — the adapter handles the canonical current format.

---

## CME (`Boutquin.MarketData.Adapter.Cme`)

**Provider:** Local CSV files (CME Group daily settlement reports)
**Records:** `FuturesSettlement`
**Auth:** File-based — no network call. Point `CmeOptions.SettlementDirectory` at the directory containing CME settlement CSV exports.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataCme(opts =>
    {
        opts.SettlementDirectory = configuration["Cme:SettlementDirectory"]!;
        opts.FilePattern = "*.csv"; // default
    });
```

**Notes:** CME settlement files must be obtained separately from the CME Group website or a licensed data vendor. The adapter reads from the local directory — it does not fetch from CME's network APIs.
