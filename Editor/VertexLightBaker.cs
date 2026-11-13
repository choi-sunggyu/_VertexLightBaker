// 메뉴: Tools > Vertex Light Baker
//
// 목표: 실행 중에는 방향광·실시간 그림자를 쓰지 않고, 에디터에서 미리 계산한 값으로 같은 모습을 낸다.
//
// 하는 일
//  1) 밝기: 방향광 기준으로 정점마다 주변광과 N·L 을 계산해 메시 복사본의 UV 채널 3번(TEXCOORD3)에 저장
//     (면이 빛을 받는 정도는 부드럽게 변하므로 정점으로 충분)
//  2) 그림자: 씬에 BakedShadowMap 을 두고, 빛 방향에서 찍은 그림자 지도를 시작·켜짐·꺼짐 때만 찍어 저장
//     (그림자 모양은 픽셀마다 지도에서 읽으므로 실시간 그림자와 같은 모양)
//  3) 원본 모델 파일은 건드리지 않고, 복사본 메시를 저장 폴더에 에셋으로 저장
//  4) (선택) 한 번에 적용: 방향광 값을 BakedShadowMap 에 저장하고 방향광 지우기 + 재질 복사본 교체
//     (이후에는 방향광 오브젝트 없이 저장된 값으로 다시 구움)
//
// 그림자 관련 옵션
//  - 그림자 길이 줄이기: 그림자 지도를 찍는 방향만 위쪽으로 기울임 (명암은 그대로)
//  - 그림자 제외: 지정한 오브젝트가 그림자를 드리우지 않게 함
//  - 켜졌다 꺼지는 오브젝트: BakedShadowCaster 표식이 있으면 켜져 있을 때만 그림자를 드리움

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using VLB;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

[assembly: InternalsVisibleTo("VLB.Editor.Tests")]

public class VertexLightBaker : EditorWindow
{
    /// <summary>
    /// 프로젝트 전용 "함께 켜지고 꺼지는 오브젝트 묶음" 찾기 (예: 해금 시스템의 대상 목록).
    /// 굽기 때마다 불리고, 돌려준 오브젝트에는 BakedShadowCaster 표식이 자동으로 붙는다.
    /// 비워 두면 씬에 직접 붙인 BakedShadowCaster 만 사용한다.
    /// </summary>
    public static Func<List<List<GameObject>>> ExtraToggleGroups;

    const int BakeUVChannel = 3;          // 셰이더의 TEXCOORD3 와 짝을 이룸
    const string BakedSuffix = "_VLBaked"; // 이 도구가 만든 메시 파일 이름 표식

    Color ambientColor = new Color(0.30f, 0.32f, 0.38f);

    bool bakeShadows = true;
    LayerMask occluderMask = ~0;
    int shadowResolution = 2048;

    string outputFolder = "Assets/BakedMeshes";
    Shader bakedShader;

    // 주변광: 씬의 환경광(Environment Lighting)을 법선 방향별로 읽어 원본 URP Lit 의 그늘 밝기와 맞춤
    bool useSceneAmbient = true;

    // 그림자 모양
    float shadowSteepen = 0f;     // 0 = 빛 방향 그대로, 1 = 바로 위에서 (그림자 길이 0). 명암(N·L)에는 영향 없음

    // 그림자를 만들지 않을 오브젝트. 자기 자신의 명암은 정상적으로 구워짐
    [SerializeField] List<GameObject> noCastRoots = new List<GameObject>();
    SerializedObject windowSO;

    // 해금 오브젝트(켜졌다 꺼졌다 하는 것)는 켜져 있을 때만 그림자를 드리우고, 꺼져 있어도 밝기는 구워 둠
    bool separateUnlockShadows = true;

    // 보고서용 통계
    int statNoCast, statCasters;
    long statVertsAfter;
    bool statAmbientFallback;
    string shadowMapSummary = "-";

    [MenuItem("Tools/Vertex Light Baker")]
    static void Open() => GetWindow<VertexLightBaker>("Vertex Light Baker");

