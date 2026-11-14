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

// ORBreakout15 - Opening Range Breakout for MNQ / NQ on a 15-minute chart.
//
// Logic summary:
//   * Opening range (OR) = high/low of all bars whose CLOSE time (New York) is
//     > OrStartTime and <= OrEndTime (default 09:30-09:45 = the first 15-min bar).
//   * Long  : a bar closes above OR high + buffer (and passes filters) -> market buy on the next bar.
//   * Short : a bar closes below OR low  - buffer (and passes filters) -> market sell on the next bar.
//   * Stop  : opposite side of the OR (or OR midpoint) +/- buffer, clamped to min/max points.
//   * Target: R multiple of the initial risk.
//   * Optional breakeven and R-based trailing stop.
//   * Filters: session VWAP (anchored at OrStartTime), EMA, OR size in points.
//   * Max trades per day, daily $ loss limit, entry window and forced exit (all New York time).
//
// NinjaTrader time stamps a bar with its CLOSE time. A signal evaluated on a bar
// that closes at 10:00 is filled at the open of the next bar, i.e. at ~10:00.
// Therefore the entry window is compared against the signal bar's close time.

namespace NinjaTrader.NinjaScript.Strategies
{
	public class ORBreakout15 : Strategy
	{
		private const string LongSignal  = "ORB_L";
		private const string ShortSignal = "ORB_S";

		// Drawing offset (display only, in ticks) for arrows/text.
		private const int DrawOffsetTicks = 4;

		// Time zones
		private TimeZoneInfo platformTz;
		private TimeZoneInfo nyTz;

		// Indicators
		private EMA ema;

		// Daily state
		private DateTime currentDay = DateTime.MinValue;
		private string   dayKey     = string.Empty;
		private double   orHigh;
		private double   orLow;
		private bool     orHasData;
		private bool     orComplete;
		private int      orStartBar;
		private int      tradesToday;
		private bool     lossLimitHit;
		private double   dayStartCumProfit;

		// Session VWAP (anchored at OrStartTime)
		private double   vwapPV;
		private double   vwapVol;
		private bool     vwapValid;
		private double   vwapValue;

		// Position state
		private bool     posInitialized;
		private int      entryBar;
		private double   entryPx;
		private double   riskPts;
		private int      lastRiskTicks;
		private int      lastTargetTicks;
		private double   currentStopPx;
		private double   targetPx;
		private double   extremePx;
		private bool     beDone;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                                 = "Opening Range Breakout for MNQ/NQ (15-min). New York time based, VWAP/EMA/range filters, R-based target, breakeven, trailing, daily loss limit.";
				Name                                        = "ORBreakout15";
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

				AddPlot(new Stroke(Brushes.Orange, 2), PlotStyle.Line, "VWAP");

				// 01. Time (New York)
				OrStartTime        = 930;
				OrEndTime          = 945;
				EntryStartTime     = 945;
				EntryEndTime       = 1200;
				ForceExitTime      = 1545;

				// 02. Entry
				BreakoutBufferTicks = 2;
				AllowLong           = true;
				AllowShort          = true;

				// 03. Filters
				UseVwapFilter      = true;
				UseEmaFilter       = false;
				EmaPeriod          = 50;
				MinRangePoints     = 0;
				MaxRangePoints     = 0;

				// 04. Stop / Target
				UseMidpointStop    = false;
				StopBufferTicks    = 2;
				MinStopPoints      = 10;
				MaxStopPoints      = 60;
				TargetR            = 2.0;

				// 05. Trade management
				BreakevenTriggerR  = 1.0;
				BreakevenOffsetTicks = 2;
				TrailTriggerR      = 0;
				TrailDistanceR     = 1.0;

				// 06. Risk limits
				Contracts          = 1;
				MaxTradesPerDay    = 1;
				MaxDailyLoss       = 0;

