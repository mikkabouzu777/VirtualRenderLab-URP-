using UnityEngine;

namespace VRL
{
    // 動く影の確かめ用。毎秒 degreesPerSecond 度、axis まわりに回す。
    public class VrlSpin : MonoBehaviour
    {
        public Vector3 axis = Vector3.forward;
        public float degreesPerSecond = 40f;

        void Update() => transform.Rotate(axis, degreesPerSecond * Time.deltaTime, Space.Self);
    }
}