    void OnEnable()
    {
        if (bakedShader == null) bakedShader = Shader.Find("Custom/BakedVertexLit");
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("광원", EditorStyles.boldLabel);
        DrawBakeLightStatus();
        useSceneAmbient = EditorGUILayout.Toggle("씬 환경광 사용", useSceneAmbient);
        using (new EditorGUI.DisabledScope(useSceneAmbient))
            ambientColor = EditorGUILayout.ColorField("주변광 색 (환경광 미사용 시)", ambientColor);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("그림자 (저장된 그림자 지도)", EditorStyles.boldLabel);
        bakeShadows = EditorGUILayout.Toggle("그림자 지도 설정", bakeShadows);
        using (new EditorGUI.DisabledScope(!bakeShadows))
        {
            occluderMask = LayerMaskField("그림자를 만드는 레이어", occluderMask);
            shadowResolution = EditorGUILayout.IntPopup("그림자 지도 해상도", shadowResolution,
                new[] { "1024 (2MB)", "2048 (8MB)", "4096 (32MB)" }, new[] { 1024, 2048, 4096 });
            shadowSteepen = EditorGUILayout.Slider("그림자 길이 줄이기 (0~1)", shadowSteepen, 0f, 1f);

            separateUnlockShadows = EditorGUILayout.Toggle(new GUIContent("켜고 끄는 오브젝트 그림자 따로",
                "BakedShadowCaster 표식이 있는 오브젝트는 실행 중 켜져 있을 때만 그림자를 드리움 (꺼져 있어도 밝기는 구움)"),
                separateUnlockShadows);

            if (windowSO == null) windowSO = new SerializedObject(this);
            windowSO.Update();
            EditorGUILayout.PropertyField(windowSO.FindProperty("noCastRoots"),
                new GUIContent("그림자 제외", "여기 넣은 오브젝트와 그 자식은 그림자를 드리우지 않음"), true);
            windowSO.ApplyModifiedProperties();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("저장 / 셰이더", EditorStyles.boldLabel);
        outputFolder = EditorGUILayout.TextField("저장 폴더", outputFolder);
        bakedShader = (Shader)EditorGUILayout.ObjectField("교체할 셰이더", bakedShader, typeof(Shader), false);

        EditorGUILayout.Space();
        if (GUILayout.Button("선택한 오브젝트 굽기", GUILayout.Height(30)))
            Bake(CollectTargets(Selection.gameObjects));

        if (GUILayout.Button("씬의 Static 오브젝트 전체 굽기", GUILayout.Height(30)))
            Bake(CollectStaticTargets(ToggleSet()));

        if (GUILayout.Button("안 쓰는 구운 파일 정리"))
            CleanupUnusedBakedAssets(report: true);

        if (GUILayout.Button("선택한 오브젝트 재질 셰이더 교체"))
            SwapShader(CollectTargets(Selection.gameObjects));

        EditorGUILayout.Space(12);
        EditorGUILayout.LabelField("한 번에 적용", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Static 전체 굽기·그림자 지도 설정 → 방향광 값 저장 후 지우기 → 배경/움직이는 물체 재질 복사본 교체를 한 번에 실행합니다. " +
            "씬을 크게 바꾸므로 깃에 커밋한 뒤 실행하세요.", MessageType.Info);
        if (GUILayout.Button("한 번에 적용", GUILayout.Height(36)))
            ApplyCaseC();
    }

    // ───────────────────────── 굽기 ─────────────────────────

    // 실제로 구운 MeshFilter 목록을 돌려줌 (취소·실패 시 그때까지 구운 것만). 그림자 지도 설정도 함께 갱신
    List<MeshFilter> Bake(List<MeshFilter> targets)
    {
        var baked = new List<MeshFilter>();
        if (!TryGetBakeLight(out Vector3 dirToLight, out _))
        {
            EditorUtility.DisplayDialog("Vertex Light Baker", NoLightMessage, "확인");
            return baked;
        }
        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog("Vertex Light Baker", "구울 오브젝트가 없습니다.", "확인");
            return baked;
        }

        EnsureFolder(outputFolder);

        Color ambient = ambientColor.linear; // 선형 색 공간에서 셰이더와 값을 맞춤

        statNoCast = 0; statCasters = 0; statVertsAfter = 0; statAmbientFallback = false; shadowMapSummary = "-";
        if (useSceneAmbient && IsProbeBlack(RenderSettings.ambientProbe))
        {
            statAmbientFallback = true;
            Debug.LogWarning("[VertexLightBaker] 씬 환경광이 비어 있어 '주변광 색' 값을 사용합니다. " +
                             "Lighting 창에서 Generate Lighting 을 한 번 실행하면 환경광이 계산됩니다.");
        }

        var usedThisRun = new HashSet<Mesh>();
        try
        {
            for (int t = 0; t < targets.Count; t++)
            {
                var mf = targets[t];
                if (EditorUtility.DisplayCancelableProgressBar("정점 밝기 굽기", mf.name, (float)t / targets.Count))
                    break;
                BakeOne(mf, dirToLight, ambient, usedThisRun);
                baked.Add(mf);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
        }

        if (bakeShadows)
            shadowMapSummary = SetupShadowMap(dirToLight);

        Debug.Log($"[VertexLightBaker] {baked.Count}개 오브젝트 굽기 완료 / 정점 {statVertsAfter:N0} / 그림자 지도: {shadowMapSummary}");
        return baked;
    }

    // 씬에 BakedShadowMap 하나를 두고, 빛 방향·영역·그림자를 드리울 물체 목록을 채움. 요약 문장을 돌려줌
    string SetupShadowMap(Vector3 dirToLight)
    {
        Shader casterShader = Shader.Find("Hidden/VLB/ShadowCaster");
        if (casterShader == null) return "건너뜀 (Hidden/VLB/ShadowCaster 셰이더 없음)";

        // 해금 오브젝트에 표식을 붙여, 켜지고 꺼질 때 그림자 지도를 다시 찍게 함
        HashSet<GameObject> toggles = null;
        if (separateUnlockShadows)
        {
            var groups = CollectToggleGroups();
            EnsureCasterMarkers(groups);
            statCasters = groups.Count;
            toggles = Flatten(groups);
        }

        var casters = new List<Renderer>();
        var owners = new List<BakedShadowCaster>();
        foreach (var mr in FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!mr.gameObject.scene.IsValid() || !mr.enabled || !VisibleSometime(mr.transform, toggles)) continue;
            // 가장 가까운 해금 오브젝트 표식 (해금 대상 안의 시설 레벨 모델이면 그 레벨 모델)
            var owner = toggles != null ? mr.GetComponentInParent<BakedShadowCaster>(true) : null;
            if (!mr.gameObject.isStatic && owner == null) continue; // 정적 배경과 해금 오브젝트만 (움직이는 물체는 바닥 투영 그림자)
            if (mr.shadowCastingMode == ShadowCastingMode.Off) continue;
            if (((1 << mr.gameObject.layer) & occluderMask.value) == 0) continue;
            if (IsUnderAny(mr.transform, noCastRoots)) { statNoCast++; continue; }
            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            casters.Add(mr);
            owners.Add(owner);
        }
        if (casters.Count == 0) return "건너뜀 (그림자를 드리울 물체 없음)";

        string matFolder = outputFolder.TrimEnd('/') + "/Materials";
        EnsureFolder(matFolder);
        string matPath = $"{matFolder}/VLB_ShadowCaster.mat";
        var casterMat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (casterMat == null)
        {
            casterMat = new Material(casterShader) { name = "VLB_ShadowCaster" };
            AssetDatabase.CreateAsset(casterMat, matPath);
        }

        var map = FindOrCreateShadowMap();

        // 그림자 길이 줄이기: 지도를 찍는 방향만 위쪽으로 기울임 (명암 N·L 은 원래 빛 방향 그대로)
        Vector3 shadowDir = Vector3.Slerp(dirToLight, Vector3.up, shadowSteepen).normalized;

        var so = new SerializedObject(map);
        so.FindProperty("lightDir").vector3Value = shadowDir;
        so.FindProperty("resolution").intValue = shadowResolution;
        so.FindProperty("casterMaterial").objectReferenceValue = casterMat;
        var castersProp = so.FindProperty("casters");
        var ownersProp = so.FindProperty("owners");
        castersProp.arraySize = casters.Count;
        ownersProp.arraySize = casters.Count;
        for (int i = 0; i < casters.Count; i++)
        {
            castersProp.GetArrayElementAtIndex(i).objectReferenceValue = casters[i];
            ownersProp.GetArrayElementAtIndex(i).objectReferenceValue = owners[i];
        }
        so.ApplyModifiedProperties();
        BakedShadowMap.RequestRender();
        EditorSceneManager.MarkSceneDirty(map.gameObject.scene);

        int unlockCasters = owners.FindAll(o => o != null).Count;
        return $"{shadowResolution}×{shadowResolution} (실행 중 켜진 물체만 감쌈), 물체 {casters.Count}개(켜고 끄는 것 {unlockCasters}개)";
    }

    // ───────────────────────── 한 번에 적용 ─────────────────────────
    // 원칙: 원본 재질은 절대 수정하지 않는다. 항상 복사본(저장 폴더/Materials)을 만들어 갈아 끼운다.
    //  - 배경(Static)  + URP 기본 셰이더 → Custom/BakedVertexLit 복사본
    //  - 배경(Static)  + 툰 셰이더        → Custom/BakedToon 복사본 (툰 색·명암 단계 값을 옮겨 스타일 유지)
    //  - 움직이는 물체 + URP 기본 셰이더 → Custom/LowCostCharacter 복사본
    //  - 움직이는 물체 + 툰 셰이더, 외곽선 셰이더, 그 외 커스텀 셰이더 → 그대로 둠

