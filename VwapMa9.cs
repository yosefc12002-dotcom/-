#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// VwapMa9 - VWAP + moving average cross strategy for MNQ futures.
//
// Concept:
//   SHORT: bar closes below VWAP, MA is below VWAP (or crossed below it within the
//          last N bars), and the previous bar closed above VWAP or above the MA.
//   LONG : mirror image.
//   Entry at market on the open of the next bar (Calculate = OnBarClose).
//
// Stop  : highest high / lowest low of the last X bars plus a tick buffer.
// Target: Risk:Reward multiple of the stop distance.
// VWAP  : built manually from typical price * volume, reset daily at the NY session start.

namespace NinjaTrader.NinjaScript.Strategies
{
	// Enums are declared outside the strategy class so they serialize cleanly in workspaces/templates.
	public enum VwapMa9Direction
	{
		Both,
		Long,
		Short
	}

	public enum VwapMa9MAType
	{
		EMA,
		SMA
	}

	public class VwapMa9 : Strategy
	{
		#region Constants

		private const string LongSignal       = "VM9_Long";
		private const string ShortSignal      = "VM9_Short";
		private const string TimeExitSignal   = "VM9_TimeExit";
		private const string DailyLossSignal  = "VM9_DailyLoss";
		private const double MnqPointValue    = 2.0;   // Fallback USD per point for MNQ

		#endregion

		#region Private fields

		// Indicators
		private ISeries<double> ma;
		private MAX swingHigh;
		private MIN swingLow;
		private EMA trendEma;

		// Manual VWAP state
		private Series<double> vwapSeries;
		private double cumPV;
		private double cumVol;
		private DateTime vwapDate = DateTime.MinValue;   // NY date of the last VWAP reset
		private int sessionBars;                         // Bars since the VWAP reset (current bar = 1)

		// Time zones
		private TimeZoneInfo chartTz;
		private TimeZoneInfo nyTz;

		// Daily counters
		private SessionIterator sessionIterator;
		private DateTime currentTradingDay = DateTime.MinValue;
		private int tradesToday;
		private double dayStartCumProfit;
		private bool dailyLossHit;

		// Order / trade state
		private bool pendingEntry;
		private bool exitPending;
		private int lastExitBar = -1;
		private double pendStop;
		private double pendTarget;
		private int pendQty;
		private double entryFillPrice;
		private double initialRiskPoints;
		private bool breakevenMoved;

		// Visual state for stop/target lines
		private bool lineActive;
		private int lineStartBar;
		private int lineId;
		private double lineStop;
		private double lineTarget;

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                                 = "VWAP + MA cross strategy for MNQ with swing stop, R:R target, $ risk sizing and NY session filters.";
				Name                                        = "VwapMa9";
				Calculate                                   = Calculate.OnBarClose;
				EntriesPerDirection                         = 1;
				EntryHandling                               = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy                = true;
				ExitOnSessionCloseSeconds                   = 30;
				IsFillLimitOnTouch                          = false;
				MaximumBarsLookBack                         = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution                         = OrderFillResolution.Standard;
				Slippage                                    = 0;
				StartBehavior                               = StartBehavior.WaitUntilFlat;
				TimeInForce                                 = TimeInForce.Gtc;
				TraceOrders                                 = false;
				RealtimeErrorHandling                       = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling                          = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade                         = 20;
				IsInstantiatedOnEachOptimizationIteration   = true;

				// 1. Position Sizing
				UseRiskSizing       = true;
				RiskPerTrade        = 225;
				FixedContracts      = 1;
				MaxContracts        = 20;

				// 2. Setup
				TradeDirection      = VwapMa9Direction.Both;
				MovingAvgType       = VwapMa9MAType.EMA;
				MALength            = 9;
				CrossLookbackBars   = 3;
				MaxTradesPerDay     = 1;
				RequirePrevCloseAcrossVwap = false;
				RequireRecentMaCross       = false;
				TrendEmaLength             = 0;

				// 3. Stop and Target
				SwingLookbackBars   = 10;
				StopBufferTicks     = 2;
				MaxStopPoints       = 0;
				RiskReward          = 2;
				BreakevenAtR        = 0;

				// 4. Session (NY time)
				SessionStartHour    = 9;
				SessionStartMinute  = 30;
				LastEntryHour       = 11;
				LastEntryMinute     = 0;
				ExitHour            = 15;
				ExitMinute          = 50;
				UseSecondWindow     = false;
				SecondStartHour     = 13;
				SecondStartMinute   = 30;
				SecondEndHour       = 15;
				SecondEndMinute     = 0;

