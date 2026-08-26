#include <Arduino.h>
#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLEUtils.h>
#include <BLE2902.h>

#define DEVICE_NAME "ESP32-BLE-Text"
#define SERVICE_UUID        "4fafc201-1fb5-459e-8fcc-c5c9c331914b"
#define CHARACTERISTIC_UUID "beb5483e-36e1-4688-b7f5-ea07361b26a8"
#define MAX_LEN 512

BLEServer *pServer = nullptr;
BLECharacteristic *pCharacteristic = nullptr;
bool deviceConnected = false;
bool oldDeviceConnected = false;
char lineBuf[MAX_LEN];
int lineLen = 0;

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
    BLECharacteristic::PROPERTY_NOTIFY
  );
  pCharacteristic->addDescriptor(new BLE2902());

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
  // Read serial input
  while (Serial.available() > 0) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') {
      if (lineLen > 0) {
        lineBuf[lineLen] = '\0';

        // Trim \r
        if (lineLen > 0 && lineBuf[lineLen - 1] == '\r') {
          lineBuf[--lineLen] = '\0';
        }

        String text = String(lineBuf);

        if (deviceConnected) {
          if (text == "CLR" || text.length() == 0) {
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
      // Skip control chars below 0x20 (except those already handled)
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
