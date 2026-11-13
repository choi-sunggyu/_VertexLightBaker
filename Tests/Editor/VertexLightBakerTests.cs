using System.Collections.Generic;
using VLB;
using NUnit.Framework;
using UnityEngine;

// Window > General > Test Runner > EditMode 에서 실행
public class VertexLightBakerTests
{
    readonly List<Object> created = new List<Object>();

    [TearDown]
    public void TearDown()
    {
        foreach (var o in created)
            if (o != null) Object.DestroyImmediate(o);
        created.Clear();
    }

    // 그림자 지도 좌표: 영역 전체가 지도 안(0~1)에 들어가고, 빛 쪽에 있는 점이 더 "앞"(그림자를 드리우는 쪽)이어야 함
    [Test]
    public void ShadowMatrix_CoversAreaAndOrdersDepthTowardLight()
    {
        var area = new Bounds(new Vector3(10f, 1f, -5f), new Vector3(30f, 4f, 20f));
        var lightDir = new Vector3(0.4f, 0.8f, -0.45f).normalized; // 표면에서 빛 쪽
        var m = BakedShadowMap.ComputeMatrices(lightDir, area, 2048, out _, out _, out float texel);

        for (int i = 0; i < 8; i++)
        {
            var corner = area.center + Vector3.Scale(area.extents,
                new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
            var c = m.MultiplyPoint(corner);
            Assert.That(c.x, Is.InRange(0f, 1f), "x");
            Assert.That(c.y, Is.InRange(0f, 1f), "y");
            Assert.That(c.z, Is.InRange(0f, 1f), "z");
        }

        // 빛 쪽으로 옮긴 점은 같은 지도 위치에 찍히고 깊이만 다름 (= 아래 점을 가림)
        var ground = area.center;
        var above = ground + lightDir * 3f;
        var g = m.MultiplyPoint(ground);
        var a = m.MultiplyPoint(above);
        Assert.AreEqual(g.x, a.x, 1e-4f);
        Assert.AreEqual(g.y, a.y, 1e-4f);
        Assert.AreNotEqual(g.z, a.z);
        Assert.Greater(texel, 0f);
    }

    // 원인 재현: 지역 10개(x 0~1800m)가 한 영역에 들어가 픽셀이 수 m 가 됐음. 꺼진 지역은 빠져야 함
    [Test]
    public void ActiveBounds_SkipsInactiveAreas()
    {
        var here = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var farArea = new GameObject("FarArea");
        var far = GameObject.CreatePrimitive(PrimitiveType.Cube);
        created.Add(here); created.Add(farArea); created.Add(far);
        far.transform.SetParent(farArea.transform);
        far.transform.position = new Vector3(1800f, 0f, 0f);
        farArea.SetActive(false);

        var renderers = new Renderer[] { here.GetComponent<Renderer>(), far.GetComponent<Renderer>(), null };
        Assert.IsTrue(BakedShadowMap.ActiveBounds(renderers, out var area));
        Assert.Less(area.size.x, 2f);

        here.SetActive(false);
        Assert.IsFalse(BakedShadowMap.ActiveBounds(renderers, out _));
    }

    // 움직이는 물체 그림자 대상: 움직이는 물체 재질 + 그림자 드리우기 켜짐 (실시간 그림자와 같은 규칙)
    [Test]
    public void IsDynamicCaster_UsesDynamicMaterialAndCastSetting()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var mat = new Material(Shader.Find("Custom/DynamicToon"));
        created.Add(go); created.Add(mat);
        var r = go.GetComponent<MeshRenderer>();
        Assert.IsFalse(BakedShadowMap.IsDynamicCaster(r)); // 기본 재질 = 정적·기타

        r.sharedMaterial = mat;
        Assert.IsTrue(BakedShadowMap.IsDynamicCaster(r));

        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        Assert.IsFalse(BakedShadowMap.IsDynamicCaster(r));
    }

    [Test]
    public void BakedShadowCaster_ShownWhileActive()
    {
        var go = new GameObject("CasterTest");
        created.Add(go);
        var caster = go.AddComponent<BakedShadowCaster>();
        Assert.IsTrue(caster.Shown); // 에디터에서는 켜지면 바로

        go.SetActive(false);
        Assert.IsFalse(caster.Shown);

        go.SetActive(true);
        Assert.IsTrue(caster.Shown);
        // 편집 모드에는 등장 연출이 없음: 동적 지도로 찍히는 상태가 남으면 안 됨
        Assert.IsFalse(caster.Appearing);
        Assert.AreEqual(0, BakedShadowCaster.AppearingCount);
    }

