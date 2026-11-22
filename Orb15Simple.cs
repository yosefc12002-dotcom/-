#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// Orb15Simple
// 15-minute Opening Range Breakout on the New York open.
// - Opening range = high/low of 09:30-09:45 New York time.
// - Run on a 5-minute chart. Entry when a bar CLOSES outside the range.
// - Stop loss at the middle of the range. Target = RewardRisk x risk (default 1:2).
// - One trade per day maximum.
// All time logic is done in New York time, independent of the PC timezone.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class Orb15Simple : Strategy
    {
        // ---- Fixed opening range window (New York time) ----
        private static readonly TimeSpan RangeStartNy = new TimeSpan(9, 30, 0);
        private static readonly TimeSpan RangeEndNy   = new TimeSpan(9, 45, 0);

        private const string LongSignal  = "OrbLong";
        private const string ShortSignal = "OrbShort";

        // ---- Internal state ----
        private TimeZoneInfo nyZone;
        private TimeSpan lastEntryNy;
        private TimeSpan flattenNy;
        private bool settingsInvalid;

        private DateTime currentNyDate = DateTime.MinValue;
        private double rangeHigh;
        private double rangeLow;
        private double rangeMid;
        private int rangeBarCount;
        private int expectedRangeBars;
        private bool rangeComplete;
        private bool tradeTakenToday;
        private DateTime rangeFirstBarTime;   // platform time, used only for drawing
        private string drawTagSuffix = string.Empty;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "15-minute Opening Range Breakout on the New York open (5-minute chart). Stop at range middle, target 1:2, one trade per day.";
                Name = "Orb15Simple";

                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 1;
                IsInstantiatedOnEachOptimizationIteration = true;

                // ---- User parameters (defaults) ----
                Contracts = 1;
                RewardRisk = 2.0;
                LastEntryTimeHHMM = 1200;
                FlattenTimeHHMM = 1555;
                MaxRiskDollars = 1000;
                MinRangeTicks = 8;
                DrawRangeLines = true;
            }
            else if (State == State.DataLoaded)
            {
                // Windows timezone ID. Handles US daylight saving automatically.
                nyZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

                settingsInvalid = false;

                // The strategy is designed for a minute chart whose bar size divides 15 (5-minute recommended).
                if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value <= 0 || 15 % BarsPeriod.Value != 0)
                {
                    Print("Orb15Simple: ERROR - use a Minute chart (5-minute recommended). Current chart is not supported. Strategy will not trade.");
                    settingsInvalid = true;
                }
                else
                {
                    expectedRangeBars = 15 / BarsPeriod.Value;
                }

                if (!TryParseHHMM(LastEntryTimeHHMM, out lastEntryNy) || !TryParseHHMM(FlattenTimeHHMM, out flattenNy))
                {
                    Print("Orb15Simple: ERROR - time parameters must be in HHMM format, e.g. 1200 or 1555. Strategy will not trade.");
                    settingsInvalid = true;
                }
                else if (lastEntryNy <= RangeEndNy || flattenNy <= lastEntryNy)
                {
                    Print("Orb15Simple: ERROR - required order: 09:45 < Last entry time < Flatten time. Strategy will not trade.");
                    settingsInvalid = true;
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (settingsInvalid || BarsInProgress != 0)
                return;

            if (CurrentBar < BarsRequiredToTrade)
                return;

            // Bar timestamp (bar CLOSE time) converted from platform time to New York time.
            DateTime nyTime = ToNewYork(Time[0]);
            TimeSpan tod = nyTime.TimeOfDay;

            // ---- New New York trading day: reset everything ----
            if (nyTime.Date != currentNyDate)
            {
                currentNyDate = nyTime.Date;
                rangeHigh = 0;
                rangeLow = 0;
                rangeMid = 0;
                rangeBarCount = 0;
                rangeComplete = false;
                tradeTakenToday = false;
                drawTagSuffix = currentNyDate.ToString("yyyyMMdd");
            }

            // ---- 1. Build the opening range: bars closing after 09:30 and up to 09:45 NY ----
            if (tod > RangeStartNy && tod <= RangeEndNy)
            {
                if (rangeBarCount == 0)
                {
                    rangeHigh = High[0];
                    rangeLow = Low[0];
                    rangeFirstBarTime = Time[0];
                }
                else
                {
                    rangeHigh = Math.Max(rangeHigh, High[0]);
                    rangeLow = Math.Min(rangeLow, Low[0]);
                }
                rangeBarCount++;

                if (tod == RangeEndNy)
                    FinalizeRange();

                return;
            }

            // Safety: if the exact 09:45 bar was missing, finalize on the first bar after it.
            if (!rangeComplete && rangeBarCount > 0 && tod > RangeEndNy)
                FinalizeRange();

            // ---- Keep range lines extended on the chart until flatten time ----
            if (DrawRangeLines && rangeComplete && rangeMid > 0 && tod <= flattenNy)
                DrawRange();

            // ---- 2. Flatten at the end of the trading window ----
            if (tod >= flattenNy)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("TimeExit", LongSignal);
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("TimeExit", ShortSignal);
                return;
            }

            // ---- 3. Entry logic ----
            if (!rangeComplete || tradeTakenToday)
                return;
            if (Position.MarketPosition != MarketPosition.Flat)
                return;
            if (tod <= RangeEndNy || tod > lastEntryNy)
                return;

            double pointValue = Instrument.MasterInstrument.PointValue;
            double entryEstimate = Close[0];

            if (Close[0] > rangeHigh)
            {
                double risk = entryEstimate - rangeMid;
                double riskDollars = risk * pointValue * Contracts;

                if (riskDollars > MaxRiskDollars)
                {
                    Print(string.Format("Orb15Simple: {0:yyyy-MM-dd} LONG signal skipped. Risk ${1:F0} > max ${2:F0}. No more trades today.", currentNyDate, riskDollars, MaxRiskDollars));
                    tradeTakenToday = true;
                    return;
                }

                double target = Instrument.MasterInstrument.RoundToTickSize(entryEstimate + risk * RewardRisk);
                SetStopLoss(LongSignal, CalculationMode.Price, rangeMid, false);
                SetProfitTarget(LongSignal, CalculationMode.Price, target);
                EnterLong(Contracts, LongSignal);
                tradeTakenToday = true;

                Print(string.Format("Orb15Simple: {0:yyyy-MM-dd} LONG. Close {1} > range high {2}. Stop {3}, target {4} (est.).", currentNyDate, Close[0], rangeHigh, rangeMid, target));
            }
            else if (Close[0] < rangeLow)
            {
                double risk = rangeMid - entryEstimate;
                double riskDollars = risk * pointValue * Contracts;

                if (riskDollars > MaxRiskDollars)
                {
                    Print(string.Format("Orb15Simple: {0:yyyy-MM-dd} SHORT signal skipped. Risk ${1:F0} > max ${2:F0}. No more trades today.", currentNyDate, riskDollars, MaxRiskDollars));
                    tradeTakenToday = true;
                    return;
                }

                double target = Instrument.MasterInstrument.RoundToTickSize(entryEstimate - risk * RewardRisk);
                SetStopLoss(ShortSignal, CalculationMode.Price, rangeMid, false);
                SetProfitTarget(ShortSignal, CalculationMode.Price, target);
                EnterShort(Contracts, ShortSignal);
                tradeTakenToday = true;

                Print(string.Format("Orb15Simple: {0:yyyy-MM-dd} SHORT. Close {1} < range low {2}. Stop {3}, target {4} (est.).", currentNyDate, Close[0], rangeLow, rangeMid, target));
            }
        }

        // Re-calculate the profit target from the ACTUAL fill price, so the trade is exactly 1:RewardRisk.
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;
            if (execution.Order.OrderState != OrderState.Filled)
                return;

            string orderName = execution.Order.Name;
            double fill = execution.Order.AverageFillPrice;

            if (orderName == LongSignal)
            {
                double risk = fill - rangeMid;
                if (risk <= 0)
                {
                    // Filled at or below the stop level (gap). Exit immediately.
                    ExitLong("BadFillExit", LongSignal);
                    return;
                }
                SetProfitTarget(LongSignal, CalculationMode.Price,
                    Instrument.MasterInstrument.RoundToTickSize(fill + risk * RewardRisk));
            }
            else if (orderName == ShortSignal)
            {
                double risk = rangeMid - fill;
                if (risk <= 0)
                {
                    // Filled at or above the stop level (gap). Exit immediately.
                    ExitShort("BadFillExit", ShortSignal);
                    return;
                }
                SetProfitTarget(ShortSignal, CalculationMode.Price,
                    Instrument.MasterInstrument.RoundToTickSize(fill - risk * RewardRisk));
            }
        }

        #region Helpers

        private void FinalizeRange()
        {
            rangeComplete = true;

            if (rangeBarCount < expectedRangeBars)
            {
                Print(string.Format("Orb15Simple: {0:yyyy-MM-dd} opening range incomplete ({1}/{2} bars). No trade today.", currentNyDate, rangeBarCount, expectedRangeBars));
                tradeTakenToday = true;
                return;
            }

            rangeMid = Instrument.MasterInstrument.RoundToTickSize((rangeHigh + rangeLow) / 2.0);

            double rangeTicks = (rangeHigh - rangeLow) / TickSize;
            if (rangeTicks < MinRangeTicks)
            {
                Print(string.Format("Orb15Simple: {0:yyyy-MM-dd} range too small ({1:F0} ticks < {2}). No trade today.", currentNyDate, rangeTicks, MinRangeTicks));
                tradeTakenToday = true;
            }

            Print(string.Format("Orb15Simple: {0:yyyy-MM-dd} range High {1}  Low {2}  Mid {3}", currentNyDate, rangeHigh, rangeLow, rangeMid));

            if (DrawRangeLines)
                DrawRange();
        }

        private void DrawRange()
        {
            Draw.Line(this, "OrbHigh" + drawTagSuffix, false, rangeFirstBarTime, rangeHigh, Time[0], rangeHigh, Brushes.LimeGreen, DashStyleHelper.Solid, 2);
            Draw.Line(this, "OrbLow" + drawTagSuffix, false, rangeFirstBarTime, rangeLow, Time[0], rangeLow, Brushes.Red, DashStyleHelper.Solid, 2);
            Draw.Line(this, "OrbMid" + drawTagSuffix, false, rangeFirstBarTime, rangeMid, Time[0], rangeMid, Brushes.Gold, DashStyleHelper.Dash, 1);
        }

        private DateTime ToNewYork(DateTime platformTime)
        {
            DateTime unspecified = DateTime.SpecifyKind(platformTime, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTime(unspecified, NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, nyZone);
        }

        private static bool TryParseHHMM(int hhmm, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            int hours = hhmm / 100;
            int minutes = hhmm % 100;
            if (hhmm < 0 || hours > 23 || minutes > 59)
                return false;
            result = new TimeSpan(hours, minutes, 0);
            return true;
        }

        #endregion

        #region Properties

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Contracts", Description = "Number of contracts per trade", Order = 1, GroupName = "1. Trade")]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "Reward / Risk", Description = "Target as a multiple of risk (2 = 1:2)", Order = 2, GroupName = "1. Trade")]
        public double RewardRisk { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Max risk per trade ($)", Description = "Skip the trade if distance to the stop costs more than this (all contracts)", Order = 3, GroupName = "1. Trade")]
        public double MaxRiskDollars { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Min range (ticks)", Description = "Skip the day if the opening range is smaller than this", Order = 4, GroupName = "1. Trade")]
        public int MinRangeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(946, 2359)]
        [Display(Name = "Last entry time NY (HHMM)", Description = "No new entries after this New York time, e.g. 1200", Order = 1, GroupName = "2. Time (New York)")]
        public int LastEntryTimeHHMM { get; set; }

        [NinjaScriptProperty]
        [Range(947, 2359)]
        [Display(Name = "Flatten time NY (HHMM)", Description = "Close any open position at this New York time, e.g. 1555", Order = 2, GroupName = "2. Time (New York)")]
        public int FlattenTimeHHMM { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw range lines", Order = 1, GroupName = "3. Display")]
        public bool DrawRangeLines { get; set; }

        #endregion
    }
}
