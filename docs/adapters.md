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
**Records:** `Bar` (daily OHLCAV price bars), `InstrumentMetadata`, `DividendPayment` (cash dividends by ex-date)
**Auth:** API token — register at tiingo.com, then set `TiingoOptions.ApiToken`.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataTiingo(opts =>
    {
        opts.ApiToken = configuration["Tiingo:ApiToken"]!;
    });
```

**Notes:** `Open`, `High`, `Low` and `Close` are Tiingo's raw (unadjusted) prices; `AdjustedClose` is Tiingo's split- and dividend-adjusted `adjClose`, anchored by the provider to the present. A row without a positive numeric `adjClose` is never filled from the raw close: the symbol returns a `FETCH_FAILED` issue naming the date and no bars. Price fetches accept exactly one symbol because `Bar` has no symbol field; partition batches through the kernel before dispatch. Instrument metadata leaves `InceptionDate` unset because the provider's `startDate` is the first available price-history date, not an instrument inception date. `DataProvenance.DataDate` is set to the actual business date of the bar, which may differ from the request end date when markets were closed. For cash dividend history, send a `DividendHistoryRequest` through `IDataPipeline.ExecuteAsync<DividendHistoryRequest, DividendPayment>` after registering Tiingo. The adapter emits one USD `DividendPayment` for each positive `divCash` ex-date, with `DividendType.Unclassified` because Tiingo does not provide a reliable payment classification. Non-payment days are expected and are not coverage gaps. A request with a non-US exchange hint is not handled by this adapter. Partition multi-symbol requests before consuming the flat records because `DividendPayment` has no symbol field; consumers calculate any trailing yield themselves.

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

**Notes:** Price requests accept exactly one symbol and supported venues XTSE, NEOE, XNYS and XNAS (or no venue hint). Supplied symbol, MIC and interval metadata must agree with the request. Legacy responses omitting metadata remain supported; its absence is not independent identity proof. Instrument metadata uses the ETF catalogue (`/etfs`). Register both providers when callers need both declared bases; fallback never crosses from one price basis to the other. TwelveData `AdjustedClose` is a total return computed from Twelve Data's listed events only (`twelvedata_computed_total_return_listed_events_only`), and three provider-list failure modes go undetected: splits the provider does not list; cash distributions the provider omits (an empty or short `/dividends` response is read as no dividends; for example VCN on XTSE for 2020 returned three rows, none between 2020-06-22 and 2020-12-30); and a listed row that was a reinvested or notional distribution, which is credited as cash. Several ex-dates credited to one bar are summed, so that bar's return is exactly `(C + D1 + D2) / C_prev - 1`.

**Price basis.** Prices are requested split-adjusted (`adjust=splits`, raw key `twelvedata/daily-splits/...`). `Open`, `High`, `Low` and `Close` are those split-adjusted prices, a price-return series restated for later splits. `AdjustedClose` is a total-return close computed by the adapter from the split-adjusted closes and the cash dividends listed by Twelve Data's `/dividends` endpoint (raw key `twelvedata/dividends/...`).

- For a dividend `D` with ex-date `E` after the first bar and on or before the last bar, every bar dated before `E` is multiplied by `C_E / (C_E + D)`, where `C_E` is the close of the first bar on or after `E`. The adjusted one-day return into that bar is exactly `(C_E + D) / C_prev - 1`. An ex-date on a non-trading day is credited to the next bar.
- The last bar of the fetched window is the anchor (`AdjustedClose == Close`). Adjusted levels are relative to the request end, not to today as with Tiingo; returns are exact. When combining separately fetched windows, chain returns rather than levels.
- Twelve Data's own `adjust=all` series is not used. Compared with its cash dividend list, it applied a wrong-signed extra adjustment on some distribution dates (Canadian ETF year-end distributions that were reinvested and consolidated, which are price-neutral), so its one-day returns on those dates were wrong by up to about four percent. Only cash dividends listed by `/dividends` adjust the series.
- Fail-closed rules: a failed, credit-limited or error-bearing `/dividends` request yields `FETCH_FAILED` and no bars; a successful empty list means no cash dividends. Dividend amounts are requested unadjusted (`adjust=false`) because the provider's split restatement of dividends is inconsistent across listings. If `/splits` (raw key `twelvedata/splits/...`, queried from the earliest applicable ex-date to today) records a split on or after an applicable ex-date, the symbol returns `DIVIDEND_SPLIT_BASIS_UNVERIFIED` and no bars. Negative amounts or conflicting amounts on one ex-date fail the symbol; identical repeated rows count once; zero amounts are ignored. A dividend dated on or before the first bar, or after the last bar, is reported as a `DIVIDEND_OUTSIDE_BARS` warning because no return inside the window spans it. A split that Twelve Data does not list cannot be detected.
- Cost: each symbol fetch adds one `/dividends` call and, when an applicable dividend exists, one `/splits` call. Twelve Data prices both as fundamentals endpoints (more credits than a price page) on plans that include fundamentals.

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

`FrankfurterOptions.BaseUrl` defaults to `https://api.frankfurter.dev/v1`. The adapter appends the time-series path directly to it, so an override (a self-hosted instance, a proxy) must include the API version segment — the service answers the unversioned path with HTTP 404.

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
        opts.NormalizePercentToDecimal = true; // opt in only for percent-denominated series
    });
