#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
#endregion

// אסטרטגיית "הנר הראשון" ל-NinjaTrader 8
// 1. High / Low של נר 5 הדקות הראשון אחרי 09:30 (שעון ניו יורק)
// 2. סגירת נר מחוץ לטווח ואז נר ריטסט שנוגע ברמה וסוגר מחוץ לה
// 3. סטופ באמצע הטווח, יעד לפי יחס RewardRisk (ברירת מחדל 2:1)
namespace NinjaTrader.NinjaScript.Strategies
{
	public class FirstCandleORB : Strategy
	{
		private TimeZoneInfo nyTz;
		private DateTime currentDay = DateTime.MinValue;
		private double orHigh = double.NaN;
		private double orLow  = double.NaN;
		private int    state;      // 1 = נשבר למעלה, -1 = נשבר למטה, 0 = בתוך הטווח
		private bool   traded;
		private bool   warned;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name                          = "FirstCandleORB";
				Description                   = "First 5-minute candle break & retest (NY open), stop at range midpoint, fixed R:R target.";
				Calculate                     = Calculate.OnBarClose;
				EntriesPerDirection           = 1;
				EntryHandling                 = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy  = true;
				ExitOnSessionCloseSeconds     = 30;
				BarsRequiredToTrade           = 1;
				StartBehavior                 = StartBehavior.WaitUntilFlat;
				TimeInForce                   = TimeInForce.Gtc;
				IsInstantiatedOnEachOptimizationIteration = true;

				Quantity    = 1;
				RewardRisk  = 2.0;
				CutoffTime  = 1200;
				FlattenTime = 1555;
				AllowLong   = true;
				AllowShort  = true;

				AddPlot(Brushes.LimeGreen, "OR High");
				AddPlot(Brushes.Red,       "OR Low");
				AddPlot(Brushes.Gray,      "OR Mid (Stop)");
			}
			else if (State == State.DataLoaded)
			{
				// מזהה Windows — מטפל אוטומטית בשעון קיץ/חורף
				nyTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 1)
				return;

			if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 5)
			{
				if (!warned)
				{
					Log("FirstCandleORB: run on a 5-minute chart only.", LogLevel.Warning);
					warned = true;
				}
				return;
			}

			// ב-NinjaTrader זמן הנר הוא זמן הסגירה שלו
			DateTime ny   = TimeZoneInfo.ConvertTime(Time[0], Core.Globals.GeneralOptions.TimeZoneInfo, nyTz);
			int      hhmm = ny.Hour * 100 + ny.Minute;

			if (ny.Date != currentDay)
			{
				currentDay = ny.Date;
				orHigh = orLow = double.NaN;
				state  = 0;
				traded = false;
			}

			// הנר הראשון: 09:30–09:35
			if (hhmm == 935)
			{
				orHigh = High[0];
				orLow  = Low[0];
				state  = 0;
				traded = false;
			}

			if (double.IsNaN(orHigh))
				return;

			double orMid = (orHigh + orLow) / 2.0;

			if (hhmm >= 935 && hhmm <= 1600)
			{
				Values[0][0] = orHigh;
				Values[1][0] = orLow;
				Values[2][0] = orMid;
			}

			// סגירת פוזיציות בסוף היום
			if (hhmm >= FlattenTime && Position.MarketPosition != MarketPosition.Flat)
			{
				ExitLong();
				ExitShort();
				return;
			}

			bool inWindow = hhmm > 935 && hhmm <= CutoffTime;
			if (hhmm == 935 || traded || !inWindow || Position.MarketPosition != MarketPosition.Flat)
				return;

			if (state == 1)
			{
				// ריטסט: נוגע ב-High וסוגר מעליו
				if (Low[0] <= orHigh && Close[0] > orHigh)
				{
					if (AllowLong)
					{
						double risk = Close[0] - orMid;
						SetStopLoss("Long", CalculationMode.Price, orMid, false);
						SetProfitTarget("Long", CalculationMode.Price, Close[0] + RewardRisk * risk);
						EnterLong(Quantity, "Long");
						traded = true;
					}
					state = 0;
				}
				else if (Close[0] <= orHigh)
				{
					state = Close[0] < orLow ? -1 : 0;   // פייק-אאוט
				}
			}
			else if (state == -1)
			{
				// ריטסט: נוגע ב-Low וסוגר מתחתיו
				if (High[0] >= orLow && Close[0] < orLow)
				{
					if (AllowShort)
					{
						double risk = orMid - Close[0];
						SetStopLoss("Short", CalculationMode.Price, orMid, false);
						SetProfitTarget("Short", CalculationMode.Price, Close[0] - RewardRisk * risk);
						EnterShort(Quantity, "Short");
						traded = true;
					}
					state = 0;
				}
				else if (Close[0] >= orLow)
				{
					state = Close[0] > orHigh ? 1 : 0;
				}
			}
			else
			{
				// שלב א': סגירת נר מחוץ לטווח
				if (Close[0] > orHigh)
					state = 1;
				else if (Close[0] < orLow)
					state = -1;
			}
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Quantity (contracts)", Order = 1, GroupName = "Parameters")]
		public int Quantity { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 10)]
		[Display(Name = "Reward:Risk", Order = 2, GroupName = "Parameters")]
		public double RewardRisk { get; set; }

		[NinjaScriptProperty]
		[Range(940, 1600)]
		[Display(Name = "Last entry time (HHMM, New York)", Order = 3, GroupName = "Parameters")]
		public int CutoffTime { get; set; }

		[NinjaScriptProperty]
		[Range(940, 2359)]
		[Display(Name = "Flatten time (HHMM, New York)", Order = 4, GroupName = "Parameters")]
		public int FlattenTime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Allow long", Order = 5, GroupName = "Parameters")]
		public bool AllowLong { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Allow short", Order = 6, GroupName = "Parameters")]
		public bool AllowShort { get; set; }
		#endregion
	}
}
