using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Android;

namespace BleTest
{
    /// <summary>
    /// C# wrapper around the native Android BleBridge Java class.
    /// Attach to a GameObject that receives UnitySendMessage callbacks.
    /// </summary>
    public class BleClientService : MonoBehaviour
    {
        public static BleClientService Instance { get; private set; }

        public event Action<string> TextReceived;
        public event Action<string> StateChanged;   // "scanning", "connecting", "connected", "disconnected"
        public event Action<string> Error;
        public event Action<int> AckReceived;        // seq number from ESP32
        public event Action<string> RttMessageReceived;  // "RTT:<seq>,<T0>"
        public event Action<string> ResultsReceived;     // "RESULTS:rate,avgRtt,..."

        private AndroidJavaObject _bridge;
        private bool _isInitialized;

        private const float INITIAL_SCAN_DELAY = 1.5f;
        private const float PERMISSION_TIMEOUT = 15f;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            StartCoroutine(PermissionAndInit());
#else
            Debug.Log("[BleClient] Not on Android - BLE bridge disabled");
#endif
        }

        private IEnumerator PermissionAndInit()
        {
            // Request BLE permissions (Android 12+ / Quest 3)
            yield return RequestAndAwait("android.permission.BLUETOOTH_SCAN");
            yield return RequestAndAwait("android.permission.BLUETOOTH_CONNECT");

            Debug.Log("[BleClient] BLE permissions check done");

            InitializeBridge();

            if (_isInitialized)
            {
                yield return new WaitForSeconds(INITIAL_SCAN_DELAY);
                StartScan();
            }
        }

        private IEnumerator RequestAndAwait(string permission)
        {
            if (Permission.HasUserAuthorizedPermission(permission))
                yield break;

            Debug.Log($"[BleClient] Requesting {permission}...");
            Permission.RequestUserPermission(permission);

            float elapsed = 0f;
            while (!Permission.HasUserAuthorizedPermission(permission) && elapsed < PERMISSION_TIMEOUT)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!Permission.HasUserAuthorizedPermission(permission))
                Debug.LogWarning($"[BleClient] Permission {permission} not granted after {PERMISSION_TIMEOUT}s — scan may fail");
        }

        private void InitializeBridge()
        {
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                _bridge = new AndroidJavaObject(
                    "com.bletest.blebridge.BleBridge",
                    activity,
                    gameObject.name
                );

                _isInitialized = true;
                Debug.Log("[BleClient] Bridge initialized");
            }
            catch (Exception e)
            {
                Debug.LogError($"[BleClient] Init failed: {e.Message}");
                Error?.Invoke("Initialization failed: " + e.Message);
            }
        }

        public bool IsBluetoothAvailable()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null) return false;
            return _bridge.Call<bool>("isBluetoothAvailable");
#else
            return false;
#endif
        }

        public void StartScan()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null)
            {
                Error?.Invoke("Bridge not initialized");
                return;
            }
            _bridge.Call("startScan");
            Debug.Log("[BleClient] Scan started");
#endif
        }

        public void StopScan()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("stopScan");
#endif
        }

        public void Disconnect()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("disconnect");
#endif
        }

        public void SendAck(int seq)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null) return;
            byte[] data = Encoding.UTF8.GetBytes($"ACK:{seq}");
            _bridge.Call("writeCharacteristic", data);
#endif
        }

        public void StartLatencyTest(int rate)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null) return;
            byte[] data = Encoding.UTF8.GetBytes($"TEST:{rate}");
            _bridge.Call("writeCharacteristic", data);
            Debug.Log($"[BleClient] Latency test started: {rate} msg/sec");
#endif
        }

        private void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("cleanup");
            _bridge?.Dispose();
            _bridge = null;
#endif
        }

        // --- Callbacks from Java via UnitySendMessage ---

        public void OnTextReceived(string text)
        {
            Debug.Log($"[BleClient] Text received: {text}");
            if (text.StartsWith("RTT:"))
            {
                RttMessageReceived?.Invoke(text);
            }
            else if (text.StartsWith("RESULTS:"))
            {
                ResultsReceived?.Invoke(text);
            }
            else
            {
                TextReceived?.Invoke(text);
            }
        }

        public void OnAckReceived(string data)
        {
            if (int.TryParse(data.Substring(4), out int seq))
            {
                AckReceived?.Invoke(seq);
            }
        }

        public void OnBleStateChanged(string state)
        {
            Debug.Log($"[BleClient] State: {state}");
            StateChanged?.Invoke(state);
        }

        public void OnBleError(string error)
        {
            Debug.LogError($"[BleClient] Error: {error}");
            Error?.Invoke(error);
        }
    }
}
