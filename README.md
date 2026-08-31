# MA50 Breakout + Confluence

אסטרטגיית מסחר ל-TradingView (Pine Script v5) הנכנסת לעסקה רק כאשר **כל** התנאים הבאים מתקיימים יחד (AND מלא):

1. **פריצת MA50** – המחיר חוצה מעל/מתחת לממוצע נע 50 (`crossover`/`crossunder`).
2. **נפח מסחר** – נר הפריצה בנפח גבוה מ-SMA(20) של הווליום, כפול מקדם (ברירת מחדל 1.2).
3. **שעת מסחר** – הפריצה קורית בתוך חלון שעות מוגדר (ברירת מחדל 09:30–16:00 שעון ניו יורק).
4. **מומנטום** – גודל גוף הנר (`|close-open|`) גדול מהממוצע שלו (ברירת מחדל ממוצע 20 נרות).
5. **מספרים עגולים** – המחיר בקרבת רמת מספר עגול (כפולות של ערך מוגדר, ברירת מחדל 50), במרחק מקסימלי מוגדר.

**ניהול עסקה:**
- כניסה בכיוון הפריצה בלבד (Long בפריצה מעלה, Short בפריצה מטה).
- יחס סיכון:סיכוי קבוע (ברירת מחדל 1:2, ניתן להתאמה).
- Stop מבוסס ATR (ניתן להגדיר אורך ומכפיל).
- מקסימום עסקה אחת ביום.

הקובץ: [`ma50_breakout_confluence.pine`](./ma50_breakout_confluence.pine) — להעתקה ישירה לעורך ה-Pine Script של TradingView.

## בוט מסחר אוטומטי (Webhook)

תיקיית [`bot/`](./bot) מכילה שרת Python שמקבל את ה-alerts מ-TradingView ושולח פקודות אמיתיות לברוקר:

- **קריפטו** → Binance Futures (`bot/binance_broker.py`)
- **חוזים עתידיים על נאסד״ק (NQ)** → Tradovate (`bot/tradovate_broker.py`)

### הפעלה

```bash
cd bot
pip install -r requirements.txt
cp .env.example .env   # מלאו את המפתחות/פרטי החשבון
python webhook_server.py
```

השרת חייב להיות נגיש מהאינטרנט (למשל דרך ngrok, או פריסה בשרת ציבורי) כדי ש-TradingView יוכל לקרוא לו.

### הגדרת Alert ב-TradingView

1. הוסיפו את `ma50_breakout_confluence.pine` לגרף, והגדירו את השדה **Asset Class (for webhook bot)** ל-`Crypto` או `Futures` לפי הגרף, ואת **Webhook Secret** לערך שהגדרתם ב-`.env` (`WEBHOOK_SECRET`).
2. צרו Alert חדש → Condition: `MA50 Breakout + Confluence` → `Any alert() function call`.
3. תחת Notifications, סמנו **Webhook URL** והזינו את `https://<your-server>/webhook`.
4. הפעילו את הגרף ב-testnet/demo (`BINANCE_TESTNET=true`, `TRADOVATE_ENV=demo`) ואמתו כמה עסקאות לפני מעבר לחשבון אמיתי.

⚠️ **אזהרה:** זהו קוד שמבצע עסקאות אמיתיות בכסף אמיתי כאשר הוא מוגדר על סביבת live. יש לבדוק ביסודיות על חשבונות דמו/טסטנט לפני שימוש בחשבון מסחר אמיתי.

## Overview (EN)

A TradingView (Pine Script v5) strategy that enters a trade only when **all** of the following align (full AND, no partial scoring):

1. **MA50 breakout** — price crosses over/under the 50-period moving average.
2. **Volume confirmation** — the breakout candle's volume exceeds SMA(20) of volume by a configurable multiplier (default 1.2x).
3. **Trading session window** — the breakout occurs inside a configurable time window (default 09:30–16:00 New York time).
4. **Momentum** — candle body size (`|close-open|`) exceeds its own moving average (default 20-bar lookback), filtering out doji/hesitation candles.
5. **Round numbers** — price is near a round-number level (multiples of a configurable step, default 50), within a configurable max distance.

**Trade management:**
- Entry only in the breakout direction (long on upside breakout, short on downside breakout).
- Fixed risk:reward ratio (default 1:2, configurable).
- ATR-based stop-loss (configurable length and multiplier).
- Maximum one trade per day.

See [`ma50_breakout_confluence.pine`](./ma50_breakout_confluence.pine) — paste directly into TradingView's Pine Editor.

## Automated Trading Bot (Webhook)

[`bot/`](./bot) contains a Python webhook server that receives TradingView alerts and places real orders:

- **Crypto** → Binance Futures (`bot/binance_broker.py`)
- **NASDAQ futures (NQ)** → Tradovate (`bot/tradovate_broker.py`)

### Run it

```bash
cd bot
pip install -r requirements.txt
cp .env.example .env   # fill in your API keys / account details
python webhook_server.py
```

The server needs to be reachable from the internet (e.g. via ngrok, or a public deployment) so TradingView can call it.

### TradingView alert setup

1. Add `ma50_breakout_confluence.pine` to the chart, set **Asset Class (for webhook bot)** to `Crypto` or `Futures` to match the chart, and set **Webhook Secret** to the same value as `WEBHOOK_SECRET` in `.env`.
2. Create a new Alert → Condition: `MA50 Breakout + Confluence` → `Any alert() function call`.
3. Under Notifications, check **Webhook URL** and enter `https://<your-server>/webhook`.
4. Run it against testnet/demo (`BINANCE_TESTNET=true`, `TRADOVATE_ENV=demo`) and verify several trades before pointing it at a live account.

⚠️ **Warning:** this code places real orders with real money once configured against a live environment. Test thoroughly on demo/testnet accounts first.
