# Migrating price requests to 3.0

Every `PriceHistoryRequest` must state a validated `PriceBasis`. Choose the convention the consuming strategy requires; changing provider can change both OHLC and adjusted-close conventions.

```csharp
var request = new PriceHistoryRequest([new Symbol("SPY")], range)
{
    Basis = TiingoPriceBarAdapter.DeliveredPriceBasis
};
```

For Twelve Data, use `TwelveDataPriceBarAdapter.DeliveredPriceBasis`. Tiingo's token is `all.provider.none`; Twelve Data's is `all.computed.splits`. Both adjusted closes include splits and dividends, but Tiingo supplies its level, while Twelve Data computes it from split-adjusted prices and cash dividends with the request's final bar as anchor. The numeric algorithms are unchanged.

`CanHandle` returns false for a different basis. Direct `FetchAsync` refuses it with `PriceBasisNotSupportedException` before I/O. The kernel checks declarations before dispatch and rejects missing or mismatched provenance. It never silently falls back between these conventions. Handle the typed basis errors separately from provider availability and `PARTITION_FAILED` issues.

Custom price-adapter decorators must implement `IPriceBasisAdapter` and forward `DeliveredBasis` from the wrapped adapter. They must preserve every provenance entry's basis. An undeclared decorator is skipped; a request with no declaring adapter is refused before HTTP.

Kernel cache identities now include the basis token. Existing unproven snapshot results are refetched; a proven mismatched basis fails explicitly. Raw time-series identities gain a basis suffix, so 2.0.x series are not addressed by 3.0 price requests. Re-ingest each price series on first use; old series can be reclaimed separately. Window-dependent adjusted levels are not eligible for raw time-series assembly. Legacy L2 price imports require both `PriceHistoryRequest.Basis = PriceBasis.Raw` and `LegacyL2Snapshot<TRequest, Bar>.AttestsRawPriceBasis = true`. Set the attestation only when the stored bars are known to be unadjusted; a raw request alone does not establish the provenance of legacy bars. Do not relabel historical adjusted data as raw.

Non-price adapters and Tiingo dividend requests retain their existing contracts. Provider raw-document keys, price URL adjustment settings and numeric payloads are unchanged.
