using UnityEngine;

namespace IronFront
{
    /// <summary>RTS 相机：固定俯角，WASD/边缘/中键平移，滚轮缩放。</summary>
    public class RtsCamera : MonoBehaviour
    {
        public float Pitch = 55f;
        public float MinHeight = 18f;
        public float MaxHeight = 45f;
        public float PanSpeed = 30f;
        public float EdgeSize = 18f;
        public float Limit = 42f;

        public Vector3 HomeTarget = new Vector3(-20f, 0f, -21f);

        private Vector3 _target;
        private float _height = 30f;
        private float _targetHeight = 30f;
        private Vector3 _dragOrigin;
        private bool _dragging;

        private void Start()
        {
            _target = HomeTarget;
            _height = 30f;
            _targetHeight = 30f;
            Apply();
        }

        private void Update()
        {
            if (RtsGame.I != null && RtsGame.I.State == GameState.Paused)
            {
                Apply();
                return;
            }

            float dt = Time.unscaledDeltaTime;
            Vector3 dir = Vector3.zero;

            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) dir.z += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) dir.z -= 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) dir.x -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) dir.x += 1f;

            if (dir.sqrMagnitude > 0.01f)
            {
                _target += dir.normalized * PanSpeed * dt * (_height / 34f);
            }

            HandleEdgeScroll(dt);
            HandleMiddleDrag();
            HandleZoom();

            if (Input.GetKeyDown(KeyCode.Home))
            {
                RtsBuilding hq = RtsGame.I != null ? RtsGame.I.PlayerHQ : null;
                _target = hq != null ? hq.transform.position : HomeTarget;
                _targetHeight = 30f;
            }

            _target.x = Mathf.Clamp(_target.x, -Limit, Limit);
            _target.z = Mathf.Clamp(_target.z, -Limit, Limit);
            _height = Mathf.Lerp(_height, _targetHeight, Mathf.Clamp01(dt * 7f));

            Apply();
        }

        private void HandleEdgeScroll(float dt)
        {
            if (!Application.isFocused) return;

            Vector3 m = Input.mousePosition;
            if (m.x < 0f || m.x > Screen.width || m.y < 0f || m.y > Screen.height) return;

            Vector3 dir = Vector3.zero;
            if (m.x < EdgeSize) dir.x -= 1f;
            if (m.x > Screen.width - EdgeSize) dir.x += 1f;
            if (m.y < EdgeSize) dir.z -= 1f;
            if (m.y > Screen.height - EdgeSize) dir.z += 1f;

            if (dir.sqrMagnitude > 0.01f)
            {
                _target += dir.normalized * PanSpeed * dt * (_height / 34f);
            }
        }

        private void HandleMiddleDrag()
        {
            if (Input.GetMouseButtonDown(2))
            {
                _dragging = true;
                _dragOrigin = _target;
            }
            if (Input.GetMouseButtonUp(2)) _dragging = false;

            if (_dragging)
            {
                float k = _height / 34f * 4.5f;
                _target = _dragOrigin + new Vector3(
                    -Input.GetAxis("Mouse X") * k,
                    0f,
                    -Input.GetAxis("Mouse Y") * k);
            }
        }

        private void HandleZoom()
        {
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.001f)
            {
                _targetHeight = Mathf.Clamp(_targetHeight - scroll * 14f, MinHeight, MaxHeight);
            }
        }

        private void Apply()
        {
            float back = _height / Mathf.Tan(Pitch * Mathf.Deg2Rad);
            transform.position = new Vector3(_target.x, _height, _target.z - back);
            transform.rotation = Quaternion.Euler(Pitch, 0f, 0f);
        }
    }
}
