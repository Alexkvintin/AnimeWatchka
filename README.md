# AnimeWatchka

Приложение для Windows 10/11: библиотека аниме, источники озвучки, встроенный плеер и персональное оформление.

**Текущая тестовая версия: 0.1.16.** Репозиторий содержит исходники и тесты. Личные настройки, ключи, базы, журналы и готовые установщики в Git не хранятся.

## Возможности

- Библиотека и синхронизация Shikimori, поиск, история и расписание источников.
- AnimeVost, AniLiberty, Glass Moon и FanVoxUA, локальные файлы.
- Плеер mpv и совместимый режим VLC; выбор качества, продолжение просмотра, полный экран и трей.
- Светлая, тёмная и дополнительные темы, Луми, пользовательские PNG/GIF-персонажи.
- Три пресета оформления и обмен файлом `.awtheme` со встроенными изображениями.
- Скачивание выбранной серии, отмена, очистка недокачанных файлов и офлайн-просмотр.

## Сборка с нуля

Требуются Windows x64, [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0), Git и [7-Zip](https://www.7-zip.org/) для подготовки mpv. Для установщика нужен [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
git clone https://github.com/Alexkvintin/AnimeWatchka.git
cd AnimeWatchka
./tools/Prepare-Native.ps1
dotnet restore AnimeApp.slnx
dotnet build AnimeApp.slnx -c Release
dotnet test AnimeApp.Tests/AnimeApp.Tests.csproj -c Release --filter 'Category!=Live&Category!=Native&Category!=AccountLive'
dotnet run --project AnimeApp/AnimeApp.csproj
```

`Prepare-Native.ps1` получает закреплённую сборку mpv из upstream и проверяет SHA256 DLL. Файл размером около 115 МиБ не включён в Git. VLC, Avalonia и остальные зависимости восстанавливаются через NuGet.

Для входа Shikimori разработчику нужно настроить собственное OAuth-приложение и сервис обмена токенов: [инструкция](auth-worker/README.md). Без этого доступны гостевой режим и просмотр. Никогда не добавляйте Client Secret в клиент, установщик или репозиторий.

## Установщик

Скопируйте `installer/oauth-public.example.json` в `installer/oauth-public.json` и укажите публичные параметры своего приложения, если нужен вход в аккаунт. Этот локальный файл исключён из Git.

```powershell
./installer/Build.ps1 -Compiler 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
```

Результат находится в `artifacts/installer/`. Закройте приложение, включая трей, перед обновлением. Стабильный AppId позволяет устанавливать новую версию поверх предыдущей с сохранением данных.

## Документация

- [Состояние проекта и следующие шаги](docs/ROADMAP.md)
- [Изменения последних версий](CHANGELOG.md)
- [Работа с исходниками](CONTRIBUTING.md)
- [Компоненты и лицензии](AnimeApp/Assets/Licenses/THIRD-PARTY.txt)

## Текущие ограничения

Скачивается одна серия за раз; возобновления загрузки, защищённого HLS и отдельных дорожек пока нет. HLS нужно переносить всей папкой серии. Офлайн-просмотр пока не отправляет прогресс в Shikimori. Анимированные фоны ещё в плане; GIF-персонажи работают. Android версия планируется.

Anime4K требует рабочего mpv и подходящего графического драйвера; в совместимом VLC оно недоступно. Совместимость со старым оборудованием проверяется. Доступность внешних источников может меняться.
