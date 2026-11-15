#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// Liquidity Sweep strategy for NinjaTrader 8
//
// SHORT: price trades ABOVE the reference high (sweeps the buy-side liquidity)
//        and then closes back BELOW that high  -> sell short.
// LONG:  price trades BELOW the reference low (sweeps the sell-side liquidity)
//        and then closes back ABOVE that low   -> buy long.
//
// The reference high/low is either the highest high / lowest low of the last
// N bars (SwingLookback) or the prior trading day's high/low.
// Take profit and stop loss are entered in POINTS and converted to ticks.
//
// Install: NinjaTrader 8 -> New -> NinjaScript Editor -> Strategies folder ->
// right-click -> New Strategy (or drop this file into
// Documents\NinjaTrader 8\bin\Custom\Strategies) and press F5 to compile.

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum SweepLevelMode
    {
        LookbackBars,
        PriorDayHighLow
    }

    public class LiquiditySweepStrategy : Strategy
    {
        // Active sweep tracking
        private double sweptHighLevel = double.NaN;
        private int    highSweepBar   = -1;
        private double sweptLowLevel  = double.NaN;
        private int    lowSweepBar    = -1;

        // Prior-day levels
        private double curDayHigh = double.MinValue;
        private double curDayLow  = double.MaxValue;
        private double prevDayHigh = double.NaN;
        private double prevDayLow  = double.NaN;

        private int tradesToday;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Shorts when price sweeps a high and closes back below it; " +
                              "goes long when price sweeps a low and closes back above it. TP/SL in points.";
                Name                         = "LiquiditySweepStrategy";
                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                IsFillLimitOnTouch           = false;
                MaximumBarsLookBack          = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution          = OrderFillResolution.Standard;
                Slippage                     = 0;
                StartBehavior                = StartBehavior.WaitUntilFlat;
                TimeInForce                  = TimeInForce.Gtc;
                TraceOrders                  = false;
                RealtimeErrorHandling        = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling           = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade          = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // Defaults
                Quantity          = 1;
                TakeProfitPoints  = 20;
                StopLossPoints    = 10;
                LevelMode         = SweepLevelMode.LookbackBars;
                SwingLookback     = 20;
                ReclaimBars       = 3;
                MinSweepPoints    = 0;
                EnableLongs       = true;
                EnableShorts      = true;
                UseTimeFilter     = false;
                StartTime         = DateTime.Parse("09:30", System.Globalization.CultureInfo.InvariantCulture);
                EndTime           = DateTime.Parse("15:45", System.Globalization.CultureInfo.InvariantCulture);
                MaxTradesPerDay   = 0;
                DrawLevels        = true;
            }
            else if (State == State.DataLoaded)
            {
                // TickSize is only guaranteed once data is loaded (Instrument can be null in Configure)
                int tpTicks = Math.Max(1, (int)Math.Round(TakeProfitPoints / TickSize));
                int slTicks = Math.Max(1, (int)Math.Round(StopLossPoints  / TickSize));

                SetProfitTarget("SweepLong",  CalculationMode.Ticks, tpTicks);
                SetStopLoss    ("SweepLong",  CalculationMode.Ticks, slTicks, false);
                SetProfitTarget("SweepShort", CalculationMode.Ticks, tpTicks);
                SetStopLoss    ("SweepShort", CalculationMode.Ticks, slTicks, false);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            // ----- Session / day bookkeeping -----
            if (Bars.IsFirstBarOfSession)
            {
                if (curDayHigh != double.MinValue)
                {
                    prevDayHigh = curDayHigh;
                    prevDayLow  = curDayLow;
                }
                curDayHigh  = double.MinValue;
                curDayLow   = double.MaxValue;
                tradesToday = 0;
                ResetSweeps();
            }

            if (CurrentBar < Math.Max(BarsRequiredToTrade, SwingLookback + 1))
            {
                UpdateDayRange();
                return;
            }

            // ----- Reference levels (exclude the current bar) -----
            double refHigh, refLow;
            if (LevelMode == SweepLevelMode.LookbackBars)
            {
                refHigh = MAX(High, SwingLookback)[1];
                refLow  = MIN(Low,  SwingLookback)[1];
            }
            else
            {
                refHigh = prevDayHigh;
                refLow  = prevDayLow;
            }

            UpdateDayRange();

            if (double.IsNaN(refHigh) || double.IsNaN(refLow))
                return;

            double minSweep = MinSweepPoints;

            // ----- Detect new sweeps -----
            // High sweep: wick pokes above the reference high
            if (High[0] > refHigh + minSweep && (double.IsNaN(sweptHighLevel) || refHigh != sweptHighLevel))
            {
                sweptHighLevel = refHigh;
                highSweepBar   = CurrentBar;
            }
            // Low sweep: wick pokes below the reference low
            if (Low[0] < refLow - minSweep && (double.IsNaN(sweptLowLevel) || refLow != sweptLowLevel))
            {
                sweptLowLevel = refLow;
                lowSweepBar   = CurrentBar;
            }

            // Expire stale sweeps
            if (highSweepBar >= 0 && CurrentBar - highSweepBar > ReclaimBars)
            {
                sweptHighLevel = double.NaN;
                highSweepBar   = -1;
            }
            if (lowSweepBar >= 0 && CurrentBar - lowSweepBar > ReclaimBars)
            {
                sweptLowLevel = double.NaN;
                lowSweepBar   = -1;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
                return;
            if (UseTimeFilter && !InTradingWindow(Time[0]))
                return;
            if (MaxTradesPerDay > 0 && tradesToday >= MaxTradesPerDay)
                return;

            // ----- Entries: close back inside the range after the sweep -----
            if (EnableShorts && highSweepBar >= 0 && Close[0] < sweptHighLevel)
            {
                if (DrawLevels)
                    Draw.ArrowDown(this, "SweepShort" + CurrentBar, true, 0, High[0] + 2 * TickSize, Brushes.Red);
                EnterShort(Quantity, "SweepShort");
                tradesToday++;
                ResetSweeps();
                return;
            }

            if (EnableLongs && lowSweepBar >= 0 && Close[0] > sweptLowLevel)
            {
                if (DrawLevels)
                    Draw.ArrowUp(this, "SweepLong" + CurrentBar, true, 0, Low[0] - 2 * TickSize, Brushes.LimeGreen);
                EnterLong(Quantity, "SweepLong");
                tradesToday++;
                ResetSweeps();
            }
        }

        private void UpdateDayRange()
        {
            curDayHigh = Math.Max(curDayHigh, High[0]);
            curDayLow  = Math.Min(curDayLow,  Low[0]);
        }

        private void ResetSweeps()
        {
            sweptHighLevel = double.NaN;
            highSweepBar   = -1;
            sweptLowLevel  = double.NaN;
            lowSweepBar    = -1;
        }

        private bool InTradingWindow(DateTime t)
        {
            TimeSpan now   = t.TimeOfDay;
            TimeSpan start = StartTime.TimeOfDay;
            TimeSpan end   = EndTime.TimeOfDay;
            return start <= end ? (now >= start && now <= end)
                                : (now >= start || now <= end); // overnight window
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Quantity", Order = 1, GroupName = "1. Orders")]
        public int Quantity { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Take Profit (points)", Order = 2, GroupName = "1. Orders")]
        public double TakeProfitPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "Stop Loss (points)", Order = 3, GroupName = "1. Orders")]
        public double StopLossPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Level Mode", Description = "LookbackBars = highest high / lowest low of last N bars. PriorDayHighLow = yesterday's high/low.", Order = 1, GroupName = "2. Sweep")]
        public SweepLevelMode LevelMode { get; set; }

        [NinjaScriptProperty]
        [Range(2, 200)]
        [Display(Name = "Swing Lookback (bars)", Order = 2, GroupName = "2. Sweep")]
        public int SwingLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Reclaim Within (bars)", Description = "0 = must close back inside on the sweep bar itself. N = allow N more bars to close back inside.", Order = 3, GroupName = "2. Sweep")]
        public int ReclaimBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Min Sweep Distance (points)", Description = "How far past the level price must trade to count as a sweep.", Order = 4, GroupName = "2. Sweep")]
        public double MinSweepPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Longs", Order = 5, GroupName = "2. Sweep")]
        public bool EnableLongs { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Shorts", Order = 6, GroupName = "2. Sweep")]
        public bool EnableShorts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Time Filter", Order = 1, GroupName = "3. Filters")]
        public bool UseTimeFilter { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Start Time", Order = 2, GroupName = "3. Filters")]
        public DateTime StartTime { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "End Time", Order = 3, GroupName = "3. Filters")]
        public DateTime EndTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Max Trades Per Day (0 = unlimited)", Order = 4, GroupName = "3. Filters")]
        public int MaxTradesPerDay { get; set; }

        [Display(Name = "Draw Entry Arrows", Order = 5, GroupName = "3. Filters")]
        public bool DrawLevels { get; set; }
        #endregion
    }
}
