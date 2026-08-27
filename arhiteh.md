# Архитектура проекта BLE-Text-Display

## Обзор

Проект реализует сквозной пайплайн передачи текста по Bluetooth Low Energy (BLE) от ESP32 через Android-мост до VR-шлема Quest 3 с отображением в AR/VR (Passthrough). Включает систему измерения RTT (Round-Trip Time) и jitter для профилирования задержек BLE-канала.

---

## Системная архитектура

```
┌─────────────┐    BLE notify     ┌──────────────────────┐   UnitySendMessage   ┌─────────────────┐
│   ESP32      │ ──────────────→  │  BleBridge.java      │ ──────────────────→  │ BleClientService│
│  (Peripheral)│ ←──────────────  │  (Android Native)    │ ←──────────────────  │ (Unity C#)      │
│              │    write ACK     │  GATT Client          │   writeCharacteristic│                 │
└─────────────┘                   └──────────────────────┘                      └────────┬────────┘
                                                                                         │
                                          ┌──────────────────────────────────────────────┤
                                          │                Events                        │
                                          ▼                ▼              ▼               ▼
                                   TextReceived    AckReceived    RttMessageReceived  ResultsReceived
                                          │                │              │               │
                                          ▼                ▼              ▼               ▼
                                   TextDisplay    LatencyProfiler (отправка ACK)   LatencyProfiler
                                   Controller                                    (парсинг результатов)
```

---

## Компоненты

### 1. ESP32 Firmware (`firmware/src/ble/main.cpp`)

**Микроконтроллер:** ESP32-D0WD-V3 (DevKit V1)
**Фреймворк:** Arduino + ESP32 BLE Library
**Сборка:** PlatformIO (`pio run -e ble --target upload`)

#### BLE-сервис

| Параметр | Значение |
|----------|----------|
| Device Name | `ESP32-BLE-Text` |
| Service UUID | `4fafc201-1fb5-459e-8fcc-c5c9c331914b` |
| Characteristic UUID | `beb5483e-36e1-4688-b7f5-ea07361b26a8` |
| Properties | READ + NOTIFY + WRITE |
| Макс. длина | 512 байт |
| Терминатор | `\n` |

#### Режимы работы

1. **Serial → BLE:** Ввод с Serial монитора пересылается как BLE notification
2. **CLR:** Очистка текста на дисплее
3. **RTT-тестирование:** Принимает команду `TEST:<rate>`, отправляет 100 сообщений, собирает ACK, вычисляет RTT

#### RTT-протокол

```
ESP32 → Unity:  RTT:<seq>,<T0>          (notify, T0 = millis())
Unity → ESP32:  ACK:<seq>               (write)
ESP32 → Unity:  RESULTS:<rate>,<avg>,<min>,<max>,<jitter>,<recv>/<total>
```

- Каждое RTT-сообщение содержит порядковый номер и временную метку ESP32
- При получении ACK, ESP32 вычисляет RTT = T_received - T0
- После отправки всех 100 сообщений, ESP32 вычисляет статистику и отправляет RESULTS
- Jitter вычисляется как stddev интервалов между приходом ACK

#### Память

- `sentTimestamps[100]` — времена отправки каждого сообщения
- `receivedAcks[100]` — времена получения ACK
- `ackReceived[100]` — флаги получения ACK
- Serial buffer: 512 байт

---

### 2. Android BLE Bridge (`Assets/Plugins/Android/BleBridge.java`)

**Пакет:** `com.bletest.blebridge`
**Цель:** Мост между нативным Android BLE API и Unity C#

#### Инициализация

```java
BleBridge(Context context, String callbackObjectName)
// callbackObjectName = gameObject.name для UnitySendMessage
```

#### Методы

| Метод | Описание |
|-------|----------|
| `startScan()` | Сканирование с фильтром по имени устройства, 30s таймаут |
| `stopScan()` | Остановка сканирования |
| `disconnect()` | Отключение от GATT-сервера |
| `writeCharacteristic(byte[] data)` | Запись данных в characteristic (ACK, TEST) |
| `isBluetoothAvailable()` | Проверка включенного Bluetooth |
| `cleanup()` | Очистка ресурсов |

#### BLE-процесс

1. **Scan:** `SCAN_MODE_LOW_LATENCY`, фильтр по `DEVICE_NAME`
2. **Connect:** `connectGatt(context, false, callback, TRANSPORT_LE)`
3. **Discover:** `discoverServices()` → поиск SERVICE_UUID → CHARACTERISTIC_UUID
4. **Subscribe:** `setCharacteristicNotification(true)` + запись CCCD дескриптора
5. **MTU:** `requestMtu(512)` для больших RTT-сообщений

#### Маршрутизация callbacks

```java
// onCharacteristicChanged:
if (text.startsWith("ACK:")) → sendToUnity("OnAckReceived", text)
else → sendToUnity("OnTextReceived", text)

// onMtuChanged: логирование результата
```

#### Потокобезопасность

Все Unity-вызовы диспатчатся в主线程 через `Handler(Looper.getMainLooper())`:

