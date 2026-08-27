using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BleTest
{
    public struct MeasurementPoint
    {
        public int seq;
        public long espTimestampMs;    // T0 from ESP32
        public float unityReceivedMs;  // Time.realtimeSinceStartup * 1000
        public long espRttMs;          // T2 - T0 (from ESP32 RESULTS)
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

    public class LatencyProfiler : MonoBehaviour
    {
        public static LatencyProfiler Instance { get; private set; }

        private BleClientService _bleService;
        private Dictionary<int, MeasurementPoint> _currentPoints = new();
        private List<BatchResult> _allResults = new();
        private bool _collecting;

        public bool IsCollecting => _collecting;
        public List<BatchResult> AllResults => _allResults;

        public event Action<BatchResult> BatchCompleted;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            _bleService = BleClientService.Instance;
            if (_bleService == null)
            {
                Debug.LogError("[LatencyProfiler] BleClientService not found");
                return;
            }

            _bleService.RttMessageReceived += OnRttMessage;
            _bleService.ResultsReceived += OnResults;
        }

        private void OnDestroy()
        {
            if (_bleService != null)
            {
                _bleService.RttMessageReceived -= OnRttMessage;
                _bleService.ResultsReceived -= OnResults;
            }
        }

        public void StartCollecting()
        {
            _currentPoints.Clear();
            _collecting = true;
            Debug.Log("[LatencyProfiler] Collecting started");
        }

        private void OnRttMessage(string text)
        {
            if (!_collecting) return;

            // Format: RTT:<seq>,<T0>
            string[] parts = text.Substring(4).Split(',');
            if (parts.Length < 2) return;

            if (int.TryParse(parts[0], out int seq) && long.TryParse(parts[1], out long t0))
            {
                float unityTime = Time.realtimeSinceStartup * 1000f;
                _currentPoints[seq] = new MeasurementPoint
                {
                    seq = seq,
                    espTimestampMs = t0,
                    unityReceivedMs = unityTime
                };

                _bleService.SendAck(seq);
            }
        }

        private void OnResults(string text)
        {
            if (!_collecting) return;

            // Format: RESULTS:<rate>,<avgRTT>,<minRTT>,<maxRTT>,<jitter>,<received>/<total>
            string[] parts = text.Substring(8).Split(',');
            if (parts.Length < 6) return;

            if (int.TryParse(parts[0], out int rate) &&
                float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float avgRtt) &&
                float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float minRtt) &&
                float.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float maxRtt) &&
                float.TryParse(parts[4], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float jitter))
            {
                string[] receivedParts = parts[5].Split('/');
                int.TryParse(receivedParts[0], out int received);
                int.TryParse(receivedParts[1], out int total);

                // Calculate Unity-side jitter from inter-arrival times
                float unityJitter = CalculateUnityJitter();

                var result = new BatchResult
                {
                    rate = rate,
                    avgRtt = avgRtt,
                    minRtt = minRtt,
                    maxRtt = maxRtt,
                    jitterStddev = unityJitter > 0 ? unityJitter : jitter,
                    received = received,
                    total = total
                };

                _allResults.Add(result);
                _collecting = false;

                Debug.Log($"[LatencyProfiler] Rate={rate} avgRTT={avgRtt:F1}ms jitter={unityJitter:F1}ms received={received}/{total}");
                BatchCompleted?.Invoke(result);
            }
        }

        private float CalculateUnityJitter()
        {
            if (_currentPoints.Count < 2) return 0;

            var ordered = _currentPoints.Values.OrderBy(p => p.unityReceivedMs).ToList();
            var intervals = new List<float>();

            for (int i = 1; i < ordered.Count; i++)
            {
                intervals.Add(ordered[i].unityReceivedMs - ordered[i - 1].unityReceivedMs);
            }

            float mean = intervals.Average();
            float sumSq = intervals.Sum(d => (d - mean) * (d - mean));
            return Mathf.Sqrt(sumSq / intervals.Count);
        }

        public void SaveToCsv()
        {
            if (_allResults.Count == 0)
            {
                Debug.LogWarning("[LatencyProfiler] No results to save");
                return;
            }

            string path = Path.Combine(Application.persistentDataPath, "latency_results.csv");
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("rate,avgRtt_ms,minRtt_ms,maxRtt_ms,jitter_ms,received,total,timestamp");
                foreach (var r in _allResults)
                {
                    writer.WriteLine($"{r.rate},{r.avgRtt:F1},{r.minRtt},{r.maxRtt},{r.jitterStddev:F1},{r.received},{r.total},{DateTime.Now:O}");
                }
            }

            Debug.Log($"[LatencyProfiler] CSV saved: {path}");
        }

        public void Clear()
        {
            _allResults.Clear();
            _currentPoints.Clear();
            _collecting = false;
        }
    }
}
