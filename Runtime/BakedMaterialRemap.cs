using System.Collections.Generic;
using UnityEngine;

namespace VLB
{
    /// <summary>
    /// 실행 중 프리팹에서 생성되는 오브젝트의 재질을, 씬 방향광 없이 동작하는 복사본으로 바꾼다.
    /// 씬에 놓인 오브젝트는 VertexLightBaker의 한 번에 적용이 직접 바꾸지만, 프리팹에서 생성되는 것은 생성할 때 바꿔야 한다.
    /// VertexLightBaker가 씬에 하나 두고 짝을 채운다. 생성하는 쪽이 처음 만들 때 Apply를 한 번 부른다 (매 프레임 비용 없음).
    /// 씬에 이 컴포넌트가 없으면 Apply는 아무것도 하지 않는다.
    /// </summary>
    public sealed class BakedMaterialRemap : MonoBehaviour
    {
        [SerializeField] Material[] from = new Material[0];
        [SerializeField] Material[] to = new Material[0];

        static Dictionary<Material, Material> map;
        static bool searched;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            map = null;
            searched = false;
        }

        void OnDestroy()
        {
            // 씬이 바뀌면 다음 씬의 짝을 다시 찾는다
            map = null;
            searched = false;
        }

        // 생성 순서와 상관없이 처음 쓸 때 씬에서 찾는다 (풀이 이 컴포넌트의 Awake보다 먼저 만들 수 있음)
        static Dictionary<Material, Material> Map
        {
            get
            {
                if (searched)
                    return map;
                searched = true;
                var remap = FindFirstObjectByType<BakedMaterialRemap>(FindObjectsInactive.Include);
                if (remap == null)
                    return null;
                map = new Dictionary<Material, Material>();
                for (var i = 0; i < Mathf.Min(remap.from.Length, remap.to.Length); i++)
                    if (remap.from[i] != null && remap.to[i] != null)
                        map[remap.from[i]] = remap.to[i];
                return map;
            }
        }

        /// <summary>root와 그 자식 렌더러의 재질 중 짝이 있는 것을 복사본으로 바꾼다.</summary>
        public static void Apply(GameObject root)
        {
            var m = Map;
            if (m == null || m.Count == 0 || root == null)
                return;

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                var changed = false;
                for (var i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && m.TryGetValue(mats[i], out var copy))
                    {
                        mats[i] = copy;
                        changed = true;
                    }
                }
                if (changed)
                    r.sharedMaterials = mats;
            }
            BakedShadowMap.AddDynamics(root); // 바뀐 재질 기준으로 움직이는 물체 그림자에 넣음
        }
    }
}
