using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VLB
{
    /// <summary>
    /// 실시간 그림자 대신, 빛 방향에서 찍은 그림자 지도(깊이 텍스처)를 배경 셰이더가 읽게 한다 (VLBShadow.hlsl).
    /// - 정적 지도: 배경·해금 오브젝트. 시작할 때와 해금·철거·지역 이동 때만 다시 찍는다.
    /// - 동적 지도: 움직이는 물체(LowCostCharacter·DynamicToon 재질)만. 같은 빛 방향·같은 영역으로 매 프레임 찍는다 (깊이만, 그림자 계산 없음).
    ///   배경 셰이더가 두 지도 중 어두운 쪽을 쓰므로, 움직이는 물체 그림자도 바닥 셰이더 자신의 공식으로 정적 그림자와 같은 색이 되고 겹쳐도 더 진해지지 않는다.
    /// 찍는 영역은 그때 켜져 있는 정적 물체만 감싼다 (지역 하나 크기 → 픽셀이 작아 실시간 그림자와 같은 모양).
    /// VertexLightBaker가 한 번에 적용 때 씬에 하나 두고 빛 방향·그릴 물체 목록을 채운다.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)] // 다른 LateUpdate 가 물체를 옮긴 뒤 찍어야 그림자가 한 프레임 늦지 않음
    public sealed class BakedShadowMap : MonoBehaviour
    {
        [Tooltip("표면에서 빛 쪽 방향 (월드)")]
        [SerializeField] Vector3 lightDir = Vector3.up;
        [Tooltip("정적 그림자 지도 한 변의 픽셀 수. 2048 = 메모리 8MB")]
        [SerializeField] int resolution = 2048;
        [Tooltip("움직이는 물체 그림자 지도 한 변의 픽셀 수 (매 프레임 찍음). 1024 = 메모리 2MB")]
        [SerializeField] int dynamicResolution = 1024;
        [Tooltip("그림자 지도 픽셀 크기 대비 빛 방향 밀기 (표면 얼룩 방지)")]
        [SerializeField] float depthBias = 1.5f;
        [Tooltip("그림자 지도 픽셀 크기 대비 표면 안쪽 밀기 (비스듬한 면 얼룩 방지)")]
        [SerializeField] float normalBias = 1f;
        [SerializeField] Material casterMaterial;
        [Tooltip("그림자 지도에 그릴 물체 (정적 배경 + 해금 오브젝트)")]
        [SerializeField] Renderer[] casters = new Renderer[0];
        [Tooltip("casters 와 같은 순서. 해금 오브젝트면 가장 가까운 표식, 정적 배경이면 비움")]
        [SerializeField] BakedShadowCaster[] owners = new BakedShadowCaster[0];

        // 실행 중에는 쓰지 않음. 씬에 방향광 오브젝트가 없어도 VertexLightBaker 가 이 값으로 다시 구움
#pragma warning disable 0414 // 에디터 도구가 SerializedObject 로만 읽고 씀
        [Header("굽기 기준 빛 (방향광 오브젝트 대신)")]
        [Tooltip("방향광의 회전값과 같음. 바꾼 뒤 Vertex Light Baker 로 다시 적용해야 화면에 반영됨")]
        [SerializeField] Vector3 bakeLightRotation = new Vector3(50f, -30f, 0f);
        [SerializeField] Color bakeLightColor = Color.white;
        [Tooltip("0 = 아직 저장 안 됨 (굽기 도구가 이 값을 쓰지 않음)")]
        [SerializeField] float bakeLightIntensity = 0f;
#pragma warning restore 0414

        static readonly int MapId = Shader.PropertyToID("_VLB_ShadowMap");
        static readonly int DynMapId = Shader.PropertyToID("_VLB_DynShadowMap");
        static readonly int MatrixId = Shader.PropertyToID("_VLB_WorldToShadow");
        static readonly int OnId = Shader.PropertyToID("_VLB_ShadowOn");
        static readonly int BiasId = Shader.PropertyToID("_VLB_ShadowBias");
        static readonly int LightDirId = Shader.PropertyToID("_VLB_ShadowLightDir");

        static BakedShadowMap active;
        RenderTexture map, dynMap;
        CommandBuffer cmd;
        bool dirty = true;
        bool hasView; // 정적 지도를 찍어 아래 행렬이 유효함
        Matrix4x4 view, proj;
        float dynTexel;
        readonly List<Renderer> dynamics = new List<Renderer>();
        readonly HashSet<Renderer> dynamicSet = new HashSet<Renderer>();

        /// <summary>다음 LateUpdate에서 정적 지도를 다시 찍는다 (같은 프레임에 여러 번 불려도 한 번만)</summary>
        public static void RequestRender()
        {
            if (active == null)
                return;
            active.dirty = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        /// <summary>실행 중 생성된 물체(root와 자식)를 움직이는 물체 그림자에 넣는다. BakedMaterialRemap.Apply 가 재질을 바꾼 뒤 부름</summary>
        public static void AddDynamics(GameObject root)
        {
            if (active == null || root == null)
                return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                active.TryAddDynamic(r);
        }

        /// <summary>움직이는 물체 재질(씬 방향광 없이 쓰는 복사본)을 쓰고, 그림자 드리우기가 켜진 렌더러인지</summary>
        public static bool IsDynamicCaster(Renderer r)
        {
            if (r == null || !(r is MeshRenderer || r is SkinnedMeshRenderer) || r.shadowCastingMode == ShadowCastingMode.Off)
                return false;
            foreach (var m in r.sharedMaterials)
                if (m != null && (m.shader.name == "Custom/LowCostCharacter" || m.shader.name == "Custom/DynamicToon"))
                    return true;
            return false;
        }

        void TryAddDynamic(Renderer r)
        {
            // 정적 지도에 들어간 물체(해금 오브젝트 포함)는 켜짐·꺼짐 규칙을 그쪽이 따르므로 제외
            if (IsDynamicCaster(r) && System.Array.IndexOf(casters, r) < 0 && dynamicSet.Add(r))
                dynamics.Add(r);
        }

        void OnEnable()
        {
            active = this;
            RequestRender();
        }

        void OnDisable()
        {
            if (active == this)
                active = null;
            Shader.SetGlobalFloat(OnId, 0f);
            Release(ref map);
            Release(ref dynMap);
            cmd?.Release();
            cmd = null;
            hasView = false;
        }

        static void Release(ref RenderTexture rt)
        {
            if (rt == null)
                return;
            rt.Release();
            DestroyImmediate(rt);
            rt = null;
        }

        static RenderTexture Ensure(ref RenderTexture rt, int size, string name)
        {
            if (rt != null && rt.width == size)
                return rt;
            Release(ref rt);
            rt = new RenderTexture(size, size, 16, RenderTextureFormat.Shadowmap)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        void OnValidate() => dirty = true;

        void LateUpdate()
        {
            if (dirty)
                Render();
            if (hasView)
                RenderDynamic();
        }

        void Render()
        {
            dirty = false;
            hasView = false;
            // 지금 켜진 물체만 감쌈: 지역은 한 번에 하나만 켜지므로, 전 지역(수 km)을 한 장에 담아 픽셀이 수 m 가 되는 일을 막음
            if (casterMaterial == null || !ActiveBounds(casters, out var area) ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Shadowmap))
            {
                // shortcut: 그림자 지도 형식을 지원하지 않는 기기는 그림자 없이 그림. 그런 기기가 대상에 있으면 float 텍스처 비교로 대체할 것
                Shader.SetGlobalFloat(OnId, 0f);
                return;
            }

            Ensure(ref map, resolution, "VLB_ShadowMap");
            Ensure(ref dynMap, dynamicResolution, "VLB_DynShadowMap");
            var worldToShadow = ComputeMatrices(lightDir, area, resolution, out view, out proj, out var texel);
            ComputeMatrices(lightDir, area, dynamicResolution, out _, out _, out dynTexel);

            Shader.SetGlobalVector(LightDirId, lightDir.normalized);
            BeginMap(map, texel);
            for (var i = 0; i < casters.Length; i++)
            {
                var r = casters[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                    continue;
                // 해금 오브젝트는 등장 연출이 끝난 뒤부터, 꺼질 때까지만
                var owner = i < owners.Length ? owners[i] : null;
                if (owner != null && !owner.Shown)
                    continue;
                Draw(r);
            }
            Graphics.ExecuteCommandBuffer(cmd);

            // 움직이는 물체 목록: 지역 이동·해금 때 씬에서 다시 찾음 (실행 중 생성되는 것은 AddDynamics 로 들어옴)
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                TryAddDynamic(r);

            Shader.SetGlobalTexture(MapId, map);
            Shader.SetGlobalTexture(DynMapId, dynMap);
            Shader.SetGlobalMatrix(MatrixId, worldToShadow);
            Shader.SetGlobalFloat(OnId, 1f);
            hasView = true;
        }

        // 움직이는 물체만 매 프레임 같은 시점·영역으로 찍음 (정적 지도와 같은 좌표라 셰이더는 좌표 1번으로 두 지도를 읽음)
        void RenderDynamic()
        {
            BeginMap(dynMap, dynTexel);
            for (var i = dynamics.Count - 1; i >= 0; i--)
            {
                var r = dynamics[i];
                if (r == null) // 파괴됨
                {
                    dynamicSet.Remove(r);
                    dynamics.RemoveAt(i);
                    continue;
                }
                if (r.enabled && r.gameObject.activeInHierarchy)
                    Draw(r);
            }
            Graphics.ExecuteCommandBuffer(cmd);
        }

        void BeginMap(RenderTexture target, float texel)
        {
            cmd ??= new CommandBuffer { name = "VLB Shadow Map" };
            cmd.Clear();
            cmd.SetRenderTarget(target);
            cmd.ClearRenderTarget(true, false, Color.clear);
            cmd.SetViewProjectionMatrices(view, proj);
            cmd.SetGlobalVector(BiasId, new Vector4(texel * depthBias, texel * normalBias, 0f, 0f));
        }

        void Draw(Renderer r)
        {
            var subMeshes = r.sharedMaterials.Length;
            for (var s = 0; s < subMeshes; s++)
                cmd.DrawRenderer(r, casterMaterial, s, 0);
        }

        /// <summary>
        /// 켜져 있는 물체들의 월드 범위. 해금 연출 중(아직 그림자 안 그림)인 물체도 포함해 연출이 끝날 때 범위가 바뀌지 않게 함.
        /// 켜진 물체가 없으면 false
        /// </summary>
        public static bool ActiveBounds(Renderer[] renderers, out Bounds area)
        {
            area = default;
            var found = false;
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                    continue;
                if (found)
                    area.Encapsulate(r.bounds);
                else
                {
                    area = r.bounds;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// 빛 방향에서 영역 전체를 담는 직교 카메라의 뷰·투영과, 월드 → 그림자 지도 좌표(0~1) 변환을 구한다.
        /// URP 의 방향광 그림자와 같은 규칙 (역방향 깊이 버퍼면 z 를 뒤집음).
        /// </summary>
        public static Matrix4x4 ComputeMatrices(Vector3 lightDir, Bounds area, int resolution,
            out Matrix4x4 view, out Matrix4x4 proj, out float texel)
        {
            var forward = lightDir.sqrMagnitude > 0f ? -lightDir.normalized : Vector3.down; // 빛이 나아가는 방향 = 카메라가 보는 방향
            var up = Mathf.Abs(forward.y) > 0.99f ? Vector3.forward : Vector3.up;
            // 카메라 관례: 뷰 공간은 -Z 를 봄
            view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) *
                   Matrix4x4.TRS(area.center, Quaternion.LookRotation(forward, up), Vector3.one).inverse;

            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            for (var i = 0; i < 8; i++)
            {
                var corner = area.center + Vector3.Scale(area.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                var v = view.MultiplyPoint3x4(corner);
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            const float margin = 0.5f;
            proj = Matrix4x4.Ortho(min.x - margin, max.x + margin, min.y - margin, max.y + margin,
                -max.z - margin, -min.z + margin); // 앞쪽 거리 = -z
            texel = Mathf.Max(max.x - min.x, max.y - min.y) / Mathf.Max(1, resolution);

            var p = proj;
            if (SystemInfo.usesReversedZBuffer)
            {
                p.m20 = -p.m20;
                p.m21 = -p.m21;
                p.m22 = -p.m22;
                p.m23 = -p.m23;
            }
            var toTexture = Matrix4x4.identity; // -1~1 → 0~1
            toTexture.m00 = toTexture.m11 = toTexture.m22 = 0.5f;
            toTexture.m03 = toTexture.m13 = toTexture.m23 = 0.5f;
            return toTexture * p * view;
        }
    }
}
