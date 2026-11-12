# Аппаратный видеотракт QDS2 — 13 ноября 2026

В APK добавлен новый основной путь; `legacyJpeg=true` явно включает прежний QDS1.
Шлем при разработке отключён: сборка и локальные проверки выполнены, но новый
MediaCodec/Surface путь **ещё не проверен на устройстве**. Старое подтверждение
отсутствия мерцания относится к предыдущему APK.

## Архитектура

```text
Выбранный display Windows (физический или уже установленный виртуальный)
 → D3D11 Desktop Duplication / FFmpeg ddagrab (GPU frame)
 → NVENC HEVC Main / H.264, 8-bit 4:2:0, без B-frames/lookahead
 → Annex-B access units → AES-256-GCM → UDP, FEC 8+1
 → независимый Java network worker → ограниченная очередь compressed frames
 → аппаратный Android MediaCodec → Android Surface
 → OVROverlay Underlay → композитор Quest
```

Пиксели не читаются обратно на CPU ни для захвата, ни для показа. Python/Java
обрабатывают только сжатые данные. Нет JPEG/BitmapFactory/RGBA-массивов,
`LoadRawTextureData`, `Texture.Apply` или зависимости получения кадра от Unity Update.
Старый JPEG-код сохранён исключительно для явного выбора совместимости.

Vulkan сохранён. Собственный native Vulkan/GLES bridge не нужен: установленный
Meta SDK поддерживает external Surface у OVROverlay. Видео — underlay с depth=0;
passthrough SDK использует int.MaxValue, Unity-кнопки и курсоры находятся над видео.
Прямоугольник UI остаётся геометрической целью захватов, но не рисует видеотекстуру.
Размер/поза слоя повторяют его трансформ; HUD наследует head anchor. Яркость и alpha
передаются через per-layer color scale с premultiplied alpha.
Правильность порядка слоёв/ориентации/прозрачности нужно подтвердить на Quest.

При смене размеров или отключении Surface освобождается только после подтверждения
декодирующего потока. Epoch handshake защищает также гонку создания MediaCodec.
Ожидание не блокирует Unity main thread. Счётчики полученных, декодированных и
переданных Surface кадров разделены; FPS в HUD берётся из onFrameRendered, а не VR FPS.

## Разрешение и режимы

- Нет фиксированного 1920×1080/3840×2160 в протоколе или UI. Источник передаётся
  в исходном разрешении; точный видимый размер читается из SPS HEVC/H.264 с учётом crop.
- Поддерживаются широкие, 16:10 и портретные пропорции. Захваты за углы используют
  текущий аспект потока. Изменение физического размера VR-экрана не уменьшает исходник.
- При изменении режима display encoder перезапускается с новым размером и IDR;
  соединение и последовательность пакетов сохраняются. Клиент пересоздаёт Surface/decoder.
- Ограничения: чётные размеры для 4:2:0, стороны 2..8192, не более 32 мегапикселей;
  фактический предел дополнительно определяется NVENC и проверкой MediaCodec capabilities.
  Ошибка неподдерживаемого режима показывается явно; программного decoder fallback нет.
- HEVC по умолчанию; H.264 выбирается в конфигурации. 60 FPS по умолчанию, 30 — явный
  резерв. Битрейт 50 Мбит/с по умолчанию, допустимая настройка 5–65 Мбит/с до FEC.
- Разрешение и геометрия адаптируются автоматически. Автоматического управления
  битрейтом по Wi-Fi congestion сейчас нет; снижать его можно настройкой профиля.

## Транспорт и безопасность

QDS2 использует TCP 47990 для TLS control и UDP 47991 для видео.
TLS 1.2: pin SHA-256 сертификата + существующий токен. На каждое подключение сервер
создаёт новый 256-битный AES key и 32-битный nonce prefix и передаёт их внутри TLS.
Nonce = prefix || uint64(sequence); sequence не переиспользуется в сессии.
До начала отправки сервер проверяет UDP-пробу HMAC-SHA256(key, `QDS2 hello`) и адрес
того же клиента TLS. Это также обнаруживает внешний UDP endpoint через NAT.
Токен и ключи не выводятся в диагностике.

Каждый access unit шифруется целиком AES-GCM. AAD: sequence uint32, width/height uint16,
flags uint8, PTS uint64, big-endian. Изменённые данные/размер/PTS не попадают в decoder.
Шифротекст делится на фрагменты по 1152 байта; полный UDP payload не более 1181 байта.
Заголовок (29 байт): `!4sIHHBQHHI` = QDV2, sequence, width, height, flags, PTS,
index, data-shard count, ciphertext length. Flags bit0 — keyframe.
PTS — микросекунды host monotonic от начала сессии, **не сквозная задержка**.

