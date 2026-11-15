using UnityEngine;

namespace VRL
{
    // 観客用カメラの代わり。右ドラッグで見回し、WASD/QE で移動、Shift で速く。P で自動の周回(録画やスクショ用)。
    public class VrlFlyCam : MonoBehaviour
    {
        [Tooltip("移動の速さ(m/秒)")] public float speed = 8f;
        [Tooltip("Shift を押した時の倍率")] public float fastMul = 5f;
        [Tooltip("見回しの感度(度/ピクセル)")] public float lookSens = 0.15f;

        [Header("自動の周回(P)")]
        public bool autopilot;
        [Tooltip("周回の中心")] public Vector3 orbitCenter = Vector3.zero;
        [Tooltip("周回の半径(m)")] public float orbitRadius = 40f;
        [Tooltip("周回の高さ(m)")] public float orbitHeight = 6f;
        [Tooltip("1 周の秒数")] public float orbitPeriod = 60f;

        float yaw, pitch, orbitT;

        void Start()
        {
            var e = transform.eulerAngles;
            yaw = e.y; pitch = e.x > 180 ? e.x - 360 : e.x;
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.P)) autopilot = !autopilot;
            if (autopilot)
            {
                orbitT += Time.deltaTime / orbitPeriod;
                float a = orbitT * Mathf.PI * 2f;
                var p = orbitCenter + new Vector3(Mathf.Cos(a) * orbitRadius, orbitHeight, Mathf.Sin(a) * orbitRadius);
                transform.position = p;
                var look = orbitCenter + new Vector3(Mathf.Cos(a + 0.6f) * orbitRadius * 0.5f, orbitHeight * 0.4f, Mathf.Sin(a + 0.6f) * orbitRadius * 0.5f);
                transform.rotation = Quaternion.LookRotation(look - p);
                var e = transform.eulerAngles; yaw = e.y; pitch = e.x > 180 ? e.x - 360 : e.x;
                return;
            }

            if (Input.GetMouseButton(1))
            {
                yaw += Input.GetAxisRaw("Mouse X") * lookSens * 10f;
                pitch -= Input.GetAxisRaw("Mouse Y") * lookSens * 10f;
                pitch = Mathf.Clamp(pitch, -89f, 89f);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0);
            }

            var v = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) v += Vector3.forward;
            if (Input.GetKey(KeyCode.S)) v += Vector3.back;
            if (Input.GetKey(KeyCode.A)) v += Vector3.left;
            if (Input.GetKey(KeyCode.D)) v += Vector3.right;
            if (Input.GetKey(KeyCode.E)) v += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) v += Vector3.down;
            float s = speed * (Input.GetKey(KeyCode.LeftShift) ? fastMul : 1f);
            transform.position += transform.TransformDirection(v) * s * Time.deltaTime;
        }
    }
}
