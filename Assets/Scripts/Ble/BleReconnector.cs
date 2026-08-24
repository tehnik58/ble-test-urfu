using UnityEngine;

namespace BleTest
{
    /// <summary>
    /// Handles automatic reconnection with exponential backoff.
    /// </summary>
    public class BleReconnector : MonoBehaviour
    {
        [SerializeField] private float _initialDelay = 2f;
        [SerializeField] private float _maxDelay = 30f;
        [SerializeField] private float _backoffMultiplier = 2f;

        private BleClientService _bleService;
        private float _currentDelay;
        private float _timer;
        private bool _shouldReconnect;
        private bool _isConnected;

        private void Start()
        {
            _bleService = BleClientService.Instance;
            if (_bleService == null)
            {
                Debug.LogError("[BleReconnector] BleClientService not found");
                return;
            }

            _bleService.OnStateChanged += OnStateChanged;
            _bleService.OnError += OnError;
            _currentDelay = _initialDelay;
        }

        private void OnDestroy()
        {
            if (_bleService != null)
            {
                _bleService.OnStateChanged -= OnStateChanged;
                _bleService.OnError -= OnError;
            }
        }

        private void Update()
        {
            if (!_shouldReconnect || _isConnected) return;

            _timer -= Time.deltaTime;
            if (_timer <= 0f)
            {
                Debug.Log("[BleReconnector] Attempting reconnect...");
                _bleService.StartScan();
                _timer = _currentDelay;
                _currentDelay = Mathf.Min(_currentDelay * _backoffMultiplier, _maxDelay);
            }
        }

        private void OnStateChanged(string state)
        {
            switch (state)
            {
                case "connected":
                    _isConnected = true;
                    _shouldReconnect = false;
                    _currentDelay = _initialDelay;
                    break;
                case "disconnected":
                    _isConnected = false;
                    _shouldReconnect = true;
                    _timer = _initialDelay;
                    break;
                case "scanning":
                case "connecting":
                    break;
            }
        }

        private void OnError(string error)
        {
            if (error.Contains("timeout") || error.Contains("not found"))
            {
                _shouldReconnect = true;
                _timer = _currentDelay;
            }
        }

        public void StopReconnecting()
        {
            _shouldReconnect = false;
        }
    }
}
