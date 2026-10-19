# Авторизация AnimeWatchka на новых компьютерах

Конфигурация развёртывания и секреты владельца в репозиторий не включены. Для собственной сборки разверните свой Worker по инструкции ниже.

Worker пересылает только обмен кода и обновление токенов на фиксированный адрес Shikimori. Секрет хранится в Cloudflare Secrets. Токены не сохраняются на сервере и не журналируются; библиотека остаётся между приложением и Shikimori. Учтите, что владелец сервиса технически получает доступ к передаваемым токенам. Пользовательский вход и разрешение происходят в системном браузере, callback проверяет случайный state.

## Развёртывание владельцем приложения

1. Создать аккаунт Cloudflare и включить Workers. Проверить актуальные условия бесплатного плана. Платный план для этой конфигурации не требуется выбирать автоматически.
2. Из этой папки выполнить `npx wrangler login --device --scopes account:read user:read workers:write workers_scripts:write`, затем `npx wrangler deploy`. Вход по коду устройства использован из-за тайм-аута обычного локального callback Wrangler.
3. Задать `npx wrangler secret put SHIKIMORI_CLIENT_ID` и `npx wrangler secret put SHIKIMORI_CLIENT_SECRET`. Вводить значения интерактивно, не передавать их аргументами команды, не копировать в репозиторий.
4. В OAuth-приложении Shikimori оставить `http://127.0.0.1:17842/callback/`, разрешение `user_rates`.
5. Создать `installer/oauth-public.json` с публичными полями `ClientId`, `TokenBrokerUrl` (реальный HTTPS адрес Worker с `/token`) и `RedirectUri` (адрес выше). Секрета в этом файле быть не должно.
6. Пересобрать через `installer/Build.ps1`, проверить полный вход и обновление токена на чистом Windows-профиле. Не объявлять вход готовым до этой проверки.

Проверки Worker: `node --test worker.test.mjs`.
Документация: https://developers.cloudflare.com/workers/configuration/secrets/ и https://shikimori.io/oauth