После каждых восьми data shards передаётся XOR parity (индекс count+group), включая
последнюю неполную группу. Это восстанавливает одну потерю в каждой группе, без RTT
и ретрансляций. Parity покрывает ciphertext; успешное восстановление всё равно
требует валидного GCM tag. Overhead около 12.5%, выше на очень маленьких кадрах.

- Максимальный AU 4 MiB, до 8 сборок; незавершённые сборки старше 100 мс удаляются.
- Replay window ограничен последними восемью frame sequences; дубликаты не декодируются.
- Перестановка готовых кадров ожидается максимум 12 мс / четыре готовых кадра.
- Очередь перед decoder — три AU. При переполнении она очищается, зависимые P-кадры
  пропускаются до следующего IDR. При пропуске sequence действует та же политика.
- IDR каждые полсекунды. При невосстановимой потере возможна пауза до следующего IDR;
  это ограничение текущей версии, request-IDR/ARQ ещё не реализованы.
- Heartbeat раз в секунду; при потере control более пяти секунд сервер прекращает
  кодирование. Клиент прекращает сессию при отсутствии валидного видео 10 секунд.

Annex-B parser определяет конец AU по следующему AUD; это добавляет до одного
интервала видеокадра. Дальнейшее уменьшение задержки возможно через framed packet API
libav/NVENC вместо stdout. Текущая реализация не заявляет абсолютный минимум latency.

## Почему реализация отличается от первоначального исследования

Sunshine/Moonlight оставался предпочтительным готовым стеком до проверки SDK.
External Surface убрал необходимость копирования в Unity-текстуру. Для текущей
замены сделан автономный QDS2 host/client, сохраняющий provisioning приложения и
не добавляющий GPL Moonlight в APK. Sunshine/Moonlight не установлены и не используются.
Цена этого решения — собственная поддержка сетевого протокола; пока нет динамического
bitrate control, ARQ, аудио и ввода мыши/клавиатуры. FFmpeg используется как отдельный
процесс; его лицензия и notices находятся в portable дистрибутиве и должны сохраняться
при его распространении. В Git бинарники FFmpeg не включаются.

## Запуск

Из корня репозитория, с уже созданными `pc-host/private`:

Обычный запуск: `./pc-host/start_video.ps1`; параметры `-Output 1 -Fps 30 -Bitrate 35`.
Первичная установка Python-зависимостей и provisioning описаны в [README](../README.md#пк-установка-и-запуск-qds2).
Ниже — прямой запуск для дополнительных параметров и тестового источника.

```powershell
# При отсутствии portable FFmpeg:
./tools/install_ffmpeg.ps1
$ffmpeg = '.tools/ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe'
./.venv/Scripts/python.exe pc-host/video_host.py --ffmpeg $ffmpeg
# Другой DXGI output, 30 FPS / меньший битрейт:
./.venv/Scripts/python.exe pc-host/video_host.py --ffmpeg $ffmpeg --output 1 --fps 30 --bitrate 35
```

`--output` — **нулевой DXGI индекс**. На системах с несколькими GPU проверить выбранный
display и что он доступен NVIDIA capture/encoder. MSS здесь только перечисляет display
для обнаружения изменений, пикселей не захватывает; SPS авторитетнее MSS для размера.
Прежний `host.py` не должен одновременно занимать TCP 47990. Ключи не пересоздавать.
Для LAN нужны TCP 47990 и UDP 47991; для WAN перенаправить оба порта на ПК.
Правила firewall/NAT автоматически не менялись; WAN пока не проверялся.

Дополнительные поля `connection.json` (старые token/pin/host/port сохранить):

```json
{"legacyJpeg": false, "videoCodec": "hevc", "videoFps": 60, "videoBitrateMbps": 50}
```

Для старого сервера `host.py` установить `legacyJpeg=true`. Тихого отката с аппаратного
потока на JPEG нет. В редакторе Unity Surface playback недоступен; для preview выбрать
JPEG, для новой сети/кодеков использовать симулятор ниже.

## Локальные проверки

Замеры на RTX 2080 / Windows 11, 13.11.2026 (округление до 0.1 FPS):

| Источник / сценарий | Длительность | Decode FPS после прогрева | Результат |
|---|---:|---:|---|
| HEVC 3840×2160, движущийся testsrc2 | 120 с | 60.0 | 7177 декодированных, 25 пропущено при запуске; после прогрева пропусков нет |
| HEVC 2560×1600 | 15 с | 59.9 | Нативные пропорции 16:10 |
| H.264 1080×1920 | 15 с | 60.0 | Portrait, резервный codec |
| HEVC 4K, случайные потери 0.1% UDP | 20 с | 59.2 | 85 пакетов удалено стендом; 1 невосстановимый AU, восстановление на IDR, без decode corruption |
| Реальный GPU desktop 1920×1080 | 15 с | 54.7 | Без потерянных AU/очередей; аналогичный предел 53–55 FPS у отдельно запущенного ddagrab/NVENC без сети |
| 1080p → 2560×1600 → portrait → 1080p | 18 с | Не benchmark FPS | Все три размера прошли один сеанс с NVDEC, без steady queue drops |

Синтетический источник при старте/перезапуске может выдавать burst; его среднюю
частоту за весь короткий тест нельзя считать устойчивой FPS. Для таблицы взят
steady decode interval после двух секунд прогрева. При смене режимов отдельные
bursts исключают смысл общей средней FPS. Новый `setpts` задаёт уникальные PTS,
не создавая видеокадров и не копируя GPU-пиксели; устраняет коллизии DTS ddagrab
при квантовании в encoder timebase. Реальный desktop пока около 55, а не обещанные 60.
Минимум 30 на ПК выполнен; целевые 60 desktop и Quest требуют дальнейшего измерения.

Сводные исходные метрики: [video-local-results.json](video-local-results.json).

```powershell
./.venv/Scripts/python.exe -m unittest discover -s tests -v
./.venv/Scripts/python.exe tools/video_simulator.py --ffmpeg $ffmpeg --seconds 120 --output .device-check/qds2-4k-sustained.json
./.venv/Scripts/python.exe tools/video_simulator.py --ffmpeg $ffmpeg --resolution desktop --seconds 15
./.venv/Scripts/python.exe tools/video_simulator.py --ffmpeg $ffmpeg --resolution 1920x1080,2560x1600,1080x1920 --seconds 18
./.venv/Scripts/python.exe tools/video_simulator.py --ffmpeg $ffmpeg --loss 0.001 --seconds 20
```

Симулятор создаёт временные отдельные credentials, слушает только loopback, проверяет
TLS pin, реально получает/расшифровывает UDP и декодирует через NVDEC. Захваченные
изображения не сохраняются. Синтетический testsrc2 движется, поэтому тест не сводится
к повторной передаче статичного кадра. Cold start decoder учитывается отдельно.
FFmpeg warning `not enough frames to estimate rate` ожидаем при минимальном probesize;
это не ошибка декодирования. Результаты описывают ПК, не Quest/Wi-Fi/задержку экрана.

`tests/test_video.py` запускает **точный VideoPackets.java из APK** через desktop JDK:
шифрование Python → Java decode, потери каждого восьмого фрагмента, перестановки,
дубли, короткий последний shard, повреждённая аутентификация, разные размеры.
Также проверяются Python reference receiver, лимиты, replay, восстановление после
невосстановимого AU и Annex-B parser. Старые TLS/JPEG тесты сохранены.
Unity Editor regressions проверяют реальные обработчики захватов, углов, poke и HUD.

## Что остаётся для приёмки на шлеме

Перед автоматической приёмкой адаптировать `tests/check_device.py`: он ещё ожидает
точное значение QDS1 `Connected`. В QDS2 статус содержит codec/hardware; нужны
проверка свежести `sampledAtUtc` и новые video counters. JPEG `decodeMs`/`bitmapDecodeMs`
не измеряют аппаратный decoder. В отчёте нельзя считать старые снимки свежими кадрами.

1. Установить новый APK, запустить video_host.py, проверить выбранный hardware decoder,
   received/decoded/rendered FPS, CPU/thermal и 10-минутный 4K60 прогон.
2. Проверить ориентацию, цветовой диапазон, цветной мелкий текст (4:2:0), passthrough,
   HUD alpha 0/0.5/1, яркость, обе глаза, повороты головы и все жесты.
3. Смена разрешения, разрыв Wi-Fi, sleep/resume, быстрые Connect/Disconnect без утечки Surface.
4. Отдельно измерить capture-to-photon latency; host monotonic PTS и Java timestamps
   без синхронизации часов нельзя вычитать как end-to-end latency.
5. Виртуальный монитор: драйвер пока не установлен. Новый host уже захватывает
   произвольный существующий DXGI display, но сам дополнительных мониторов не создаёт.

Источники: [FFmpeg ddagrab](https://ffmpeg.org/ffmpeg-filters.html#ddagrab),
[NVENC FFmpeg low latency](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/ffmpeg-with-nvidia-gpu/index.html),
[Meta OVROverlay external Surface](https://developers.meta.com/vr/reference/unity/v201/class_o_v_r_overlay/).
Фактически использована реализация OVROverlay установленного SDK 209 из PackageCache.