    static readonly HashSet<string> UrpLitShaders = new HashSet<string>
    {
        "Universal Render Pipeline/Lit",
        "Universal Render Pipeline/Simple Lit",
        "Universal Render Pipeline/Complex Lit",
        "Universal Render Pipeline/Baked Lit",
    };

    static bool IsOurShader(Shader s) =>
        s.name == "Custom/BakedVertexLit" || s.name == "Custom/BakedToon" || s.name == "Custom/LowCostCharacter" ||
        s.name == "Custom/DynamicToon";

    static bool IsOutline(Shader s) => s.name.IndexOf("outline", System.StringComparison.OrdinalIgnoreCase) >= 0;

    // 주 방향광을 받는 셰이더인지 (URP Lit 계열, Lit 셰이더 그래프, 대부분의 서드파티 Lit).
    // 방향광 그림자 키워드를 선언했는지로 판단: 이름과 무관하게 "방향광을 지우면 어두워지는 셰이더"를 찾음
    internal static bool UsesMainLight(Shader s)
    {
        if (s == null || IsOurShader(s)) return false;
        foreach (string k in s.keywordSpace.keywordNames)
            if (k.StartsWith("_MAIN_LIGHT_SHADOWS", StringComparison.Ordinal)) return true;
        return false;
    }

    static bool IsToon(Shader s) =>
        !IsOutline(s) &&
        (s.name.IndexOf("toon", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
         s.name.IndexOf("toony", System.StringComparison.OrdinalIgnoreCase) >= 0);

    void ApplyCaseC()
    {
        if (!EditorUtility.DisplayDialog("한 번에 적용",
                "현재 씬을 방향광 없는 구운 조명으로 바꿉니다.\n원본 재질은 수정하지 않고 복사본을 만듭니다.\n" +
                "씬에 방향광이 있으면 그 값을 BakedShadowMap 에 저장하고 방향광을 지웁니다 (Ctrl+Z 로 되돌림).\n" +
                "깃에 커밋해 두었나요?", "적용", "취소"))
            return;

        Shader vertexLit = bakedShader != null ? bakedShader : Shader.Find("Custom/BakedVertexLit");
        Shader bakedToon = Shader.Find("Custom/BakedToon");
        Shader dynamicShader = Shader.Find("Custom/LowCostCharacter");
        Shader dynamicToon = Shader.Find("Custom/DynamicToon");
        if (vertexLit == null || bakedToon == null || dynamicShader == null || dynamicToon == null)
        {
            EditorUtility.DisplayDialog("한 번에 적용",
                "셰이더 4개(BakedVertexLit, BakedToon, LowCostCharacter, DynamicToon)를 찾지 못했습니다.\n패키지 설치와 URP 를 확인하세요.", "확인");
            return;
        }
        // 1) 굽기 기준 빛 (방향·색). 씬 방향광이 있으면 그 값, 없으면 BakedShadowMap 에 저장된 값
        if (!TryGetBakeLight(out Vector3 bakedLightDir, out Color bakedLightColor))
        {
            EditorUtility.DisplayDialog("한 번에 적용", NoLightMessage, "확인");
            return;
        }
        Light sceneLight = FindMainDirectionalLight();

        // 2) Static 전체 굽기 + 소요 시간 측정 (꺼져 있는 해금 오브젝트도 포함)
        var toggles = ToggleSet();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<MeshFilter> baked = Bake(CollectStaticTargets(toggles));
        sw.Stop();
        if (baked.Count == 0) return;

        // 3) 재질 교체 (항상 복사본)
        string matFolder = outputFolder.TrimEnd('/') + "/Materials";
        EnsureFolder(matFolder);
        var ctx = new ConvertContext
        {
            folder = matFolder,
            lightColor = bakedLightColor,
            lightDir = bakedLightDir,
            dynamicToon = dynamicToon,
            ambientColor = ambientColor,
            useSceneAmbient = useSceneAmbient && !statAmbientFallback,
        };

        int staticConverted = 0, dynamicConverted = 0;
        foreach (var mf in baked)
        {
            var r = mf.GetComponent<MeshRenderer>();
            if (r == null) continue;
            staticConverted += ConvertRenderer(r, isDynamic: false, vertexLit, bakedToon, dynamicShader, ctx);
        }
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue; // 파티클·선·UI 제외
            if (!VisibleSometime(r.transform, toggles) || r.gameObject.isStatic) continue;
            dynamicConverted += ConvertRenderer(r, isDynamic: true, vertexLit, bakedToon, dynamicShader, ctx);
        }
        // 3-2) 방향광: 값을 씬(BakedShadowMap)에 저장. 변환하지 못해 아직 방향광으로 밝기를 내는 재질이 남아 있으면
        //      지우지 않음 (지우면 그 물체들이 주변광만 받아 밤처럼 어두워짐). 이후 굽기는 저장된 값으로 함
        string lightResult = "없음 (BakedShadowMap 에 저장된 값 사용)";
        List<string> needLight = MaterialsNeedingLight(toggles);
        if (sceneLight != null)
        {
            SaveBakeLight(sceneLight, FindOrCreateShadowMap());
            if (needLight.Count > 0)
                lightResult = "값을 저장했지만 지우지 않음: 방향광이 필요한 재질이 남음 (아래 목록)";
            else
                lightResult = RemoveLight(sceneLight)
                    ? "값을 BakedShadowMap 에 저장하고 지움"
                    : "값을 BakedShadowMap 에 저장하고 꺼 둠 (같은 오브젝트에 다른 컴포넌트·자식이 있어 지우지 않음)";
        }
        else if (needLight.Count > 0)
            lightResult = "없음. 방향광이 필요한 재질이 남아 그 물체들은 어둡게 보임 (아래 목록)";

        // 3-3) 실행 중 프리팹에서 생성되는 오브젝트는 생성 시 바꿔 끼우도록 재질 짝을 저장 (BakedMaterialRemap.Apply)
        int remapped = SetupMaterialRemap(vertexLit, bakedToon, dynamicShader, ctx);
        AssetDatabase.SaveAssets();

        // 움직이는 물체 그림자: BakedShadowMap 이 움직이는 물체 재질(LowCostCharacter·DynamicToon)을 쓰는 렌더러를
        // 같은 시점의 동적 그림자 지도에 매 프레임 찍음 (별도 설정 없음)

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        // 5) 이전 실행에서 남아 이제 아무도 쓰지 않는 메시·재질 복사본 삭제
        int cleaned = CleanupUnusedBakedAssets(report: false);

        // 6) 보고서용 기록
        float meshMB = FolderSizeMB(outputFolder, "*.asset", matFolder);
        string report =
            $"방향광: {lightResult}\n" +
            $"굽기: {baked.Count}개 오브젝트, {sw.Elapsed.TotalSeconds:F1}초\n" +
            $"안 쓰는 구운 파일 삭제: {cleaned}개\n" +
            $"메시 복사본 폴더 용량: {meshMB:F2} MB\n" +
            $"정점: {statVertsAfter:N0}개 (그림자용 분할 없음, 원본 정점 수)\n" +
            $"그림자 지도: {shadowMapSummary}\n" +
            $"주변광: {(ctx.useSceneAmbient ? "씬 환경광" : "주변광 색 값")}\n" +
            $"재질 교체: 배경 {staticConverted}칸(툰 {ctx.toonSlots}칸), 움직이는 물체 {dynamicConverted}칸, 복사본 {ctx.copies.Count}개\n" +
            $"툰 값(밝은 색·그림자 색·단계) 옮김: {ctx.toonParamsFound}/{ctx.toonCopies}개 재질\n" +
            $"실행 중 생성 오브젝트 재질 짝: {remapped}개\n" +
            (ctx.noMainTexture.Count > 0 ? $"주 텍스처를 못 찾은 재질(색 확인 필요): {string.Join(", ", ctx.noMainTexture)}\n" : "") +
            $"그림자 제외 오브젝트: {statNoCast}개 / 켜고 끄는 오브젝트 묶음: {statCasters}개 / 그림자 길이 줄이기: {shadowSteepen:F2}\n" +
            (ctx.skipped.Count > 0 ? $"그대로 둔 셰이더: {string.Join(", ", ctx.skipped)}" : "그대로 둔 셰이더: 없음") +
            (needLight.Count > 0 ? $"\n방향광이 필요한 재질 {needLight.Count}개: {Preview(needLight, 10)}" : "");
        if (needLight.Count > 0)
            Debug.LogWarning("[VertexLightBaker] 변환하지 못해 방향광이 필요한 재질이 남았습니다. 방향광을 지우지 않았습니다.\n" +
                             string.Join("\n", needLight));

        Debug.Log("[VertexLightBaker] 한 번에 적용\n" + report);
        EditorUtility.DisplayDialog("한 번에 적용 완료", report + "\n\n씬을 저장(Ctrl+S)한 뒤 빌드하세요.", "확인");
    }


