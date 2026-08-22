# PO3 / Time + Price / AMD Futures Strategy — TradingView (Pine Script v5)

A rule-based (non-discretionary, non-LLM) TradingView strategy implementing the
ICT-style **Power of Three (PO3)** framework: HTF candle lifecycle
(Accumulation → Manipulation → Distribution), **Time + Price confluence**, and
**IFG/CISD** entry confirmation, per the project's strategy specification.

This is a research/backtesting tool, not financial advice. All inputs must be
calibrated per-instrument on the target futures contract before any sim/live use.

## File

- [`pinescript/po3_amd_time_price_strategy.pine`](pinescript/po3_amd_time_price_strategy.pine)
  — a single self-contained Pine Script v5 `strategy()`. Paste it into
  TradingView's Pine Editor, add it to a chart, and run it in the Strategy
  Tester. Run it on your entry/LTF chart timeframe (e.g. 1min–5min); the HTF
  candle lifecycle is tracked internally from that chart's own bars via
  `timeframe.change()` — no `request.security()` HTF repaint risk.

## Config → input mapping

| Spec parameter | Pine input |
|---|---|
| `HTF` | **HTF (Higher Timeframe)** (default `240` = 4H) |
| `LTF` | the chart timeframe the script is running on |
| `ANCHOR_TIMES` | **Anchor Window 1 / 2** (session strings, exchange tz) + **Restrict HTF opens to ANCHOR_TIMES** toggle |
| `ENTRY_MODEL` | **ENTRY_MODEL** dropdown: `IFG`, `CISD`, `BOTH_AND`, `BOTH_OR` |
| `RISK_PER_TRADE` | **Base contracts per trade** (futures are traded in whole contracts, not % risk) |
| `MIN_RR` | **MIN_RR** (+ counter-trend multiplier) |
| `SESSION_FILTER` | **Allowed Trading Session** + **Restrict trading to SESSION_FILTER window** toggle |

## What it implements

- Synthetic HTF (and parent-HTF, e.g. Daily) candle tracking built from
  confirmed chart bars, reset on every new HTF open.
- Time Premium/Discount, with an optional "deep" mode requiring alignment
  across the HTF **and** its parent HTF (nested confluence).
- The Accumulation → Manipulation state machine: a range builds from the HTF
  open; a liquidity sweep of that range that closes back inside flags
  Manipulation and locks in the sweep extreme (stop level) and an Order Block
  zone (last opposite-direction candle before the sweep).
- IFG confirmation: an FVG that forms during the manipulation leg and is later
  closed-through (inverted) in the reversal direction, with a configurable
  "require N consecutive confirming closes" filter.
- CISD confirmation: a close that breaks the manipulation leg's run of
  same-direction closes, beyond that run's first candle's open.
- An optional higher-conviction mode that waits for price to retrace into the
  Order Block and re-break before entering.
- The Time + Price confluence gate (deep premium/discount **and** price at the
  Order Block or in the discount/premium half of the accumulation range),
  `MIN_RR` filtering, and a counter-trend flag that raises the RR bar and cuts
  position size when the setup runs against the parent-HTF bias.
- Trade management: stop at the sweep extreme, partial exit at the primary
  target (opposite accumulation boundary), a runner extended beyond it, and an
  optional breakeven-stop move after the partial fills.
- A debug table (state, time bias, confirmation progress, filters) and
  `alertcondition()`s for long/short confirmed entries.

## Known simplifications vs. the full spec

- "Price" confluence checks the Order Block and the accumulation range's 50%
  level rather than scanning arbitrary higher-timeframe FVGs/liquidity pools —
  a deliberate approximation to keep the script's PD-array detection bounded
  and auditable.
- The runner/secondary target is a fixed RR extension beyond the primary
  target rather than a detected higher-timeframe liquidity pool.
- One AMD cycle (and at most one trade) is tracked per HTF candle, matching the
  spec's state diagram; it does not run parallel state machines across nested
  timeframes (spec section 6).

Back-test every input against the target contract per the spec's Section 7
checklist before paper/live use.
