"""Получение OAuth-токена Яндекс.Музыки (Device Flow).

Сохраняет токен в .env (YANDEX_TOKEN=...).
Документация: https://ym.marshal.dev/token
"""

from __future__ import annotations

import re
from pathlib import Path

from yandex_music import Client

ENV_PATH = Path(__file__).resolve().parent / ".env"


def on_code(code) -> None:
    print(f"1. Откройте: {code.verification_url}")
    print(f"2. Введите код: {code.user_code}")
    print("Жду подтверждения...")


def upsert_env(key: str, value: str) -> None:
    text = ENV_PATH.read_text(encoding="utf-8") if ENV_PATH.exists() else ""
    pattern = re.compile(rf"^{re.escape(key)}=.*$", re.MULTILINE)
    line = f"{key}={value}"
    if pattern.search(text):
        text = pattern.sub(line, text)
    else:
        if text and not text.endswith("\n"):
            text += "\n"
        text += line + "\n"
    ENV_PATH.write_text(text, encoding="utf-8")


def main() -> None:
    client = Client()
    token = client.device_auth(on_code=on_code)
    client = Client(token.access_token).init()
    login = client.me.account.login if client.me and client.me.account else "?"

    upsert_env("YANDEX_TOKEN", token.access_token)
    print(f"\nOK: авторизован как {login}")
    print(f"Токен записан в {ENV_PATH}")
    print("Не коммитьте .env и не отправляйте токен третьим лицам.")


if __name__ == "__main__":
    main()
