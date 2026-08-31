"""
Webhook receiver for the MA50 Breakout + Confluence TradingView strategy.

TradingView sends a JSON alert (see ma50_breakout_confluence.pine) to
POST /webhook. This server validates the shared secret, sizes the
position from the account's risk-per-trade setting and the strategy's
own stop distance, then routes the order to the matching broker:
  - market == "crypto"  -> Binance Futures (binance_broker.py)
  - market == "futures" -> Tradovate, e.g. NQ (tradovate_broker.py)

Run on demo/testnet credentials first (see .env.example) before ever
pointing this at a live account.
"""
import hmac
import os

from dotenv import load_dotenv
from flask import Flask, jsonify, request

import binance_broker
import tradovate_broker

load_dotenv()

app = Flask(__name__)

WEBHOOK_SECRET = os.environ["WEBHOOK_SECRET"]
ACCOUNT_EQUITY = float(os.getenv("ACCOUNT_EQUITY", "10000"))
RISK_PER_TRADE_PCT = float(os.getenv("RISK_PER_TRADE_PCT", "1.0"))

# TradingView ticker -> broker-specific contract/pair symbol
CRYPTO_SYMBOL_MAP = {
    "BTCUSD": "BTCUSDT",
    "ETHUSD": "ETHUSDT",
}
FUTURES_SYMBOL_MAP = {
    "NQ1!": os.getenv("TRADOVATE_NQ_CONTRACT", "NQZ5"),
}


def position_size(entry_price, stop_price):
    risk_amount = ACCOUNT_EQUITY * (RISK_PER_TRADE_PCT / 100)
    stop_distance = abs(entry_price - stop_price)
    if stop_distance <= 0:
        return 0
    return risk_amount / stop_distance


@app.route("/webhook", methods=["POST"])
def webhook():
    payload = request.get_json(force=True, silent=True) or {}

    if not hmac.compare_digest(str(payload.get("secret", "")), WEBHOOK_SECRET):
        return jsonify({"error": "unauthorized"}), 401

    market = payload.get("market", "").lower()
    symbol = payload.get("symbol")
    side = payload.get("side")
    price = float(payload["price"])
    stop = float(payload["stop"])
    take_profit = float(payload["take_profit"])

    if side not in ("buy", "sell"):
        return jsonify({"error": f"invalid side '{side}'"}), 400

    qty = position_size(price, stop)
    if qty <= 0:
        return jsonify({"error": "computed non-positive position size"}), 400

    if market == "crypto":
        broker_symbol = CRYPTO_SYMBOL_MAP.get(symbol, symbol)
        result = binance_broker.place_order(
            broker_symbol, side, round(qty, 3), stop, take_profit
        )
    elif market == "futures":
        broker_symbol = FUTURES_SYMBOL_MAP.get(symbol, symbol)
        result = tradovate_broker.place_order(
            broker_symbol, side, max(1, round(qty)), stop, take_profit
        )
    else:
        return jsonify({"error": f"unknown market '{market}'"}), 400

    return jsonify({"status": "ok", "order": result})


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=int(os.getenv("PORT", "5000")))
