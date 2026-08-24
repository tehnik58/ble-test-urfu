package com.bletest.blebridge;

import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCallback;
import android.bluetooth.BluetoothGattCharacteristic;
import android.bluetooth.BluetoothGattDescriptor;
import android.bluetooth.BluetoothGattService;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothProfile;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanFilter;
import android.bluetooth.le.ScanResult;
import android.bluetooth.le.ScanSettings;
import android.content.Context;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

/**
 * BLE bridge for Unity: scans for ESP32-BLE-Text, connects, subscribes to Notify.
 * Callbacks are dispatched to Unity on the main thread via UnityPlayer.UnitySendMessage.
 */
public class BleBridge {

    private static final String TAG = "BleBridge";
    private static final String DEVICE_NAME = "ESP32-BLE-Text";
    private static final UUID SERVICE_UUID = UUID.fromString("4fafc201-1fb5-459e-8fcc-c5c9c331914b");
    private static final UUID CHAR_UUID = UUID.fromString("beb5483e-36e1-4688-b7f5-ea07361b26a8");
    private static final UUID DESCRIPTOR_CCCD = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
    private static final int SCAN_TIMEOUT_MS = 30000;

    private Context context;
    private BluetoothAdapter bluetoothAdapter;
    private BluetoothLeScanner bleScanner;
    private BluetoothGatt bluetoothGatt;
    private Handler mainHandler;
    private boolean isScanning = false;

    // Unity GameObject name to receive callbacks
    private String unityCallbackObject;

    public BleBridge(Context context, String callbackObjectName) {
        this.context = context;
        this.unityCallbackObject = callbackObjectName;
        this.mainHandler = new Handler(Looper.getMainLooper());

        BluetoothManager bluetoothManager = (BluetoothManager) context.getSystemService(Context.BLUETOOTH_SERVICE);
        if (bluetoothManager != null) {
            bluetoothAdapter = bluetoothManager.getAdapter();
        }
    }

    public boolean isBluetoothAvailable() {
        return bluetoothAdapter != null && bluetoothAdapter.isEnabled();
    }

    public void startScan() {
        if (isScanning) return;
        if (!isBluetoothAvailable()) {
            sendToUnity("OnBleError", "Bluetooth not available or disabled");
            return;
        }

        bleScanner = bluetoothAdapter.getBluetoothLeScanner();
        if (bleScanner == null) {
            sendToUnity("OnBleError", "BLE scanner not available");
            return;
        }

        isScanning = true;
        sendToUnity("OnBleStateChanged", "scanning");

        List<ScanFilter> filters = new ArrayList<>();
        filters.add(new ScanFilter.Builder().setDeviceName(DEVICE_NAME).build());

        ScanSettings settings = new ScanSettings.Builder()
                .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
                .build();

        // Timeout handler
        mainHandler.postDelayed(() -> {
            if (isScanning) {
                stopScan();
                sendToUnity("OnBleError", "Scan timeout - device not found");
            }
        }, SCAN_TIMEOUT_MS);

        try {
            bleScanner.startScan(filters, settings, scanCallback);
            Log.i(TAG, "BLE scan started for: " + DEVICE_NAME);
        } catch (SecurityException e) {
            isScanning = false;
            sendToUnity("OnBleError", "BLE permission denied: " + e.getMessage());
        }
    }

    public void stopScan() {
        if (!isScanning) return;
        isScanning = false;
        try {
            if (bleScanner != null) {
                bleScanner.stopScan(scanCallback);
            }
        } catch (Exception e) {
            Log.w(TAG, "Error stopping scan: " + e.getMessage());
        }
        Log.i(TAG, "BLE scan stopped");
    }

    public void disconnect() {
        stopScan();
        if (bluetoothGatt != null) {
            try {
                bluetoothGatt.disconnect();
                bluetoothGatt.close();
            } catch (Exception e) {
                Log.w(TAG, "Error disconnecting: " + e.getMessage());
            }
            bluetoothGatt = null;
        }
        sendToUnity("OnBleStateChanged", "disconnected");
    }

    public void cleanup() {
        disconnect();
        mainHandler.removeCallbacksAndMessages(null);
    }

