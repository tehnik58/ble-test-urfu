# PLAN1.md — Замер BLE-задержек

## Цель
Замерить round-trip time (RTT) и jitter BLE-канала между ESP32 и Quest 3 при частотах 20, 30, 60, 90, 120 сообщений/сек. Группы по 100 сообщений, среднее по каждому батчу.

## Метрики

| Метрика | Описание | Точность |
|---|---|---|
| **RTT** | Время от ESP32 notify → Quest 3收到 → Quest 3 write ACK → ESP32收到 ACK | ±1–3мс |
| **Jitter** | Стандартное отклонение интервалов между приходом сообщений на Quest 3 | ±0.1мс |
| **One-way latency** | Приблизительная: (RTT / 2). Без синхронизации часов — грубая оценка | ±10–25мс |

## Почему RTT, а не one-way

Односторонняя задержка (ESP32 millis → Unity Time) **бессмысленна без синхронизации часов**. Часы ESP32 и Quest 3 могут отличаться на сотни миллисекунд. RTT не требует синхронизации — оба замера (отправка и получение ACK) происходят на одном устройстве.

---

## Архитектура измерения

```
ESP32                          Quest 3 (Unity)
──────                         ───────────────
millis()=T0
  → notify("RTT:0,T0")  ───► 收到, millis()=T1
                                → write("ACK:0")  ───►
收到 ACK, millis()=T2
  RTT = T2 - T0                RTT_estimated = T1 - T0 (approx, для логов)
```

RTT = `T2 - T0` (измеряется на ESP32, точное).

---

## Детали реализации

### 1. ESP32 firmware (`firmware/src/ble/main.cpp`)

#### 1.1. Добавить PROPERTY_WRITE

Текущая конфигурация: `PROPERTY_READ | PROPERTY_NOTIFY`. Нужно добавить `PROPERTY_WRITE` чтобы Quest 3 мог отправлять ACK.

```cpp
pCharacteristic = pService->createCharacteristic(
    CHARACTERISTIC_UUID,
    BLECharacteristic::PROPERTY_READ |
    BLECharacteristic::PROPERTY_NOTIFY |
    BLECharacteristic::PROPERTY_WRITE  // ← добавить
);
```

#### 1.2. Добавить callback на получение write

```cpp
class MyCallbacks : public BLECharacteristicCallbacks {
    void onWrite(BLECharacteristic *pCharacteristic) {
        std::string value = pCharacteristic->getValue();
        if (value.length() > 0) {
            // Парсим ACK:<seq>
            // Записываем T2 = millis()
            // Вычисляем RTT = T2 - T0[seq]
        }
    }
};
pCharacteristic->setCallbacks(new MyCallbacks());
```

#### 1.3. Добавить команду `TEST:<rate>`

Формат входящей команды (из Serial или write): `TEST:20`, `TEST:30`, ..., `TEST:120`

Логика:
```
onReceive("TEST:<rate>"):
  testRate = atoi(rate)
  testBatchSize = 100
  testSeq = 0
  testRunning = true
  testStartTime = millis()
  lastSendTime = 0

void loop():
  if (!testRunning) return
  now = millis()
  if (now - lastSendTime >= 1000 / testRate):
    lastSendTime = now
    seq = testSeq++
    T0 = millis()
    sentTimestamps[seq] = T0           // сохраняем для вычисления RTT
    msg = "RTT:" + str(seq) + "," + str(T0)
    pCharacteristic->setValue(msg)
    pCharacteristic->notify()
    if (seq >= testBatchSize - 1):
      testRunning = false
      // через 2 секунды вывести результаты
      printResults()
```

#### 1.4. Хранение результатов

```cpp
#define MAX_BATCH 100
long sentTimestamps[MAX_BATCH];     // T0 для каждого seq
long receivedAcks[MAX_BATCH];       // T2 для каждого seq
bool ackReceived[MAX_BATCH];
int testRate;
int testBatchSize = 100;
```

#### 1.5. Вывод результатов (через Serial)

```
RESULTS:<rate>,<avgRTT>,<minRTT>,<maxRTT>,<jitter>,<received>/<total>
```

Формат:
```
RESULTS:60,85.3,72,112,8.2,98/100
```

- avgRTT: среднее RTT в мс
- minRTT, maxRTT: мин/макс
- jitter: stddev интервалов прихода ACK на ESP32
- received/total: сколько ACK получено (учёт потерь)

#### 1.6. Обработка потерь

Если ACK не пришёл через 500мс — считать потерянным. Не блокировать отправку следующих сообщений.

#### 1.7. MTU

Добавить `onMtuChanged` callback. По умолчанию MTU=23 (20 байт полезной нагрузки). Запросить MTU 512:

