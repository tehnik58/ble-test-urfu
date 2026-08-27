#include <Arduino.h>
#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLEUtils.h>
#include <BLE2902.h>

#define DEVICE_NAME "ESP32-BLE-Text"
#define SERVICE_UUID        "4fafc201-1fb5-459e-8fcc-c5c9c331914b"
#define CHARACTERISTIC_UUID "beb5483e-36e1-4688-b7f5-ea07361b26a8"
#define MAX_LEN 512
#define MAX_BATCH 100
#define ACK_TIMEOUT_MS 500

BLEServer *pServer = nullptr;
BLECharacteristic *pCharacteristic = nullptr;
bool deviceConnected = false;
bool oldDeviceConnected = false;
char lineBuf[MAX_LEN];
int lineLen = 0;

// --- Latency test state ---
bool testRunning = false;
int testRate = 0;
int testBatchSize = MAX_BATCH;
int testSeq = 0;
unsigned long lastSendTime = 0;

unsigned long sentTimestamps[MAX_BATCH];
unsigned long receivedAcks[MAX_BATCH];
bool ackReceived[MAX_BATCH];

// --- Forward declarations ---
void startTest(int rate);
void sendTestMessage();
void printResults();
void processSerialCommand(const char *cmd);

class ServerCallbacks : public BLEServerCallbacks {
  void onConnect(BLEServer *pServer) {
    deviceConnected = true;
    Serial.println("[BLE] Client connected");
  }
  void onDisconnect(BLEServer *pServer) {
    deviceConnected = false;
    Serial.println("[BLE] Client disconnected");
  }
};

class CharacteristicCallbacks : public BLECharacteristicCallbacks {
  void onWrite(BLECharacteristic *pCharacteristic) {
    std::string value = pCharacteristic->getValue();
    if (value.length() > 0) {
      String val = String(value.c_str());
      if (val.startsWith("ACK:")) {
        int seq = val.substring(4).toInt();
        if (seq >= 0 && seq < MAX_BATCH && testRunning) {
          receivedAcks[seq] = millis();
          ackReceived[seq] = true;
          Serial.printf("[BLE<-] ACK:%d (RTT=%lums)\n", seq, receivedAcks[seq] - sentTimestamps[seq]);
        }
      } else {
        Serial.printf("[BLE<-] Write: %s\n", value.c_str());
        processSerialCommand(val.c_str());
      }
    }
  }
};

void processSerialCommand(const char *cmd) {
  String s = String(cmd);
  if (s.startsWith("TEST:")) {
    int rate = s.substring(5).toInt();
    if (rate > 0 && rate <= 500) {
      startTest(rate);
    } else {
      Serial.printf("[TEST] Invalid rate: %d\n", rate);
    }
  } else if (s == "RESULTS?") {
    printResults();
  }
}

void startTest(int rate) {
  testRate = rate;
  testBatchSize = MAX_BATCH;
  testSeq = 0;
  testRunning = true;
  lastSendTime = 0;

  for (int i = 0; i < MAX_BATCH; i++) {
    sentTimestamps[i] = 0;
    receivedAcks[i] = 0;
    ackReceived[i] = false;
  }

  Serial.printf("[TEST] Start: %d msg/sec, batch=%d\n", testRate, testBatchSize);
}

void sendTestMessage() {
  if (!testRunning || !deviceConnected) return;

  unsigned long now = millis();
  unsigned long interval = 1000 / testRate;

  if (now - lastSendTime >= interval) {
    lastSendTime = now;
    int seq = testSeq;

    sentTimestamps[seq] = millis();
    char msg[64];
    snprintf(msg, sizeof(msg), "RTT:%d,%lu", seq, sentTimestamps[seq]);

    pCharacteristic->setValue((uint8_t *)msg, strlen(msg));
    pCharacteristic->notify();

    testSeq++;

    if (testSeq >= testBatchSize) {
      testRunning = false;
      Serial.printf("[TEST] Batch sent, waiting for remaining ACKs...\n");
      delay(ACK_TIMEOUT_MS);
      printResults();
    }
  }
}

