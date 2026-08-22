# Timebased PO3 - 4H Alignment — TradingView (Pine Script v5)

A rule-based (non-discretionary, non-LLM) TradingView strategy implementing a
concrete PO3/AMD futures setup: fixed 4H opens (NY time), a 15-minute FVG used
as the expected "manipulation zone" for that 4H candle's wick, and an
entry-timeframe (1-5min) inversion-FVG / BOS-CHoCH confirmation.

This is a research/backtesting tool, not financial advice. Defaults in this
version were tuned for **MNQ** (Micro E-mini Nasdaq-100). Back-test and
calibrate every input on your target contract before any sim/live use.

## File

- [`pinescript/po3_amd_time_price_strategy.pine`](pinescript/po3_amd_time_price_strategy.pine)
  — a single self-contained Pine Script v5 `strategy()`. Paste it into
  TradingView's Pine Editor, add it to a 1min or 5min chart, and run it in the
  Strategy Tester.

## Logic

1. **4H anchor**: flags the fixed 4H opens at 02:00 / 06:00 / 10:00 / 14:00 /
   18:00 / 22:00 New York time (configurable), independent of however the
   broker/exchange natively aligns 4H bars.
2. **Entry window**: only from minute 1 to minute 30 after each anchor
   (configurable length).
3. **15min FVG zone**: the most recent 3-candle FVG on the FVG-detection
   timeframe (default 15min) is tracked as a target zone — a bullish gap
   always sits below current price (a "lower zone"), a bearish gap always
   sits above it (an "upper zone"), by construction.
4. **Manipulation**: once entry-TF price trades into the upper zone, that's
   the developing 4H candle's top wick (short bias); into the lower zone,
   the bottom wick (long bias).
5. **Confirmation**: on the entry TF, an inversion FVG (a 3-candle gap formed
   during the wick that price later closes back through) and/or a BOS/CHoCH
   (a close beyond the most recent pivot formed during the wick), per
   `ENTRY_MODEL` (`IFG`, `CHOCH`, `BOTH_AND`, or `BOTH_OR` — default `BOTH_OR`,
   i.e. either one confirms).

## Risk management

- Stop beyond the manipulation wick extreme (extended if price wicks deeper
  before confirmation).
- Fixed R:R target (`fixedRR`, default 2.0 = 1:2), single exit — no
  partials/runner.
- One trade per 4H cycle; no new entry once a position is open.
- Daily trade-count limit, daily loss lockout, optional daily profit lockout
  (all in the strategy's account currency).
- Named session filter (New York / Asia / London / All Day / Custom) plus a
  "skip first N minutes after session open" filter.

## Webhook automation (e.g. PickMyTrade)

The script manages SL/TP internally via `strategy.exit` and does not embed a
broker-specific JSON payload in an `alert()`/`alertcondition()` call, because
that payload's exact required field names are account/provider-specific and
could not be verified against PickMyTrade's docs from this environment
(network access to their docs site was blocked here). Recommended setup:

1. Create **one** TradingView alert on this strategy with alert type **"Order
   fills only"**.
2. Use a message template with TradingView's own placeholders, e.g.:
   ```json
   {"ticker": "{{ticker}}", "action": "{{strategy.order.action}}",
    "quantity": "{{strategy.order.contracts}}", "price": "{{close}}",
    "sl": 0, "tp": 0}
   ```
3. **Verify the exact field names/values your PickMyTrade account expects**
   in the PickMyTrade dashboard's own alert-template generator before wiring
   this to a live broker connection — do not assume the template above is
   final.

## Known simplifications

- The 15min FVG zone is always the single most recently detected one on each
  side (no history of multiple candidate zones).
- BOS/CHoCH uses a reactive 1-bar-left/1-bar-right pivot, not a full swing-
  structure model.
- No Order Block retracement or Time-Premium/Discount gate from earlier
  drafts of this strategy — this version follows the concrete 4H-anchor +
  15m-FVG-zone spec directly instead.
