using System;
using System.Text;
using UnityEngine;

namespace VRL
{
    // 左上の情報表示。各パーツは VrlHud.Lines に行を足す。GPU 時間は FrameTimingManager(取れない環境では出さない)。
    public class VrlHud : MonoBehaviour
    {
        public static event Action<StringBuilder> Lines;
        /// <summary>HUD の下に並べる小さな絵(デバッグ用テクスチャ)。</summary>
        public static event Action<Rect> Draw;

        [Tooltip("H で表示の出し入れ")] public bool visible = true;

        readonly StringBuilder sb = new StringBuilder(1024);
        readonly FrameTiming[] timings = new FrameTiming[1];
        float smoothDt = 0.016f;
        double gpuMs, cpuMs;
        GUIStyle style;

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.H)) visible = !visible;
            smoothDt = Mathf.Lerp(smoothDt, Time.unscaledDeltaTime, 0.05f);
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
            {
                gpuMs = Lerp(gpuMs, timings[0].gpuFrameTime, 0.05);
                cpuMs = Lerp(cpuMs, timings[0].cpuFrameTime, 0.05);
            }
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        void OnGUI()
        {
            if (!visible) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = true };
                style.normal.textColor = Color.white;
            }
            sb.Clear();
            sb.AppendFormat("<b>{0:0.0} fps</b>  ({1:0.00} ms)", 1f / smoothDt, smoothDt * 1000f);
            if (gpuMs > 0) sb.AppendFormat("   GPU {0:0.00} ms  CPU {1:0.00} ms", gpuMs, cpuMs);
            sb.Append('\n');
            Lines?.Invoke(sb);
            sb.Append("\n<color=#aaa>右ドラッグ=見回し WASD/QE=移動 Shift=速く P=自動周回 H=表示</color>");

            var content = new GUIContent(sb.ToString());
            float w = 560f;
            float h = style.CalcHeight(content, w - 16f);
            GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(new Rect(8, 8, w, h + 12), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(16, 14, w - 16, h), content, style);
            Draw?.Invoke(new Rect(8, h + 28, Screen.width - 16, Screen.height - h - 36));
        }
    }
}