void printResults() {
  int received = 0;
  unsigned long sumRtt = 0;
  unsigned long minRtt = 999999;
  unsigned long maxRtt = 0;

  for (int i = 0; i < testBatchSize; i++) {
    if (ackReceived[i]) {
      unsigned long rtt = receivedAcks[i] - sentTimestamps[i];
      sumRtt += rtt;
      if (rtt < minRtt) minRtt = rtt;
      if (rtt > maxRtt) maxRtt = rtt;
      received++;
    }
  }

  float avgRtt = received > 0 ? (float)sumRtt / received : 0;

  // Jitter: stddev of inter-arrival intervals of ACKs
  float jitter = 0;
  if (received > 1) {
    float intervals[100];
    int intervalCount = 0;
    unsigned long prevAck = 0;
    for (int i = 0; i < testBatchSize; i++) {
      if (ackReceived[i]) {
        if (prevAck > 0 && intervalCount < 100) {
          intervals[intervalCount++] = (float)(receivedAcks[i] - prevAck);
        }
        prevAck = receivedAcks[i];
      }
    }
    if (intervalCount > 0) {
      float mean = 0;
      for (int i = 0; i < intervalCount; i++) mean += intervals[i];
      mean /= intervalCount;
      float sumSq = 0;
      for (int i = 0; i < intervalCount; i++) {
        float diff = intervals[i] - mean;
        sumSq += diff * diff;
      }
      jitter = sqrt(sumSq / intervalCount);
    }
  }

  if (minRtt == 999999) minRtt = 0;

  Serial.printf("RESULTS:%d,%.1f,%lu,%lu,%.1f,%d/%d\n",
    testRate, avgRtt, minRtt, maxRtt, jitter, received, testBatchSize);

  // Also send to BLE as text for Unity to capture
  if (deviceConnected) {
    char result[128];
    snprintf(result, sizeof(result), "RESULTS:%d,%.1f,%lu,%lu,%.1f,%d/%d",
      testRate, avgRtt, minRtt, maxRtt, jitter, received, testBatchSize);
    pCharacteristic->setValue((uint8_t *)result, strlen(result));
    pCharacteristic->notify();
  }
}

void setup() {
  Serial.begin(115200);
  delay(1000);
  Serial.println("=== ESP32 BLE Text Peripheral ===");

  BLEDevice::init(DEVICE_NAME);
  pServer = BLEDevice::createServer();
  pServer->setCallbacks(new ServerCallbacks());

  BLEService *pService = pServer->createService(SERVICE_UUID);

  pCharacteristic = pService->createCharacteristic(
    CHARACTERISTIC_UUID,
    BLECharacteristic::PROPERTY_READ |
    BLECharacteristic::PROPERTY_NOTIFY |
    BLECharacteristic::PROPERTY_WRITE
  );
  pCharacteristic->addDescriptor(new BLE2902());
  pCharacteristic->setCallbacks(new CharacteristicCallbacks());

  pService->start();

  BLEAdvertising *pAdvertising = BLEDevice::getAdvertising();
  pAdvertising->addServiceUUID(SERVICE_UUID);
  pAdvertising->setScanResponse(true);
  pAdvertising->setMinPreferred(0x06);
  pAdvertising->setMinPreferred(0x12);
  BLEDevice::startAdvertising();

  Serial.println("[BLE] Advertising started");
  Serial.println("[BLE] Waiting for connection...");
}

void loop() {
  // Latency test
  if (testRunning) {
    sendTestMessage();
  }

  // Read serial input
  while (Serial.available() > 0) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') {
      if (lineLen > 0) {
        lineBuf[lineLen] = '\0';

        if (lineLen > 0 && lineBuf[lineLen - 1] == '\r') {
          lineBuf[--lineLen] = '\0';
        }

        String text = String(lineBuf);

        if (deviceConnected) {
          if (text.startsWith("TEST:")) {
            processSerialCommand(lineBuf);
          } else if (text == "CLR" || text.length() == 0) {
            pCharacteristic->setValue("");
            pCharacteristic->notify();
            Serial.println("[BLE->] CLR (cleared)");
          } else {
            pCharacteristic->setValue((uint8_t *)lineBuf, lineLen);
            pCharacteristic->notify();
            Serial.printf("[BLE->] \"%s\"\n", lineBuf);
          }
        } else {
          Serial.printf("[BLE] Not connected, dropped: %s\n", lineBuf);
        }

        lineLen = 0;
      }
    } else if (lineLen < MAX_LEN - 1) {
      if (c >= 0x20) {
        lineBuf[lineLen++] = c;
      }
    }
  }

  // Advertising restart on disconnect
  if (!deviceConnected && oldDeviceConnected) {
    delay(500);
    pServer->startAdvertising();
    Serial.println("[BLE] Restarted advertising");
    oldDeviceConnected = deviceConnected;
  }
  if (deviceConnected && !oldDeviceConnected) {
    oldDeviceConnected = deviceConnected;
  }
}
