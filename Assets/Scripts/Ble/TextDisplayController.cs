using UnityEngine;
using TMPro;

namespace BleTest
{
    /// <summary>
    /// Subscribes to BLE text events and updates TextMeshPro display.
    /// Hides text on empty string or CLR command (FR-6).
    /// </summary>
    public class TextDisplayController : MonoBehaviour
    {
        [SerializeField] private TextMeshPro _textMesh;
        [SerializeField] private TextMeshPro _statusLabel;
        [SerializeField] private GameObject _textRoot;

        private BleClientService _bleService;

        private void Start()
        {
            _bleService = BleClientService.Instance;
            if (_bleService == null)
            {
                Debug.LogError("[TextDisplay] BleClientService not found");
                return;
            }

            _bleService.TextReceived += OnTextReceived;
            _bleService.StateChanged += OnStateChanged;
            _bleService.Error += OnError;

            UpdateStatus("disconnected");
            SetTextVisible(false);
        }

        private void OnDestroy()
        {
            if (_bleService != null)
            {
                _bleService.TextReceived -= OnTextReceived;
                _bleService.StateChanged -= OnStateChanged;
                _bleService.Error -= OnError;
            }
        }

        private void OnTextReceived(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Trim() == "CLR")
            {
                SetTextVisible(false);
                Debug.Log("[TextDisplay] Text cleared");
            }
            else
            {
                _textMesh.text = text;
                SetTextVisible(true);
                Debug.Log($"[TextDisplay] Text updated: {text}");
            }
        }

        private void OnStateChanged(string state)
        {
            UpdateStatus(state);
        }

        private void OnError(string error)
        {
            UpdateStatus("Error: " + error);
        }

        private void UpdateStatus(string state)
        {
            if (_statusLabel == null) return;

            switch (state)
            {
                case "scanning":
                    _statusLabel.text = "Scanning...";
                    _statusLabel.color = new Color(1f, 0.8f, 0.2f);
                    break;
                case "connecting":
                    _statusLabel.text = "Connecting...";
                    _statusLabel.color = new Color(0.2f, 0.6f, 1f);
                    break;
                case "connected":
                    _statusLabel.text = "Connected";
                    _statusLabel.color = new Color(0.2f, 1f, 0.3f);
                    break;
                case "disconnected":
                    _statusLabel.text = "Disconnected";
                    _statusLabel.color = new Color(0.7f, 0.7f, 0.7f);
                    break;
                default:
                    _statusLabel.text = state;
                    _statusLabel.color = new Color(1f, 0.3f, 0.3f);
                    break;
            }
        }

        private void SetTextVisible(bool visible)
        {
            if (_textRoot != null)
                _textRoot.SetActive(visible);
            else if (_textMesh != null)
                _textMesh.gameObject.SetActive(visible);
        }
    }
}
