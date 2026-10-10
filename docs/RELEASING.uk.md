# Збирання релізу і публікація на GitHub

Документ для того, хто збирає FreeMon і викладає релізи. Усе робиться на Linux-сервері збірки
(перевірено під openSUSE Tumbleweed; на Debian / Ubuntu / RHEL — так само, відрізняються лише команди встановлення пакетів).

## Що виходить

`./release.sh` створює теку `release/<версія>/`:

| Шлях | Що це |
| --- | --- |
| `assets/freemon-<версія>-win-x64-setup.exe` | інсталятор Windows |
| `assets/freemon-<версія>-win-x64-portable.zip` | портабельна Windows |
| `assets/freemon_<версія>-1_amd64.deb` | пакет Debian / Ubuntu |
| `assets/freemon-<версія>-1.x86_64.rpm` | пакет RHEL / Fedora / openSUSE |
| `assets/freemon-<версія>-linux-x64-portable.tar.gz` | портабельна Linux |
| `assets/SHA256SUMS` | контрольні суми всіх файлів |
| `source/` | файли проєкту для завантаження на GitHub |
| `RELEASE_NOTES.md` | заготовка опису релізу |
| `build/` | проміжні файли (на GitHub не потрібні) |

## Чим що збирається і навіщо Podman

| Що | Чим | Звідки береться |
| --- | --- | --- |
| програма (Linux і Windows) | .NET 10 SDK (`dotnet publish`) | встановлюється один раз (нижче) |
| `.deb` і `.rpm` | nFPM 2.47.0 (MIT) | `release.sh` сам завантажує в `.tools/` і звіряє контрольну суму |
| інсталятор Windows | NSIS (`makensis`) | **в офіційних репозиторіях openSUSE його немає** |
| архіви | `tar`, `python3` | уже є в системі |

**Навіщо Podman.** `makensis` — компілятор інсталяторів Windows — в openSUSE є лише в сторонніх репозиторіях. Щоб не
підключати їх до сервера, `release.sh` запускає `makensis` у тимчасовому контейнері Debian (там NSIS — звичайний
пакет). Контейнер створюється на час збирання і зникає (`--rm`), на сервері нічого не встановлюється, крім самого Podman.
Якщо `makensis` встановлено на сервері (наприклад, на Debian / Ubuntu: `sudo apt install nsis`), Podman не потрібен.
Portainer для збирання не потрібен — це лише веб-інтерфейс для керування контейнерами.

## Підготовка сервера (один раз)

1. **.NET 10 SDK** — у теку інструментів, без змін у системі (приклад для `/appdev`):
   ```
   mkdir -p /appdev/tools
   curl -fL -o /appdev/tools/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
   bash /appdev/tools/dotnet-install.sh --channel 10.0 --install-dir /appdev/tools/dotnet
   ```
   Файл `/appdev/env.sh` (його автоматично підхоплюють `build.sh` і `release.sh`, якщо проєкт лежить у `/appdev/freemon`):
   ```
   export DOTNET_ROOT=/appdev/tools/dotnet
   export PATH="$DOTNET_ROOT:$PATH"
   export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
   ```
   Перевірка: `. /appdev/env.sh && dotnet --version` — має бути `10.0.…`.
2. **Podman:** `sudo zypper install podman`. Перевірка: `podman --version`.
3. **Проєкт** — у `/appdev/freemon` (переносимо окремо).

## Збирання релізу

1. Підняти версію в `FreeMon.csproj`: рядок `<Version>…</Version>`. Номер релізу береться звідти;
   якщо `release/<версія>` вже існує, скрипт зупиниться і нічого не перезапише.
2. Перевірити, що програма збирається і працює: `./build.sh linux-x64`.
3. Зібрати реліз (аргумент — адреса репозиторію, потрапляє в опис пакетів):
   ```
   ./release.sh https://github.com/<user>/freemon
   ```
   Перший запуск довший: завантажуються nFPM і образ Debian для NSIS.
4. Перевірити результат:
   ```
   ls -lh release/<версія>/assets
   cd release/<версія>/assets && sha256sum -c SHA256SUMS
   ```
5. Бажано перевірити пакети на тестових машинах:
   openSUSE — `sudo zypper install ./freemon-<версія>-1.x86_64.rpm`, Debian / Ubuntu — `sudo apt install ./freemon_<версія>-1_amd64.deb`,
   Windows — запустити `setup.exe`.

**Обмеження збірки на Linux:** у `freemon.exe` немає іконки й відомостей про версію у властивостях файлу
(ярлики й інсталятор іконку мають). Повністю правильний exe дає лише `build.cmd` на Windows. Програма не підписана
цифровим підписом — Windows SmartScreen покаже попередження.

## Перша публікація на GitHub (через браузер)

1. **Репозиторій:** github.com → «+» → **New repository** → назва `freemon`, **Public**, галочки README / .gitignore /
   license **не ставити** → **Create repository**.
2. **Код:** на сторінці порожнього репозиторію — посилання **«uploading an existing file»**. Перетягнути у вікно браузера
   **весь вміст** `release/<версія>/source/` (разом із прихованим `.gitignore` і теками `src`, `assets`, `lang`,
   `packaging`, `docs`). Повідомлення — `FreeMon <версія>` → **Commit changes**.
   Обмеження браузера: до 100 файлів за раз і до 25 МБ на файл — проєкт уміщується.
   Якщо файли з сервера по SSH, спершу скопіюйте теку на свій комп'ютер (наприклад, `scp -r` або Dolphin: `fish://` / `sftp://`).
3. **Опис репозиторію:** ⚙ біля «About» → опис, теми (`monitoring`, `ping`, `network`, `avalonia`, `dotnet`).

## Реліз на GitHub (через браузер)

1. **Releases → Create a new release** (або «Draft a new release»).
2. **Choose a tag** → ввести `v<версія>` (наприклад `v1.10.0`) → «Create new tag … on publish». Target — `main`.
3. **Release title:** `FreeMon <версія>`.
4. **Опис:** вставити вміст `release/<версія>/RELEASE_NOTES.md` і дописати розділ «Changes».
5. **Attach binaries:** перетягнути всі файли з `release/<версія>/assets/` (6 файлів).
6. **Set as the latest release** → **Publish release**.

GitHub сам додасть архіви вихідного коду для тегу.

## Наступні релізи

1. Змінити код → підняти `<Version>` → `./release.sh …`.
2. Оновити код на GitHub: **Add file → Upload files** у корені репозиторію, перетягнути вміст нового `source/`
   (файли з тими самими шляхами замінюються). Файли, яких більше немає в проєкті, видалити вручну через сторінку файлу.
3. Створити новий реліз з новим тегом (розділ вище).

Коли проєкт житиме в git на сервері, оновлення коду й релізи робитимуться командами (`git push`, `gh release create`)
замість браузера — це наступний крок після перенесення.