    // --- Scan callback ---
    private final ScanCallback scanCallback = new ScanCallback() {
        @Override
        public void onScanResult(int callbackType, ScanResult result) {
            BluetoothDevice device = result.getDevice();
            Log.i(TAG, "Found device: " + device.getName() + " [" + device.getAddress() + "]");
            stopScan();
            connectToDevice(device);
        }

        @Override
        public void onScanFailed(int errorCode) {
            isScanning = false;
            sendToUnity("OnBleError", "Scan failed with error code: " + errorCode);
        }
    };

    // --- Connect ---
    private void connectToDevice(BluetoothDevice device) {
        sendToUnity("OnBleStateChanged", "connecting");
        try {
            bluetoothGatt = device.connectGatt(context, false, gattCallback, BluetoothDevice.TRANSPORT_LE);
            Log.i(TAG, "Connecting to " + device.getName());
        } catch (SecurityException e) {
            sendToUnity("OnBleError", "Connect permission denied: " + e.getMessage());
        }
    }

    // --- GATT callback ---
    private final BluetoothGattCallback gattCallback = new BluetoothGattCallback() {
        @Override
        public void onConnectionStateChange(BluetoothGatt gatt, int status, int newState) {
            if (newState == BluetoothProfile.STATE_CONNECTED) {
                Log.i(TAG, "GATT connected, discovering services...");
                try {
                    gatt.discoverServices();
                } catch (SecurityException e) {
                    sendToUnity("OnBleError", "Service discovery permission denied");
                }
            } else if (newState == BluetoothProfile.STATE_DISCONNECTED) {
                Log.i(TAG, "GATT disconnected");
                bluetoothGatt = null;
                sendToUnity("OnBleStateChanged", "disconnected");
            }
        }

        @Override
        public void onServicesDiscovered(BluetoothGatt gatt, int status) {
            if (status != BluetoothGatt.GATT_SUCCESS) {
                sendToUnity("OnBleError", "Service discovery failed: " + status);
                return;
            }

            BluetoothGattService service = gatt.getService(SERVICE_UUID);
            if (service == null) {
                sendToUnity("OnBleError", "Service not found: " + SERVICE_UUID);
                return;
            }

            BluetoothGattCharacteristic characteristic = service.getCharacteristic(CHAR_UUID);
            if (characteristic == null) {
                sendToUnity("OnBleError", "Characteristic not found: " + CHAR_UUID);
                return;
            }

            // Enable notifications
            try {
                gatt.setCharacteristicNotification(characteristic, true);
                BluetoothGattDescriptor descriptor = characteristic.getDescriptor(DESCRIPTOR_CCCD);
                if (descriptor != null) {
                    descriptor.setValue(BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);
                    gatt.writeDescriptor(descriptor);
                }
                sendToUnity("OnBleStateChanged", "connected");
                Log.i(TAG, "Subscribed to notifications");
            } catch (Exception e) {
                sendToUnity("OnBleError", "Subscribe failed: " + e.getMessage());
            }
        }

        @Override
        public void onCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic) {
            if (characteristic.getUuid().equals(CHAR_UUID)) {
                byte[] data = characteristic.getValue();
                if (data != null && data.length > 0) {
                    String text = new String(data, StandardCharsets.UTF_8);
                    Log.d(TAG, "Received: " + text);
                    sendToUnity("OnTextReceived", text);
                }
            }
        }

        @Override
        public void onCharacteristicRead(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, int status) {
            if (status == BluetoothGatt.GATT_SUCCESS && characteristic.getUuid().equals(CHAR_UUID)) {
                byte[] data = characteristic.getValue();
                if (data != null && data.length > 0) {
                    String text = new String(data, StandardCharsets.UTF_8);
                    sendToUnity("OnTextReceived", text);
                }
            }
        }
    };

    // --- Send callback to Unity ---
    private void sendToUnity(String method, String message) {
        mainHandler.post(() -> {
            try {
                com.unity3d.player.UnityPlayer.UnitySendMessage(unityCallbackObject, method, message);
            } catch (Exception e) {
                Log.w(TAG, "UnitySendMessage failed: " + e.getMessage());
            }
        });
    }
}