    // 실행 중 보이는 렌더러 중, 변환하지 못해 아직 방향광으로 밝기를 내는 재질 ("재질 (셰이더)")
    static List<string> MaterialsNeedingLight(HashSet<GameObject> toggles)
    {
        var found = new SortedSet<string>();
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!(r is MeshRenderer || r is SkinnedMeshRenderer) || !r.enabled || !r.gameObject.scene.IsValid()) continue;
            if (!VisibleSometime(r.transform, toggles)) continue;
            foreach (var m in r.sharedMaterials)
                if (m != null && UsesMainLight(m.shader)) found.Add($"{m.name} ({m.shader.name})");
        }
        return new List<string>(found);
    }

    static string Preview(List<string> items, int max) =>
        string.Join(", ", items.GetRange(0, Math.Min(max, items.Count))) + (items.Count > max ? $" 외 {items.Count - max}개" : "");

    class ConvertContext
    {
        public string folder;
        public Color lightColor, ambientColor;
        public Vector3 lightDir;   // 표면에서 빛 쪽 (월드). 움직이는 물체 셰이더가 씬 방향광 대신 씀
        public Shader dynamicToon;
        public bool useSceneAmbient;
        public readonly Dictionary<(Material, Shader), Material> copies = new Dictionary<(Material, Shader), Material>();
        public readonly HashSet<string> skipped = new HashSet<string>();
        public readonly HashSet<string> noMainTexture = new HashSet<string>();
        public int toonSlots, toonCopies, toonParamsFound;
    }

    // 렌더러의 재질 칸마다 알맞은 대상 셰이더의 "복사본"으로 교체. 바꾼 칸 수를 돌려줌
    int ConvertRenderer(Renderer r, bool isDynamic, Shader vertexLit, Shader bakedToon, Shader dynamicShader, ConvertContext ctx)
    {
        Material[] mats = r.sharedMaterials;
        bool slotChanged = false;
        int converted = 0;

        for (int i = 0; i < mats.Length; i++)
        {
            Material m = mats[i];
            if (m == null) continue;
            if (IsOurShader(m.shader))
            {
                // 이미 복사본이면 교체하지 않고, 빛 방향·색만 이번 기준으로 갱신 (이전 실행의 복사본도 씬 방향광 없이 동작하도록)
                SetLightProps(m, ctx);
                continue;
            }

            Material copy = CopyFor(m, isDynamic, vertexLit, bakedToon, dynamicShader, ctx);
            if (copy == null) continue;

            if (copy.shader == bakedToon || copy.shader == ctx.dynamicToon) ctx.toonSlots++;
            mats[i] = copy;
            slotChanged = true;
            converted++;
        }

        if (slotChanged)
        {
            Undo.RecordObject(r, "VLB Renderer");
            r.sharedMaterials = mats;
        }
        return converted;
    }

    // 원본 재질에 맞는 대상 셰이더의 복사본 (같은 원본·대상이면 한 번만 만듦). 바꿀 수 없는 재질이면 null
    Material CopyFor(Material m, bool isDynamic, Shader vertexLit, Shader bakedToon, Shader dynamicShader, ConvertContext ctx)
    {
        Shader target;
        if (IsToon(m.shader)) target = isDynamic ? ctx.dynamicToon : bakedToon;
        else if (UrpLitShaders.Contains(m.shader.name) || (!IsOutline(m.shader) && UsesMainLight(m.shader)))
            target = isDynamic ? dynamicShader : vertexLit;
        else { ctx.skipped.Add(m.shader.name); return null; }

        // 투명 재질은 제외 (구운 셰이더는 불투명·알파 컷만 지원). 알파 컷 재질(AlphaTest 큐 2450)은 변환함
        bool transparent = (m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f) ||
                           m.renderQueue > (int)RenderQueue.GeometryLast;
        if (transparent) { ctx.skipped.Add($"{m.name}(투명)"); return null; }

        var key = (m, target);
        if (ctx.copies.TryGetValue(key, out Material copy)) return copy;

        string suffix = target == bakedToon ? "_BakedToon" : target == ctx.dynamicToon ? "_DynamicToon"
                      : isDynamic ? "_Dynamic" : "_Baked";
        copy = new Material(target) { name = m.name + suffix };

        if (!TransferMainProps(m, copy)) ctx.noMainTexture.Add(m.name);
        CopyIfExists(m, copy, "_UseVertexColor");
        TransferAlphaClip(m, copy);
        SetLightProps(copy, ctx);

        if (target == bakedToon || target == ctx.dynamicToon)
        {
            ctx.toonCopies++;
            if (TransferToonProps(m, copy)) ctx.toonParamsFound++;
        }
        if (target == dynamicShader)
        {
            copy.SetColor("_AmbientColor", ctx.ambientColor);
            copy.SetFloat("_UseSceneAmbient", ctx.useSceneAmbient ? 1f : 0f);
        }

        AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath($"{ctx.folder}/{Sanitize(copy.name)}.mat"));
        ctx.copies[key] = copy;
        return copy;
    }

    // 실행 중 프리팹에서 생성되는 오브젝트용 재질 짝: 씬이 (설정 데이터 등을 거쳐) 참조하는 모든 재질 → 움직이는 물체용 복사본.
    // 씬에 BakedMaterialRemap 하나를 두고 채움. 생성 코드(오브젝트 풀 등)가 생성 시 한 번 바꿔 끼움. 짝 개수를 돌려줌
    int SetupMaterialRemap(Shader vertexLit, Shader bakedToon, Shader dynamicShader, ConvertContext ctx)
    {
        var from = new List<Material>();
        var to = new List<Material>();
        var scene = EditorSceneManager.GetActiveScene();
        foreach (var o in EditorUtility.CollectDependencies(scene.GetRootGameObjects()))
        {
            if (!(o is Material m) || m.shader == null || IsOurShader(m.shader)) continue;
            if (!AssetDatabase.Contains(m)) continue;
            Material copy = CopyFor(m, isDynamic: true, vertexLit, bakedToon, dynamicShader, ctx);
            if (copy == null) continue;
            from.Add(m);
            to.Add(copy);
        }

        var remap = FindFirstObjectByType<BakedMaterialRemap>(FindObjectsInactive.Include);
        if (remap == null)
        {
            var go = new GameObject("BakedMaterialRemap");
            Undo.RegisterCreatedObjectUndo(go, "Baked Material Remap");
            remap = go.AddComponent<BakedMaterialRemap>();
        }
        var so = new SerializedObject(remap);
        SetArray(so.FindProperty("from"), from);
        SetArray(so.FindProperty("to"), to);
        so.ApplyModifiedProperties();
        return from.Count;
    }

    static void SetArray(SerializedProperty arr, List<Material> values)
    {
        arr.arraySize = values.Count;
        for (int i = 0; i < values.Count; i++) arr.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
    }

    // 굽기에 쓴 빛 색(구운 그림자 빼기·툰 색)과 빛 방향(움직이는 물체의 N·L)을 재질에 넣음. 없는 속성은 건너뜀
    static void SetLightProps(Material m, ConvertContext ctx)
    {
        if (m.HasProperty("_BakedLightColor")) m.SetColor("_BakedLightColor", ctx.lightColor);
        if (m.HasProperty("_BakedLightDir")) m.SetVector("_BakedLightDir", ctx.lightDir.normalized);
        EditorUtility.SetDirty(m);
    }

    // 원본 셰이더가 "주 텍스처 / 주 색"으로 쓰는 속성을 찾아 _BaseMap / _BaseColor 로 옮김.
    // 셰이더마다 이름이 달라서(_BaseMap, _MainTex, _Color …) 이름을 가정하지 않고 셰이더의 표시([MainTexture])를 먼저 본다.
    static bool TransferMainProps(Material src, Material dst)
    {
        Shader s = src.shader;
        string texName = null, colName = null;
        for (int i = 0; i < s.GetPropertyCount(); i++)
        {
            var flags = s.GetPropertyFlags(i);
            if ((flags & ShaderPropertyFlags.MainTexture) != 0) texName = s.GetPropertyName(i);
            if ((flags & ShaderPropertyFlags.MainColor) != 0) colName = s.GetPropertyName(i);
        }
        texName ??= FirstProperty(src, ShaderPropertyType.Texture, "_BaseMap", "_MainTex", "_Albedo", "_Albedo_Map", "_AlbedoMap",
                                  "_MainTexture", "_BaseTexture", "_Base_Map", "_Diffuse", "_DiffuseMap");
        texName ??= TextureNameContaining(src, "albedo", "diffuse", "basemap", "base_map", "maintex"); // 그 밖의 이름
        colName ??= FirstProperty(src, ShaderPropertyType.Color, "_BaseColor", "_Color", "_MainColor", "_Tint");

        if (colName != null) dst.SetColor("_BaseColor", src.GetColor(colName));
        if (texName == null) return false;

        dst.SetTexture("_BaseMap", src.GetTexture(texName));
        dst.SetTextureScale("_BaseMap", src.GetTextureScale(texName));
        dst.SetTextureOffset("_BaseMap", src.GetTextureOffset(texName));
        return true;
    }

    // 알파 컷 재질이면 복사본도 같은 기준값으로 자름 (URP Lit = _Cutoff, 셰이더 그래프 = _AlphaClipThreshold, Synty = _Alpha_Clip_Threshold)
    internal static void TransferAlphaClip(Material src, Material dst)
    {
        bool clip = src.IsKeywordEnabled("_ALPHATEST_ON") ||
                    (src.HasProperty("_AlphaClip") && src.GetFloat("_AlphaClip") > 0.5f);
        if (!clip) return;
        string n = FirstProperty(src, ShaderPropertyType.Range, "_Cutoff", "_AlphaClipThreshold", "_Alpha_Clip_Threshold") ??
                   FirstProperty(src, ShaderPropertyType.Float, "_Cutoff", "_AlphaClipThreshold", "_Alpha_Clip_Threshold");
        dst.SetFloat("_AlphaClip", 1f);
        dst.EnableKeyword("_ALPHATEST_ON");
        dst.SetFloat("_Cutoff", n != null ? src.GetFloat(n) : 0.5f);
        dst.renderQueue = (int)RenderQueue.AlphaTest;
    }

    // 툰 셰이더의 밝은 쪽 색, 그림자 색, 명암 경계 위치·부드러움을 옮김 (Toony Colors Pro 2 이름 기준, 없으면 기본값)
    static bool TransferToonProps(Material src, Material dst)
    {
        bool any = false;
        any |= CopyColor(src, dst, "_HColor", "_HColor", "_HighlightColor", "_LightColor");
        any |= CopyColor(src, dst, "_SColor", "_SColor", "_ShadowColor", "_ShadeColor");
        any |= CopyFloat(src, dst, "_RampThreshold", "_RampThreshold", "_ShadowThreshold", "_Threshold");
        any |= CopyFloat(src, dst, "_RampSmoothing", "_RampSmoothing", "_ShadowSmoothness", "_Smoothness");
        return any;
    }

    static string TextureNameContaining(Material m, params string[] parts)
    {
        Shader s = m.shader;
        for (int i = 0; i < s.GetPropertyCount(); i++)
        {
            if (s.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
            string name = s.GetPropertyName(i);
            foreach (string part in parts)
                if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return name;
        }
        return null;
    }

    static string FirstProperty(Material m, ShaderPropertyType type, params string[] names)
    {
        foreach (var n in names)
        {
            int idx = m.shader.FindPropertyIndex(n);
            if (idx >= 0 && m.shader.GetPropertyType(idx) == type) return n;
        }
        return null;
    }

    static bool CopyColor(Material src, Material dst, string dstName, params string[] srcNames)
    {
        string n = FirstProperty(src, ShaderPropertyType.Color, srcNames);
        if (n == null) return false;
        dst.SetColor(dstName, src.GetColor(n));
        return true;
    }

    static bool CopyFloat(Material src, Material dst, string dstName, params string[] srcNames)
    {
        string n = FirstProperty(src, ShaderPropertyType.Range, srcNames) ?? FirstProperty(src, ShaderPropertyType.Float, srcNames);
        if (n == null) return false;
        dst.SetFloat(dstName, src.GetFloat(n));
        return true;
    }

    static void CopyIfExists(Material src, Material dst, string name)
    {
        if (src.HasProperty(name) && dst.HasProperty(name)) dst.SetFloat(name, src.GetFloat(name));
    }

    static float FolderSizeMB(string assetFolder, string pattern, string excludeFolder)
    {
        string full = Path.Combine(Application.dataPath, assetFolder.Substring("Assets".Length).TrimStart('/'));
        string exclude = Path.Combine(Application.dataPath, excludeFolder.Substring("Assets".Length).TrimStart('/'));
        if (!Directory.Exists(full)) return 0f;

        long bytes = 0;
        foreach (string f in Directory.GetFiles(full, pattern, SearchOption.AllDirectories))
            if (!Path.GetFullPath(f).StartsWith(Path.GetFullPath(exclude))) bytes += new FileInfo(f).Length;
        return bytes / (1024f * 1024f);
    }

    // 정점마다 주변광(rgb)과 N·L(a, -1~1)을 저장. 그림자는 저장하지 않음 (그림자 지도가 픽셀마다 처리)
    void BakeOne(MeshFilter mf, Vector3 dirToLight, Color ambient, HashSet<Mesh> usedThisRun)
    {
        Mesh dst = GetOrCreateBakedMesh(mf, usedThisRun);
        usedThisRun.Add(dst);

        // 법선은 로컬→월드 행렬의 역전치 행렬로 변환해야 비균일 스케일에서도 방향이 맞음
        Matrix4x4 n2w = mf.transform.localToWorldMatrix.inverse.transpose;

        Vector3[] normals = dst.normals;
        if (normals == null || normals.Length != dst.vertexCount)
        {
            dst.RecalculateNormals();
            normals = dst.normals;
        }
        statVertsAfter += dst.vertexCount;

        // 월드 법선을 먼저 모두 구해 두고, 씬 환경광을 법선 방향별로 한 번에 계산
        var worldNormals = new Vector3[normals.Length];
        for (int i = 0; i < normals.Length; i++)
            worldNormals[i] = n2w.MultiplyVector(normals[i]).normalized;

        Color[] ambients = null;
        if (useSceneAmbient && !statAmbientFallback)
        {
            ambients = new Color[normals.Length];
            // URP 셰이더의 SampleSH 와 같은 값: 위를 보는 면은 하늘색, 아래를 보는 면은 지면색에 가까움
            RenderSettings.ambientProbe.Evaluate(worldNormals, ambients);
        }

        var baked = new List<Vector4>(normals.Length);
        for (int i = 0; i < normals.Length; i++)
        {
            Color a = ambients != null ? ambients[i] : ambient;
            // 핵심 가설: 밝기 = N·L (면이 빛을 얼마나 정면으로 받는가). 음수(빛 반대편)도 그대로 저장, 셰이더가 0 으로 자름
            float d = Vector3.Dot(worldNormals[i], dirToLight);
            baked.Add(new Vector4(a.r, a.g, a.b, d));
        }

        dst.SetUVs(BakeUVChannel, baked);
        dst.uv5 = null; // 예전 방식(해금 그림자 번호)이 남긴 채널 정리
        EditorUtility.SetDirty(dst);

        if (mf.sharedMesh != dst)
        {
            Undo.RecordObject(mf, "Vertex Light Bake");
            mf.sharedMesh = dst;
        }
    }

    // 같은 모델이라도 놓인 위치·방향이 다르면 밝기가 다르므로 오브젝트마다 복사본이 필요
    Mesh GetOrCreateBakedMesh(MeshFilter mf, HashSet<Mesh> usedThisRun)
    {
        Mesh src = mf.sharedMesh;
        string srcPath = AssetDatabase.GetAssetPath(src);

        // 저장 폴더 안의 메시면 이 도구가 만든 것. 이름으로 판단하면 안 됨: 저장 후 이름이 파일 이름("…_VLBaked 1")으로 바뀌어
        // 매번 새 복사본을 만들고 이전 것이 쌓였음
        bool isOurs = srcPath.StartsWith(outputFolder.TrimEnd('/') + "/");

        // 다시 구울 때는 원본 모델 메시에서 새로 시작 (이미 나눈 메시를 또 나눠 정점이 계속 늘던 문제 방지).
        // 원본은 프리팹 원본의 MeshFilter 에서 찾음. 프리팹이 아니면 찾을 수 없어 지금 메시에서 이어감
        Mesh original = null;
        if (isOurs)
        {
            var sourceMf = PrefabUtility.GetCorrespondingObjectFromOriginalSource(mf);
            if (sourceMf != null && sourceMf != mf && sourceMf.sharedMesh != null &&
                !AssetDatabase.GetAssetPath(sourceMf.sharedMesh).StartsWith(outputFolder.TrimEnd('/') + "/"))
                original = sourceMf.sharedMesh;
        }

        // 이미 이 도구로 만든 메시이고, 이번 굽기에서 다른 오브젝트가 쓰지 않았다면 같은 파일에 덮어쓰기
        if (isOurs && !usedThisRun.Contains(src))
        {
            if (original != null)
            {
                string keepName = src.name;
                EditorUtility.CopySerialized(original, src);
                src.name = keepName;
            }
            return src;
        }

        Mesh copy = Instantiate(original != null ? original : src);
        string baseName = src.name.Replace(BakedSuffix, "");
        copy.name = baseName + BakedSuffix;

        string path = AssetDatabase.GenerateUniqueAssetPath(
            $"{outputFolder}/{Sanitize(mf.gameObject.name)}{BakedSuffix}.asset");
        AssetDatabase.CreateAsset(copy, path);
        return copy;
    }

    // 실행 중 언젠가 보이는 것: 켜져 있거나, 꺼져 있어도 해금 오브젝트(toggles)라서 꺼진 경우만
    static bool VisibleSometime(Transform t, HashSet<GameObject> toggles)
    {
        for (; t != null; t = t.parent)
            if (!t.gameObject.activeSelf && (toggles == null || !toggles.Contains(t.gameObject))) return false;
        return true;
    }

    static bool IsProbeBlack(UnityEngine.Rendering.SphericalHarmonicsL2 sh)
    {
        var c = new Color[1];
        sh.Evaluate(new[] { Vector3.up }, c);
        return c[0].r + c[0].g + c[0].b < 0.001f;
    }

    static bool IsUnderAny(Transform t, List<GameObject> roots)
    {
        if (roots == null) return false;
        foreach (var r in roots)
            if (r != null && (t == r.transform || t.IsChildOf(r.transform))) return true;
        return false;
    }

    // ───────────────────────── 셰이더 교체 ─────────────────────────

    void SwapShader(List<MeshFilter> targets)
    {
        if (bakedShader == null)
        {
            EditorUtility.DisplayDialog("Vertex Light Baker", "교체할 셰이더를 지정하세요.", "확인");
            return;
        }

        var mats = new HashSet<Material>();
        foreach (var mf in targets)
            foreach (var m in mf.GetComponent<MeshRenderer>().sharedMaterials)
                if (m != null) mats.Add(m);

        var arr = new List<Material>(mats).ToArray();
        Undo.RecordObjects(arr, "Swap To Baked Shader");
        // _BaseMap, _BaseColor 이름이 URP Lit 과 같아서 텍스처·색은 그대로 유지됨
        bool hasLight = TryGetBakeLight(out _, out Color lightColor);
        foreach (var m in arr)
        {
            m.shader = bakedShader;
            if (hasLight && m.HasProperty("_BakedLightColor"))
                m.SetColor("_BakedLightColor", lightColor);
        }

        Debug.Log($"[VertexLightBaker] 재질 {arr.Length}개 셰이더 교체");
    }

    // ───────────────────────── 켜졌다 꺼지는 오브젝트 ─────────────────────────

    // 실행 중 함께 켜지고 꺼지는 오브젝트 묶음: 씬에 직접 붙인 BakedShadowCaster 하나당 한 묶음 + ExtraToggleGroups 결과.
    // 한 오브젝트가 여러 묶음에 있으면 먼저 나온 묶음에만 넣음
    static List<List<GameObject>> CollectToggleGroups()
    {
        var groups = new List<List<GameObject>>();
        var seen = new HashSet<GameObject>();
        void AddGroup(IEnumerable<GameObject> members)
        {
            var g = new List<GameObject>();
            foreach (var go in members)
                if (go != null && go.scene.IsValid() && seen.Add(go)) g.Add(go);
            if (g.Count > 0) groups.Add(g);
        }

        foreach (var c in FindObjectsByType<BakedShadowCaster>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            AddGroup(new[] { c.gameObject });
        var extra = ExtraToggleGroups?.Invoke();
        if (extra != null)
            foreach (var g in extra)
                if (g != null) AddGroup(g);
        return groups;
    }

    // 묶음에 든 오브젝트마다 BakedShadowCaster 표식을 붙임 (켜지고 꺼질 때 그림자 지도를 다시 찍게 함).
    // 직접 붙인 표식은 지우지 않음 (필요 없어지면 직접 지울 것)
    static void EnsureCasterMarkers(List<List<GameObject>> groups)
    {
        foreach (var g in groups)
            foreach (var go in g)
                if (go.GetComponent<BakedShadowCaster>() == null) Undo.AddComponent<BakedShadowCaster>(go);
    }

    // 켜고 끄는 오브젝트 전체 (기능을 끄면 null = 구분 없음, 꺼진 것은 모두 제외)
    HashSet<GameObject> ToggleSet() =>
        bakeShadows && separateUnlockShadows ? Flatten(CollectToggleGroups()) : null;

    static HashSet<GameObject> Flatten(List<List<GameObject>> groups)
    {
        var set = new HashSet<GameObject>();
        foreach (var g in groups) set.UnionWith(g);
        return set;
    }

    // ───────────────────────── 정리 ─────────────────────────

    // 저장 폴더에서 어떤 씬·프리팹도 쓰지 않는 메시·재질 복사본을 지움. 지운 개수를 돌려줌.
    // 열려 있는 씬은 메모리 상태(방금 구운 것)와 저장된 파일 상태를 "둘 다" 지킴:
    // 저장하지 않고 닫아도 저장된 씬이 쓰던 메시가 남아 있어야 함 (그 메시는 씬을 저장한 뒤 다음 정리 때 지워짐)
    int CleanupUnusedBakedAssets(bool report)
    {
        string folder = outputFolder.TrimEnd('/');
        if (!AssetDatabase.IsValidFolder(folder)) return 0;

        var used = new HashSet<string>();
        for (int i = 0; i < EditorSceneManager.sceneCount; i++)
        {
            var scene = EditorSceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            foreach (var o in EditorUtility.CollectDependencies(scene.GetRootGameObjects()))
                if (o != null) used.Add(AssetDatabase.GetAssetPath(o));
        }
        foreach (string guid in AssetDatabase.FindAssets("t:Scene t:Prefab"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.StartsWith("Assets/")) continue;
            used.UnionWith(AssetDatabase.GetDependencies(path, true));
        }

        var unused = new List<string>();
        foreach (string guid in AssetDatabase.FindAssets("t:Mesh t:Material", new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!used.Contains(path)) unused.Add(path);
        }
        if (unused.Count > 0)
        {
            var failed = new List<string>();
            AssetDatabase.DeleteAssets(unused.ToArray(), failed);
            if (failed.Count > 0) Debug.LogWarning("[VertexLightBaker] 지우지 못한 파일: " + string.Join(", ", failed));
        }

        if (report)
            EditorUtility.DisplayDialog("Vertex Light Baker", $"안 쓰는 구운 파일 {unused.Count}개를 지웠습니다.", "확인");
        return unused.Count;
    }

    // 원본(프리팹)과 값이 같아 의미 없는 덮어쓰기 중, 굽기 도구가 예전에 남긴 종류(켜짐·활성·Static)를 지움. 보이는 결과는 바뀌지 않음
    [MenuItem("Tools/Vertex Light Baker 정리/원본과 같은 덮어쓰기 지우기")]
    static void RemoveRedundantOverrides()
    {
        int removed = 0, remainingActive = 0;
        foreach (var go in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go.gameObject)) continue;
            var mods = PrefabUtility.GetPropertyModifications(go.gameObject);
            if (mods == null) continue;

            var keep = new List<PropertyModification>(mods.Length);
            foreach (var m in mods)
            {
                if (IsRedundant(m)) { removed++; continue; }
                if (m.propertyPath == "m_IsActive") remainingActive++;
                keep.Add(m);
            }
            if (keep.Count == mods.Length) continue;
            Undo.RegisterCompleteObjectUndo(go.gameObject, "Remove Redundant Overrides");
            PrefabUtility.SetPropertyModifications(go.gameObject, keep.ToArray());
        }
        EditorSceneManager.MarkAllScenesDirty();
        EditorUtility.DisplayDialog("덮어쓰기 정리",
            $"원본과 같은 덮어쓰기 {removed}개를 지웠습니다.\n원본과 다른 '켜짐/꺼짐' 덮어쓰기 {remainingActive}개는 그대로 두었습니다 (직접 켠 것일 수 있음).\n씬을 저장하세요.", "확인");
    }

    static bool IsRedundant(PropertyModification m)
    {
        if (m.target == null) return false;
        if (m.propertyPath != "m_IsActive" && m.propertyPath != "m_Enabled" && m.propertyPath != "m_StaticEditorFlags") return false;
        var p = new SerializedObject(m.target).FindProperty(m.propertyPath);
        if (p == null) return false;
        switch (p.propertyType)
        {
            case SerializedPropertyType.Boolean: return (p.boolValue ? "1" : "0") == m.value;
            case SerializedPropertyType.Integer: return p.longValue.ToString() == m.value;
            default: return false;
        }
    }

    // ───────────────────────── 보조 함수 ─────────────────────────

    static List<MeshFilter> CollectTargets(GameObject[] roots)
    {
        var set = new HashSet<MeshFilter>();
        foreach (var go in roots)
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null && mf.GetComponent<MeshRenderer>() != null)
                    set.Add(mf);
        return new List<MeshFilter>(set);
    }

    // Static 물체 전체. 꺼져 있어도 해금 오브젝트라서 꺼진 것은 포함 (실행 중 켜지면 구운 밝기로 보여야 함)
    static List<MeshFilter> CollectStaticTargets(HashSet<GameObject> toggles)
    {
        var list = new List<MeshFilter>();
        foreach (var mf in FindObjectsByType<MeshFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (mf.sharedMesh != null && mf.gameObject.isStatic && mf.GetComponent<MeshRenderer>() != null &&
                mf.gameObject.scene.IsValid() && VisibleSometime(mf.transform, toggles))
                list.Add(mf);
        return list;
    }

    // 꺼 둔(비활성) 오브젝트의 방향광도 찾음: 실행 중에는 안 쓰지만 굽기 기준으로는 유효함
    internal static Light FindMainDirectionalLight()
    {
        if (RenderSettings.sun != null) return RenderSettings.sun;
        foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (l.type == LightType.Directional && l.gameObject.scene.IsValid()) return l;
        return null;
    }

    // ───────────────────────── 굽기 기준 빛 ─────────────────────────
    // 방향광 오브젝트가 없어도 굽도록, 빛 방향·색을 씬의 BakedShadowMap 에 저장해 둔다.

    const string NoLightMessage =
        "굽기 기준 빛이 없습니다.\n씬에 방향광을 하나 두거나, BakedShadowMap 의 '굽기 기준 빛' 값을 설정하세요.";

    static bool TryGetBakeLight(out Vector3 dirToLight, out Color linearColor) =>
        ResolveBakeLight(FindMainDirectionalLight(),
            FindFirstObjectByType<BakedShadowMap>(FindObjectsInactive.Include), out dirToLight, out linearColor);

    // 방향광이 있으면 그 값, 없으면 저장된 값. dirToLight = 표면에서 빛 쪽, linearColor = 선형 색 × 세기
    internal static bool ResolveBakeLight(Light light, BakedShadowMap map, out Vector3 dirToLight, out Color linearColor)
    {
        if (light != null && light.type == LightType.Directional)
        {
            dirToLight = -light.transform.forward; // 방향광의 forward = 빛이 나아가는 방향
            linearColor = light.color.linear * light.intensity;
            return true;
        }
        if (map != null)
        {
            var so = new SerializedObject(map);
            float intensity = so.FindProperty("bakeLightIntensity").floatValue;
            if (intensity > 0f) // 0 = 아직 저장 안 됨. 기본값을 실제 빛처럼 쓰지 않음
            {
                dirToLight = -(Quaternion.Euler(so.FindProperty("bakeLightRotation").vector3Value) * Vector3.forward);
                linearColor = so.FindProperty("bakeLightColor").colorValue.linear * intensity;
                return true;
            }
        }
        dirToLight = default;
        linearColor = default;
        return false;
    }

    internal static void SaveBakeLight(Light light, BakedShadowMap map)
    {
        var so = new SerializedObject(map);
        so.FindProperty("bakeLightRotation").vector3Value = light.transform.rotation.eulerAngles;
        so.FindProperty("bakeLightColor").colorValue = light.color;
        so.FindProperty("bakeLightIntensity").floatValue = light.intensity;
        so.ApplyModifiedProperties(); // Undo 기록 포함
    }

    static BakedShadowMap FindOrCreateShadowMap()
    {
        var map = FindFirstObjectByType<BakedShadowMap>(FindObjectsInactive.Include);
        if (map != null) return map;
        var go = new GameObject("BakedShadowMap");
        Undo.RegisterCreatedObjectUndo(go, "Baked Shadow Map");
        return go.AddComponent<BakedShadowMap>();
    }

    // 방향광 오브젝트를 지움. 같은 오브젝트에 다른 컴포넌트나 자식이 있으면 지우지 않고 꺼 둠. 지웠으면 true
    static bool RemoveLight(Light light)
    {
        var go = light.gameObject;
        bool onlyLight = go.transform.childCount == 0;
        foreach (var c in go.GetComponents<Component>())
            if (!(c is Transform || c is Light || c.GetType().Name == "UniversalAdditionalLightData"))
                onlyLight = false;

        if (onlyLight)
        {
            Undo.DestroyObjectImmediate(go);
            return true;
        }
        Undo.RecordObject(light, "VLB Light");
        light.shadows = LightShadows.None;
        light.enabled = false;
        return false;
    }

    void DrawBakeLightStatus()
    {
        Light light = FindMainDirectionalLight();
        var map = FindFirstObjectByType<BakedShadowMap>(FindObjectsInactive.Include);
        if (light != null)
            EditorGUILayout.HelpBox($"기준 빛: 씬 방향광 '{light.name}'. 한 번에 적용 때 값을 BakedShadowMap 에 저장하고 지웁니다.", MessageType.None);
        else if (map != null && ResolveBakeLight(null, map, out _, out _))
        {
            EditorGUILayout.HelpBox("기준 빛: BakedShadowMap 에 저장된 값 (방향광 오브젝트 없음). 바꾸려면 BakedShadowMap 에서 고친 뒤 다시 적용.", MessageType.None);
            if (GUILayout.Button("BakedShadowMap 선택")) Selection.activeObject = map;
        }
        else
            EditorGUILayout.HelpBox(NoLightMessage, MessageType.Warning);
    }

    static void EnsureFolder(string path)
    {
        path = path.TrimEnd('/');
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    // 레이어 목록 순서와 실제 레이어 번호가 다를 수 있어서 변환이 필요
    static LayerMask LayerMaskField(string label, LayerMask mask)
    {
        string[] layers = UnityEditorInternal.InternalEditorUtility.layers;
        int display = 0;
        for (int i = 0; i < layers.Length; i++)
            if ((mask.value & (1 << LayerMask.NameToLayer(layers[i]))) != 0) display |= 1 << i;

        display = EditorGUILayout.MaskField(label, display, layers);
        if (display == -1) return ~0;

        int result = 0;
        for (int i = 0; i < layers.Length; i++)
            if ((display & (1 << i)) != 0) result |= 1 << LayerMask.NameToLayer(layers[i]);
        return result;
    }
}
