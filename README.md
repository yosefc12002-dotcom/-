# Liquidity Sweep Strategy (NinjaTrader 8)

`LiquiditySweepStrategy.cs` takes a trade when price sweeps a high or low and then comes back:

- **Short:** price trades above the reference high, then a bar closes back below it.
- **Long:** price trades below the reference low, then a bar closes back above it.

Take profit and stop loss are set in **points**. The strategy converts them to ticks for whatever instrument it runs on (e.g. 10 points on ES = 40 ticks).

## Install
1. Copy `LiquiditySweepStrategy.cs` to `Documents\NinjaTrader 8\bin\Custom\Strategies\`.
2. In NinjaTrader, open **New → NinjaScript Editor** and press **F5** to compile.
3. Add the strategy to a chart or the Strategy Analyzer.

## Settings
| Setting | Meaning |
|---|---|
| Take Profit / Stop Loss (points) | Exit distances in points from the entry |
| Level Mode | `LookbackBars` = highest high / lowest low of the last N bars; `PriorDayHighLow` = previous session's high/low |
| Swing Lookback | N bars used in `LookbackBars` mode |
| Reclaim Within (bars) | 0 = the sweep bar itself must close back inside; N = allows N more bars |
| Min Sweep Distance (points) | How far past the level price must trade to count as a sweep |
| Enable Longs / Shorts | Turn each side on or off |
| Time filter, Max trades per day | Optional filters |

Backtest in the Strategy Analyzer and run in Sim before using it live. This is not financial advice.
