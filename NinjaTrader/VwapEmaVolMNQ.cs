#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// NinjaTrader 8 port of "VWAP + EMA + Vol ORB - Bar Close - MNQ" (Pine v5).
// Orders go straight to the account the strategy runs on (Rithmic / Tradovate / etc.),
// so the PickMyTrade webhook part is not needed.
namespace NinjaTrader.NinjaScript.Strategies
{
    public class VwapEmaVolMNQ : Strategy
    {
        private EMA trendEma;
        private SMA volAvg;
        private TimeZoneInfo nyTz;

        // session VWAP (hlc3, resets each New York day)
        private DateTime currentNyDate = DateTime.MinValue;
        private double cumPV;
        private double cumVol;
        private double vwapValue;

        // daily state
        private int tradesToday;
        private double dailyPnL;
        private bool dayLocked;
        private int prevClosedTrades;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                         = "VwapEmaVolMNQ";
                Description                  = "VWAP + EMA + Volume filter, bar-close entries, fixed tick stop/target, daily risk limits.";
                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                BarsRequiredToTrade          = 20;
                Slippage                     = 2;
                StartBehavior                = StartBehavior.WaitUntilFlat;
                IsInstantiatedOnEachOptimizationIteration = true;

                Qty              = 1;
                StopTicks        = 10;
                TargetTicks      = 10;
                FlattenStart     = 1555;
                MaxTradesPerDay  = 2;
                DailyLossLimit   = 150;
                MaxLossPerTrade  = 300;
                EmaLength        = 50;
                VolumeMultiplier = 1.30;

                AddPlot(Brushes.Orange, "VWAP");
                AddPlot(Brushes.DodgerBlue, "TrendEMA");
            }
            else if (State == State.Configure)
            {
                SetStopLoss(CalculationMode.Ticks, StopTicks);
                SetProfitTarget(CalculationMode.Ticks, TargetTicks);
            }
            else if (State == State.DataLoaded)
            {
                trendEma = EMA(Close, EmaLength);
                volAvg   = SMA(Volume, 20);
                nyTz     = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
        }

        protected override void OnBarUpdate()
        {
            // NinjaTrader stamps bars with their close time; step back 1s so the
            // time matches the bar's open, like Pine's "time".
            DateTime ny = TimeZoneInfo.ConvertTime(Time[0], Core.Globals.GeneralOptions.TimeZoneInfo, nyTz).AddSeconds(-1);
            int hhmm = ny.Hour * 100 + ny.Minute;

            // ===== NEW DAY =====
            if (ny.Date != currentNyDate)
            {
                currentNyDate = ny.Date;
                cumPV         = 0;
                cumVol        = 0;
                tradesToday   = 0;
                dailyPnL      = 0;
                dayLocked     = false;
            }

            // ===== VWAP =====
            double hlc3 = (High[0] + Low[0] + Close[0]) / 3.0;
            cumPV  += hlc3 * Volume[0];
            cumVol += Volume[0];
            vwapValue = cumVol > 0 ? cumPV / cumVol : hlc3;

            Values[0][0] = vwapValue;
            Values[1][0] = trendEma[0];

            // ===== CLOSED TRADES -> DAILY STATE =====
            int closed = SystemPerformance.AllTrades.Count;
            if (closed > prevClosedTrades)
            {
                for (int i = prevClosedTrades; i < closed; i++)
                {
                    dailyPnL += SystemPerformance.AllTrades[i].ProfitCurrency;
                    tradesToday++;
                }
                prevClosedTrades = closed;
                if (tradesToday >= MaxTradesPerDay)
                    dayLocked = true;
                if (DailyLossLimit > 0 && dailyPnL <= -DailyLossLimit)
                    dayLocked = true;
            }

            DrawDiagnostics();

            if (CurrentBar < BarsRequiredToTrade)
                return;

            bool inRTH     = hhmm >= 930 && hhmm < 1600;
            bool inFlatten = hhmm >= FlattenStart && hhmm < 1600;
            bool flat      = Position.MarketPosition == MarketPosition.Flat;

            // ===== FORCED CLOSE (hard loss / daily loss / end of day) =====
            if (!flat)
            {
                double openPnL      = Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]);
                double runningDaily = dailyPnL + openPnL;

                if (MaxLossPerTrade > 0 && openPnL <= -MaxLossPerTrade)
                    ExitPosition("Hard Max Loss Per Trade Hit");
                else if (DailyLossLimit > 0 && runningDaily <= -DailyLossLimit)
                {
                    ExitPosition("Daily Loss Limit Hit");
                    dayLocked = true;
                }
                else if (inFlatten)
                    ExitPosition("EOD Flatten");
                return;
            }

            // ===== ENTRIES (bar close, volume + VWAP + EMA filters) =====
            bool canEnter        = inRTH && !inFlatten && !dayLocked;
            bool volumeConfirmed = Volume[0] > volAvg[0] * VolumeMultiplier;

            if (canEnter && volumeConfirmed)
            {
                if (Close[0] > vwapValue && Close[0] > trendEma[0])
                    EnterLong(Qty, "Long");
                else if (Close[0] < vwapValue && Close[0] < trendEma[0])
                    EnterShort(Qty, "Short");
            }
        }

        private void ExitPosition(string reason)
        {
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong(reason, "Long");
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort(reason, "Short");
        }

        private void DrawDiagnostics()
        {
            if (CurrentBar < Count - 2)
                return;
            string text =
                  "VWAP: "         + vwapValue.ToString("0.00")
                + "\nEMA (" + EmaLength + "): " + trendEma[0].ToString("0.00")
                + "\nVol / Avg: "  + Volume[0].ToString("0") + " / " + volAvg[0].ToString("0")
                + "\nTrades today: " + tradesToday + " / " + MaxTradesPerDay
                + "\nDaily P&L: $" + dailyPnL.ToString("0.00")
                + "\nLocked: "     + (dayLocked ? "YES" : "no");
            Draw.TextFixed(this, "diag", text, TextPosition.TopRight);
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Trade Quantity", GroupName = "Trade Settings", Order = 1)]
        public int Qty { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Stop Distance (ticks)", GroupName = "Trade Settings", Order = 2)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Target Distance (ticks)", GroupName = "Trade Settings", Order = 3)]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "EOD Flatten Start (HHmm, NY time)", GroupName = "Risk Management", Order = 1)]
        public int FlattenStart { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Trades Per Day", GroupName = "Risk Management", Order = 2)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Daily Loss Limit (USD, 0=off)", GroupName = "Risk Management", Order = 3)]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Hard Max Loss Per Trade (USD, 0=off)", GroupName = "Risk Management", Order = 4)]
        public double MaxLossPerTrade { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Trend EMA Length", GroupName = "Indicators Settings", Order = 1)]
        public int EmaLength { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Volume Multiplier (x 20-bar avg)", GroupName = "Indicators Settings", Order = 2)]
        public double VolumeMultiplier { get; set; }
        #endregion
    }
}
