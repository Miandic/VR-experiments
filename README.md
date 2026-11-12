# Quest Desktop

Самостоятельное приложение для Meta Quest 3: экран Windows-ПК в passthrough,
в пространстве или на HUD, с управлением руками без контроллеров.
Общих зависимостей с проектом трекинга рук/ЭМГ нет; данные рук используются только для UI.

Версия **0.2.0**, Android ARM64 / IL2CPP / Vulkan. Основной тракт — **QDS2:
GPU capture → NVENC HEVC/H.264 → encrypted UDP/FEC → MediaCodec Surface → композитор Quest**.
JPEG сохранён как явный резерв.

Локально: движущийся тестовый источник 3840×2160 — около **60 FPS** за 120 секунд
после прогрева; реальный рабочий стол 1920×1080 — около **55 FPS**.
Новый видеотракт **ещё не проверен на Quest**. Виртуальный монитор пока не создаётся.
Полное состояние и план: [PROJECT.md](PROJECT.md).

## ПК: установка и запуск QDS2

Windows, NVIDIA GPU с NVENC, Python 3.11+, FFmpeg с ddagrab/NVENC.
Проверено на RTX 2080. PowerShell из корня репозитория:

```powershell
# Только первичная настройка:
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r pc-host\requirements.txt
.\tools\install_ffmpeg.ps1
# Замените пример адреса на LAN IP своего ПК:
.\.venv\Scripts\python.exe pc-host\setup_host.py --lan-host 192.168.0.163

# Обычный запуск с существующими ключами:
.\pc-host\start_video.ps1
# Другой DXGI output / профиль:
.\pc-host\start_video.ps1 -Output 1 -Fps 30 -Bitrate 35
```

`setup_host.py` создаёт сертификат, ключ, токен и connection.json в
`pc-host/private/`. Повторная генерация для обновления APK не нужна.
Папка исключена из Git; файл подключения предоставляет доступ к просмотру экрана.
Для смены адреса исправьте lanHost/remoteHost в существующем файле.

По умолчанию: DXGI output **0**, исходное разрешение, HEVC, 60 FPS, 50 Мбит/с до FEC.
Один зритель, остановка Ctrl+C. Новый output считается с нуля; старый monitor — с единицы.
На нескольких GPU проверьте выбор display. Новый и старый серверы не могут одновременно
занимать TCP 47990.

Для LAN нужны входящие **TCP 47990 и UDP 47991**. Для WAN за NAT — перенаправление
обоих портов; remoteHost — публичный IP/DNS. Firewall/NAT автоматически не меняются,
внешняя сеть пока не проверена. 100 Мбит/с тарифа провайдера не ограничивают LAN.
При отсутствии NAT loopback дома выбирайте LAN.

## Quest: APK и конфигурация

Файл: `QuestDesktop/Builds/QuestDesktop.apk` (исключён из Git).
Для сборки: Unity **6000.3.28f1**, Android Build Support, SDK/NDK/OpenJDK.
Откройте существующий QuestDesktop, дождитесь импорта, выберите **Desktop → 3. Собрать APK**.
Пересоздавать существующую сцену для обычной сборки не требуется.

```powershell
$adb = 'C:\Program Files\Meta VR Developer Hub\resources\bin\adb.exe'
& $adb install --no-streaming -r QuestDesktop\Builds\QuestDesktop.apk
# Запустите приложение один раз, затем:
& $adb push pc-host\private\connection.json /sdcard/Android/data/com.miandic.questdesktop/files/connection.json
```

При нескольких ADB transports добавьте `-s <serial>`.
В приложении: **Reload config → LAN / Internet → Connect**.
IP также редактируется через **Edit IP**; DNS, порт, pin и токен — через файл.
При замене сертификата обновите pin на шлеме.

Дополнительные поля для существующего connection.json (host/token/pin сохранить):

```json
{
  "legacyJpeg": false,
  "videoCodec": "hevc",
  "videoFps": 60,
  "videoBitrateMbps": 50,
  "autoConnect": true
}
```

Это фрагмент, не полный файл подключения. Допустимы hevc/h264, 30/60 FPS, 5–65 Мбит/с.
Отсутствующие video-поля старого JSON получают HEVC/60/50. По умолчанию autoConnect=false;
при true подключение восстанавливается после пробуждения. При pause соединение закрывается.

Разрешение берётся из источника/SPS, включая portrait и смену режима.
Размер VR-окна не уменьшает исходное разрешение. Пределы: чётные стороны 2..8192,
до 32 Мп, плюс реальные возможности NVENC/MediaCodec.
Автоматической адаптации битрейта к Wi-Fi пока нет.

## Управление

- **РЯДОМ** подносит экран для прямого взаимодействия.
- Край — захват щипком/кистью и перемещение; угол — размер с сохранением аспекта.
- Кнопки — палец или луч со щипком. Края/углы также доступны лучом.
- Справа проявляются ползунки яркости и прозрачности HUD.
- **HUD / МИР** переключает привязку; **МЕНЮ** показывает панель подключения.
- **Recenter** возвращает экран перед пользователем; постоянных spatial anchors нет.

Подробности: [управление руками](docs/hand-interaction.md).
Руки и отсутствие мерцания подтверждены пользователем на прежней JPEG-сборке;
после перехода на Surface аппаратную регрессию нужно повторить.

## Проверки без шлема

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
$ffmpeg = '.tools/ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe'
.\.venv\Scripts\python.exe tools\video_simulator.py --ffmpeg $ffmpeg --seconds 120
# Реальный desktop вместо движущегося синтетического источника:
.\.venv\Scripts\python.exe tools\video_simulator.py --ffmpeg $ffmpeg --resolution desktop --seconds 15
```

Симулятор: отдельные временные credentials, loopback, реальные NVENC/NVDEC, UDP/FEC.
Это не замер Quest/Wi-Fi/capture-to-photon. Java receiver из APK тестируется на desktop JVM.
Unity entry point `QuestDesktop.EditorTools.DesktopValidation.RunAndBuild` выполняет регрессии и сборку.

Development APK пишет device-state.json: sampledAtUtc и received/decoded/rendered/dropped.
Проверяйте свежесть: уснувший Quest файл не обновляет.
`tests/check_device.py` пока требует адаптации: он ожидает QDS1 status == "Connected",
не проверяет новые counters и свежесть. Это ещё не автоматическая приёмка QDS2.

## Legacy JPEG

Для прежнего пути задайте legacyJpeg=true и запустите
`.\.venv\Scripts\python.exe pc-host\host.py`. Здесь нужен только TCP 47990;
по умолчанию monitor 1, JPEG 4:4:4 Q97, исходное разрешение.
CPU-прототип давал около 12 FPS и не выполняет цель 4K60.
Тихого отката QDS2 на JPEG нет. Для Unity Editor preview доступен JPEG;
MediaCodec Surface требует Android/Quest.

## Документы

- [PROJECT.md](PROJECT.md) — требования, состояние, структура и план.
- [QDS2](docs/video-pipeline-qds2.md) — архитектура, протокол, ограничения и приёмка.
- [Проверки](docs/validation.md) — актуальный результат и история по версиям.
- [Метрики](docs/video-local-results.json), [сборка и SHA-256](docs/video-build.json).
- [Исследование 12.11](docs/video-streaming-design.md) — архив; первоначальный Sunshine/Moonlight план заменён QDS2.
