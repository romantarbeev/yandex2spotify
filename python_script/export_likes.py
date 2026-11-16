"""Экспорт плейлиста «Мне нравится» из Яндекс.Музыки в JSON."""

from __future__ import annotations

import argparse
import json
import os
from datetime import datetime, timezone
from pathlib import Path

from dotenv import load_dotenv
from yandex_music import Client

DATA_DIR = Path(__file__).resolve().parent / "data"
DEFAULT_OUT = DATA_DIR / "likes.json"


def track_to_dict(track) -> dict:
    artists = [a.name for a in (track.artists or []) if a and a.name]
    albums = []
    year = None
    if track.albums:
        for album in track.albums:
            if album and album.title:
                albums.append(album.title)
            if year is None and album is not None:
                year = getattr(album, "year", None)

    # В актуальном API ISRC часто отсутствует — поле оставляем на будущее
    isrc = None
    for attr in ("external_id", "external_ids"):
        raw = getattr(track, attr, None)
        if isinstance(raw, dict):
            isrc = raw.get("isrc") or raw.get("ISRC")
            if isrc:
                break

    return {
        "id": str(track.id),
        "title": track.title,
        "artists": artists,
        "albums": albums,
        "duration_ms": track.duration_ms,
        "isrc": isrc,
        "year": year,
    }


def export_likes(token: str, limit: int | None = None) -> list[dict]:
    client = Client(token).init()
    likes = client.users_likes_tracks()
    if not likes or not likes.tracks:
        return []

    short = likes.tracks
    if limit is not None:
        short = short[:limit]

    ids = [f"{t.id}:{t.album_id}" if t.album_id else str(t.id) for t in short]
    # API принимает пачки; берём по 50
    tracks: list = []
    for i in range(0, len(ids), 50):
        batch = client.tracks(ids[i : i + 50])
        tracks.extend(batch or [])

    # Сохраняем порядок «Мне нравится» (новые сверху, как в Яндексе)
    by_id = {str(t.id): t for t in tracks if t}
    ordered = []
    for t in short:
        full = by_id.get(str(t.id))
        if full:
            ordered.append(track_to_dict(full))
    return ordered


def main() -> None:
    load_dotenv()
    parser = argparse.ArgumentParser(description="Экспорт «Мне нравится» в JSON")
    parser.add_argument("-o", "--output", type=Path, default=DEFAULT_OUT)
    parser.add_argument("--limit", type=int, default=None, help="Только первые N треков (тест)")
    args = parser.parse_args()

    token = os.getenv("YANDEX_TOKEN", "").strip()
    if not token:
        raise SystemExit("Нет YANDEX_TOKEN. Запустите: python get_yandex_token.py")

    tracks = export_likes(token, limit=args.limit)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "source": "yandex_music",
        "playlist": "Мне нравится",
        "exported_at": datetime.now(timezone.utc).isoformat(),
        "count": len(tracks),
        "tracks": tracks,
    }
    args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Экспортировано {len(tracks)} треков → {args.output}")


if __name__ == "__main__":
    main()