```java
private void sendToUnity(String method, String message) {
    mainHandler.post(() -> {
        UnityPlayer.UnitySendMessage(unityCallbackObject, method, message);
    });
}
```

---

### 3. Unity C# скрипты

#### 3.1 BleClientService.cs

**Singleton.** C#-обёртка над BleBridge.java.

**События:**

| Событие | Тип | Описание |
|---------|-----|----------|
| `TextReceived` | `Action<string>` | Обычный текст от ESP32 |
| `StateChanged` | `Action<string>` | Состояние: scanning/connecting/connected/disconnected |
| `Error` | `Action<string>` | Ошибки BLE |
| `AckReceived` | `Action<int>` | Номер последовательности ACK |
| `RttMessageReceived` | `Action<string>` | RTT-сообщение: `RTT:<seq>,<T0>` |
| `ResultsReceived` | `Action<string>` | Результаты: `RESULTS:rate,avg,...` |

**Ключевые методы:**

- `SendAck(int seq)` — отправка `ACK:<seq>` обратно на ESP32
- `StartLatencyTest(int rate)` — отправка команды `TEST:<rate>`
- `OnTextReceived(string)` — маршрутизация RTT/RESULTS/обычный текст
- `OnAckReceived(string)` — парсинг `ACK:<seq>`

**JNI-интеграция:**

```csharp
_bridge = new AndroidJavaObject("com.bletest.blebridge.BleBridge", activity, gameObject.name);
_bridge.Call("startScan");
_bridge.Call("writeCharacteristic", data);
```

#### 3.2 BleReconnector.cs

Автоматическое переподключение с экспоненциальным backoff:

```
2s → 4s → 8s → 16s → 30s (максимум)
```

Сбрасывается при успешном подключении.

#### 3.3 TextDisplayController.cs

Отображение текста через TextMeshPro:

- Подписывается на `TextReceived`, `StateChanged`, `Error`
- Обновляет TMP-компоненты: основной текст + статус
- Цветовая кодировка статуса: жёлтый=сканирование, синий=подключение, зелёный=подключено, серый=отключено, красный=ошибка
- CLR-команда очищает дисплей

#### 3.4 HeadLockedText.cs

Billboard/head-locked позиционирование текста:

- Расстояние: 0.35м перед камерой
- Сглаживание: Lerp с `followSpeed = 8`
- Всегда повёрнут к пользователю: `LookRotation(camera - transform)`

#### 3.5 LatencyProfiler.cs

**Singleton.** Сбор и анализ RTT-данных.

**Структуры данных:**

```csharp
struct MeasurementPoint {
    int seq;               // Порядковый номер
    long espTimestampMs;   // T0 от ESP32
    float unityReceivedMs; // Time.realtimeSinceStartup * 1000
}

struct BatchResult {
    int rate;              // Частота (msg/sec)
    float avgRtt, minRtt, maxRtt;
    float jitterStddev;
    int received, total;
}
```

**Логика:**

- `OnRttMessage()` — **всегда** отправляет ACK (даже если не собирает данные), затем сохраняет точку если `_collecting = true`
- `OnResults()` — парсит RESULTS с `InvariantCulture`, вычисляет Unity-side jitter, сохраняет `BatchResult`
- `CalculateUnityJitter()` — stddev интервалов прихода на Unity
- `SaveToCsv()` — запись в `Application.persistentDataPath/latency_results.csv`

#### 3.6 LatencyTestRunner.cs

Автоматизация тестирования:

- `RunAll()` — перебор частот [20, 30, 60, 90, 120] msg/sec с паузой 2 сек
- `RunSingle(rate)` — запуск одного теста
- `ExportCsv()` — выгрузка результатов
- Таймаут: 30 сек на каждый батч

---

### 4. Unity Сцена (`Assets/Scenes/BleTextScene.unity`)

#### Иерархия GameObjects

```
BleTextScene
├── OVRCameraRig          (VR камера + Passthrough)
│   ├── TrackingSpace
│   │   ├── LeftEyeAnchor
│   │   ├── RightEyeAnchor
│   │   └── CenterEyeAnchor
│   └── ...
├── Directional Light
├── EventSystem
├── BleTextRoot           (Transform, NOT RectTransform)
│   └── StatusLabel       (TextMeshPro, Transform)
├── BleClientManager      (BleClientService)
├── LatencyTestManager    (LatencyProfiler + LatencyTestRunner)
└── ...
```

**Важно:** BleTextRoot и StatusLabel используют `Transform`, а не `RectTransform` (т.к. нет Canvas-родителя).

---

### 5. Настройки Android

#### AndroidManifest.xml

```xml
<uses-permission android:name="android.permission.BLUETOOTH_SCAN" />
<uses-permission android:name="android.permission.BLUETOOTH_CONNECT" />
<uses-permission android:name="android.permission.BLUETOOTH_ADVERTISE" />
<uses-permission android:name="android.permission.ACCESS_FINE_LOCATION" />

<activity android:name="com.unity3d.player.UnityPlayerGameActivity"
          android:exported="true">
    <category android:name="com.oculus.intent.category.VR" />
    <meta-data android:name="com.oculus.vr.focusaware" android:value="true" />
</activity>
```