    // 방향광을 지운 뒤에도 같은 빛으로 굽기: 저장한 값이 방향광에서 읽은 값과 같아야 함
    [Test]
    public void BakeLight_StoredValueMatchesDeletedLight()
    {
        var lightGo = new GameObject("LightTest");
        var mapGo = new GameObject("MapTest");
        created.Add(lightGo); created.Add(mapGo);
        mapGo.SetActive(false); // 그림자 지도를 실제로 찍지 않도록 꺼 둔 채 값만 씀
        var map = mapGo.AddComponent<BakedShadowMap>();
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = new Color(1f, 0.9f, 0.7f);
        light.intensity = 1.7f;
        lightGo.transform.rotation = Quaternion.Euler(35f, 120f, 0f);

        Assert.IsFalse(VertexLightBaker.ResolveBakeLight(null, null, out _, out _));
        // 저장한 적 없는 기본값을 실제 빛처럼 쓰면 안 됨 (원인: 꺼진 방향광을 못 찾고 흰색·세기 1 로 구울 뻔함)
        Assert.IsFalse(VertexLightBaker.ResolveBakeLight(null, map, out _, out _));
        Assert.IsTrue(VertexLightBaker.ResolveBakeLight(light, null, out var dirFromLight, out var colorFromLight));

        VertexLightBaker.SaveBakeLight(light, map);
        Object.DestroyImmediate(lightGo);
        Assert.IsTrue(VertexLightBaker.ResolveBakeLight(null, map, out var dirStored, out var colorStored));

        Assert.Less(Vector3.Distance(dirFromLight, dirStored), 1e-4f);
        Assert.AreEqual(colorFromLight.r, colorStored.r, 1e-4f);
        Assert.AreEqual(colorFromLight.g, colorStored.g, 1e-4f);
        Assert.AreEqual(colorFromLight.b, colorStored.b, 1e-4f);
    }

    // 꺼 둔(비활성) 오브젝트의 방향광도 굽기 기준으로 찾아야 함
    [Test]
    public void FindMainDirectionalLight_IncludesInactiveObject()
    {
        var lightGo = new GameObject("InactiveLightTest");
        created.Add(lightGo);
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        lightGo.SetActive(false);
        var found = VertexLightBaker.FindMainDirectionalLight();
        Assert.IsNotNull(found);
        Assert.AreEqual(LightType.Directional, found.type);
    }

    // 그림자 받기·보정 마스크를 넣은 뒤에도 모든 셰이더가 오류 없이 컴파일되어야 함
    [Test]
    public void Shaders_CompileWithoutErrors()
    {
        foreach (var name in new[] { "Custom/BakedVertexLit", "Custom/BakedToon", "Custom/LowCostCharacter",
                                     "Custom/DynamicToon", "Hidden/VLB/ShadowCaster" })
        {
            var s = Shader.Find(name);
            Assert.IsNotNull(s, name);
            Assert.IsFalse(UnityEditor.ShaderUtil.ShaderHasError(s), name);
        }
    }

    // 원인 재현: Synty 처럼 이름이 URP Lit 이 아닌 Lit 셰이더(그래프)를 변환하지 않아, 방향광을 지운 뒤 배경이 밤처럼 어두워졌음.
    // 이름이 아니라 "주 방향광을 받는가"로 판단해야 함
    [Test]
    public void UsesMainLight_DetectsLitByBehaviourNotName()
    {
        Assert.IsTrue(VertexLightBaker.UsesMainLight(Shader.Find("Universal Render Pipeline/Lit")));
        Assert.IsTrue(VertexLightBaker.UsesMainLight(Shader.Find("Universal Render Pipeline/Simple Lit")));
        Assert.IsFalse(VertexLightBaker.UsesMainLight(Shader.Find("Universal Render Pipeline/Unlit")));
        foreach (var name in new[] { "Custom/BakedVertexLit", "Custom/BakedToon", "Custom/LowCostCharacter", "Custom/DynamicToon" })
            Assert.IsFalse(VertexLightBaker.UsesMainLight(Shader.Find(name)), name); // 변환 결과는 방향광이 필요 없음
    }

    // 알파 컷 재질도 변환: 기준값을 옮기고 같은 방식으로 자름 (예전에는 건너뛰어 방향광 없이 어두워졌음)
    [Test]
    public void TransferAlphaClip_CopiesThresholdAndKeyword()
    {
        var src = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        var opaque = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        var dst = new Material(Shader.Find("Custom/BakedVertexLit"));
        var dstOpaque = new Material(Shader.Find("Custom/BakedVertexLit"));
        created.Add(src); created.Add(opaque); created.Add(dst); created.Add(dstOpaque);
        src.SetFloat("_AlphaClip", 1f);
        src.EnableKeyword("_ALPHATEST_ON");
        src.SetFloat("_Cutoff", 0.3f);

        VertexLightBaker.TransferAlphaClip(src, dst);
        Assert.IsTrue(dst.IsKeywordEnabled("_ALPHATEST_ON"));
        Assert.AreEqual(0.3f, dst.GetFloat("_Cutoff"), 1e-4f);
        Assert.AreEqual((int)UnityEngine.Rendering.RenderQueue.AlphaTest, dst.renderQueue);

        VertexLightBaker.TransferAlphaClip(opaque, dstOpaque); // 불투명은 그대로
        Assert.IsFalse(dstOpaque.IsKeywordEnabled("_ALPHATEST_ON"));
    }

    // 보정 마스크는 기본 꺼짐: 기존 재질의 모습이 바뀌지 않음
    [Test]
    public void ShadeMask_OffByDefault()
    {
        foreach (var name in new[] { "Custom/LowCostCharacter", "Custom/DynamicToon" })
        {
            var mat = new Material(Shader.Find(name));
            created.Add(mat);
            Assert.AreEqual(0f, mat.GetFloat("_ShadeMask"), name);
            Assert.IsFalse(mat.IsKeywordEnabled("_SHADE_MASK"), name);
        }
    }
}
