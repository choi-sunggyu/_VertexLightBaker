# Vertex Light Baker

URP 프로젝트에서 **방향광(Directional Light)과 실시간 그림자를 지우고**, 미리 구운 값으로 같은 모습을 내는 도구입니다. 빛 방향이 고정된 모바일 게임을 전제로 합니다.

| 대상 | 밝기 (명암) | 그림자 |
|---|---|---|
| 정적 배경 (Static) | 에디터에서 정점마다 N·L·주변광을 구워 메시 복사본 UV3에 저장 | **정적 그림자 지도**: 시작할 때, 표식 오브젝트가 켜지거나 꺼질 때만 찍음 |
| 켜졌다 꺼지는 오브젝트 (`BakedShadowCaster`) | 위와 같음 (꺼져 있어도 구워 둠) | 등장 연출 중에는 동적 지도에 매 프레임 찍혀 그림자가 연출을 따라감. 끝나면 정적 지도로 옮겨 꺼질 때까지 유지 |
| 움직이는 물체 | 정점 셰이더가 고정 빛 방향(`_BakedLightDir`)과 법선으로 계산 | **동적 그림자 지도**: 움직이는 물체만 매 프레임 깊이만 찍음 |

## 요구 사항
- Unity 6 (6000.0 이상)
- URP 17 이상
- 불투명 재질만 변환합니다. 투명 재질과 알파 컷 재질은 그대로 둡니다.

## 설치
**Window > Package Manager > + > Install package from git URL…** 에 다음 주소를 넣습니다.
```
https://github.com/choi-sunggyu/_VertexLightBaker.git#v0.1.1
```
`#v0.1.1` 부분은 버전 태그입니다. 빼면 main 최신 상태를 받습니다.

## 사용 방법
> 씬을 크게 바꾸는 작업입니다. 깃에 커밋한 뒤 실행하세요. 원본 모델과 재질은 수정하지 않고 복사본을 만듭니다.

1. 씬에 방향광을 하나 두고, 빛 방향과 색을 원하는 대로 맞춥니다.
2. (권장) **Lighting** 창에서 **Generate Lighting**을 한 번 실행합니다. 씬 환경광(주변광)이 계산됩니다.
3. 배경 오브젝트를 **Static**으로 표시합니다.
4. 실행 중 켜졌다 꺼지는 오브젝트(해금 대상, 철거되는 벽 등)에 `BakedShadowCaster` 컴포넌트를 붙입니다.
5. **Tools > Vertex Light Baker**를 열고 **한 번에 적용**을 누릅니다.
6. 결과 창 내용을 확인하고 씬을 저장합니다. 그림자는 **Play 모드**에서 확인합니다.

**한 번에 적용**을 누르면 다음이 순서대로 실행됩니다.
1. Static 오브젝트 전체의 밝기를 굽습니다. 씬에 `BakedShadowMap`을 두고 그림자를 드리울 물체 목록을 채웁니다.
2. 방향광의 회전·색·세기를 `BakedShadowMap`에 저장하고 방향광을 지웁니다. Ctrl+Z로 되돌릴 수 있습니다.
3. 재질을 복사본으로 교체합니다.

   | 원본 | 정적 오브젝트 | 움직이는 물체 |
   |---|---|---|
   | URP Lit / Simple Lit / Complex Lit / Baked Lit | `Custom/BakedVertexLit` | `Custom/LowCostCharacter` |
   | 이름에 toon이 들어간 셰이더 | `Custom/BakedToon` | `Custom/DynamicToon` |
   | 외곽선(outline) 셰이더, 그 밖의 셰이더 | 그대로 둠 | 그대로 둠 |

   툰 셰이더에서는 밝은 색·그림자 색·경계 값(`_HColor`, `_SColor`, `_RampThreshold`, `_RampSmoothing`)을 옮깁니다.
4. 실행 중 생성되는 오브젝트용 재질 짝을 `BakedMaterialRemap`에 저장합니다.
5. 아무도 쓰지 않는 구운 파일을 지웁니다.

## 게임 코드에서 연결할 것

### 1. 프리팹에서 생성하는 오브젝트
프리팹에서 생성하는 물체는 처음 만들 때 아래를 한 번 부릅니다. 재질을 복사본으로 바꾸고 동적 그림자 대상에 넣습니다.
```csharp
var go = Instantiate(prefab);
VLB.BakedMaterialRemap.Apply(go);
```

### 2. 등장 연출과 그림자 맞추기 (선택)
연출 중에는 그림자가 움직임을 따라가고, 끝나면 정적 그림자로 고정됩니다. 설정하지 않으면 켜진 다음 프레임에 고정됩니다. DOTween을 쓴다면 다음처럼 연결합니다.
```csharp
[RuntimeInitializeOnLoadMethod]
static void HookVLB() => VLB.BakedShadowCaster.IsAnimating = t => DG.Tweening.DOTween.IsTweening(t);
```

### 3. 지역 이동처럼 표식 없이 배경이 바뀔 때
```csharp
VLB.BakedShadowMap.RequestRender(); // 다음 LateUpdate에서 정적 지도를 한 번 다시 찍음
```