#### Player Settings

| Параметр | Значение |
|----------|----------|
| Package | `com.bletest.bletextviewer` |
| Min API Level | 29 (Android 10) |
| Scripting Backend | IL2CPP |
| Target Architectures | ARM64 |
| Active Input Handler | Input System (old) |

#### link.xml (JNI/IL2CPP)

```xml
<assembly fullname="Unity.RenderPipelines.Universal.Runtime" preserve="all"/>
<assembly fullname="Unity.XR.OpenXR" preserve="all"/>
```

---

## Процесс сборки и развёртывания

### ESP32

```bash
cd firmware
platformio run -e ble --target upload    # Компиляция + загрузка через USB (COM10)
```

### Unity APK

```bash
# Через Unity MCP (build tool) или Unity Editor
# Output: Builds/Android/ble-test.apk
```

### Установка на Quest 3

```bash
adb install -r Builds/Android/ble-test.apk
adb shell am start -n com.bletest.bletextviewer/com.unity3d.player.UnityPlayerGameActivity
```

---

## Измерение RTT

### Протокол

```
1. ESP32 → Unity:  RTT:<seq>,<T0>                    [notify]
2. Unity → ESP32:  ACK:<seq>                          [write]
3. ESP32 вычисляет: RTT = T_received - T0
4. После 100 сообщений:
   ESP32 → Unity:  RESULTS:<rate>,<avg>,<min>,<max>,<jitter>,<recv>/<total>
```

### Результаты

| Rate | RTT avg | RTT min | RTT max | Jitter | Delivery |
|------|---------|---------|---------|--------|----------|
| 20 msg/s | 62.7ms | 47ms | 68ms | 15.0ms | 66/100 |
| 30 msg/s | 57.1ms | 45ms | 73ms | 13.9ms | 49/100 |
| 60 msg/s | 63.3ms | 41ms | 73ms | 2.6ms | 26/100 |
| 90 msg/s | 64.7ms | 60ms | 72ms | 15.0ms | 17/100 |
| 120 msg/s | 66.0ms | 66ms | 66ms | 0.0ms | 6/100 |

**Вывод:** RTT стабилен ~57-66ms вне зависимости от частоты. Delivery rate падает из-за BLE connection interval (~7.5ms).

---

## Диаграмма последовательности

```
ESP32              BleBridge.java        BleClientService    LatencyProfiler    LatencyTestRunner
  │                      │                      │                  │                   │
  │   RTT:0,1000        │   OnTextReceived     │                  │                   │
  │ ──────────────────→  │ ──────────────────→  │  RttMessageReceived                  │
  │                      │                      │ ──────────────→  │                   │
  │   ACK:0             │                      │  SendAck(0)      │                   │
  │ ←──────────────────  │ ←──────────────────  │ ←──────────────  │                   │
  │                      │                      │                  │                   │
  │   ... (100 messages) │                      │                  │                   │
  │                      │                      │                  │                   │
  │   RESULTS:20,...     │   OnTextReceived     │                  │                   │
  │ ──────────────────→  │ ──────────────────→  │  ResultsReceived │                   │
  │                      │                      │ ──────────────→  │  BatchCompleted   │
  │                      │                      │                  │ ──────────────→   │
  │                      │                      │                  │                   │
```

---

## Структура файлов

```
D:\proj\ble-test\
├── firmware/
│   ├── platformio.ini
│   └── src/ble/main.cpp
├── Assets/
│   ├── Plugins/Android/
│   │   ├── AndroidManifest.xml
│   │   └── BleBridge.java
│   ├── Scripts/Ble/
│   │   ├── BleClientService.cs
│   │   ├── BleReconnector.cs
│   │   ├── TextDisplayController.cs
│   │   ├── HeadLockedText.cs
│   │   ├── LatencyProfiler.cs
│   │   └── LatencyTestRunner.cs
│   ├── Scenes/BleTextScene.unity
│   └── link.xml
├── PLAN.md
├── PLAN1.md
└── README.md
```

---

## Git-история (ключевые коммиты)

| Хеш | Описание |
|-----|----------|
| `938834c` | feat: BLE text display ESP32→Quest 3, working end-to-end |
| `40f80d4` | feat(firmware): PROPERTY_WRITE + onWrite callback |
| `1111886` | feat(firmware): RTT-тестирование — TEST:rate, RTT, ACK, результаты |
| `648f450` | feat(Java bridge): writeCharacteristic, requestMtu, OnAckReceived |
| `c2811a3` | feat(BleClientService): SendAck, RTT/ACK парсинг |
| `c828416` | feat(LatencyProfiler): сбор RTT/jitter, CSV |
| `00ea19b` | feat(LatencyTestRunner): автоматизация тестов |
| `84ef90a` | fix(LatencyProfiler): ACK отправляется всегда |
