// ORB Assistant - בוט עוזר לאסטרטגיית פריצת טווח הפתיחה (Opening Range Breakout)
// NinjaTrader 8 / NinjaScript
//
// מה הבוט עושה:
//  1. מסמן את הגבוה והנמוך של 15 הדקות הראשונות מפתיחת ניו יורק (9:30 NY), כולל פתילות.
//  2. על גרף 5 דקות: נר שנסגר מחוץ לטווח = סימן. בפתיחת הנר הבא מוצגת הצעת עסקה בחלון קופץ.
//  3. רק אחרי לחיצה על "אשר עסקה" נשלחת פקודת שוק עם סטופ ויעד (יחס 1:2 כברירת מחדל).
//  4. מזיז סטופ לנקודת הכניסה, סופר עסקאות ביום, שומר על מגבלת הפסד יומית, ומציג לוח מצב.
//  5. בבדיקה על נתוני עבר (Strategy Analyzer) הבוט נכנס אוטומטית, כדי שאפשר יהיה לבדוק את השיטה.
//  6. שומר על תיק מימון: עוקב לבד אחרי סף ה-Drawdown (גם כשהוא עולה עם הרווחים),
//     מקטין או חוסם עסקאות כשמתקרבים לסף, סוגר עסקה פתוחה לפני שנוגעים בו,
//     וסוגר הכל בשעה קבועה לפני סוף היום.
//
// התקנה: שמרו את הקובץ בתיקייה  Documents\NinjaTrader 8\bin\Custom\Strategies
//        ואז ב-NinjaTrader: New > NinjaScript Editor, ולחצו F5 כדי לקמפל.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.Core.FloatingPoint;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // FixedTicks    = סטופ קבוע במספר טיקים
    // SignalCandle  = סטופ מעבר לקצה של נר הפריצה
    // RangeOpposite = סטופ בצד השני של טווח הפתיחה
    public enum OrbStopMode { FixedTicks, SignalCandle, RangeOpposite }

    // TrailingIntraday  = הסף עולה עם כל שיא חדש ביתרה, גם באמצע היום (הכי מחמיר, מתאים לכל הפירמות)
    // TrailingEndOfDay  = הסף עולה רק לפי היתרה בסוף יום
    // Static            = הסף קבוע (יתרת פתיחה פחות ה-Drawdown)
    public enum OrbDrawdownType { TrailingIntraday, TrailingEndOfDay, Static }

    // מונה עסקאות משותף לכל הגרפים שעליהם רץ הבוט (לפי חשבון ויום)
    public static class OrbSharedCounter
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

        public static int Get(string key)
        {
            lock (Sync) { int c; return Counts.TryGetValue(key, out c) ? c : 0; }
        }

        public static void Increment(string key)
        {
            lock (Sync) { int c; Counts.TryGetValue(key, out c); Counts[key] = c + 1; }
        }
    }

    public class OrbAssistant : Strategy
    {
        private class Proposal
        {
            public bool IsLong;
            public double StopPrice;
            public DateTime Created;
        }

        private const string SigLong = "ORB_L";
        private const string SigShort = "ORB_S";

        private TimeZoneInfo nyTz;
        private TimeZoneInfo ilTz;
        private List<int> newsTimes = new List<int>();

        // מצב יומי
        private DateTime curDay = DateTime.MinValue;
        private double orHigh, orLow;
        private bool hasRange, orReady;
        private int orStartBar;
        private bool longSignaled, shortSignaled;
        private int histTradesToday;
        private double histDayPnl;
        private bool dayStopped;
        private readonly List<string> dayReport = new List<string>();

        // עסקה פתוחה
        private int riskTicks;
        private bool beDone;

        // הצעה ממתינה
        private Proposal pending;
        private System.Windows.Window proposalWindow;

        // מעקב Drawdown של תיק המימון
        private double histPeak;          // שיא יתרה בבדיקה על נתוני עבר
        private double rtPeak;            // שיא יתרה בזמן אמת (נשמר לקובץ)
        private bool rtPeakDirty;
        private double rtLastEquity;      // יתרה אחרונה שנראתה (ל-TrailingEndOfDay)
        private bool blown;               // בבדיקה על נתוני עבר: החשבון היה נשרף
        private bool flattening;

        private int lastProcessedBar = -1;
        private DateTime lastStatusUpdate = DateTime.MinValue;
        private string lastMessage = "";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "ORB Assistant - מציע עסקאות פריצת טווח פתיחה, הכניסה באישור ידני";
                Name = "OrbAssistant";
                Calculate = Calculate.OnPriceChange;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 0;
                IsInstantiatedOnEachOptimizationIteration = true;

                SessionOpenNy = 930;
                OpeningRangeMinutes = 15;
                LastSignalTimeIsrael = 2130;
                NewsTimesIsrael = "";
                NewsBufferMinutes = 15;

                StopMode = OrbStopMode.FixedTicks;
                StopTicks = 40;
                StopOffsetTicks = 2;
                RewardRisk = 2.0;
                BreakevenAtR = 1.0;

                RiskPerTrade = 200;
                MaxContracts = 5;
                MaxTradesPerDay = 2;
                CountTradesAcrossCharts = true;
                AllowOppositeDirection = true;
                DailyLossLimit = 400;

                AccountStartBalance = 50000;
                MaxDrawdown = 2000;
                DrawdownType = OrbDrawdownType.TrailingIntraday;
                LockAtStartBalance = true;
                ManualFloor = 0;
                SafetyBuffer = 100;
                FlatTimeNy = 1555;

                ProposalValidMinutes = 5;
                AutoTradeInBacktest = true;
                PlaySounds = true;
            }
            else if (State == State.DataLoaded)
            {
                nyTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                ilTz = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");
                newsTimes = ParseTimes(NewsTimesIsrael);

                histPeak = AccountStartBalance;
                blown = false;

                if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute)
                    Print("ORB: הבוט מיועד לגרף דקות (5 דקות). סוג הגרף הנוכחי: " + BarsPeriod.BarsPeriodType);
            }
            else if (State == State.Realtime)
            {
                LoadPeak();
            }
            else if (State == State.Terminated)
            {
                SavePeak();
                CloseProposalWindow();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1)
                return;

            if (State == State.Historical)
            {
                // בנתוני עבר OnBarUpdate נקרא פעם אחת בסגירת כל נר
                ProcessClosedBar(0);
            }
            else
            {
                if (IsFirstTickOfBar)
                    ProcessClosedBar(1);

                GuardAccountRealtime();
                CheckFlatTimeRealtime();
                CheckBreakevenRealtime();

                if ((DateTime.Now - lastStatusUpdate).TotalSeconds >= 1)
                    UpdateStatus();
            }
        }

        // ===== לוגיקה על נר סגור =====
        private void ProcessClosedBar(int b)
        {
            int barIndex = CurrentBar - b;
            if (barIndex <= lastProcessedBar)
                return;
            lastProcessedBar = barIndex;

            DateTime tNy = ToZone(Time[b], nyTz);
            DateTime tIl = ToZone(Time[b], ilTz);
            int minuteNy = tNy.Hour * 60 + tNy.Minute;
            int openMin = (SessionOpenNy / 100) * 60 + SessionOpenNy % 100;
            int rangeEnd = openMin + OpeningRangeMinutes;
            int flatMin = (FlatTimeNy / 100) * 60 + FlatTimeNy % 100;
            bool afterFlat = minuteNy >= flatMin && minuteNy < 18 * 60;

            // בדיקה על נתוני עבר: סגירה בשעה הקבועה
            if (State == State.Historical && afterFlat && Position.MarketPosition != MarketPosition.Flat)
                CloseAll();

            // רק נרות מזמן הפתיחה ואילך פותחים יום חדש
            if (minuteNy > openMin && tNy.Date != curDay)
                StartNewDay(tNy.Date);

            if (tNy.Date != curDay)
                return;

            // בניית טווח הפתיחה
            if (minuteNy > openMin && minuteNy <= rangeEnd)
            {
                if (!hasRange)
                {
                    orHigh = High[b];
                    orLow = Low[b];
                    orStartBar = barIndex;
                    hasRange = true;
                }
                else
                {
                    orHigh = Math.Max(orHigh, High[b]);
                    orLow = Math.Min(orLow, Low[b]);
                }

                if (minuteNy == rangeEnd)
                    orReady = true;

                DrawRange(b);
                return;
            }

            if (!hasRange)
                return;
            orReady = true;
            DrawRange(b);

            if (State == State.Historical)
            {
                CheckBreakevenHistorical(b);
                if (histDayPnl <= -DailyLossLimit)
                    dayStopped = true;
            }
            else if (GetDayPnl() <= -DailyLossLimit)
            {
                dayStopped = true;
            }

            if (Hhmm(tIl) > LastSignalTimeIsrael || afterFlat || dayStopped || blown)
                return;

            bool longSig = Close[b] > orHigh && !longSignaled;
            bool shortSig = Close[b] < orLow && !shortSignaled;
            if (!longSig && !shortSig)
                return;

            bool isLong = longSig;
            if (!AllowOppositeDirection && (longSignaled || shortSignaled))
                return;
            if (isLong) longSignaled = true; else shortSignaled = true;

            string tag = "orbSig" + barIndex;
            if (isLong)
                Draw.ArrowUp(this, tag, false, b, Low[b] - 4 * TickSize, Brushes.LimeGreen);
            else
                Draw.ArrowDown(this, tag, false, b, High[b] + 4 * TickSize, Brushes.Red);

            if (IsNewsTime(tIl))
            {
                Note(b, "חדשות - אין כניסה");
                return;
            }

            if (TradesToday() >= MaxTradesPerDay)
            {
                Note(b, "הגענו ל-" + MaxTradesPerDay + " עסקאות היום");
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            double stopPrice = CalcStopPrice(isLong, b);

            if (State == State.Historical)
            {
                if (AutoTradeInBacktest && TryEnter(isLong, stopPrice, Close[b]))
                    histTradesToday++;
                return;
            }

            // זמן אמת: הצעת עסקה לאישור
            int ticks, qty;
            string reason;
            if (!CalcSize(isLong, stopPrice, Close[b], out ticks, out qty, out reason))
            {
                lastMessage = "יש סימן אבל אין הצעה: " + reason;
                Note(b, reason);
                Alert("orbNo" + barIndex, Priority.Medium, "ORB " + Instrument.FullName + ": " + lastMessage,
                    PlaySounds ? NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert4.wav" : "", 10, Brushes.Gray, Brushes.White);
                return;
            }

            pending = new Proposal { IsLong = isLong, StopPrice = stopPrice, Created = DateTime.Now };

            double tickVal = TickSize * Instrument.MasterInstrument.PointValue;
            double refPrice = Close[b];
            double stopPx = isLong ? refPrice - ticks * TickSize : refPrice + ticks * TickSize;
            double tgtPx = isLong ? refPrice + ticks * RewardRisk * TickSize : refPrice - ticks * RewardRisk * TickSize;

            Draw.Line(this, "orbStop" + barIndex, false, b, stopPx, 0, stopPx, Brushes.Red, DashStyleHelper.Dash, 2);
            Draw.Line(this, "orbTgt" + barIndex, false, b, tgtPx, 0, tgtPx, Brushes.LimeGreen, DashStyleHelper.Dash, 2);

            string dir = isLong ? "קנייה (Long)" : "מכירה בחסר (Short)";
            string text =
                dir + " ב-" + Instrument.FullName + "\n" +
                "נר נסגר " + (isLong ? "מעל" : "מתחת") + " לטווח הפתיחה (" + Fmt(isLong ? orHigh : orLow) + ")\n\n" +
                "חוזים: " + qty + "\n" +
                "סטופ: " + ticks + " טיקים (בערך " + Fmt(stopPx) + ")\n" +
                "יעד: " + (int)Math.Round(ticks * RewardRisk) + " טיקים (בערך " + Fmt(tgtPx) + ")\n" +
                "סיכון: $" + (qty * ticks * tickVal).ToString("F0") + "\n\n" +
                "ההצעה בתוקף " + ProposalValidMinutes + " דקות. הכניסה בפקודת שוק במחיר הנוכחי.";

            lastMessage = "הצעה ממתינה: " + dir;
            ShowProposalWindow(text);
            Alert("orbSig" + barIndex, Priority.High, "ORB " + Instrument.FullName + ": הצעת " + dir,
                PlaySounds ? NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav" : "", 10, Brushes.Orange, Brushes.Black);
            UpdateStatus();
        }

        private void StartNewDay(DateTime day)
        {
            if (curDay != DateTime.MinValue)
                PrintDayReport();

            // סוף יום: ב-TrailingEndOfDay הסף עולה לפי היתרה של סוף היום הקודם
            if (State == State.Realtime && DrawdownType == OrbDrawdownType.TrailingEndOfDay && rtLastEquity > rtPeak)
            {
                rtPeak = rtLastEquity;
                rtPeakDirty = true;
            }
            flattening = false;

            curDay = day;
            hasRange = false;
            orReady = false;
            orHigh = 0;
            orLow = 0;
            longSignaled = false;
            shortSignaled = false;
            histTradesToday = 0;
            histDayPnl = 0;
            dayStopped = false;
            dayReport.Clear();
            pending = null;
        }

        // ===== חישובי סטופ וכמות =====
        private double CalcStopPrice(bool isLong, int b)
        {
            switch (StopMode)
            {
                case OrbStopMode.SignalCandle:
                    return isLong ? Low[b] - StopOffsetTicks * TickSize : High[b] + StopOffsetTicks * TickSize;
                case OrbStopMode.RangeOpposite:
                    return isLong ? orLow - StopOffsetTicks * TickSize : orHigh + StopOffsetTicks * TickSize;
                default:
                    return isLong ? Close[b] - StopTicks * TickSize : Close[b] + StopTicks * TickSize;
            }
        }

        private bool CalcSize(bool isLong, double stopPrice, double refPrice, out int ticks, out int qty, out string reason)
        {
            reason = "";
            qty = 0;
            ticks = StopMode == OrbStopMode.FixedTicks
                ? StopTicks
                : (int)Math.Round((isLong ? refPrice - stopPrice : stopPrice - refPrice) / TickSize);

            if (ticks < 1)
            {
                reason = "המחיר כבר עבר את הסטופ";
                return false;
            }

            double riskPerContract = ticks * TickSize * Instrument.MasterInstrument.PointValue;
            qty = (int)Math.Floor(RiskPerTrade / riskPerContract);
            qty = Math.Min(qty, MaxContracts);

            double dayRoom = DailyLossLimit + GetDayPnl();   // כמה כסף נשאר עד מגבלת ההפסד היומית
            double ddRoom = DrawdownRoom();                  // כמה כסף נשאר עד סף ה-Drawdown (פחות מרווח ביטחון)
            double room = Math.Min(dayRoom, ddRoom);
            int qtyByRoom = (int)Math.Floor(room / riskPerContract);
            qty = Math.Min(qty, qtyByRoom);

            if (qty < 1)
            {
                if (riskPerContract > RiskPerTrade)
                    reason = "הסטופ גדול מדי לחוזה אחד ($" + riskPerContract.ToString("F0") + "). אפשר לעבור לחוזה מיקרו";
                else if (ddRoom < dayRoom)
                    reason = "קרוב מדי לסף ה-Drawdown (נשארו $" + Math.Max(0, ddRoom).ToString("F0") + ")";
                else
                    reason = "אין מספיק מרווח עד מגבלת ההפסד היומית";
                return false;
            }
            return true;
        }

        private bool TryEnter(bool isLong, double stopPrice, double refPrice)
        {
            int ticks, qty;
            string reason;
            if (!CalcSize(isLong, stopPrice, refPrice, out ticks, out qty, out reason))
                return false;

            string sig = isLong ? SigLong : SigShort;
            SetStopLoss(sig, CalculationMode.Ticks, ticks, false);
            SetProfitTarget(sig, CalculationMode.Ticks, ticks * RewardRisk);

            riskTicks = ticks;
            beDone = false;

            if (isLong)
                EnterLong(qty, sig);
            else
                EnterShort(qty, sig);
            return true;
        }

        // ===== אישור הצעה (נקרא מהכפתור בחלון) =====
        private void ApproveProposal()
        {
            Proposal p = pending;
            pending = null;
            if (p == null)
                return;

            if ((DateTime.Now - p.Created).TotalMinutes > ProposalValidMinutes)
            {
                lastMessage = "ההצעה פגה, העסקה לא נשלחה";
                UpdateStatus();
                return;
            }
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                lastMessage = "כבר יש עסקה פתוחה";
                UpdateStatus();
                return;
            }
            if (TradesToday() >= MaxTradesPerDay)
            {
                lastMessage = "הגענו למספר העסקאות המקסימלי היום";
                UpdateStatus();
                return;
            }
            if (dayStopped)
            {
                lastMessage = "המסחר נעצר להיום בגלל מגבלת ההפסד";
                UpdateStatus();
                return;
            }
            if (AfterFlatNow())
            {
                lastMessage = "עברה שעת הסגירה, העסקה לא נשלחה";
                UpdateStatus();
                return;
            }

            if (TryEnter(p.IsLong, p.StopPrice, Close[0]))
            {
                OrbSharedCounter.Increment(CounterKey());
                lastMessage = "נשלחה פקודה: " + (p.IsLong ? "קנייה" : "מכירה בחסר");
            }
            else
            {
                int t, q;
                string reason;
                CalcSize(p.IsLong, p.StopPrice, Close[0], out t, out q, out reason);
                lastMessage = "העסקה לא נשלחה: " + reason;
            }
            UpdateStatus();
        }

        // ===== הזזת סטופ לנקודת הכניסה =====
        private void CheckBreakevenRealtime()
        {
            if (beDone || riskTicks <= 0 || BreakevenAtR <= 0 || Position.MarketPosition == MarketPosition.Flat)
                return;

            double avg = Position.AveragePrice;
            double trigger = BreakevenAtR * riskTicks * TickSize;

            if (Position.MarketPosition == MarketPosition.Long && Close[0] >= avg + trigger)
                MoveToBreakeven(SigLong, avg);
            else if (Position.MarketPosition == MarketPosition.Short && Close[0] <= avg - trigger)
                MoveToBreakeven(SigShort, avg);
        }

        private void CheckBreakevenHistorical(int b)
        {
            if (beDone || riskTicks <= 0 || BreakevenAtR <= 0 || Position.MarketPosition == MarketPosition.Flat)
                return;

            double avg = Position.AveragePrice;
            double trigger = BreakevenAtR * riskTicks * TickSize;

            if (Position.MarketPosition == MarketPosition.Long && High[b] >= avg + trigger)
                MoveToBreakeven(SigLong, avg);
            else if (Position.MarketPosition == MarketPosition.Short && Low[b] <= avg - trigger)
                MoveToBreakeven(SigShort, avg);
        }

        private void MoveToBreakeven(string sig, double price)
        {
            SetStopLoss(sig, CalculationMode.Price, price, false);
            beDone = true;
            if (State == State.Realtime)
            {
                lastMessage = "הסטופ הוזז לנקודת הכניסה";
                Alert("orbBe" + CurrentBar, Priority.Medium, "ORB " + Instrument.FullName + ": " + lastMessage,
                    PlaySounds ? NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert3.wav" : "", 10, Brushes.SteelBlue, Brushes.White);
            }
        }

        // ===== תיעוד עסקאות שנסגרו =====
        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition != MarketPosition.Flat)
                return;

            riskTicks = 0;
            beDone = false;
            flattening = false;

            if (State == State.Historical)
            {
                if (SystemPerformance.AllTrades.Count > 0)
                    histDayPnl += SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1].ProfitCurrency;

                // מעקב Drawdown בבדיקה על נתוני עבר (לפי עסקאות סגורות)
                double eq = Equity();
                if (DrawdownType != OrbDrawdownType.Static && eq > histPeak)
                    histPeak = eq;
                if (!blown && eq <= Floor())
                {
                    blown = true;
                    Print("ORB: !!! בתאריך " + curDay.ToString("yyyy-MM-dd") + " החשבון היה נשרף. יתרה $" + eq.ToString("F0")
                        + ", סף $" + Floor().ToString("F0") + ". מכאן הבוט לא נכנס לעסקאות בבדיקה הזו.");
                }
                return;
            }

            if (SystemPerformance.RealTimeTrades.Count == 0)
                return;

            Trade t = SystemPerformance.RealTimeTrades[SystemPerformance.RealTimeTrades.Count - 1];
            string dir = t.Entry.MarketPosition == MarketPosition.Long ? "קנייה" : "מכירה בחסר";
            DateTime entryIl = ToZone(t.Entry.Time, ilTz);

            string line = string.Join(",",
                entryIl.ToString("yyyy-MM-dd"), entryIl.ToString("HH:mm"), Instrument.FullName, dir,
                t.Quantity, Fmt(t.Entry.Price), Fmt(t.Exit.Price), t.ProfitCurrency.ToString("F2"));
            dayReport.Add(line);
            WriteReportLine(line);

            lastMessage = "עסקה נסגרה: " + (t.ProfitCurrency >= 0 ? "רווח" : "הפסד") + " $" + Math.Abs(t.ProfitCurrency).ToString("F0");

            if (GetDayPnl() <= -DailyLossLimit)
            {
                dayStopped = true;
                lastMessage = "הגענו למגבלת ההפסד היומית. אין עוד עסקאות היום";
                Alert("orbStop", Priority.High, "ORB: " + lastMessage,
                    PlaySounds ? NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav" : "", 60, Brushes.Red, Brushes.White);
            }
            UpdateStatus();
        }

        private void WriteReportLine(string line)
        {
            try
            {
                string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "ORB_Reports");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "orb_" + curDay.ToString("yyyy-MM-dd") + ".csv");
                bool isNew = !File.Exists(file);
                using (StreamWriter sw = new StreamWriter(file, true, new UTF8Encoding(true)))
                {
                    if (isNew)
                        sw.WriteLine("תאריך,שעה,נכס,כיוון,חוזים,כניסה,יציאה,רווח והפסד $");
                    sw.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                Print("ORB: לא הצלחתי לכתוב את הדוח: " + ex.Message);
            }
        }

        private void PrintDayReport()
        {
            if (dayReport.Count == 0)
                return;
            Print("===== דוח יומי ORB " + Instrument.FullName + " " + curDay.ToString("yyyy-MM-dd") + " =====");
            foreach (string l in dayReport)
                Print(l);
        }

        // ===== חלון הצעת עסקה =====
        private void ShowProposalWindow(string text)
        {
            if (System.Windows.Application.Current == null)
                return;

            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (proposalWindow != null)
                {
                    proposalWindow.Close();
                    proposalWindow = null;
                }

                var w = new System.Windows.Window
                {
                    Title = "הצעת עסקה - ORB " + Instrument.FullName,
                    Width = 420,
                    SizeToContent = System.Windows.SizeToContent.Height,
                    Topmost = true,
                    ResizeMode = System.Windows.ResizeMode.NoResize,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                    FlowDirection = System.Windows.FlowDirection.RightToLeft
                };

                var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(18) };
                panel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = text,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    FontSize = 15
                });

                var row = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin = new System.Windows.Thickness(0, 18, 0, 0)
                };

                var approve = new System.Windows.Controls.Button
                {
                    Content = "אשר עסקה",
                    FontSize = 15,
                    Padding = new System.Windows.Thickness(18, 6, 18, 6),
                    Margin = new System.Windows.Thickness(0, 0, 0, 0)
                };
                approve.Click += (s, e) =>
                {
                    w.Close();
                    TriggerCustomEvent(o => ApproveProposal(), null);
                };

                var reject = new System.Windows.Controls.Button
                {
                    Content = "דחה",
                    FontSize = 15,
                    Padding = new System.Windows.Thickness(18, 6, 18, 6),
                    Margin = new System.Windows.Thickness(12, 0, 0, 0)
                };
                reject.Click += (s, e) =>
                {
                    w.Close();
                    TriggerCustomEvent(o => { pending = null; lastMessage = "ההצעה נדחתה"; UpdateStatus(); }, null);
                };

                row.Children.Add(approve);
                row.Children.Add(reject);
                panel.Children.Add(row);
                w.Content = panel;
                w.Closed += (s, e) => { if (proposalWindow == w) proposalWindow = null; };
                proposalWindow = w;
                w.Show();
            });
        }

        private void CloseProposalWindow()
        {
            if (System.Windows.Application.Current == null)
                return;
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (proposalWindow != null)
                {
                    proposalWindow.Close();
                    proposalWindow = null;
                }
            });
        }

        // ===== תצוגה על הגרף =====
        private void DrawRange(int b)
        {
            int startAgo = CurrentBar - orStartBar + b;
            string d = curDay.ToString("yyyyMMdd");
            Draw.Line(this, "orbHi" + d, false, startAgo, orHigh, b, orHigh, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
            Draw.Line(this, "orbLo" + d, false, startAgo, orLow, b, orLow, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
        }

        private void Note(int b, string text)
        {
            Draw.Text(this, "orbNote" + (CurrentBar - b), text, b, High[b] + 12 * TickSize, Brushes.Goldenrod);
        }

        private void UpdateStatus()
        {
            lastStatusUpdate = DateTime.Now;
            if (State != State.Realtime)
                return;

            SavePeak();

            double dayPnl = GetDayPnl();
            double room = DailyLossLimit + dayPnl;
            double netLiq = Equity();
            double floor = Floor();

            var sb = new StringBuilder();
            sb.AppendLine("ORB Assistant | " + Instrument.FullName);
            sb.AppendLine(hasRange ? "טווח פתיחה: " + Fmt(orHigh) + " / " + Fmt(orLow) : "טווח פתיחה: עוד לא נקבע");
            sb.AppendLine("רווח והפסד היום: $" + dayPnl.ToString("F0"));
            sb.AppendLine("נשאר עד מגבלה יומית: $" + Math.Max(0, room).ToString("F0"));
            sb.AppendLine("יתרה: $" + netLiq.ToString("F0") + " | סף Drawdown: $" + floor.ToString("F0"));
            sb.AppendLine("מרחק מה-Drawdown: $" + (netLiq - floor).ToString("F0"));
            sb.AppendLine("עסקאות היום: " + TradesToday() + " / " + MaxTradesPerDay);
            if (dayStopped) sb.AppendLine("המסחר נעצר להיום");
            if (!string.IsNullOrEmpty(lastMessage)) sb.AppendLine(lastMessage);

            Draw.TextFixed(this, "orbStatus", sb.ToString(), TextPosition.TopLeft, Brushes.White,
                new SimpleFont("Arial", 13), Brushes.Transparent, Brushes.Black, 70);
        }

        // ===== שמירה על תיק המימון =====
        // יתרה נוכחית: בזמן אמת מהחשבון, בבדיקה על נתוני עבר = יתרת פתיחה + רווח העסקאות
        private double Equity()
        {
            if (State == State.Realtime)
                return Account.Get(AccountItem.NetLiquidation, Currency.UsDollar);
            return AccountStartBalance + SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
        }

        // סף ה-Drawdown: אם היתרה יורדת אליו החשבון נשרף
        private double Floor()
        {
            double floor;
            if (DrawdownType == OrbDrawdownType.Static)
            {
                floor = AccountStartBalance - MaxDrawdown;
            }
            else
            {
                double peak = State == State.Realtime ? rtPeak : histPeak;
                floor = Math.Max(peak, AccountStartBalance) - MaxDrawdown;
                if (LockAtStartBalance)
                    floor = Math.Min(floor, AccountStartBalance);
            }
            // אם הוזן סף מהדשבורד, לוקחים את המחמיר מבין השניים
            if (State == State.Realtime && ManualFloor > 0)
                floor = Math.Max(floor, ManualFloor);
            return floor;
        }

        // כמה כסף אפשר להפסיד בעסקה הבאה בלי להתקרב לסף
        private double DrawdownRoom()
        {
            return Equity() - Floor() - SafetyBuffer;
        }

        private void GuardAccountRealtime()
        {
            double eq = Equity();
            if (eq <= 0)
                return;   // נתוני החשבון עוד לא הגיעו
            rtLastEquity = eq;

            if (DrawdownType == OrbDrawdownType.TrailingIntraday && eq > rtPeak)
            {
                rtPeak = eq;
                rtPeakDirty = true;
            }

            if (Position.MarketPosition != MarketPosition.Flat && !flattening && eq - Floor() <= SafetyBuffer)
            {
                CloseAll();
                dayStopped = true;
                lastMessage = "קרוב מדי לסף ה-Drawdown! העסקה נסגרה והמסחר נעצר להיום";
                Alert("orbDd", Priority.High, "ORB: " + lastMessage,
                    PlaySounds ? NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav" : "", 60, Brushes.Red, Brushes.White);
                UpdateStatus();
            }
        }

        private bool AfterFlatNow()
        {
            DateTime nowNy = ToZone(NinjaTrader.Core.Globals.Now, nyTz);
            int m = nowNy.Hour * 60 + nowNy.Minute;
            int flatMin = (FlatTimeNy / 100) * 60 + FlatTimeNy % 100;
            return m >= flatMin && m < 18 * 60;
        }

        private void CheckFlatTimeRealtime()
        {
            if (!AfterFlatNow())
                return;

            if (pending != null)
            {
                pending = null;
                CloseProposalWindow();
            }

            if (Position.MarketPosition != MarketPosition.Flat && !flattening)
            {
                CloseAll();
                lastMessage = "סגירה לפי שעה (" + FlatTimeNy + " ניו יורק)";
                UpdateStatus();
            }
        }

        private void CloseAll()
        {
            flattening = true;
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong();
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort();
        }

        private string PeakFile()
        {
            string name = Account.Name;
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "ORB_Reports", "drawdown_peak_" + name + ".txt");
        }

        // השיא נשמר לקובץ כדי שלא יאבד כשסוגרים את NinjaTrader
        private void LoadPeak()
        {
            rtPeak = AccountStartBalance;
            try
            {
                string file = PeakFile();
                double saved;
                if (File.Exists(file) && double.TryParse(File.ReadAllText(file).Trim(),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out saved))
                    rtPeak = Math.Max(rtPeak, saved);
            }
            catch (Exception ex)
            {
                Print("ORB: לא הצלחתי לקרוא את קובץ ה-Drawdown: " + ex.Message);
            }

            double eq = Equity();
            if (DrawdownType != OrbDrawdownType.Static && Position.MarketPosition == MarketPosition.Flat && eq > rtPeak)
                rtPeak = eq;
            rtPeakDirty = true;
            SavePeak();
            Print("ORB: שיא יתרה $" + rtPeak.ToString("F0") + ", סף Drawdown $" + Floor().ToString("F0"));
        }

        private void SavePeak()
        {
            if (!rtPeakDirty || rtPeak <= 0)
                return;
            try
            {
                string file = PeakFile();
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, rtPeak.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                rtPeakDirty = false;
            }
            catch (Exception ex)
            {
                Print("ORB: לא הצלחתי לשמור את קובץ ה-Drawdown: " + ex.Message);
            }
        }

        // ===== עזרים =====
        private double GetDayPnl()
        {
            if (State != State.Realtime)
                return histDayPnl;
            return Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar)
                 + Account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
        }

        private int TradesToday()
        {
            if (State != State.Realtime)
                return histTradesToday;
            return OrbSharedCounter.Get(CounterKey());
        }

        private string CounterKey()
        {
            string scope = CountTradesAcrossCharts ? "ALL" : Instrument.FullName;
            return Account.Name + "|" + scope + "|" + curDay.ToString("yyyyMMdd");
        }

        private bool IsNewsTime(DateTime tIl)
        {
            int m = tIl.Hour * 60 + tIl.Minute;
            foreach (int n in newsTimes)
                if (Math.Abs(m - n) <= NewsBufferMinutes)
                    return true;
            return false;
        }

        private static List<int> ParseTimes(string s)
        {
            var list = new List<int>();
            if (string.IsNullOrWhiteSpace(s))
                return list;
            foreach (string part in s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] hm = part.Trim().Split(':');
                int h, mm;
                if (hm.Length == 2 && int.TryParse(hm[0], out h) && int.TryParse(hm[1], out mm))
                    list.Add(h * 60 + mm);
            }
            return list;
        }

        private static DateTime ToZone(DateTime t, TimeZoneInfo tz)
        {
            TimeZoneInfo src = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
            DateTime u = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
            // ביום שמזיזים את השעון קדימה יש שעה שלא קיימת, וההמרה נכשלת. מזיזים שעה קדימה.
            if (src.IsInvalidTime(u))
                u = u.AddHours(1);
            return TimeZoneInfo.ConvertTime(u, src, tz);
        }

        private static int Hhmm(DateTime t)
        {
            return t.Hour * 100 + t.Minute;
        }

        private string Fmt(double price)
        {
            return Instrument.MasterInstrument.FormatPrice(price);
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "שעת פתיחה ניו יורק (HHMM)", Order = 1, GroupName = "1. זמנים")]
        public int SessionOpenNy { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "אורך טווח הפתיחה (דקות)", Order = 2, GroupName = "1. זמנים")]
        public int OpeningRangeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "שעה אחרונה לסימנים, שעון ישראל (HHMM)", Order = 3, GroupName = "1. זמנים")]
        public int LastSignalTimeIsrael { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "שעות חדשות היום, שעון ישראל (למשל 15:30, 21:00)", Order = 4, GroupName = "1. זמנים")]
        public string NewsTimesIsrael { get; set; }

        [NinjaScriptProperty]
        [Range(0, 120)]
        [Display(Name = "דקות לפני ואחרי חדשות בלי כניסה", Order = 5, GroupName = "1. זמנים")]
        public int NewsBufferMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "סוג סטופ", Description = "FixedTicks = טיקים קבועים, SignalCandle = מעבר לנר הפריצה, RangeOpposite = צד שני של הטווח", Order = 1, GroupName = "2. סטופ ויעד")]
        public OrbStopMode StopMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "סטופ בטיקים (לסוג FixedTicks)", Order = 2, GroupName = "2. סטופ ויעד")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "מרווח סטופ בטיקים (לשני הסוגים האחרים)", Order = 3, GroupName = "2. סטופ ויעד")]
        public int StopOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10)]
        [Display(Name = "יחס סיכון לסיכוי (יעד = פי X מהסטופ)", Order = 4, GroupName = "2. סטופ ויעד")]
        public double RewardRisk { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "הזזה לנקודת כניסה ברווח של פי X מהסטופ (0 = כבוי)", Order = 5, GroupName = "2. סטופ ויעד")]
        public double BreakevenAtR { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "סיכון מקסימלי לעסקה ($)", Order = 1, GroupName = "3. ניהול סיכונים")]
        public double RiskPerTrade { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "מקסימום חוזים", Order = 2, GroupName = "3. ניהול סיכונים")]
        public int MaxContracts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "מקסימום עסקאות ביום", Order = 3, GroupName = "3. ניהול סיכונים")]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "לספור עסקאות על כל הגרפים יחד", Order = 4, GroupName = "3. ניהול סיכונים")]
        public bool CountTradesAcrossCharts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "לאפשר פריצה בכיוון ההפוך באותו יום", Order = 5, GroupName = "3. ניהול סיכונים")]
        public bool AllowOppositeDirection { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "מגבלת הפסד יומית ($)", Order = 6, GroupName = "3. ניהול סיכונים")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Range(1000, 10000000)]
        [Display(Name = "יתרת פתיחה של התיק ($)", Order = 1, GroupName = "5. תיק מימון")]
        public double AccountStartBalance { get; set; }

        [NinjaScriptProperty]
        [Range(100, 1000000)]
        [Display(Name = "Drawdown מקסימלי ($)", Order = 2, GroupName = "5. תיק מימון")]
        public double MaxDrawdown { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "סוג Drawdown", Description = "TrailingIntraday = עולה עם כל שיא, גם באמצע היום (הכי בטוח). TrailingEndOfDay = עולה לפי סוף יום. Static = קבוע", Order = 3, GroupName = "5. תיק מימון")]
        public OrbDrawdownType DrawdownType { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "הסף מפסיק לעלות ביתרת הפתיחה", Order = 4, GroupName = "5. תיק מימון")]
        public bool LockAtStartBalance { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000000)]
        [Display(Name = "סף Drawdown נוכחי מהדשבורד ($, 0 = חישוב אוטומטי)", Order = 5, GroupName = "5. תיק מימון")]
        public double ManualFloor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "מרווח ביטחון מעל הסף ($)", Order = 6, GroupName = "5. תיק מימון")]
        public double SafetyBuffer { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "שעת סגירת כל העסקאות, ניו יורק (HHMM)", Order = 7, GroupName = "5. תיק מימון")]
        public int FlatTimeNy { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "תוקף הצעה (דקות)", Order = 1, GroupName = "4. כללי")]
        public int ProposalValidMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "כניסה אוטומטית בבדיקה על נתוני עבר", Order = 2, GroupName = "4. כללי")]
        public bool AutoTradeInBacktest { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "צלילי התראה", Order = 3, GroupName = "4. כללי")]
        public bool PlaySounds { get; set; }
        #endregion
    }
}