```cpp
pServer->updateConnParams(address, 6, 12, 0, 51);  // минимум
// MTU negotiation происходит на стороне central (Quest 3)
```

На стороне Unity (Java bridge) — вызвать `requestMtu(512)` после подключения.

---

### 2. Unity — Java Bridge (`Assets/Plugins/Android/BleBridge.java`)

#### 2.1. Добавить `requestMtu`

После `onServicesDiscovered`:
```java
gatt.requestMtu(512);
```

В `onMtuChanged` — проверить результат:
```java
@Override
public void onMtuChanged(BluetoothGatt gatt, int mtu, int status) {
    Log.d("BleBridge", "MTU changed to " + mtu);
}
```

#### 2.2. Добавить метод `writeCharacteristic`

```java
public void writeCharacteristic(byte[] data) {
    if (bluetoothGatt == null || writeCharacteristic == null) return;
    writeCharacteristic.setValue(data);
    bluetoothGatt.writeCharacteristic(writeCharacteristic);
}
```

Найти write-характеристику при discovery:
```java
writeCharacteristic = service.getCharacteristic(CHARACTERISTIC_UUID);
```

#### 2.3. Forward ACK в Unity

```java
// В onCharacteristicChanged — определить это ACK
String text = new String(data, StandardCharsets.UTF_8);
if (text.startsWith("ACK:")) {
    sendToUnity("OnAckReceived", text);
} else {
    sendToUnity("OnTextReceived", text);
}
```

---

### 3. Unity C# — `BleClientService.cs`

#### 3.1. Добавить событие ACK

```csharp
public event Action<int> AckReceived;  // seq number
```

#### 3.2. Новый метод `SendAck`

```csharp
public void SendAck(int seq)
{
#if UNITY_ANDROID && !UNITY_EDITOR
    _bridge?.Call("writeCharacteristic", Encoding.UTF8.GetBytes($"ACK:{seq}"));
#endif
}
```

#### 3.3. Callback `OnAckReceived`

```csharp
public void OnAckReceived(string data)
{
    // "ACK:42" → seq = 42
    if (int.TryParse(data.Substring(4), out int seq))
        AckReceived?.Invoke(seq);
}
```

#### 3.4. Парсинг RTT-сообщений

В `OnTextReceived` — если сообщение начинается с `RTT:` — парсить и передавать в LatencyProfiler, а не в TextDisplayController.

```csharp
public void OnTextReceived(string text)
{
    if (text.StartsWith("RTT:"))
    {
        // Парсим "RTT:<seq>,<T0>"
        RttMessageReceived?.Invoke(text);
        return;
    }
    TextReceived?.Invoke(text);
}
```

---

### 4. Unity C# — новый скрипт `LatencyProfiler.cs`

#### 4.1. Структуры данных

```csharp
public struct MeasurementPoint
{
    public int seq;
    public long espTimestampMs;    // T0 из ESP32
    public float unityReceivedMs;  // Time.realtimeSinceStartup * 1000
    public float unitySentAckMs;   // когда отправили ACK
    public long espRttMs;          // T2 - T0 (из ESP32, точное)
}

public struct BatchResult
{
    public int rate;
    public float avgRtt;
    public float minRtt;
    public float maxRtt;
    public float jitterStddev;
    public int received;
    public int total;
}
```

#### 4.2. Логика

```
OnRttMessageReceived("RTT:<seq>,<T0>"):
  seq = parse(seq)
  T0 = parse(T0)
  unityTime = Time.realtimeSinceStartup * 1000
  points[seq] = MeasurementPoint { seq, T0, unityTime }
  SendAck(seq)

OnAckFromEsp32("ACK:<seq>"):
  T2 = Time.realtimeSinceStartup * 1000
  points[seq].espRttMs = ...  // или считаем RTT на ESP32

OnResultsReceived("RESULTS:..."):
  Собрать статистику из points
  Вывести в TMP + лог
  Записать CSV
```

#### 4.3. Статистика

```csharp
BatchResult CalculateResults(List<MeasurementPoint> points, int expectedRate)
{
    var rtts = points.Where(p => p.espRttMs > 0).Select(p => (float)p.espRttMs).ToList();
    var intervals = points.OrderBy(p => p.unityReceivedMs)
        .Select((p, i) => i == 0 ? 0 : p.unityReceivedMs - points[i-1].unityReceivedMs)
        .Skip(1).ToList();

    return new BatchResult
    {
        rate = expectedRate,
        avgRtt = rtts.Average(),
        minRtt = rtts.Min(),
        maxRtt = rtts.Max(),
        jitterStddev = StdDev(intervals),
        received = points.Count,
        total = 100
    };
}
```