				// 5. Risk Limits
				MaxDailyLoss        = 0;

				// 6. Visuals
				ShowVisuals         = true;

				// Plots are only filled with values when ShowVisuals is true.
				AddPlot(new Stroke(Brushes.Orange, 2), PlotStyle.Line, "VWAP");
				AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "MA");
			}
			else if (State == State.Configure)
			{
			}
			else if (State == State.DataLoaded)
			{
				if (MovingAvgType == VwapMa9MAType.SMA)
					ma = SMA(Close, MALength);
				else
					ma = EMA(Close, MALength);

				if (TrendEmaLength > 0)
					trendEma = EMA(Close, TrendEmaLength);

				swingHigh       = MAX(High, SwingLookbackBars);
				swingLow        = MIN(Low, SwingLookbackBars);
				vwapSeries      = new Series<double>(this, MaximumBarsLookBack.Infinite);
				sessionIterator = new SessionIterator(Bars);

				// Bar timestamps are expressed in the time zone selected in NinjaTrader general options.
				chartTz = Core.Globals.GeneralOptions.TimeZoneInfo;
				nyTz    = FindNewYorkTimeZone();
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;

			DateTime nyTime = ToNewYork(Time[0]);
			DateTime nyDate = nyTime.Date;
			TimeSpan nyTod  = nyTime.TimeOfDay;

			TimeSpan sessionStart = new TimeSpan(SessionStartHour, SessionStartMinute, 0);
			TimeSpan lastEntry    = new TimeSpan(LastEntryHour, LastEntryMinute, 0);
			TimeSpan exitTime     = new TimeSpan(ExitHour, ExitMinute, 0);

			// ---------- Daily counter reset (trading day from the session iterator) ----------
			if (Bars.IsFirstBarOfSession || currentTradingDay == DateTime.MinValue)
			{
				DateTime tradingDay = nyDate;
				try
				{
					sessionIterator.GetNextSession(Time[0], true);
					tradingDay = sessionIterator.ActualTradingDayExchange.Date;
				}
				catch
				{
					// Fall back to the NY calendar date if the session iterator fails.
				}

				if (tradingDay != currentTradingDay)
					ResetDailyCounters(tradingDay);
			}

			// ---------- Manual VWAP, reset at the NY session start ----------
			// A bar timestamp is its close time, so the first session bar is the first bar
			// whose NY close time is strictly after the session start.
			if (nyTod > sessionStart && nyDate != vwapDate)
			{
				cumPV       = 0;
				cumVol      = 0;
				sessionBars = 0;
				vwapDate    = nyDate;
			}

			bool vwapValid = (vwapDate == nyDate && nyTod > sessionStart);

			if (vwapValid)
			{
				double typical = (High[0] + Low[0] + Close[0]) / 3.0;
				double vol     = Volume[0];
				cumPV  += typical * vol;
				cumVol += vol;
				sessionBars++;
				vwapSeries[0] = cumVol > 0 ? cumPV / cumVol : typical;
			}
			else
			{
				// Outside the VWAP window: carry the last value so the series stays defined.
				vwapSeries[0] = CurrentBar > 0 ? vwapSeries[1] : Close[0];
			}

			double ma0   = ma[0];
			double vwap0 = vwapSeries[0];

			// ---------- Plots ----------
			TimeSpan plotEnd = exitTime > new TimeSpan(16, 0, 0) ? exitTime : new TimeSpan(16, 0, 0);
			if (ShowVisuals && vwapValid && nyTod <= plotEnd)
			{
				Values[0][0] = vwap0;
				Values[1][0] = ma0;
			}

			// ---------- Register a closed trade (set by OnPositionUpdate) ----------
			if (exitPending)
			{
				lastExitBar = CurrentBar;
				exitPending = false;
			}

			// ---------- Stop / target lines for the active trade ----------
			if (ShowVisuals && lineActive)
			{
				int barsAgo = Math.Max(0, CurrentBar - lineStartBar);
				Draw.Line(this, "VM9_Stop_" + lineId, false, barsAgo, lineStop, 0, lineStop, Brushes.Red, DashStyleHelper.Dash, 2);
				Draw.Line(this, "VM9_Target_" + lineId, false, barsAgo, lineTarget, 0, lineTarget, Brushes.LimeGreen, DashStyleHelper.Dash, 2);
			}
			if (lineActive && !pendingEntry && Position.MarketPosition == MarketPosition.Flat && CurrentBar > lineStartBar)
				lineActive = false;

			int barsNeeded = Math.Max(Math.Max(MALength, SwingLookbackBars), CrossLookbackBars + 1) + 2;
			if (CurrentBar < Math.Max(barsNeeded, BarsRequiredToTrade))
				return;

			// ---------- Max daily loss ----------
			double realizedToday = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - dayStartCumProfit;
			double unrealized    = Position.MarketPosition != MarketPosition.Flat
				? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0])
				: 0;

			if (MaxDailyLoss > 0 && !dailyLossHit && realizedToday + unrealized <= -MaxDailyLoss)
			{
				dailyLossHit = true;
				Print(string.Format("VwapMa9 | {0:yyyy-MM-dd HH:mm} NY | Max daily loss reached ({1:F2}). No more entries today.",
					nyTime, realizedToday + unrealized));
			}

			if (dailyLossHit && Position.MarketPosition != MarketPosition.Flat)
			{
				FlattenPosition(DailyLossSignal);
				return;
			}

			// ---------- Breakeven: move the stop to entry once price has moved X R in favor ----------
			if (BreakevenAtR > 0 && !breakevenMoved && initialRiskPoints > 0 && Position.MarketPosition != MarketPosition.Flat)
			{
				double tickSz = TickSize > 0 ? TickSize : 0.25;
				if (Position.MarketPosition == MarketPosition.Long && High[0] >= entryFillPrice + BreakevenAtR * initialRiskPoints)
				{
					double beStop = Instrument.MasterInstrument.RoundToTickSize(entryFillPrice + tickSz);
					if (Close[0] > beStop)   // Only move if the new stop is below the current price
					{
						SetStopLoss(LongSignal, CalculationMode.Price, beStop, false);
						breakevenMoved = true;
					}
				}
				else if (Position.MarketPosition == MarketPosition.Short && Low[0] <= entryFillPrice - BreakevenAtR * initialRiskPoints)
				{
					double beStop = Instrument.MasterInstrument.RoundToTickSize(entryFillPrice - tickSz);
					if (Close[0] < beStop)   // Only move if the new stop is above the current price
					{
						SetStopLoss(ShortSignal, CalculationMode.Price, beStop, false);
						breakevenMoved = true;
					}
				}
				if (breakevenMoved && ShowVisuals && lineActive)
					lineStop = Instrument.MasterInstrument.RoundToTickSize(Position.MarketPosition == MarketPosition.Long ? entryFillPrice + tickSz : entryFillPrice - tickSz);
			}

			// ---------- Time exit ----------
			if (Position.MarketPosition != MarketPosition.Flat && nyTod >= exitTime)
			{
				FlattenPosition(TimeExitSignal);
				return;
			}

			// ---------- Entry filters ----------
			if (Position.MarketPosition != MarketPosition.Flat || pendingEntry)
				return;
			if (dailyLossHit)
				return;
			if (tradesToday >= MaxTradesPerDay)
				return;
			if (!vwapValid || sessionBars < 2)   // Need the previous bar inside today's session
				return;
			// Entry windows: main window (session start .. last entry) and an optional second window.
			bool inMainWindow   = nyTod > sessionStart && nyTod <= lastEntry;
			bool inSecondWindow = UseSecondWindow
				&& nyTod >= new TimeSpan(SecondStartHour, SecondStartMinute, 0)
				&& nyTod <= new TimeSpan(SecondEndHour, SecondEndMinute, 0);
			if (!(inMainWindow || inSecondWindow) || nyTod >= exitTime)
				return;
			// Fresh cross after a closed trade: the "previous bar" of the signal must come after the exit bar.
			if (lastExitBar >= 0 && CurrentBar - 1 <= lastExitBar)
				return;

			double vwap1 = vwapSeries[1];
			double ma1   = ma[1];

			// MA condition: loose = MA beyond VWAP OR crossed recently; strict = MA beyond VWAP AND crossed within the lookback.
			bool maShortOk = RequireRecentMaCross
				? (ma0 < vwap0 && MaCrossedWithin(false))
				: (ma0 < vwap0 || MaCrossedWithin(false));
			bool maLongOk = RequireRecentMaCross
				? (ma0 > vwap0 && MaCrossedWithin(true))
				: (ma0 > vwap0 || MaCrossedWithin(true));

			// Previous bar condition: loose = beyond VWAP OR beyond MA; strict = beyond VWAP (a true VWAP cross).
			bool prevShortOk = RequirePrevCloseAcrossVwap
				? Close[1] > vwap1
				: (Close[1] > vwap1 || Close[1] > ma1);
			bool prevLongOk = RequirePrevCloseAcrossVwap
				? Close[1] < vwap1
				: (Close[1] < vwap1 || Close[1] < ma1);

			bool shortSignal = Close[0] < vwap0 && maShortOk && prevShortOk;
			bool longSignal  = Close[0] > vwap0 && maLongOk && prevLongOk;

			// Optional trend filter: trade only in the direction of a slower EMA.
			if (trendEma != null)
			{
				if (Close[0] <= trendEma[0]) longSignal  = false;
				if (Close[0] >= trendEma[0]) shortSignal = false;
			}

			if (TradeDirection == VwapMa9Direction.Long)
				shortSignal = false;
			if (TradeDirection == VwapMa9Direction.Short)
				longSignal = false;

			if (shortSignal)
				TryEnter(false, nyTime);
			else if (longSignal)
				TryEnter(true, nyTime);
		}

		#region Entry logic

		private void TryEnter(bool isLong, DateTime nyTime)
		{
			double tick     = TickSize > 0 ? TickSize : 0.25;
			double refEntry = Close[0];   // Reference price; the actual fill is the next bar open
			double stop;

			if (isLong)
				stop = Instrument.MasterInstrument.RoundToTickSize(swingLow[0] - StopBufferTicks * tick);
			else
				stop = Instrument.MasterInstrument.RoundToTickSize(swingHigh[0] + StopBufferTicks * tick);

			double stopPoints = isLong ? refEntry - stop : stop - refEntry;

			if (double.IsNaN(stopPoints) || double.IsInfinity(stopPoints) || stopPoints <= 0)
			{
				Print(string.Format("VwapMa9 | {0:yyyy-MM-dd HH:mm} NY | {1} skipped: invalid stop distance.", nyTime, isLong ? "LONG" : "SHORT"));
				return;
			}

			// Optional stop cap: skip the trade instead of shrinking the stop.
			if (MaxStopPoints > 0 && stopPoints > MaxStopPoints)
			{
				Print(string.Format("VwapMa9 | {0:yyyy-MM-dd HH:mm} NY | {1} skipped: stop {2:F2} pts > max {3:F2} pts.",
					nyTime, isLong ? "LONG" : "SHORT", stopPoints, MaxStopPoints));
				return;
			}

			double target = isLong
				? Instrument.MasterInstrument.RoundToTickSize(refEntry + RiskReward * stopPoints)
				: Instrument.MasterInstrument.RoundToTickSize(refEntry - RiskReward * stopPoints);

			int qty = CalculateContracts(stopPoints);
			if (qty <= 0)
			{
				Print(string.Format("VwapMa9 | {0:yyyy-MM-dd HH:mm} NY | {1} skipped: 1 contract risk exceeds ${2:F2} (stop {3:F2} pts).",
					nyTime, isLong ? "LONG" : "SHORT", RiskPerTrade, stopPoints));
				return;
			}

			string signal = isLong ? LongSignal : ShortSignal;

			// Stop and target must be set before the entry order is submitted.
			SetStopLoss(signal, CalculationMode.Price, stop, false);
			SetProfitTarget(signal, CalculationMode.Price, target);

			pendStop     = stop;
			pendTarget   = target;
			pendQty      = qty;
			pendingEntry = true;

			if (isLong)
				EnterLong(qty, signal);
			else
				EnterShort(qty, signal);

			if (ShowVisuals)
			{
				if (isLong)
					Draw.ArrowUp(this, "VM9_Arrow_" + CurrentBar, false, 0, Low[0] - 2 * tick, Brushes.LimeGreen);
				else
					Draw.ArrowDown(this, "VM9_Arrow_" + CurrentBar, false, 0, High[0] + 2 * tick, Brushes.Red);

				lineId++;
				lineActive   = true;
				lineStartBar = CurrentBar;
				lineStop     = stop;
				lineTarget   = target;
				Draw.Line(this, "VM9_Stop_" + lineId, false, 0, lineStop, 0, lineStop, Brushes.Red, DashStyleHelper.Dash, 2);
				Draw.Line(this, "VM9_Target_" + lineId, false, 0, lineTarget, 0, lineTarget, Brushes.LimeGreen, DashStyleHelper.Dash, 2);
			}
		}

		// Returns the number of contracts, or 0 when the trade must be skipped.
		private int CalculateContracts(double stopPoints)
		{
			if (!UseRiskSizing)
				return FixedContracts >= 1 ? FixedContracts : 1;

			double pointValue = Instrument.MasterInstrument.PointValue;
			if (double.IsNaN(pointValue) || double.IsInfinity(pointValue) || pointValue <= 0)
				pointValue = MnqPointValue;

			double riskPerContract = stopPoints * pointValue;
			if (double.IsNaN(riskPerContract) || double.IsInfinity(riskPerContract) || riskPerContract <= 0)
				return 1;   // Invalid calculation: default to 1 contract

			// Even a single contract risks more than allowed: skip.
			if (riskPerContract > RiskPerTrade)
				return 0;

			double raw = Math.Floor(RiskPerTrade / riskPerContract);
			if (double.IsNaN(raw) || double.IsInfinity(raw))
				return 1;

			int qty = (int)Math.Min(raw, int.MaxValue);
			int cap = MaxContracts >= 1 ? MaxContracts : 1;
			if (qty < 1)   qty = 1;
			if (qty > cap) qty = cap;
			return qty;
		}

		// True if the MA crossed VWAP in the given direction on this bar or within the last N bars
		// (only bars inside today's VWAP session are considered).
		private bool MaCrossedWithin(bool up)
		{
			for (int i = 0; i <= CrossLookbackBars; i++)
			{
				if (i + 1 > sessionBars - 1 || i + 1 > CurrentBar)
					break;

				double maNow   = ma[i];
				double maPrev  = ma[i + 1];
				double vwNow   = vwapSeries[i];
				double vwPrev  = vwapSeries[i + 1];

				if (up && maNow > vwNow && maPrev <= vwPrev)
					return true;
				if (!up && maNow < vwNow && maPrev >= vwPrev)
					return true;
			}
			return false;
		}

		private void FlattenPosition(string signalName)
		{
			if (Position.MarketPosition == MarketPosition.Long)
				ExitLong(signalName, LongSignal);
			else if (Position.MarketPosition == MarketPosition.Short)
				ExitShort(signalName, ShortSignal);
		}

		#endregion

		#region Order and position events

		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
			double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
		{
			if (order == null)
				return;

			bool isEntry = order.Name == LongSignal || order.Name == ShortSignal;
			if (!isEntry)
				return;

			if (orderState == OrderState.Filled)
			{
				pendingEntry      = false;
				tradesToday++;
				entryFillPrice    = averageFillPrice;
				initialRiskPoints = Math.Abs(averageFillPrice - pendStop);
				breakevenMoved    = false;

				Print(string.Format("VwapMa9 | {0:yyyy-MM-dd HH:mm} NY | {1} | entry {2} | stop {3} | target {4} | contracts {5}",
					ToNewYork(time),
					order.Name == LongSignal ? "LONG" : "SHORT",
					averageFillPrice.ToString("F2"),
					pendStop.ToString("F2"),
					pendTarget.ToString("F2"),
					filled > 0 ? filled : pendQty));
			}
			else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
			{
				pendingEntry = false;
			}
		}

		protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
		{
			// A flat position means the trade just closed; OnBarUpdate records the exit bar.
			if (marketPosition == MarketPosition.Flat)
				exitPending = true;
		}

		#endregion

		#region Helpers

		private void ResetDailyCounters(DateTime tradingDay)
		{
			currentTradingDay = tradingDay;
			tradesToday       = 0;
			dailyLossHit      = false;
			dayStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
		}

		private static TimeZoneInfo FindNewYorkTimeZone()
		{
			try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
			catch { }
			try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
			catch { }
			return null;
		}

		// Converts a bar/order timestamp from the chart (general options) time zone to New York time.
		private DateTime ToNewYork(DateTime t)
		{
			if (nyTz == null || chartTz == null)
				return t;
			try
			{
				return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), chartTz, nyTz);
			}
			catch
			{
				return t;
			}
		}

		#endregion

		#region Properties

		// ----- 1. Position Sizing -----

		[NinjaScriptProperty]
		[Display(Name = "Use $ Risk Sizing", Order = 1, GroupName = "1. Position Sizing")]
		public bool UseRiskSizing { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "Risk Per Trade ($)", Order = 2, GroupName = "1. Position Sizing")]
		public double RiskPerTrade { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Contracts if Risk Sizing off", Order = 3, GroupName = "1. Position Sizing")]
		public int FixedContracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Max Contracts", Order = 4, GroupName = "1. Position Sizing")]
		public int MaxContracts { get; set; }

		// ----- 2. Setup -----

		[NinjaScriptProperty]
		[Display(Name = "Direction", Order = 1, GroupName = "2. Setup")]
		public VwapMa9Direction TradeDirection { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "MA Type", Order = 2, GroupName = "2. Setup")]
		public VwapMa9MAType MovingAvgType { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "MA Length", Order = 3, GroupName = "2. Setup")]
		public int MALength { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Cross Lookback Bars", Order = 4, GroupName = "2. Setup")]
		public int CrossLookbackBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Max Trades Per Day", Order = 5, GroupName = "2. Setup")]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Require Prev Close Across VWAP", Description = "On: the previous bar must have closed on the other side of VWAP (true VWAP cross). Off: other side of VWAP or of the MA.", Order = 6, GroupName = "2. Setup")]
		public bool RequirePrevCloseAcrossVwap { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Require Recent MA Cross", Description = "On: the MA must be beyond VWAP AND have crossed it within Cross Lookback Bars. Off: beyond VWAP OR crossed recently.", Order = 7, GroupName = "2. Setup")]
		public bool RequireRecentMaCross { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Trend EMA Length", Description = "0 = off. Longs only above this EMA, shorts only below it.", Order = 8, GroupName = "2. Setup")]
		public int TrendEmaLength { get; set; }

		// ----- 3. Stop and Target -----

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Swing Lookback Bars", Order = 1, GroupName = "3. Stop and Target")]
		public int SwingLookbackBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Stop Buffer Ticks", Order = 2, GroupName = "3. Stop and Target")]
		public int StopBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Max Stop Points", Description = "0 = off. If the stop is wider than this, the trade is skipped.", Order = 3, GroupName = "3. Stop and Target")]
		public double MaxStopPoints { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Risk:Reward", Order = 4, GroupName = "3. Stop and Target")]
		public double RiskReward { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Breakeven At R", Description = "0 = off. Move the stop to entry +1 tick once price reaches this multiple of the initial risk.", Order = 5, GroupName = "3. Stop and Target")]
		public double BreakevenAtR { get; set; }

		// ----- 4. Session (NY time) -----

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Session Start Hour", Order = 1, GroupName = "4. Session (NY time)")]
		public int SessionStartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Session Start Minute", Order = 2, GroupName = "4. Session (NY time)")]
		public int SessionStartMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Last Entry Hour", Order = 3, GroupName = "4. Session (NY time)")]
		public int LastEntryHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Last Entry Minute", Order = 4, GroupName = "4. Session (NY time)")]
		public int LastEntryMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Exit Hour", Order = 5, GroupName = "4. Session (NY time)")]
		public int ExitHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Exit Minute", Order = 6, GroupName = "4. Session (NY time)")]
		public int ExitMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Second Entry Window", Order = 7, GroupName = "4. Session (NY time)")]
		public bool UseSecondWindow { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Second Window Start Hour", Order = 8, GroupName = "4. Session (NY time)")]
		public int SecondStartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Second Window Start Minute", Order = 9, GroupName = "4. Session (NY time)")]
		public int SecondStartMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Second Window End Hour", Order = 10, GroupName = "4. Session (NY time)")]
		public int SecondEndHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Second Window End Minute", Order = 11, GroupName = "4. Session (NY time)")]
		public int SecondEndMinute { get; set; }

		// ----- 5. Risk Limits -----

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Max Daily Loss $", Description = "0 = off.", Order = 1, GroupName = "5. Risk Limits")]
		public double MaxDailyLoss { get; set; }

		// ----- 6. Visuals -----

		[NinjaScriptProperty]
		[Display(Name = "Show Lines and Arrows", Order = 1, GroupName = "6. Visuals")]
		public bool ShowVisuals { get; set; }

		// ----- Plot accessors -----

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> VwapPlot
		{
			get { return Values[0]; }
		}

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> MaPlot
		{
			get { return Values[1]; }
		}

		#endregion
	}
}