```

**Notes:** FRED serves series with different units. Raw published values are preserved by default and carry `Units = "unknown"` because the observations response does not establish a unit. Enable `NormalizePercentToDecimal` only when the configured series is known to be percent-denominated; the adapter then divides by 100 and labels the result `decimal`. Frequency hints describe expected coverage; this adapter does not aggregate native observations into another frequency.

---

## Bank of Canada (`Boutquin.MarketData.Adapter.BankOfCanada`)

**Provider:** [Bank of Canada Valet API](https://www.bankofcanada.ca/valet/docs) for FX and CORRA; the [zero-coupon yield curve download](https://www.bankofcanada.ca/rates/interest-rates/bond-yield-curves/) for the curve
**Records:** `YieldCurveQuote` (Canadian zero-coupon yield curve), `FxRate` (CAD FX rates), `ScalarObservation` (CORRA fixing)
**Auth:** Anonymous.

```csharp
services
    .AddMarketDataKernel()
    .AddMarketDataBankOfCanada();
```

**CORRA identity and publication dates:** `CorraGroupName` selects the Valet group endpoint; `CorraSeriesCode` independently selects the rate field, default `AVG.INTWO`. Volume/transparency fields are never substituted for the rate. BoC FX and CORRA defaults use `CAST`, the Canadian statutory calendar, rather than TSX trading dates. Archived observations from 2020-01-02 through 2026-10-23 match that calendar; later table dates remain subject to the documented dataset limits.

**Notes:** The zero-coupon curve is not a Valet dataset. `BankOfCanadaYieldCurveAdapter` reads the Bank's CSV lookup at `BankOfCanadaOptions.YieldCurveLookupUrl` (default `https://www.bankofcanada.ca/stats/results/csv`), requesting a 30-day window ending on the as-of date and serving the chronologically latest usable row within that window, regardless of response order. Future rows and rows before the lookback are excluded. Terms are labelled `3M`, `6M`, `9M`, `1Y`, `15M`, … `30Y` (120 tenors); rates are decimals as published. The Bank refreshes the file weekly with a lag of about two weeks, so a request for a recent date normally carries a `DATE_ROLLBACK` warning naming the curve date actually served. The curve adapter serves `BOC-ZERO` / `CAD-ZERO` only when `BankOfCanadaOptions.Currency` is `CAD`; another currency setting makes it ineligible. `GroupName` is obsolete and ignored. FX (`FXUSDCAD` / `FXCADUSD`, from 2017-01-03) and CORRA come from Valet at `BaseUrl`. The FX adapter serves one currency pair per request because `FxRate` rows do not carry pair identity; partition multi-pair requests before dispatch.

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

**Notes:** SOFR is published on the next business day (T+1). This range adapter returns only observations inside the inclusive requested range and reports gaps; it does not synthesize a prior-day fixing for an unpublished date. Corresponds to `StandardBenchmarks.UsdSofr` in the kernel and uses the USSOFR publication calendar by default.

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

**Notes:** Rates are constant-maturity Treasury (CMT) yields from the H.15 release. The feed publishes on US federal government business days; weekend and holiday dates return the prior publication with a `DATE_ROLLBACK` `DataIssue`. The default calendar is USSF. A newest entry with no usable tenors fails with `NO_QUOTES` rather than silently selecting an older curve; XML document type declarations are rejected.

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

