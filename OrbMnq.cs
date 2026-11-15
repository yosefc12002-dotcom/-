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

// OrbMnq - Opening Range Breakout strategy for MNQ / NQ futures.
//
// Rules:
//   1. Opening range = high / low of the first N minutes after the NY session start (default 9:30-9:45).
//   2. LONG : first bar that CLOSES above the range high (previous bar closed at or below it).
//      SHORT: first bar that CLOSES below the range low (previous bar closed at or above it).
//   3. Entry at market on the open of the next bar (Calculate = OnBarClose).
//   4. Fixed Stop Loss and Take Profit in points (user inputs).
//   5. Max trades per day (default 1). Flatten at the exit time.
//
// All session times are New York time and are converted from the chart time zone.

namespace NinjaTrader.NinjaScript.Strategies
{
	// Declared outside the class so it serializes cleanly in workspaces/templates.
	public enum OrbMnqDirection
	{
		Both,
		Long,
		Short
	}

	public class OrbMnq : Strategy
	{
		#region Constants

		private const string LongSignal     = "ORB_Long";
		private const string ShortSignal    = "ORB_Short";
		private const string TimeExitSignal = "ORB_TimeExit";

		#endregion

		#region Private fields

		// Time zones
		private TimeZoneInfo chartTz;
		private TimeZoneInfo nyTz;

		// Daily state
		private DateTime tradeDay = DateTime.MinValue;   // NY date of the current day
		private int tradesToday;
		private double rangeHigh;
		private double rangeLow;
		private int rangeBars;
		private bool rangeDone;
		private int rangeStartBar;

		// Order state
		private bool pendingEntry;

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                                 = "Opening Range Breakout for MNQ with fixed Stop Loss / Take Profit in points and a daily trade limit.";
				Name                                        = "OrbMnq";
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
				BarsRequiredToTrade                         = 2;
				IsInstantiatedOnEachOptimizationIteration   = true;

				// 1. Risk Management
				Contracts           = 1;
				StopLossPoints      = 25;
				TakeProfitPoints    = 50;
				MaxTradesPerDay     = 1;

				// 2. Opening Range
				TradeDirection      = OrbMnqDirection.Both;
				RangeMinutes        = 15;
				MinRangePoints      = 0;
				MaxRangePoints      = 0;

				// 3. Session (NY time)
				SessionStartHour    = 9;
				SessionStartMinute  = 30;
				LastEntryHour       = 11;
				LastEntryMinute     = 0;
				ExitHour            = 15;
				ExitMinute          = 50;

				// 4. Visuals
				ShowVisuals         = true;
			}
			else if (State == State.DataLoaded)
			{
				// Bar timestamps are expressed in the time zone selected in NinjaTrader general options.
				chartTz = Core.Globals.GeneralOptions.TimeZoneInfo;
				nyTz    = FindNewYorkTimeZone();
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0 || CurrentBar < 1)
				return;

			DateTime nyTime = ToNewYork(Time[0]);
			DateTime nyDate = nyTime.Date;
			TimeSpan nyTod  = nyTime.TimeOfDay;

			TimeSpan sessionStart = new TimeSpan(SessionStartHour, SessionStartMinute, 0);
			TimeSpan rangeEnd     = sessionStart.Add(TimeSpan.FromMinutes(RangeMinutes));
			TimeSpan lastEntry    = new TimeSpan(LastEntryHour, LastEntryMinute, 0);
			TimeSpan exitTime     = new TimeSpan(ExitHour, ExitMinute, 0);

			// ---------- New day: reset daily counters and the opening range ----------
			if (nyDate != tradeDay)
			{
				tradeDay    = nyDate;
				tradesToday = 0;
				rangeHigh   = double.MinValue;
				rangeLow    = double.MaxValue;
				rangeBars   = 0;
				rangeDone   = false;
			}

			// ---------- Build the opening range ----------
			// A bar timestamp is its close time, so range bars close after the start and at or before the range end.
			if (nyTod > sessionStart && nyTod <= rangeEnd)
			{
				if (rangeBars == 0)
					rangeStartBar = CurrentBar;
				rangeHigh = Math.Max(rangeHigh, High[0]);
				rangeLow  = Math.Min(rangeLow, Low[0]);
				rangeBars++;
			}

			if (!rangeDone && rangeBars > 0 && nyTod >= rangeEnd)
				rangeDone = true;

