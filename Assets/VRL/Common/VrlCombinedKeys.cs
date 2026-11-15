using System.Text;
using UnityEngine;

namespace VRL
{
    // 合体シーンのキー。3 つの部品は自分ではキーを読まず(handleKeys=false)、ここでまとめて切り替える。
    public class VrlCombinedKeys : MonoBehaviour
    {
        public Meshlet.MeshletField meshlet;
        public VSM.VsmManager vsm;
        public RVT.RvtManager rvt;

        int view;
        static readonly string[] ViewNames = { "仕上がり", "Meshlet 塊", "Meshlet DAG の段", "VSM ページ", "VSM 描き直したページ", "RVT mip", "Meshlet Hi-Z の 2 回目" };

        void OnEnable() { VrlHud.Lines += Hud; }
        void OnDisable() { VrlHud.Lines -= Hud; }

        void Start()
        {
            if (meshlet) meshlet.handleKeys = false;
            if (vsm) vsm.handleKeys = false;
            if (rvt) rvt.handleKeys = false;
        }

        void Update()
        {
            for (int i = 0; i < ViewNames.Length; i++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) SetView(i);
            if (Input.GetKeyDown(KeyCode.V) && vsm) vsm.SetEnabled(!vsm.vsmEnabled);
            if (Input.GetKeyDown(KeyCode.N) && vsm) vsm.caching = !vsm.caching;
            if (Input.GetKeyDown(KeyCode.B) && rvt) rvt.direct = !rvt.direct;
            if (Input.GetKeyDown(KeyCode.F) && meshlet) meshlet.Frozen = !meshlet.Frozen;
            if (Input.GetKeyDown(KeyCode.O) && meshlet) meshlet.occlusionCull = !meshlet.occlusionCull;
            if (Input.GetKeyDown(KeyCode.R) && meshlet) meshlet.SoftwareRaster = !meshlet.SoftwareRaster;
        }

        public void SetView(int v)
        {
            view = v;
            if (meshlet) meshlet.DebugMode = v == 1 ? 1 : v == 2 ? 3 : v == 6 ? 5 : 0;
            if (vsm) vsm.debugMode = v == 3 ? 1 : v == 4 ? 3 : 0;
            if (rvt) rvt.debugMode = v == 5 ? 1 : 0;
        }

        void Hud(StringBuilder sb)
        {
            sb.AppendFormat("<b>合体</b>  [1-7] 表示: {0}   [V] 影: {1}   [N] VSM 持ち越し: {2}   [B] 地面: {3}   [F] 岩の外す判定を止める: {4}   [O] Hi-Z: {5}   [R] ソフトウェアラスタ: {6}",
                ViewNames[view],
                vsm && vsm.vsmEnabled ? "VSM" : "URP",
                vsm && vsm.caching ? "入" : "<color=#f88>切</color>",
                rvt && rvt.direct ? "毎画素計算" : "RVT",
                meshlet && meshlet.Frozen ? "<color=#f88>止め中</color>" : "切",
                meshlet && meshlet.occlusionCull ? "入" : "<color=#f88>切</color>",
                meshlet && meshlet.SoftwareRaster ? "入" : "<color=#f88>切</color>");
            sb.AppendLine();
        }
    }
}