#### 4.4. CSV-вывод

```
rate,avgRtt_ms,minRtt_ms,maxRtt_ms,jitter_ms,received,total,timestamp
20,85.3,72,112,8.2,98,100,2026-08-26T12:00:00
30,92.1,68,135,12.4,97,100,2026-08-26T12:00:30
...
```

Путь: `Application.persistentDataPath + "/latency_results.csv"`

---

### 5. Unity C# — новый скрипт `LatencyTestRunner.cs`

#### 5.1. Автоматический перебор

```csharp
int[] testRates = { 20, 30, 60, 90, 120 };
int batchSize = 100;
float pauseBetweenBatches = 2f;  // секунды

IEnumerator RunAllTests()
{
    foreach (int rate in testRates)
    {
        Debug.Log($"[LatencyTest] Starting test at {rate} msg/sec");
        yield return new WaitForSeconds(pauseBetweenBatches);

        // Отправить команду ESP32
        bleClient.StartLatencyTest(rate);

        // Ждать завершения (DONE или timeout 30 сек)
        yield return StartCoroutine(WaitForTestComplete(30f));

        // Собрать и записать результаты
        profiler.SaveBatchResult(rate);
    }

    Debug.Log("[LatencyTest] All tests completed");
}
```

#### 5.2. GUI (опционально)

Кнопки в сцене для ручного запуска:
- "Run All" — автоматический перебор
- "Test 20/s", "Test 30/s", ... — ручной запуск
- "Export CSV" — выгрузка результатов

---

### 6. Сборка и деплой

#### 6.1. ESP32

```
cd firmware
pio run -e ble
pio run -e ble --target upload
```

#### 6.2. Unity APK

Через MCP build → Android → `Builds/Android/ble-test.apk`

#### 6.3. Установка

```
adb install -r Builds/Android/ble-test.apk
```

#### 6.4. Запуск теста

1. Запустить приложение на Quest 3
2. Подождать BLE-подключения к ESP32
3. На Quest 3 нажать "Run All" (или через serial: `TEST:20\n`)
4. Ждать ~3 минуты (5 батчей × 100 сообщений + паузы)
5. Результаты в логах и CSV

---

### 7. Схема потока данных

```
┌─────────┐     notify("RTT:0,1234")     ┌─────────┐
│  ESP32  │ ───────────────────────────►  │ Quest 3 │
│         │                               │         │
│ T0=1234 │     write("ACK:0")            │ T1=?    │
│         │ ◄───────────────────────────  │         │
│ T2=1289 │                               │ T1=1245 │
│         │                               │         │
│ RTT=55  │   notify("RESULTS:...")       │         │
│         │ ───────────────────────────►  │ results │
└─────────┘                               └─────────┘
```

---

### 8. Файлы для изменения/создания

| Файл | Действие |
|---|---|
| `firmware/src/ble/main.cpp` | Изменить: добавить TEST-команду, write callback, RTT-логику, результаты |
| `Assets/Plugins/Android/BleBridge.java` | Изменить: добавить writeCharacteristic, requestMtu, OnAckReceived callback |
| `Assets/Scripts/Ble/BleClientService.cs` | Изменить: добавить SendAck, AckReceived, RttMessageReceived события |
| `Assets/Scripts/Ble/LatencyProfiler.cs` | **Создать**: сбор и анализ RTT/jitter, CSV-вывод |
| `Assets/Scripts/Ble/LatencyTestRunner.cs` | **Создать**: автоматизация тестов, GUI |

---

### 9. Порядок реализации

1. **ESP32**: PROPERTY_WRITE + onWrite callback (база для ACK)
2. **ESP32**: команда TEST:\<rate\> + отправка RTT-сообщений
3. **ESP32**: приём ACK, подсчёт RTT, вывод результатов
4. **Java bridge**: writeCharacteristic + requestMtu + OnAckReceived
5. **Unity C#**: BleClientService — SendAck + парсинг RTT/ACK
6. **Unity C#**: LatencyProfiler — сбор данных, статистика
7. **Unity C#**: LatencyTestRunner — автоматизация
8. **Сборка**: ESP32 firmware + Unity APK + установка
9. **Тест**: Run All → проверка CSV

---

### 10. Ожидаемые результаты

| Rate | RTT (ожид.) | Jitter (ожид.) | Потери |
|---|---|---|---|
| 20/s | 40–80мс | <5мс | 0% |
| 30/s | 50–100мс | <8мс | 0% |
| 60/s | 60–120мс | 10–20мс | <2% |
| 90/s | 80–150мс | 15–30мс | 2–5% |
| 120/s | 100–200мс | 20–50мс | 5–15% |
