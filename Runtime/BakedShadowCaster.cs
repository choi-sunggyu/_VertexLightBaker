using System;
using UnityEngine;

namespace VLB
{
    /// <summary>
    /// 실행 중 켜졌다 꺼졌다 하는 오브젝트(해금 오브젝트, 철거되는 벽 등) 표식. 자식 포함.
    /// 켜지거나 꺼지면 저장된 그림자 지도(BakedShadowMap)를 다시 찍게 한다. 매 프레임 비용은 없다.
    /// - 켜질 때: 등장 연출이 끝난 뒤 그림자를 넣는다 (IsAnimating 을 설정한 경우)
    /// - 꺼질 때: 꺼지는 순간까지 그림자를 유지하고, 꺼지면 뺀다
    /// 직접 붙이거나, VertexLightBaker.ExtraToggleGroups 로 도구가 자동으로 붙이게 한다.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class BakedShadowCaster : MonoBehaviour
    {
        /// <summary>
        /// 등장 연출 중인지 알려 주는 함수 (트윈 라이브러리와 연결). 비워 두면 켜진 다음 프레임에 그림자를 넣는다.
        /// 예 (DOTween): BakedShadowCaster.IsAnimating = t => DG.Tweening.DOTween.IsTweening(t);
        /// </summary>
        public static Func<Transform, bool> IsAnimating;

        /// <summary>그림자 지도에 이 오브젝트(자식 포함)를 그려도 되는지</summary>
        public bool Shown { get; private set; }

        void OnEnable()
        {
            if (Application.isPlaying)
                StartCoroutine(ShowAfterAppear());
            else
                Show();
        }

        void OnDisable() // 꺼질 때 코루틴은 자동으로 멈춘다
        {
            if (!Shown)
                return;
            Shown = false;
            BakedShadowMap.RequestRender();
        }

        System.Collections.IEnumerator ShowAfterAppear()
        {
            // 등장 연출은 SetActive(true) 바로 다음에 걸리므로 한 프레임 기다린 뒤 확인 (지연 중인 연출도 포함)
            yield return null;
            while (IsAppearing())
                yield return null;
            Show();
        }

        // 자기 자신이나 바깥 표식 오브젝트(예: 레벨 모델을 품은 해금 대상)가 연출 중인지
        bool IsAppearing()
        {
            var check = IsAnimating;
            if (check == null)
                return false;
            for (var t = transform; t != null; t = t.parent)
                if ((t == transform || t.GetComponent<BakedShadowCaster>() != null) && check(t))
                    return true;
            return false;
        }

        void Show()
        {
            Shown = true;
            BakedShadowMap.RequestRender();
        }
    }
}
