# AnimeVost fixtures

Минимальные фрагменты обычных публичных ответов, исследованных 2026-10-05.

- `search.html`: результаты POST `https://animevost.org/index.php?do=search` (`do=search`, `subaction=search`, `story=Frieren`).
- `frieren.html`: `https://animevost.org/tip/tv/3044-sousou-no-frieren.html`, заголовок, год, число серий, `var data` с 28 настоящими episode ID, путь штатного frame2.
- `movie.html`: `https://animevost.org/tip/polnometrazhnyy-film/1742-kimi-no-na-wa-tvoe-imya132.html`, единственный пункт «Фильм».
- `frame.html`: минимальная структура штатного iframe frame2 → frame5.
- `media.html`: структура опубликованных 480p/720p ссылок. Временные CDN-адреса заменены адресами example; параметры подписей не сохранены.

Реклама, описание произведений, оформление и сторонние скрипты удалены. Fixtures не запускают JavaScript и не требуют сети. Malformed/empty/ongoing варианты строятся тестами на основе этих фрагментов. Ни credentials, ни cookies, ни способы обхода защиты не используются.