				// 07. Display
				ShowDrawings       = true;
			}
			else if (State == State.Configure)
			{
				// Nothing to configure (no validation that could stop the strategy here).
			}
			else if (State == State.DataLoaded)
			{
				platformTz = Core.Globals.GeneralOptions.TimeZoneInfo ?? TimeZoneInfo.Local;
				nyTz       = FindNewYorkTimeZone();

				ema = EMA(EmaPeriod);
				if (ShowDrawings && UseEmaFilter)
					AddChartIndicator(ema);
			}
		}

		#region Time helpers

		private TimeZoneInfo FindNewYorkTimeZone()
		{
			string[] ids = { "Eastern Standard Time", "America/New_York" };
			foreach (string id in ids)
			{
				try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
				catch (Exception) { }
			}
			Print(Name + ": New York time zone not found - falling back to platform time zone.");
			return platformTz;
		}

		// Converts a platform-time timestamp to New York time without ever throwing on
		// DST gaps/overlaps: GetUtcOffset never throws for invalid/ambiguous times,
		// and ConvertTimeFromUtc always receives a valid UTC value.
		private DateTime ToNewYork(DateTime platformTime)
		{
			DateTime unspecified = DateTime.SpecifyKind(platformTime, DateTimeKind.Unspecified);
			TimeSpan offset      = platformTz.GetUtcOffset(unspecified);
			DateTime utc         = DateTime.SpecifyKind(unspecified - offset, DateTimeKind.Utc);
			return TimeZoneInfo.ConvertTimeFromUtc(utc, nyTz);
		}

		private static int ToHhmm(DateTime t)
		{
			return t.Hour * 100 + t.Minute;
		}

		#endregion

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0 || CurrentBar < 1)
				return;

			DateTime ny   = ToNewYork(Time[0]);   // close time of this bar, New York
			int      hhmm = ToHhmm(ny);

			if (ny.Date != currentDay)
				StartNewDay(ny.Date);

			UpdateOpeningRange(hhmm);
			UpdateVwap(hhmm);
			DrawSessionLevels(hhmm);

			// ---- Flat housekeeping: reset stop/target to tick mode -------------------
			if (Position.MarketPosition == MarketPosition.Flat)
			{
				posInitialized = false;
				ResetStopsAndTargets();
			}

			// ---- Daily loss limit ------------------------------------------------------
			if (MaxDailyLoss > 0 && !lossLimitHit)
			{
				double realized   = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - dayStartCumProfit;
				double unrealized = Position.MarketPosition != MarketPosition.Flat
					? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]) : 0;

				if (realized + unrealized <= -MaxDailyLoss)
				{
					lossLimitHit = true;
					FlattenAll("DailyLoss");
					if (ShowDrawings)
						Draw.Text(this, "ORB_DL_" + dayKey, "Daily loss limit", 0, High[0] + DrawOffsetTicks * 3 * TickSize, Brushes.Red);
					return;
				}
			}

			// ---- Forced exit -------------------------------------------------------------
			if (hhmm >= ForceExitTime && Position.MarketPosition != MarketPosition.Flat)
			{
				FlattenAll("ForceExit");
				return;
			}

			// ---- Manage open position ----------------------------------------------------
			if (Position.MarketPosition != MarketPosition.Flat)
			{
				ManageOpenPosition();
				return;
			}

			// ---- Entry logic ---------------------------------------------------------------
			TryEntry(hhmm);
		}

		#region Daily state

		private void StartNewDay(DateTime nyDate)
		{
			currentDay        = nyDate;
			dayKey            = nyDate.ToString("yyyyMMdd");
			orHigh            = double.MinValue;
			orLow             = double.MaxValue;
			orHasData         = false;
			orComplete        = false;
			orStartBar        = -1;
			tradesToday       = 0;
			lossLimitHit      = false;
			dayStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;

			vwapPV    = 0;
			vwapVol   = 0;
			vwapValid = false;
		}

		private void UpdateOpeningRange(int hhmm)
		{
			if (orComplete)
				return;

			// Bar belongs to the OR when its close time is inside (OrStart, OrEnd].
			if (hhmm > OrStartTime && hhmm <= OrEndTime)
			{
				if (!orHasData)
					orStartBar = CurrentBar;
				orHigh    = Math.Max(orHigh, High[0]);
				orLow     = Math.Min(orLow, Low[0]);
				orHasData = true;
			}

			if (hhmm >= OrEndTime && orHasData)
				orComplete = true;
		}

		private void UpdateVwap(int hhmm)
		{
			if (hhmm <= OrStartTime)
				return;

			double vol     = Volume[0];
			double typical = (High[0] + Low[0] + Close[0]) / 3.0;
			vwapPV  += typical * vol;
			vwapVol += vol;

			if (vwapVol > 0)
			{
				vwapValue  = vwapPV / vwapVol;
				vwapValid  = true;
				Values[0][0] = vwapValue;
			}
		}

		private void DrawSessionLevels(int hhmm)
		{
			if (!ShowDrawings || !orHasData || orStartBar < 0 || hhmm > ForceExitTime)
				return;

			int startAgo = CurrentBar - orStartBar;
			Draw.Line(this, "ORB_H_" + dayKey, false, startAgo, orHigh, 0, orHigh, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
			Draw.Line(this, "ORB_Lo_" + dayKey, false, startAgo, orLow, 0, orLow, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
			if (UseMidpointStop)
			{
				double mid = (orHigh + orLow) / 2.0;
				Draw.Line(this, "ORB_M_" + dayKey, false, startAgo, mid, 0, mid, Brushes.Gray, DashStyleHelper.Dash, 1);
			}
		}

		#endregion

		#region Entries

		private void TryEntry(int hhmm)
		{
			if (!orComplete || lossLimitHit)
				return;
			if (tradesToday >= MaxTradesPerDay)
				return;
			if (hhmm <= OrEndTime || hhmm < EntryStartTime || hhmm > EntryEndTime || hhmm >= ForceExitTime)
				return;
			if (UseEmaFilter && CurrentBar < EmaPeriod)
				return;

			double range = orHigh - orLow;
			if (MinRangePoints > 0 && range < MinRangePoints)
				return;
			if (MaxRangePoints > 0 && range > MaxRangePoints)
				return;

			double buffer = BreakoutBufferTicks * TickSize;

			bool longSignal = AllowLong
				&& Close[0] > orHigh + buffer
				&& (!UseVwapFilter || (vwapValid && Close[0] > vwapValue))
				&& (!UseEmaFilter  || Close[0] > ema[0]);

			bool shortSignal = AllowShort
				&& Close[0] < orLow - buffer
				&& (!UseVwapFilter || (vwapValid && Close[0] < vwapValue))
				&& (!UseEmaFilter  || Close[0] < ema[0]);

			if (longSignal)
				SubmitEntry(true);
			else if (shortSignal)
				SubmitEntry(false);
		}

		private void SubmitEntry(bool isLong)
		{
			double stopBuffer = StopBufferTicks * TickSize;
			double baseLevel  = UseMidpointStop ? (orHigh + orLow) / 2.0 : (isLong ? orLow : orHigh);
			double stopPx     = isLong ? baseLevel - stopBuffer : baseLevel + stopBuffer;

			// Estimated entry = signal bar close (actual fill is next bar open; tick-mode
			// stop/target are applied relative to the real fill price).
			double estRisk = isLong ? Close[0] - stopPx : stopPx - Close[0];
			if (MinStopPoints > 0 && estRisk < MinStopPoints) estRisk = MinStopPoints;
			if (MaxStopPoints > 0 && estRisk > MaxStopPoints) estRisk = MaxStopPoints;
			if (estRisk <= 0)
				return;

			int riskTicks = Math.Max(1, (int)Math.Round(estRisk / TickSize));
			lastRiskTicks = riskTicks;

			string signal = isLong ? LongSignal : ShortSignal;

			// Stop and target set BEFORE the entry order.
			SetStopLoss(signal, CalculationMode.Ticks, riskTicks, false);
			if (TargetR > 0)
			{
				lastTargetTicks = Math.Max(1, (int)Math.Round(riskTicks * TargetR));
				SetProfitTarget(signal, CalculationMode.Ticks, lastTargetTicks);
			}

			if (isLong)
				EnterLong(Contracts, signal);
			else
				EnterShort(Contracts, signal);

			tradesToday++;

			if (ShowDrawings)
			{
				if (isLong)
					Draw.ArrowUp(this, "ORB_EL_" + CurrentBar, false, 0, Low[0] - DrawOffsetTicks * TickSize, Brushes.Lime);
				else
					Draw.ArrowDown(this, "ORB_ES_" + CurrentBar, false, 0, High[0] + DrawOffsetTicks * TickSize, Brushes.Red);
			}
		}

		private void ResetStopsAndTargets()
		{
			// Return any price-mode (breakeven / trailing) stop back to tick mode so the
			// next entry never inherits a stale absolute price.
			if (lastRiskTicks > 0)
			{
				SetStopLoss(LongSignal,  CalculationMode.Ticks, lastRiskTicks, false);
				SetStopLoss(ShortSignal, CalculationMode.Ticks, lastRiskTicks, false);
			}
			if (TargetR > 0 && lastTargetTicks > 0)
			{
				SetProfitTarget(LongSignal,  CalculationMode.Ticks, lastTargetTicks);
				SetProfitTarget(ShortSignal, CalculationMode.Ticks, lastTargetTicks);
			}
		}

		#endregion

		#region Position management

		private void ManageOpenPosition()
		{
			bool isLong = Position.MarketPosition == MarketPosition.Long;

			if (!posInitialized)
			{
				posInitialized = true;
				entryBar       = CurrentBar;
				entryPx        = Position.AveragePrice;
				riskPts        = lastRiskTicks * TickSize;
				currentStopPx  = isLong ? entryPx - riskPts : entryPx + riskPts;
				targetPx       = TargetR > 0 ? (isLong ? entryPx + riskPts * TargetR : entryPx - riskPts * TargetR) : 0;
				extremePx      = entryPx;
				beDone         = false;
			}

			extremePx = isLong ? Math.Max(extremePx, High[0]) : Math.Min(extremePx, Low[0]);
			double favPts = isLong ? extremePx - entryPx : entryPx - extremePx;
			string signal = isLong ? LongSignal : ShortSignal;

			// Breakeven
			if (BreakevenTriggerR > 0 && !beDone && riskPts > 0 && favPts >= BreakevenTriggerR * riskPts)
			{
				double bePx = isLong ? entryPx + BreakevenOffsetTicks * TickSize : entryPx - BreakevenOffsetTicks * TickSize;
				bePx = Instrument.MasterInstrument.RoundToTickSize(bePx);
				if (TryMoveStop(isLong, signal, bePx))
					beDone = true;
			}

			// Trailing stop (distance in R units)
			if (TrailTriggerR > 0 && TrailDistanceR > 0 && riskPts > 0 && favPts >= TrailTriggerR * riskPts)
			{
				double trailPx = isLong ? extremePx - TrailDistanceR * riskPts : extremePx + TrailDistanceR * riskPts;
				trailPx = Instrument.MasterInstrument.RoundToTickSize(trailPx);
				TryMoveStop(isLong, signal, trailPx);
			}

			if (ShowDrawings)
			{
				Draw.Line(this, "ORB_Stp_" + CurrentBar, false, 1, currentStopPx, 0, currentStopPx, Brushes.Red, DashStyleHelper.Dot, 2);
				if (TargetR > 0)
					Draw.Line(this, "ORB_Tgt_" + CurrentBar, false, 1, targetPx, 0, targetPx, Brushes.LimeGreen, DashStyleHelper.Dot, 2);
			}
		}

		// Moves the stop only in the favorable direction and only to a price that is
		// on the correct side of the current market.
		private bool TryMoveStop(bool isLong, string signal, double newStop)
		{
			bool improves  = isLong ? newStop > currentStopPx : newStop < currentStopPx;
			bool validSide = isLong ? newStop < Close[0] - TickSize : newStop > Close[0] + TickSize;
			if (!improves || !validSide)
				return false;

			SetStopLoss(signal, CalculationMode.Price, newStop, false);
			currentStopPx = newStop;
			return true;
		}

		private void FlattenAll(string reason)
		{
			if (Position.MarketPosition == MarketPosition.Long)
				ExitLong(reason, LongSignal);
			else if (Position.MarketPosition == MarketPosition.Short)
				ExitShort(reason, ShortSignal);
		}

		#endregion

		#region Properties

		// ---------- 01. Time (New York) ----------
		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "OR start (HHMM)", Description = "Opening range start, New York time (exclusive, bar close time).", Order = 1, GroupName = "01. Time (New York)")]
		public int OrStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "OR end (HHMM)", Description = "Opening range end, New York time (inclusive, bar close time).", Order = 2, GroupName = "01. Time (New York)")]
		public int OrEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Entry start (HHMM)", Description = "First allowed signal bar close / entry time.", Order = 3, GroupName = "01. Time (New York)")]
		public int EntryStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Entry end (HHMM)", Description = "Last allowed signal bar close / entry time (inclusive).", Order = 4, GroupName = "01. Time (New York)")]
		public int EntryEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Force exit (HHMM)", Description = "Flatten any open position at this New York time.", Order = 5, GroupName = "01. Time (New York)")]
		public int ForceExitTime { get; set; }

		// ---------- 02. Entry ----------
		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Breakout buffer (ticks)", Description = "Close must be beyond the OR by this many ticks.", Order = 1, GroupName = "02. Entry")]
		public int BreakoutBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Allow long", Order = 2, GroupName = "02. Entry")]
		public bool AllowLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Allow short", Order = 3, GroupName = "02. Entry")]
		public bool AllowShort { get; set; }

		// ---------- 03. Filters ----------
		[NinjaScriptProperty]
		[Display(Name = "Use VWAP filter", Description = "Long only above session VWAP, short only below.", Order = 1, GroupName = "03. Filters")]
		public bool UseVwapFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use EMA filter", Description = "Long only above EMA, short only below.", Order = 2, GroupName = "03. Filters")]
		public bool UseEmaFilter { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EMA period", Order = 3, GroupName = "03. Filters")]
		public int EmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Min OR range (points)", Description = "0 = off.", Order = 4, GroupName = "03. Filters")]
		public double MinRangePoints { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Max OR range (points)", Description = "0 = off.", Order = 5, GroupName = "03. Filters")]
		public double MaxRangePoints { get; set; }

		// ---------- 04. Stop / Target ----------
		[NinjaScriptProperty]
		[Display(Name = "Stop at OR midpoint", Description = "true = stop at OR midpoint, false = opposite side of OR.", Order = 1, GroupName = "04. Stop / Target")]
		public bool UseMidpointStop { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Stop buffer (ticks)", Order = 2, GroupName = "04. Stop / Target")]
		public int StopBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Min stop (points)", Description = "0 = off.", Order = 3, GroupName = "04. Stop / Target")]
		public double MinStopPoints { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Max stop (points)", Description = "0 = off.", Order = 4, GroupName = "04. Stop / Target")]
		public double MaxStopPoints { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Target (R)", Description = "Profit target as a multiple of initial risk. 0 = no target.", Order = 5, GroupName = "04. Stop / Target")]
		public double TargetR { get; set; }

		// ---------- 05. Trade management ----------
		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Breakeven trigger (R)", Description = "0 = off.", Order = 1, GroupName = "05. Trade Management")]
		public double BreakevenTriggerR { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Breakeven offset (ticks)", Order = 2, GroupName = "05. Trade Management")]
		public int BreakevenOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Trail trigger (R)", Description = "0 = off.", Order = 3, GroupName = "05. Trade Management")]
		public double TrailTriggerR { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Trail distance (R)", Order = 4, GroupName = "05. Trade Management")]
		public double TrailDistanceR { get; set; }

		// ---------- 06. Risk limits ----------
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Contracts", Order = 1, GroupName = "06. Risk Limits")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Max trades per day", Order = 2, GroupName = "06. Risk Limits")]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Max daily loss ($)", Description = "0 = off.", Order = 3, GroupName = "06. Risk Limits")]
		public double MaxDailyLoss { get; set; }

		// ---------- 07. Display ----------
		[NinjaScriptProperty]
		[Display(Name = "Show drawings", Order = 1, GroupName = "07. Display")]
		public bool ShowDrawings { get; set; }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Vwap
		{
			get { return Values[0]; }
		}

		#endregion
	}
}
