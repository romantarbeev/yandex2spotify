# Яндекс.Музыка → Spotify (локальный пайплайн)

Экспорт «Мне нравится» через неофициальное API (`yandex-music`) и импорт в Spotify (`spotipy`) с сохранением прогресса.

## Быстрый старт

```powershell
cd O:\local_project
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
copy .env.example .env
```

### 1. Токен Яндекса

```powershell
python get_yandex_token.py
```

Откроется ссылка и код подтверждения — токен запишется в `.env` как `YANDEX_TOKEN`.

Альтернатива: [инструкция по токену](https://ym.marshal.dev/token) (браузер / расширение).

### 2. Spotify Developer App

1. Создай приложение: https://developer.spotify.com/dashboard  
2. Settings → **Redirect URIs** → добавь `http://127.0.0.1:8888/callback`  
3. Скопируй **Client ID** и **Client Secret** в `.env`

В Development Mode Spotify иногда требует добавить свой email в Users списка приложения.

### 3. Экспорт

```powershell
python export_likes.py
# тест на 20 треках:
python export_likes.py --limit 20
```

Файл: `data/likes.json`

### 4. Импорт

В новый плейлист:

```powershell
python import_spotify.py
```

В Liked Songs (сердечко):

```powershell
python import_spotify.py --liked
```

Тест:

```powershell
python import_spotify.py --limit 20
```

При коротком rate limit (HTTP 429) скрипт ждёт и продолжает. Если Spotify вешает длинный бан (~сутки) — сохраняет прогресс в `data/import_state.json` и выходит; запусти снова позже:

```powershell
python import_spotify.py
```

По умолчанию пауза между search ≈ 1.2 с (медленнее, но реже банят). Ускорить/замедлить: `--sleep 2`.

Не найденные треки: `data/not_found.json`.

## Структура

| Файл | Назначение |
|------|------------|
| `get_yandex_token.py` | OAuth Device Flow → `.env` |
| `export_likes.py` | Яндекс → `data/likes.json` |
| `import_spotify.py` | JSON → Spotify playlist / Liked |
| `.env` | секреты (не коммитить) |

## Замечания

- Совпадение по ISRC (если есть), иначе `track + artist`. Русские/редкие треки часто не находятся в каталоге Spotify — смотри `not_found.json`.
- Один Spotify app быстро упирается в rate limit на больших коллекциях (сотни–тысячи треков). Импорт может занять время; resume уже встроен.
- Токен Яндекса = доступ к аккаунту Музыки. Не светить его и не класть в git.
