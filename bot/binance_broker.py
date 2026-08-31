"""Binance Futures order execution (crypto leg of the bot)."""
import os

from binance.client import Client
from binance.enums import (
    FUTURE_ORDER_TYPE_MARKET,
    FUTURE_ORDER_TYPE_STOP_MARKET,
    FUTURE_ORDER_TYPE_TAKE_PROFIT_MARKET,
    SIDE_BUY,
    SIDE_SELL,
)

TESTNET = os.getenv("BINANCE_TESTNET", "true").lower() == "true"

client = Client(os.environ["BINANCE_API_KEY"], os.environ["BINANCE_API_SECRET"])
if TESTNET:
    client.FUTURES_URL = "https://testnet.binancefuture.com/fapi"


def place_order(symbol, side, quantity, stop_price, take_profit_price):
    entry_side = SIDE_BUY if side == "buy" else SIDE_SELL
    exit_side = SIDE_SELL if side == "buy" else SIDE_BUY

    entry = client.futures_create_order(
        symbol=symbol,
        side=entry_side,
        type=FUTURE_ORDER_TYPE_MARKET,
        quantity=quantity,
    )

    stop = client.futures_create_order(
        symbol=symbol,
        side=exit_side,
        type=FUTURE_ORDER_TYPE_STOP_MARKET,
        stopPrice=stop_price,
        closePosition=True,
    )

    target = client.futures_create_order(
        symbol=symbol,
        side=exit_side,
        type=FUTURE_ORDER_TYPE_TAKE_PROFIT_MARKET,
        stopPrice=take_profit_price,
        closePosition=True,
    )

    return {"entry": entry, "stop": stop, "take_profit": target}