### 4. 켜고 끄는 오브젝트를 자동으로 표시하기 (선택, 에디터 코드)
해금 시스템처럼 프로젝트에 대상 목록이 이미 있다면 아래처럼 등록합니다. 굽기 때마다 해당 오브젝트에 `BakedShadowCaster`가 자동으로 붙습니다.
```csharp
// Editor 폴더 안
[UnityEditor.InitializeOnLoadMethod]
static void Register() => VertexLightBaker.ExtraToggleGroups = () =>
{
    var groups = new List<List<GameObject>>();
    foreach (var box in Object.FindObjectsByType<UnlockBox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        groups.Add(new List<GameObject> { box.Target });
    return groups;
};
```

## 구성 요소

| 이름 | 종류 | 설명 |
|---|---|---|
| `BakedShadowMap` | 컴포넌트 (씬에 1개, 도구가 만듦) | 정적 그림자 지도와 동적 그림자 지도를 찍고 셰이더에 넘깁니다. 굽기 기준 빛도 저장합니다 |
| `BakedShadowCaster` | 컴포넌트 (켜고 끄는 오브젝트) | 켜지거나 꺼질 때 정적 지도를 다시 찍게 합니다. 꺼지는 순간까지 그림자를 유지합니다 |
| `BakedMaterialRemap` | 컴포넌트 (씬에 1개, 도구가 채움) | 원본 재질과 복사본 재질의 짝을 저장합니다 |
| `Custom/BakedVertexLit` | 셰이더 | 정적 배경 |
| `Custom/BakedToon` | 셰이더 | 정적 배경 (툰) |
| `Custom/LowCostCharacter` | 셰이더 | 움직이는 물체 |
| `Custom/DynamicToon` | 셰이더 | 움직이는 물체 (툰) |
| `Hidden/VLB/ShadowCaster` | 셰이더 | 그림자 지도에 깊이를 찍을 때만 사용합니다 |

### 재질 값
- `_BakedLightDir`: 표면에서 빛 쪽을 가리키는 월드 방향입니다. 방향광 `-forward`와 같고, 움직이는 물체 셰이더만 씁니다.
- `_BakedLightColor`: 빛 색(선형) × 세기입니다. 셰이더 4개가 모두 씁니다.
- 두 값은 도구가 채웁니다. 재질에서 직접 바꾸면 구운 배경·그림자 지도와 방향이 어긋납니다. 빛을 바꾸려면 방향광을 다시 두고 **한 번에 적용**을 다시 실행하세요.

### 모델러 명암 보정 (움직이는 물체, 선택)
`LowCostCharacter`와 `DynamicToon` 재질의 **Shadow Mask**를 켜면 정점 색 R을 N·L에 더합니다. 기본은 꺼져 있습니다.

| 정점 색 R | 효과 |
|---|---|
| 0.5 | 그대로 |
| 0에 가까움 | 그늘이 잘 짐 |
| 1에 가까움 | 거의 항상 밝음 |

정점 색을 칠한 메시에만 켜세요. 정점 색이 없는 메시는 R이 1로 읽혀 전체가 밝아집니다.

## 실시간 그림자와 비교
| | URP 실시간 | 이 도구 |
|---|---|---|
| 매 프레임 그림자 지도에 그리는 것 | 그림자 거리 안의 모든 물체 × 캐스케이드 수 | 움직이는 물체만, 1장 |
| 그림자 읽기 | 부드러운 그림자 설정에 따라 픽셀당 여러 번 | 지도 2장을 각각 1번 (하드웨어가 경계를 부드럽게 처리) |
| 빛 계산 | 픽셀마다 | 배경은 미리 구움, 움직이는 물체는 정점마다 |

잃는 것
- 빛 방향을 바꾸려면 다시 구워야 합니다. 낮밤 전환을 지원하지 않습니다.
- 움직이는 물체는 배경 그림자만 받고, 다른 움직이는 물체의 그림자는 받지 않습니다.
- 변환하지 않은 셰이더(외곽선 등)를 쓰는 물체는 그림자를 받지 않습니다.
- 메모리가 늘어납니다. 그림자 지도가 정적 8MB, 동적 2MB를 상시 차지하고, 메시 복사본도 추가됩니다.
- `RenderTextureFormat.Shadowmap`을 지원하지 않는 기기에서는 그림자 없이 그립니다.

## 문제 해결
| 증상 | 원인 / 해결 |
|---|---|
| 편집 화면에서 그림자가 흐림 | 영역이 켜진 정적 물체 전체를 감쌉니다. Play 모드에서 실제로 켜진 물체 기준으로 확인하세요. 멀리 있는 물체는 "그림자 제외"에 넣으세요 |
| 표면에 얼룩·줄무늬가 생김 | `BakedShadowMap`의 Depth Bias / Normal Bias를 조금 올리세요 |
| 배경이 검게 보임 | 굽지 않은 메시에 배경 셰이더가 쓰인 경우입니다. 다시 구우세요 |
| 생성한 물체가 원래 색이고 그림자가 없음 | 생성 지점에서 `BakedMaterialRemap.Apply`를 부르지 않은 경우입니다 |
| 켜고 끄는 오브젝트 그림자가 미리 보이거나 사라지지 않음 | 해당 오브젝트에 `BakedShadowCaster`가 없는 경우입니다 |

## 테스트
테스트를 실행하려면 프로젝트의 `Packages/manifest.json`에 다음을 추가합니다.
```json
"testables": ["com.vlb.vertex-light-baker"]
```
그다음 **Window > General > Test Runner > EditMode**에서 `VertexLightBakerTests`를 실행합니다.
