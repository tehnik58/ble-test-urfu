using System;
using System.Collections;
using UnityEngine;

namespace BleTest
{
    public class LatencyTestRunner : MonoBehaviour
    {
        [SerializeField] private TMPro.TextMeshPro _statusText;

        private BleClientService _bleService;
        private LatencyProfiler _profiler;
        private bool _testInProgress;
        private bool _batchComplete;
        private Coroutine _runAllCoroutine;

        private static readonly int[] TestRates = { 20, 30, 60, 90, 120 };

        private void Start()
        {
            _bleService = BleClientService.Instance;
            _profiler = LatencyProfiler.Instance;

            if (_bleService == null)
                Debug.LogError("[LatencyTestRunner] BleClientService not found");
            if (_profiler == null)
                Debug.LogError("[LatencyTestRunner] LatencyProfiler not found");

            if (_profiler != null)
                _profiler.BatchCompleted += OnBatchCompleted;
        }

        private void OnDestroy()
        {
            if (_profiler != null)
                _profiler.BatchCompleted -= OnBatchCompleted;
        }

        public void RunAll()
        {
            if (_runAllCoroutine != null) return;
            _runAllCoroutine = StartCoroutine(RunAllTests());
        }

        public void RunSingle(int rate)
        {
            if (_testInProgress) return;
            StartCoroutine(RunSingleTest(rate));
        }

        public void ExportCsv()
        {
            _profiler?.SaveToCsv();
        }

        private IEnumerator RunAllTests()
        {
            _profiler?.Clear();
            SetStatus("Starting all tests...");

            foreach (int rate in TestRates)
            {
                yield return new WaitForSeconds(2f);
                yield return StartCoroutine(RunSingleTest(rate));
            }

            _profiler?.SaveToCsv();
            SetStatus($"All tests completed. CSV saved.");
            Debug.Log("[LatencyTestRunner] All tests completed");

            _runAllCoroutine = null;
        }

        private IEnumerator RunSingleTest(int rate)
        {
            _testInProgress = true;
            _batchComplete = false;
            _profiler?.StartCollecting();

            SetStatus($"Test {rate} msg/sec: sending...");
            Debug.Log($"[LatencyTestRunner] Starting test at {rate} msg/sec");

            _bleService?.StartLatencyTest(rate);

            float timeout = 30f;
            float elapsed = 0f;
            while (!_batchComplete && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!_batchComplete)
            {
                SetStatus($"Test {rate} msg/sec: TIMEOUT");
                Debug.LogWarning($"[LatencyTestRunner] Test {rate} timed out");
            }

            _testInProgress = false;
        }

        private void OnBatchCompleted(BatchResult result)
        {
            _batchComplete = true;
            string msg = $"Rate={result.rate} RTT={result.avgRtt:F1}ms " +
                         $"jitter={result.jitterStddev:F1}ms " +
                         $"received={result.received}/{result.total}";
            SetStatus(msg);
            Debug.Log($"[LatencyTestRunner] {msg}");
        }

        private void SetStatus(string text)
        {
            if (_statusText != null)
                _statusText.text = text;
            Debug.Log($"[LatencyTestRunner] {text}");
        }
    }
}
