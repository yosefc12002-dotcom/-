# Timebased PO3 - 4H Alignment — TradingView (Pine Script v5)

A rule-based (non-discretionary, non-LLM) TradingView strategy: fixed 4H
anchor times (each with its own entry-window length), a 5min/15min FVG used
as the expected "manipulation zone," and a direct sweep-into-zone entry — no
separate confirmation layer.

This is a research/backtesting tool, not financial advice. Defaults are tuned
for **MNQ** (Micro E-mini Nasdaq-100) and the current summer NY/Israel time
offset. Back-test and calibrate every input on your target contract before
any sim/live use.

## File

- [`pinescript/po3_amd_time_price_strategy.pine`](pinescript/po3_amd_time_price_strategy.pine)
  — a single self-contained Pine Script v5 `strategy()`. Paste it into
  TradingView's Pine Editor, add it to a 1min or 5min chart, and run it in the
  Strategy Tester.

## Logic

1. **4H anchors, each with its own window**: `ANCHOR_CONFIG` is a list of
   `NY_hour:window_minutes` pairs. Default `2:120,10:90,14:90` = 02:00 NY
   (120min window), 10:00 NY (90min), 14:00 NY (90min) — which is 9:00-11:00,
   17:00-18:30, and 21:00-22:30 **Israel time** in summer DST (Israel is NY+7h
   when both are on DST; the offset shifts to 6h or 8h for a few weeks around
   the March/late-Oct/early-Nov changeover dates — re-check then).
2. **FVG zone, dual timeframe**: the strategy watches for a 3-candle FVG on
   *both* the 15min and the 5min timeframe. Whichever forms first after the
   anchor (closest in time to the open) is locked in as the zone for that
   cycle; the other is ignored. A bullish gap is always a "lower zone" (sits
   below price), a bearish gap always an "upper zone" (sits above price), by
   construction — no comparison to the 4H open is needed.
3. **Sweep = entry, in one step**: the moment entry-TF price trades into that
   zone, that bar's extreme becomes the stop and it's an immediate market
   entry at a fixed R:R (default 1:2) — swept into the upper zone → short;
   swept into the lower zone → long. There is **no** separate inversion-FVG /
   BOS-CHoCH confirmation step in this version (this was a deliberate
   simplification — see below).
4. One trade per 4H cycle; stops once a position is open or the window
   expires.

## Risk management / filters

- Fixed R:R (`fixedRR`, default 2.0 = 1:2), single exit, no partials/runner.
- Daily trade-count limit, daily loss lockout, optional daily profit lockout.
- Named session filter (New York / Asia / London / All Day / Custom, default
  **All Day** — a continuous range like New York's 09:30-16:00 would exclude
  the 02:00 NY anchor) plus a "skip first N minutes after session open"
  filter.

## Webhook automation (e.g. PickMyTrade)

SL/TP are managed internally via `strategy.exit`. Create **one** TradingView
alert on this strategy with alert type **"Order fills only"**, with a message
template using TradingView's own placeholders, e.g.:
```json
{"ticker": "{{ticker}}", "action": "{{strategy.order.action}}",
 "quantity": "{{strategy.order.contracts}}", "price": "{{close}}",
 "sl": 0, "tp": 0}
```
Verify the exact field names your PickMyTrade account expects in its own
dashboard before wiring this to a live broker connection — this could not be
verified against PickMyTrade's docs from this environment (network access to
their docs site was blocked here).

## Why the confirmation layer was dropped

An earlier draft required, after the sweep, an additional inversion-FVG or
BOS/CHoCH confirmation on the entry timeframe before entering. This version
enters directly on the sweep-into-zone bar instead, per an explicit request
to simplify the logic. That trades fewer missed setups for more false
entries (a sweep that doesn't actually reverse) — back-test this version's
win rate and expectancy specifically; if it underperforms, reintroducing a
confirmation step is the natural next lever to try.
