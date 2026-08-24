# План реализации: ПК → ESP32 (BLE) → VR-шлем (Quest 3, Passthrough)

**PRD:** `prd.md` *(этот репозиторий)*
**Проекты:**
- `E:\proj\esp32-ble-test` — прошивка ESP32 (PlatformIO) + Python-отправщик + управляющий скрипт
- `E:\proj\ble-test-urfu` — Unity 6000.3.10f1 (URP 17.3): VR-приложение, BLE Central + Passthrough + TextMeshPro *(этот репозиторий)*

---

## BLE-контракт (фиксированный, дублируется в оба README)

| Параметр | Значение |
|---|---|
| Имя BLE-устройства | `ESP32-BLE-Text` |
| Service UUID | `4fafc201-1fb5-459e-8fcc-c5c9c331914b` |
| Characteristic UUID | `beb5483e-36e1-4688-b7f5-ea07361b26a8` |
| Свойства характеристики | READ \| NOTIFY (дескриптор BLE2902) |
| Кодировка | UTF-8 |
| Терминатор строки | `\n` (на стороне ПК; `\r` обрезается прошивкой) |
| Команда очистки | `CLR` или пустая строка |
| Макс. длина сообщения | 512 байт |

---

## Этап 1 — ESP32: прошивка, отправщик, инфраструктура

### 1.1. Инициализация репозитория и каркаса
- [x] `git init` в `E:\proj\esp32-ble-test`
- [x] `.gitignore`: `.pio/`, `__pycache__/`, `.venv/`, `*.pyc`, `.vscode/`, `Thumbs.db`
- [x] Структура папок: `src/`, `pc-sender/`
- [x] Скелет `manage.ps1` (управляющий скрипт)
- [x] Первый коммит-каркас

### 1.2. Конфигурация PlatformIO
- [x] `platformio.ini`: board `esp32doit-devkit-v1`, framework `arduino`, monitor_speed 115200, `monitor_filters = esp32_exception_decoder`, upload/monitor port по умолчанию
- [x] Проверка: `pio run` — сборка без ошибок (SUCCESS, 14.5 c)

### 1.3. Прошивка `src/main.cpp`
- [x] Константы контракта (UUID, имя, BAUD=115200, MAX_LEN=512)
- [x] Инициализация: `Serial.begin(115200)`, `BLEDevice::init("ESP32-BLE-Text")`, BLEServer, BLEService, BLECharacteristic (READ|NOTIFY), BLE2902
- [x] Колбэки сервера: `onConnect` (сброс `oldDeviceConnected`), `onDisconnect`
- [x] `loop()`: неблокирующее чтение Serial в line-буфер до `\n`
- [x] Обрезка `\r` (Windows CRLF), фильтр управляющих символов <0x20 (кроме отброшенных), пропуск UTF-8 байтов >0x7F
- [x] Пустая строка / `CLR` → `setValue("")` + notify (FR-6)
- [x] Обычная строка → `setValue(buf, len)` + `notify()` (FR-3)
- [x] Паттерн `oldDeviceConnected`: disconnect → пауза 500 мс → рестарт advertising (FR-5)
- [x] ~~Advertising interval 20–50 мс~~ (используется быстрый advertising ESP32 по умолчанию + `setMinPreferred`)
- [x] Debug-эхо в Serial: `[BLE->] "текст"` (FR-2)
- [x] Сборка `pio run` без ошибок → коммит

### 1.4. Python-отправщик `pc-sender/`
- [x] `requirements.txt`: `pyserial>=3.5`
- [x] `send_text.py`:
  - аргументы: `--port`, `--baud` (default 115200), `--message`, `--clear`, `--list`
  - `--list`: печать доступных COM-портов
  - `--message "..."`: одиночная отправка (UTF-8 + `\n`)
  - `--clear`: отправка `CLR`
  - без `--message`: интерактивный REPL (ввод → Enter → отправка; `exit`/Ctrl+C для выхода)
  - чтение ответов ESP32 в отдельном потоке (эхо), чтобы не блокировать ввод
- [x] Проверка синтаксиса `py_compile` + `--list` → коммит

### 1.5. Управляющий скрипт `manage.ps1`
- [x] Команды: `build`, `upload [-Port]`, `monitor [-Port]`, `flash [-Port]` (build+upload+monitor), `clean`, `ports`, `send [-Port] [-Message]`, `clear [-Port]`, `env`, `setup`
- [x] `$DefaultPort = "COM10"` (Silicon Labs CP210x — определена автоматически); проброс `-Port` в pio/python
- [x] `setup`: создание `.venv` + `pip install -r pc-sender/requirements.txt`
- [x] `env`: проверки pio, python, venv, pyserial
- [x] Автоактивация `.venv` для send/clear; UTF-8 BOM для PowerShell 5.1 → коммит

### 1.6. Документация
- [x] `README.md`: быстрый старт, таблица UUID-контракта, команды manage.ps1, проверка через nRF Connect
- [x] Финальный коммит Этапа 1

### 1.7. Верификация железа
- [x] `pio run -t upload --upload-port COM10` — прошивка загружена (1119120 байт, hash verified)
- [x] Эхо в Serial подтверждено: `[BLE->] "Hello VR"`
- [x] UTF-8 кириллица побайтово без искажений (`d0 9f d1 80 ...` == отправленное)
- [x] `CLR` → пустое значение характеристики
- [x] Проверка BLE Notify на ПК через bleak (`ble_receiver.py`): подписка, приём текста, CLR, reconnect
- [ ] nRF Connect (смартфон): подписка, приём текста, reconnect после разрыва — *ручная проверка владельцем*
- [x] Сквозной тест ПК → ESP32 через python (COM10)