**Notes:** The default series code, `BankOfEnglandOptions.SoniaSeriesCode = "IUDSOIA"`, is the IADB's "Daily Sterling overnight index average (SONIA) rate" (history from 1997-01-02). Corresponds to `StandardBenchmarks.GbpSonia` in the kernel. The adapter calls the IADB CSV export with `UsingCodes=Y`; the Bank publishes SONIA in percent on London business days, and the adapter returns it as a decimal rate. An unrecognised series code is answered by the IADB with an HTML error page, which the adapter reports as a `SCHEMA_MISMATCH` issue.

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

**Notes:** The default SDMX series key, `EcbOptions.EstrSeriesKey = "EST/B.EU000A2X2A25.WT"`, is the ECB Data Portal's "Euro short-term rate" series (volume-weighted trimmed mean rate, available from 2019-10-01). Corresponds to `StandardBenchmarks.EurEstr` in the kernel. €STR is published on TARGET business days; the ECB reports it in percent and the adapter returns it as a decimal rate.

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

**Notes:** Downloads factor data (e.g., three-factor, five-factor, momentum) as compressed CSV files from the Dartmouth endpoint. File formats have changed across decades of the library — the adapter handles the canonical current format. A blank line ends the selected CSV table so a subsequent table cannot contaminate its records. Expanded CSV data is limited to 64 MiB and the acquired transport stream is disposed after reading.

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

**Notes:** CME settlement files must be obtained separately from the CME Group website or a licensed data vendor. The adapter reads from the local directory — it does not fetch from CME's network APIs. The default directory is `.`; relative paths resolve against the process working directory. Use an explicit absolute directory when the host's working directory can vary.

Name each file `yyyyMMdd-PRODUCT.csv`, where `yyyyMMdd` is the settlement business date and `PRODUCT` matches the requested product code (for example, `20240110-SR3.csv`). The default `*.csv` pattern discovers candidates, but files without that date-and-product name are skipped. Rename existing unbound CSV files before relying on this adapter. The filename binds a caller-supplied date and product to the file; it does not prove that the file came from CME or a licensed vendor.

## Request and coverage boundaries

Credential-bearing Tiingo, TwelveData and FRED fetches require an absolute HTTPS base URL. Invalid configuration fails before transport. Cancellation exceptions propagate, including cancellation raised by a dependency; they are not usable partial-data responses.

Direct fetches reject unsupported frequencies, venues, benchmarks and ambiguous flat-record batches before transport. Frankfurter accepts one non-identity FX pair and checks any supplied base currency and amount against the request (unit amount 1); older snapshots without those fields remain readable. CORRA, SONIA, SOFR and FX responses are clipped to inclusive request bounds. Public failure issues omit exception text that could expose URLs, credentials or local paths; diagnostic log sinks remain the host's responsibility. FRED credentials are excluded from returned provenance URLs.

Table-based calendars GBLO, TARGET, XCME, USSF, USSOFR and CAST created by Adapter DI carry explicit holiday-data bounds 2020–2030. Algorithmic USNY and CATO calendars are not assigned those table bounds. Crossing those bounds emits `CALENDAR_RANGE_EXCEEDED` with severity `Warning`; the warning alone does not make coverage partial. It means holiday completeness outside those years is unknown. Arbitrary custom calendars have no inferred bounds, even when they share a built-in code. Register a keyed custom calendar before the adapter registration to preserve the override. New York Fed uses `USSOFR`, Treasury uses `USSF`, Bank of Canada FX/CORRA use `CAST`, and US equity defaults use `USNY`.

CME skips out-of-range files before reading or persisting them and rejects links escaping the configured root with `UNSAFE_SOURCE_PATH`. A filename binds the requested date and product, but never authenticates the provider.

## Explicit price conventions in 3.0

| Adapter | Static `DeliveredPriceBasis` and instance `DeliveredBasis` | OHLC | Adjusted close |
|---|---|---|---|
| Tiingo | `all.provider.none` | Raw | Provider supplied, split and dividend adjusted |
| Twelve Data | `all.computed.splits` | Split adjusted | Adapter computed, anchored at the window's final bar |

Set `PriceHistoryRequest.Basis` to the static property of the adapter you intend to use. Each price provenance entry carries the same basis. The adapters refuse a different basis with `PriceBasisNotSupportedException` before any HTTP call. Requests for one basis cannot use the other provider as fallback. Twelve Data continues to request `adjust=splits` for prices and `adjust=false` for dividends; it never maps requested bases to provider URL values. Raw-document keys and financial computations are unchanged. See [migration to 3.0](migration-3.0.md).
