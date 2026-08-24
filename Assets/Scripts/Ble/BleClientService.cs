using System;
using UnityEngine;

namespace BleTest
{
    /// <summary>
    /// C# wrapper around the native Android BleBridge Java class.
    /// Attach to a GameObject that receives UnitySendMessage callbacks.
    /// </summary>
    public class BleClientService : MonoBehaviour
    {
        public static BleClientService Instance { get; private set; }

        public event Action<string> OnTextReceived;
        public event Action<string> OnStateChanged;   // "scanning", "connecting", "connected", "disconnected"
        public event Action<string> OnError;

        private AndroidJavaObject _bridge;
        private bool _isInitialized;

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
            InitializeBridge();
#else
            Debug.Log("[BleClient] Not on Android - BLE bridge disabled");
#endif
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
                OnError?.Invoke("Initialization failed: " + e.Message);
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
                OnError?.Invoke("Bridge not initialized");
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
            OnTextReceived?.Invoke(text);
        }

        public void OnBleStateChanged(string state)
        {
            Debug.Log($"[BleClient] State: {state}");
            OnStateChanged?.Invoke(state);
        }

        public void OnBleError(string error)
        {
            Debug.LogError($"[BleClient] Error: {error}");
            OnError?.Invoke(error);
        }
    }
}
