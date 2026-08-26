# BLE-Test: Передача текста с ESP32 в VR-шлем Quest 3 через Bluetooth Low Energy

Прототип передачи текста с ESP32 в VR-шлем Meta Quest 3 по каналу **Bluetooth Low Energy (BLE)**. Канал связи со шлемом — **Bluetooth**.

## Архитектура

```
ESP32 (BLE Peripheral) ──BLE──> Quest 3 (Unity + Passthrough + TextMeshPro)
```

| Узел | Роль | Интерфейс |
|------|------|-----------|
| ESP32 | BLE Peripheral (источник текста) | Arduino → BLE Server → Notify |
| Quest 3 | Рендер | Unity + BLE Central → TextMeshPro 3D в Passthrough |

**Контракт BLE-связи:**

| Параметр | Значение |
|---|---|
| Имя BLE-устройства | `ESP32-BLE-Text` |
| Service UUID | `4fafc201-1fb5-459e-8fcc-c5c9c331914b` |
| Characteristic UUID | `beb5483e-36e1-4688-b7f5-ea07361b26a8` |
| Свойства характеристики | READ + NOTIFY (дескриптор CCCD 0x2902) |
| Кодировка | UTF-8 |
| Терминатор строки | `\n` |
| Команда очистки | `CLR` или пустая строка |
| Макс. длина сообщения | 512 байт |

## Требования

### Оборудование
- Meta Quest 3 (Android 12+, Snapdragon XR2 Gen 2)
- ESP32 DevKit (COM-порт через USB)

### Программное обеспечение
- Unity 6000.3.10f1 + URP 17.3
- Meta XR Core SDK 205.0 + OpenXR
- TextMeshPro
- PlatformIO (для прошивки ESP32)
- Python 3.10+ с `pyserial` (для отправки текста через Serial)

## Структура репозитория

```
ble-test/
├── Assets/
│   ├── Plugins/Android/
│   │   ├── AndroidManifest.xml          # BLE permissions
│   │   └── BleBridge.java              # Нативный BLE Central (Android API)
│   ├── Scenes/
│   │   └── BleTextScene.unity           # Основная сцена
│   ├── Scripts/Ble/
│   │   ├── BleClientService.cs          # C# обёртка BLE-моста
│   │   ├── BleReconnector.cs            # Авто-переподключение
│   │   ├── TextDisplayController.cs     # Обновление TextMeshPro по BLE
│   │   └── HeadLockedText.cs            # Billboard: текст перед камерой
│   ├── XR/                              # OpenXR настройки
│   └── Settings/                        # URP Profile
├── firmware/
│   ├── platformio.ini
│   └── src/
│       └── ble/
│           └── main.cpp                 # ESP32 BLE Peripheral прошивка
├── prd.md
└── README.md
```

## Быстрый старт

### 1. Прошивка ESP32

```bash
cd firmware
pio run -e ble -t upload --upload-port COM10
```

Проверка: `pio device monitor` — должно вывестись `[BLE] Advertising started`.

### 2. Сборка Unity APK

1. Открыть проект в Unity 6000.3.10f1
2. Убедиться что `activeInputHandler = 0` (Legacy Input Manager) в ProjectSettings
3. Убедиться что OpenXR настроен (Assets/XR/Loaders/OpenXRLoader.asset)
4. File → Build → Android (IL2CPP, ARM64, Min API 29)
5. APK: `Builds/Android/ble-test.apk`

### 3. Установка на Quest 3

```bash
adb install -r Builds/Android/ble-test.apk
adb shell am start -n com.bletest.bletextviewer/com.unity3d.player.UnityPlayerGameActivity
```

### 4. Отправка текста

```bash
python tools/sender/send_text.py --port COM10
# Интерактивный режим: ввод строки → Enter → текст в шлеме
```

Или напрямую через Serial:
```bash
python -c "import serial; s=serial.Serial('COM10',115200); s.write(b'Hello VR!\n'); s.close()"
```

## Ключевые компоненты

### BleBridge.java — Нативный BLE Central
- Сканирование по имени `ESP32-BLE-Text`
- Подключение к GATT-серверу, подписка на Notify
- Колбэки в Unity main thread через `UnitySendMessage`

### BleClientService.cs — C# обёртка
- Инициализация `BleBridge` через `AndroidJavaObject`
- События: `TextReceived`, `StateChanged`, `Error`
- Singleton с `DontDestroyOnLoad`
- Запрос BLE-разрешений при старте (Android 12+)

### BleReconnector.cs — Авто-переподключение
- Exponential backoff: 2с → 4с → 8с → ... → 30с макс.

### TextDisplayController.cs — Отображение текста
- Обновление TextMeshPro по BLE-событиям
- Скрытие при `CLR` или пустой строке
- Цветовой индикатор статуса

### HeadLockedText.cs — Billboard
- Текст на расстоянии 1.5 м от камеры, плавное слежение за взглядом

## Known Issues

| Элемент | Статус |
|---------|--------|
| BLE скан и подключение | Рабочий |
| Notify подписка | Рабочий |
| Отображение текста в Passthrough | Рабочий |
| Авто-переподключение | Рабочий |
| UTF-8 / кириллица | Поддерживается |
| Задержка end-to-end | ~300-500 мс (BLE typical) |

## Лицензия

Прототип для исследовательских целей (УрФУ).
