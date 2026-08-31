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
