"""Tradovate order execution (NASDAQ futures, e.g. NQ, leg of the bot)."""
import os

import requests

TRADOVATE_ENV = os.getenv("TRADOVATE_ENV", "demo")
BASE_URL = (
    "https://demo.tradovateapi.com/v1"
    if TRADOVATE_ENV == "demo"
    else "https://live.tradovateapi.com/v1"
)

_access_token = None


def _authenticate():
    global _access_token
    resp = requests.post(
        f"{BASE_URL}/auth/accesstokenrequest",
        json={
            "name": os.environ["TRADOVATE_USERNAME"],
            "password": os.environ["TRADOVATE_PASSWORD"],
            "appId": os.environ["TRADOVATE_APP_ID"],
            "appVersion": "1.0",
            "cid": os.environ["TRADOVATE_CID"],
            "sec": os.environ["TRADOVATE_SECRET"],
            "deviceId": os.getenv("TRADOVATE_DEVICE_ID", "ma50-bot"),
        },
        timeout=10,
    )
    resp.raise_for_status()
    _access_token = resp.json()["accessToken"]
    return _access_token


def _headers():
    token = _access_token or _authenticate()
    return {"Authorization": f"Bearer {token}"}


def place_order(symbol, side, quantity, stop_price, take_profit_price):
    account_id = int(os.environ["TRADOVATE_ACCOUNT_ID"])
    account_spec = os.environ["TRADOVATE_ACCOUNT_SPEC"]
    entry_action = "Buy" if side == "buy" else "Sell"
    exit_action = "Sell" if side == "buy" else "Buy"

    entry_resp = requests.post(
        f"{BASE_URL}/order/placeorder",
        headers=_headers(),
        json={
            "accountSpec": account_spec,
            "accountId": account_id,
            "action": entry_action,
            "symbol": symbol,
            "orderQty": quantity,
            "orderType": "Market",
            "isAutomated": True,
        },
        timeout=10,
    )
    entry_resp.raise_for_status()

    bracket_resp = requests.post(
        f"{BASE_URL}/order/placeoco",
        headers=_headers(),
        json={
            "accountSpec": account_spec,
            "accountId": account_id,
            "action": exit_action,
            "symbol": symbol,
            "orderQty": quantity,
            "isAutomated": True,
            "order1": {"orderType": "Stop", "stopPrice": stop_price},
            "order2": {"orderType": "Limit", "price": take_profit_price},
        },
        timeout=10,
    )
    bracket_resp.raise_for_status()

    return {"entry": entry_resp.json(), "bracket": bracket_resp.json()}
