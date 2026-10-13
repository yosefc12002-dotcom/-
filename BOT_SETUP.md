# ORB Bot – הוראות הפעלה ב-TradingView

1. פתח Pine Editor, הדבק את `ORB_strategy.pine`, לחץ **Add to chart** (על גרף דקות, למשל 1m).
2. בהגדרות האסטרטגיה (Inputs) מלא:
   - **Bot: Quantity** – כמות לכל עסקה
   - **Bot: Webhook passphrase** – סיסמה שהשרת/הברוקר שלך בודק
   - **Bars to hold** – כמה בארים להחזיק (ברירת מחדל 3)
3. צור Alert (שעון מעורר) → Condition: **ORB** → "Order fills only".
4. בשדה **Message** כתוב בדיוק:
   ```
   {{strategy.order.alert_message}}
   ```
5. סמן **Webhook URL** והדבק את הכתובת של השירות שמבצע את הפקודות (3Commas / PineConnector / Alpaca / שרת משלך).
6. Expiration: Open-ended.

## מה הבוט שולח
בכל כניסה/יציאה נשלח JSON:
```json
{"passphrase":"YOUR_SECRET","action":"buy|sell|close","ticker":"AAPL","qty":1,"price":123.4,"time":1700000000000}
```

## הלוגיקה
- נר נסגר מעל ORB High → `buy` בפתיחת הנר הבא.
- נר נסגר מתחת ל-ORB Low → `sell` (שורט) בפתיחת הנר הבא.
- אחרי 3 בארים → `close`.
- סיגנל הפוך בזמן פוזיציה הופך את הפוזיציה ומתחיל ספירה מחדש.

> Webhooks דורשים מנוי TradingView בתשלום. מומלץ לבדוק קודם בחשבון דמו.
