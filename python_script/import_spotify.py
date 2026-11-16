"""Импорт треков из data/likes.json в Spotify.

Цель по умолчанию — новый плейлист «Yandex — Мне нравится».
Опция --liked добавляет в Liked Songs (сердечко).

Состояние пишется в data/import_state.json — можно безопасно прерывать
и продолжать после rate limit Spotify.
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import time
from pathlib import Path

import spotipy
from dotenv import load_dotenv
from spotipy.exceptions import SpotifyException
from spotipy.oauth2 import SpotifyOAuth

DATA_DIR = Path(__file__).resolve().parent / "data"
DEFAULT_INPUT = DATA_DIR / "likes.json"
DEFAULT_STATE = DATA_DIR / "import_state.json"
CACHE_PATH = Path(__file__).resolve().parent / ".spotify_cache"

# Если Retry-After больше этого — сохраняем прогресс и выходим (не ждём сутки).
MAX_INLINE_WAIT_S = 120

SCOPES = (
    "playlist-read-private playlist-modify-public playlist-modify-private "
    "user-library-modify"
)

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("import_spotify")


class LongRateLimit(Exception):
    def __init__(self, retry_after: int):
        self.retry_after = retry_after
        hours = retry_after / 3600
        super().__init__(f"Spotify rate limit: Retry-After={retry_after}s (~{hours:.1f}h)")


def load_json(path: Path, default):
    if not path.exists():
        return default
    return json.loads(path.read_text(encoding="utf-8"))


def save_json(path: Path, data) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def spotify_client() -> spotipy.Spotify:
    client_id = os.getenv("SPOTIFY_CLIENT_ID", "").strip()
    client_secret = os.getenv("SPOTIFY_CLIENT_SECRET", "").strip()
    redirect_uri = os.getenv("SPOTIFY_REDIRECT_URI", "http://127.0.0.1:8888/callback").strip()
    if not client_id or not client_secret:
        raise SystemExit(
            "Заполните SPOTIFY_CLIENT_ID и SPOTIFY_CLIENT_SECRET в .env\n"
            "Создайте приложение: https://developer.spotify.com/dashboard\n"
            f"Redirect URI: {redirect_uri}"
        )
    auth = SpotifyOAuth(
        client_id=client_id,
        client_secret=client_secret,
        redirect_uri=redirect_uri,
        scope=SCOPES,
        cache_path=str(CACHE_PATH),
        open_browser=True,
    )
    return spotipy.Spotify(auth_manager=auth, requests_timeout=20)


def call_spotify(fn, *args, **kwargs):
    """Повторы при коротком 429 / сетевых сбоях. Длинный бан → LongRateLimit."""
    for attempt in range(1, 8):
        try:
            return fn(*args, **kwargs)
        except SpotifyException as e:
            if e.http_status == 429:
                wait = int(e.headers.get("Retry-After", 5)) + 1
                if wait > MAX_INLINE_WAIT_S:
                    raise LongRateLimit(wait) from e
                log.warning("Rate limit. Жду %s с...", wait)
                time.sleep(wait)
                continue
            raise
        except LongRateLimit:
            raise
        except Exception as e:
            if attempt >= 7:
                raise
            wait = min(2**attempt, 60)
            log.warning("Ошибка %s. Повтор через %s с...", e, wait)
            time.sleep(wait)
    raise RuntimeError("unreachable")


def search_track(sp: spotipy.Spotify, track: dict) -> str | None:
    title = (track.get("title") or "").strip()
    artists = track.get("artists") or []
    artist = artists[0] if artists else ""
    isrc = track.get("isrc")

    if isrc:
        result = call_spotify(sp.search, q=f"isrc:{isrc}", type="track", limit=1)
        items = (result.get("tracks") or {}).get("items") or []
        if items:
            return items[0]["id"]

    # Один запрос сначала — меньше нагрузки на API.
    queries = []
    if artist and title:
        queries.append(f"{artist} {title}")
        queries.append(f'track:"{title}" artist:"{artist}"')
    elif title:
        queries.append(title)

    for q in queries:
        result = call_spotify(sp.search, q=q, type="track", limit=5)
        items = (result.get("tracks") or {}).get("items") or []
        if not items:
            continue
        title_l = title.casefold()
        for item in items:
            if (item.get("name") or "").casefold() == title_l:
                return item["id"]
        return items[0]["id"]
    return None


def ensure_playlist(sp: spotipy.Spotify, name: str) -> str:
    offset = 0
    while True:
        page = call_spotify(sp.current_user_playlists, limit=50, offset=offset)
        for pl in page.get("items") or []:
            if pl.get("name") == name:
                return pl["id"]
        if page.get("next") is None:
            break
        offset += 50
    created = call_spotify(
        sp.current_user_playlist_create,
        name,
        public=False,
        description="Импорт из Яндекс.Музыка «Мне нравится»",
    )
    return created["id"]


def main() -> None:
    load_dotenv()
    parser = argparse.ArgumentParser(description="Импорт likes.json в Spotify")
    parser.add_argument("-i", "--input", type=Path, default=DEFAULT_INPUT)
    parser.add_argument("--state", type=Path, default=DEFAULT_STATE)
    parser.add_argument(
        "--playlist",
        default="Yandex — Мне нравится",
        help="Имя плейлиста Spotify (по умолчанию)",
    )
    parser.add_argument(
        "--liked",
        action="store_true",
        help="Добавлять в Liked Songs вместо плейлиста",
    )
    parser.add_argument("--limit", type=int, default=None, help="Только первые N из JSON")
    parser.add_argument(
        "--reset-state",
        action="store_true",
        help="Игнорировать сохранённый прогресс",
    )
    parser.add_argument(
        "--sleep",
        type=float,
        default=1.2,
        help="Пауза между search-запросами, секунды (по умолчанию 1.2)",
    )
    args = parser.parse_args()

    if not args.input.exists():
        raise SystemExit(f"Нет файла {args.input}. Сначала: python export_likes.py")

    payload = load_json(args.input, {})
    tracks = payload.get("tracks") or []
    if args.limit is not None:
        tracks = tracks[: args.limit]

    state = {} if args.reset_state else load_json(args.state, {})
    matched: dict[str, str] = state.get("matched") or {}
    not_found: list[dict] = state.get("not_found") or []
    added: list[str] = state.get("added") or []
    not_found_ids = {x["id"] for x in not_found if "id" in x}
    added_set = set(added)

    def persist() -> None:
        save_json(
            args.state,
            {
                "matched": matched,
                "not_found": not_found,
                "added": added,
                "playlist_id": playlist_id,
                "mode": "liked" if args.liked else "playlist",
            },
        )

    sp = spotify_client()
    me = call_spotify(sp.me)
    user_id = me["id"]
    log.info("Spotify user: %s", me.get("display_name") or user_id)
    log.info(
        "Прогресс: matched=%s added=%s not_found=%s / total=%s",
        len(matched),
        len(added),
        len(not_found),
        len(tracks),
    )

    playlist_id = state.get("playlist_id")
    if not args.liked:
        if playlist_id:
            log.info("Плейлист (из state): %s", playlist_id)
        else:
            playlist_id = ensure_playlist(sp, args.playlist)
            persist()
            log.info("Плейлист: %s (%s)", args.playlist, playlist_id)

    pending_add: list[str] = []

    def flush_add() -> None:
        nonlocal pending_add, added, added_set
        if not pending_add:
            return
        batch = list(pending_add)
        pending_add = []
        if args.liked:
            call_spotify(sp.current_user_saved_tracks_add, batch)
        else:
            call_spotify(sp.playlist_add_items, playlist_id, batch)
        added.extend(batch)
        added_set.update(batch)
        persist()
        log.info("Записано +%s (всего добавлено %s)", len(batch), len(added))

    to_process = list(reversed(tracks))

    try:
        for idx, track in enumerate(to_process, 1):
            yid = str(track.get("id"))
            label = f"{', '.join(track.get('artists') or [])} — {track.get('title')}"

            if yid in not_found_ids:
                continue

            spotify_id = matched.get(yid)
            if not spotify_id:
                spotify_id = search_track(sp, track)
                time.sleep(args.sleep)
                if not spotify_id:
                    not_found.append(
                        {
                            "id": yid,
                            "title": track.get("title"),
                            "artists": track.get("artists"),
                        }
                    )
                    not_found_ids.add(yid)
                    log.warning("[%s/%s] NOT FOUND: %s", idx, len(to_process), label)
                    persist()
                    continue
                matched[yid] = spotify_id
                persist()
                log.info("[%s/%s] OK: %s", idx, len(to_process), label)

            if spotify_id in added_set or spotify_id in pending_add:
                continue

            pending_add.append(spotify_id)
            if len(pending_add) >= 50:
                flush_add()

        flush_add()
    except LongRateLimit as e:
        flush_add()
        persist()
        hours = e.retry_after / 3600
        log.error(
            "Spotify забанил API на ~%.1f ч (Retry-After=%s с). "
            "Прогресс сохранён в %s. Запусти снова позже: python import_spotify.py",
            hours,
            e.retry_after,
            args.state,
        )
        if playlist_id:
            log.info("Плейлист: https://open.spotify.com/playlist/%s", playlist_id)
        raise SystemExit(2) from e

    report = DATA_DIR / "not_found.json"
    save_json(report, not_found)
    log.info(
        "Готово: найдено %s/%s, не найдено %s. Отчёт: %s",
        len(matched),
        len(tracks),
        len(not_found),
        report,
    )
    if not args.liked and playlist_id:
        log.info("Открыть плейлист: https://open.spotify.com/playlist/%s", playlist_id)


if __name__ == "__main__":
    main()