			// ---------- Draw the range ----------
			if (ShowVisuals && rangeDone && nyTod <= exitTime)
			{
				int startAgo = Math.Max(0, CurrentBar - rangeStartBar);
				string day = tradeDay.ToString("yyyyMMdd");
				Draw.Line(this, "ORB_High_" + day, false, startAgo, rangeHigh, 0, rangeHigh, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
				Draw.Line(this, "ORB_Low_" + day, false, startAgo, rangeLow, 0, rangeLow, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
			}

			// ---------- Time exit ----------
			if (Position.MarketPosition != MarketPosition.Flat && nyTod >= exitTime)
			{
				if (Position.MarketPosition == MarketPosition.Long)
					ExitLong(TimeExitSignal, LongSignal);
				else
					ExitShort(TimeExitSignal, ShortSignal);
				return;
			}

			// ---------- Entry filters ----------
			if (Position.MarketPosition != MarketPosition.Flat || pendingEntry)
				return;
			if (!rangeDone || nyTod <= rangeEnd)              // Entries only after the range is complete
				return;
			if (nyTod > lastEntry || nyTod >= exitTime)
				return;
			if (tradesToday >= MaxTradesPerDay)               // Daily trade limit
				return;

			double rangeSize = rangeHigh - rangeLow;
			if (rangeSize <= 0)
				return;
			if (MinRangePoints > 0 && rangeSize < MinRangePoints)
				return;
			if (MaxRangePoints > 0 && rangeSize > MaxRangePoints)
				return;

			// First close outside the range (the previous bar closed inside or on the boundary).
			bool longSignal  = Close[0] > rangeHigh && Close[1] <= rangeHigh;
			bool shortSignal = Close[0] < rangeLow  && Close[1] >= rangeLow;

			if (TradeDirection == OrbMnqDirection.Long)
				shortSignal = false;
			if (TradeDirection == OrbMnqDirection.Short)
				longSignal = false;

			if (longSignal)
				SubmitEntry(true);
			else if (shortSignal)
				SubmitEntry(false);
		}

		#region Entry logic

		private void SubmitEntry(bool isLong)
		{
			double tick   = TickSize > 0 ? TickSize : 0.25;
			int stopTicks = Math.Max(1, (int)Math.Round(StopLossPoints / tick));
			int tgtTicks  = Math.Max(1, (int)Math.Round(TakeProfitPoints / tick));
			int qty       = Contracts >= 1 ? Contracts : 1;
			string signal = isLong ? LongSignal : ShortSignal;

			// Stop and target are set in ticks from the actual fill price, before the entry is submitted.
			SetStopLoss(signal, CalculationMode.Ticks, stopTicks, false);
			SetProfitTarget(signal, CalculationMode.Ticks, tgtTicks);

			pendingEntry    = true;

			if (isLong)
				EnterLong(qty, signal);
			else
				EnterShort(qty, signal);

			if (ShowVisuals)
			{
				if (isLong)
					Draw.ArrowUp(this, "ORB_Arrow_" + CurrentBar, false, 0, Low[0] - 2 * tick, Brushes.LimeGreen);
				else
					Draw.ArrowDown(this, "ORB_Arrow_" + CurrentBar, false, 0, High[0] + 2 * tick, Brushes.Red);
			}
		}

		#endregion

		#region Order events

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
				pendingEntry = false;
				tradesToday++;

				bool isLong = order.Name == LongSignal;
				double stop = isLong ? averageFillPrice - StopLossPoints : averageFillPrice + StopLossPoints;
				double tgt  = isLong ? averageFillPrice + TakeProfitPoints : averageFillPrice - TakeProfitPoints;

				Print(string.Format("OrbMnq | {0:yyyy-MM-dd HH:mm} NY | {1} | entry {2:F2} | stop {3:F2} | target {4:F2} | contracts {5} | range {6:F2}-{7:F2}",
					ToNewYork(time), isLong ? "LONG" : "SHORT", averageFillPrice, stop, tgt, filled, rangeLow, rangeHigh));
			}
			else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
			{
				pendingEntry = false;
			}
		}

		#endregion

		#region Helpers

		private static TimeZoneInfo FindNewYorkTimeZone()
		{
			try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
			catch { }
			try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
			catch { }
			return null;
		}

		// Converts a timestamp from the chart (general options) time zone to New York time.
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

		// ----- 1. Risk Management -----

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Contracts", Order = 1, GroupName = "1. Risk Management")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, double.MaxValue)]
		[Display(Name = "Stop Loss (points)", Order = 2, GroupName = "1. Risk Management")]
		public double StopLossPoints { get; set; }

		[NinjaScriptProperty]
		[Range(0.25, double.MaxValue)]
		[Display(Name = "Take Profit (points)", Order = 3, GroupName = "1. Risk Management")]
		public double TakeProfitPoints { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Max Trades Per Day", Order = 4, GroupName = "1. Risk Management")]
		public int MaxTradesPerDay { get; set; }

		// ----- 2. Opening Range -----

		[NinjaScriptProperty]
		[Display(Name = "Direction", Order = 1, GroupName = "2. Opening Range")]
		public OrbMnqDirection TradeDirection { get; set; }

		[NinjaScriptProperty]
		[Range(1, 240)]
		[Display(Name = "Range Minutes", Description = "Length of the opening range after the session start.", Order = 2, GroupName = "2. Opening Range")]
		public int RangeMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Min Range (points)", Description = "0 = off. Skip the day if the range is smaller.", Order = 3, GroupName = "2. Opening Range")]
		public double MinRangePoints { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Max Range (points)", Description = "0 = off. Skip the day if the range is larger.", Order = 4, GroupName = "2. Opening Range")]
		public double MaxRangePoints { get; set; }

		// ----- 3. Session (NY time) -----

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Session Start Hour", Order = 1, GroupName = "3. Session (NY time)")]
		public int SessionStartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Session Start Minute", Order = 2, GroupName = "3. Session (NY time)")]
		public int SessionStartMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Last Entry Hour", Order = 3, GroupName = "3. Session (NY time)")]
		public int LastEntryHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Last Entry Minute", Order = 4, GroupName = "3. Session (NY time)")]
		public int LastEntryMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Exit Hour", Order = 5, GroupName = "3. Session (NY time)")]
		public int ExitHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Exit Minute", Order = 6, GroupName = "3. Session (NY time)")]
		public int ExitMinute { get; set; }

		// ----- 4. Visuals -----

		[NinjaScriptProperty]
		[Display(Name = "Draw Range and Arrows", Order = 1, GroupName = "4. Visuals")]
		public bool ShowVisuals { get; set; }

		#endregion
	}
}
