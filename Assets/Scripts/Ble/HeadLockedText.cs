using UnityEngine;

namespace BleTest
{
    /// <summary>
    /// Places text 1.5m in front of the camera (head-locked / billboard).
    /// FR-4: 3D text at fixed distance, always facing the user.
    /// </summary>
    public class HeadLockedText : MonoBehaviour
    {
        [SerializeField] private float _distance = 0.35f;
        [SerializeField] private float _followSpeed = 8f;

        private Transform _cameraTransform;

        private void Start()
        {
            var mainCam = Camera.main;
            if (mainCam != null)
            {
                _cameraTransform = mainCam.transform;
            }
        }

        private void LateUpdate()
        {
            if (_cameraTransform == null) return;

            Vector3 targetPos = _cameraTransform.position + _cameraTransform.forward * _distance;
            transform.position = Vector3.Lerp(transform.position, targetPos, _followSpeed * Time.deltaTime);

            transform.rotation = Quaternion.LookRotation(_cameraTransform.position - transform.position);
        }
    }
}