---

## Этап 2 — Unity: BLE Central + Passthrough рендер (`E:\proj\ble-test-urfu`)

### 2.1. Пакеты и XR-настройки
- [x] Установка Meta XR SDK (OpenXR backend) через Package Manager
  - Scoped registry URL исправлен: `https://npm.developer.oculus.com` (был `npm.developer.meta.com` — несуществующий домен)
  - Установлен `com.meta.xr.sdk.core` v205.0.0
- [x] XR Plug-in Management → OpenXR (+ Meta Quest feature group)
  - Meta XR SDK использует собственную систему (OVRManager/OVRCameraRig), а не XR Plug-in Management
  - OVRProjectSetup.FixAllAsync: hand tracking, stereo instancing, HDR off, MSAA 4, AndroidManifest
- [x] Проверка консоли Unity на ошибки компиляции

### 2.2. Сцена
- [x] Сцена `Assets/Scenes/BleTextScene`: XR Origin (Meta XR), Directional Light, EventSystem
- [x] `OVRPassthroughLayer` (Colour) на XR Origin (FR-4)
- [x] `BleTextRoot`: TextMeshPro 3D + подложка, позиция 1.5 м перед камерой, billboard
- [x] Статус-лейбл подключения (TMP: Scanning… / Connected / Reconnecting)

### 2.3. Нативный BLE-мост (JNI)
- [x] Java-класс `BleBridge` (`Assets/Plugins/Android/`): scan по имени `ESP32-BLE-Text`, connect, discoverServices, subscribe Notify (descriptor 0x2902), колбэки в main thread
- [x] Сборка в `.jar`/`.aar` (или java-плагин через Gradle)
- [x] C# `BleClientService.cs`: обёртка через `AndroidJavaObject`, события `OnTextReceived(string)`, `OnStateChanged`, диспетчеризация в Unity main thread

### 2.4. C#-скрипты `Assets/Scripts/Ble/`
- [x] `BleClientService.cs` (см. 2.3)
- [x] `BleReconnector.cs`: таймаут сканирования, перезапуск, backoff, автопереподключение (FR-5)
- [x] `TextDisplayController.cs`: подписка на `OnTextReceived`, обновление TMP, скрытие при `""`/`CLR` (FR-6)
- [x] `HeadLockedText.cs`: размещение 1.5 м от камеры (FR-4)
- [x] Проверка компиляции (read_console: без ошибок)

### 2.5. Настройки Android
- [x] Кастомный `AndroidManifest.xml` (`Assets/Plugins/Android/`): `BLUETOOTH_SCAN` (neverForLocation), `BLUETOOTH_CONNECT`, `CAMERA`
- [x] Player Settings: Min API 29, IL2CPP + ARM64, package name `com.bletest.bletextviewer`
- [x] URP-настройки под Quest (рекомендации Meta)

---

## Этап 3 — Интеграция и приёмка (PRD п.7)

- [ ] Build APK → Quest 3 → сквозной тест `Hello VR` (п.7.1–7.5)
- [ ] Обновление текста без перезапуска приложений (п.7.6)
- [ ] Разрыв BLE (выключить ESP32) → автопереподключение → текст снова приходит
- [ ] `CLR` → надпись скрывается
- [ ] UTF-8/кириллица, длинные строки
- [ ] Замер латентности < 300–500 мс
- [ ] Финальные README в обоих репозиториях

---

## Журнал выполнения

*(обновляется по ходу работ)*

| Дата | Этап | Что сделано |
|---|---|---|
| 2026-08-24 | 1.1–1.6 | Создан проект, git init, platformio.ini, прошивка main.cpp (сборка SUCCESS), pc-sender/send_text.py, manage.ps1 (env/ports/help проверены), README.md |
| 2026-08-24 | 1.7 | Прошивка загружена в плату (COM10, CP210x). Эхо Serial, UTF-8 без искажений (побайтовая сверка), CLR работает. nRF Connect — ожидает ручной проверки |
| 2026-08-24 | — | План перенесён в Unity-репозиторий `ble-test-urfu`; пути обновлены (D:\proj → E:\proj); зафиксировано: Meta XR registry уже настроен, Min SDK 25→29 требуется |
| 2026-08-24 | 2.1 | Meta XR Core SDK v205.0.0 установлен; scoped registry URL исправлен (npm.developer.oculus.com); OVRProjectSetup: hand tracking, stereo instancing, HDR off, MSAA 4 |
| 2026-08-24 | 2.2 | BleTextScene: OVRCameraRig, OVRManager, OVRPassthroughLayer, Directional Light, EventSystem, BleTextRoot (TMP 1.5m), StatusLabel |
| 2026-08-24 | 2.3 | BleBridge.java (scan/connect/notify), BleClientService.cs (AndroidJavaObject wrapper) |
| 2026-08-24 | 2.4 | BleReconnector.cs (auto-reconnect backoff), TextDisplayController.cs (TMP+status), HeadLockedText.cs (1.5m billboard) |
| 2026-08-24 | 2.5 | AndroidManifest: BLUETOOTH_SCAN/CONNECT/CAMERA; Player Settings: Min API 29, IL2CPP, ARM64, package com.bletest.bletextviewer |
