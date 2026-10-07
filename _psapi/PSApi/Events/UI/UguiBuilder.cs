using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PSApi.Events.UI
{
    /// <summary>
    /// v1.24.0 uGUI 基础设施 (方案 C: uGUI 壳 + 图元物品网格混合架构) — 场景 HUD 的非网格部分
    /// 全部 uGUI 化 (精确像素 anchoredPosition 控制), 物品网格保留原版图元 (拖拽管线深耦合, 一行不动)。
    /// 原版自己就是双轨制: 商店内 77 个面板类用 uGUI+TMP, 外出场景用图元 — 场景 HUD 迁 uGUI
    /// 是回归原版工程实践。
    /// 要点:
    /// - 层序铁律: 原版图元窗在 FloatingWindowCanvas (overlay, order=3) — 自建 canvas 垫背景 &lt;3、
    ///   盖窗口 &gt;3; 运行时 GameObject.Find("FloatingWindowCanvas") 探测 + 日志钉死, 不写死魔法数
    ///   (探测失败才回退 fallback 并在日志明示)。
    /// - 中文: TMP 必须显式绑 FontLoader.Instance.simplifiedChineseFont (不绑中文全是方块 —
    ///   v1.19.x 实证); 绑定失败回退默认字体 + Warn 一次。
    /// - 按钮: 所有 onClick 统一 0.3s 去抖 (游戏输入链路按下/抬起双派发 uGUI onClick, 铁律);
    ///   委托双 pin 防 GC。
    /// - raycastTarget 管理: 纯装饰 (面板底/文本/状态条) 关 raycast 防点击穿透; 按钮与模态底板开。
    /// Il2Cpp 红线: AddComponent 产物取组件用 GameObject 版 API; 不 foreach(Transform)。
    /// </summary>
    internal sealed class UguiBuilder
    {
        private readonly MelonLogger.Instance _logger;
        private readonly List<object> _pins;
        private bool _fontWarned;

        internal UguiBuilder(MelonLogger.Instance logger, List<object> pins)
        {
            _logger = logger;
            _pins = pins ?? new List<object>();
        }

        // ==================== canvas ====================

        /// <summary>自建 overlay canvas (+GraphicRaycaster)。返回根 GameObject (其 RectTransform 取
        /// root.transform.TryCast&lt;RectTransform&gt;() — Il2Cpp 红线不用 as)。</summary>
        internal GameObject MakeCanvas(string name, int order, bool withRaycaster = true)
        {
            var go = new GameObject(name);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            if (withRaycaster) go.AddComponent<GraphicRaycaster>();
            return go;
        }

        /// <summary>探测原版图元窗所在 overlay canvas (FloatingWindowCanvas) 的 sortingOrder,
        /// 日志钉死来源 (层级铁律: 运行时探测, 不写死); 探测失败 = fallback。</summary>
        internal int DetectPixelCanvasOrder(int fallback, string why)
        {
            int order = fallback;
            string src = "fallback(未找到 FloatingWindowCanvas)";
            try
            {
                var go = GameObject.Find("FloatingWindowCanvas");
                if (go is not null)
                {
                    var c = go.GetComponent<Canvas>();
                    if (c is not null) { order = c.sortingOrder; src = "FloatingWindowCanvas"; }
                }
            }
            catch (Exception e) { PsApi.Warn(_logger, $"[ugui] 层级探测异常 ({why}): {e.Message}"); }
            PsApi.Log(_logger, $"[ugui] 图元窗 canvas 层级探测 ({why}): order={order} (来源: {src})");
            return order;
        }

        // ==================== 字体 ====================

        /// <summary>中文字体: FontLoader.Instance.simplifiedChineseFont; 拿不到回退 TMP 默认字体
        /// + Warn 一次 (中文可能不显示)。</summary>
        internal TMP_FontAsset CnFont()
        {
            TMP_FontAsset font = null;
            try
            {
                var fl = FontLoader.Instance;
                if (!(fl is null)) font = fl.simplifiedChineseFont;
            }
            catch { }
            if (font is null)
            {
                try { font = TMP_Settings.defaultFontAsset; } catch { }
                if (!_fontWarned)
                {
                    _fontWarned = true;
                    PsApi.Warn(_logger, "[ugui] 中文字体绑定失败 (FontLoader.simplifiedChineseFont 不可用), 回退默认字体 — 中文可能不显示");
                }
            }
            return font;
        }

        // ==================== 文本 ====================

        /// <summary>TMP 文本 (纯装饰: raycastTarget=false 防点击穿透; 先绑中文字体再设文本)。</summary>
        internal TextMeshProUGUI MakeText(Transform parent, string name, string text, float fontSize, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.raycastTarget = false;
            var f = CnFont();
            if (f is not null) { try { tmp.font = f; } catch { } }
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = align;
            tmp.text = text ?? "";
            return tmp;
        }

        // ==================== 面板 ====================

        /// <summary>纯色面板框 (Image); raycast=false = 纯装饰不吃点击, true = 底板拦点击。</summary>
        internal RectTransform MakePanel(Transform parent, string name, float w, float h, Color bg, bool raycast)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(w, h);
            var img = go.AddComponent<Image>();
            img.color = bg;
            img.raycastTarget = raycast;
            return rt;
        }

        // ==================== 按钮 ====================

        /// <summary>uGUI 按钮 (Image 底 + Button + TMP 子物体); onClick 统一 0.3s 去抖
        /// (游戏输入链路按下/抬起双派发 uGUI onClick, 铁律); 回调异常 Warn 不穿透; 委托双 pin。</summary>
        internal Button MakeButton(Transform parent, string name, string label, float w, float h, float fontSize,
            System.Action onClick, out TextMeshProUGUI labelTmp)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(w, h);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.16f, 0.12f, 0.09f, 0.95f);
            img.raycastTarget = true;
            var btn = go.AddComponent<Button>();

            labelTmp = MakeText(go.transform, "txt", label, fontSize, new Color(0.92f, 0.85f, 0.70f), TextAlignmentOptions.Center);
            var trt = labelTmp.rectTransform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;

            float last = -1f;
            System.Action guarded = () =>
            {
                float now = Time.unscaledTime;
                if (!PassDebounce(ref last, now, 0.3f)) return;
                try { onClick(); }
                catch (Exception e) { PsApi.Warn(_logger, $"[ugui] 按钮 '{name}' 回调异常: {e.Message}"); }
            };
            var uact = DelegateSupport.ConvertDelegate<UnityAction>(guarded);
            _pins.Add(onClick);
            _pins.Add(guarded);
            _pins.Add(uact);
            try { btn.onClick.AddListener(uact); }
            catch (Exception e) { PsApi.Warn(_logger, $"[ugui] 按钮 '{name}' 挂监听失败: {e.Message}"); }
            return btn;
        }

        /// <summary>去抖门 (纯函数, pss_test 可测): now-last &gt;= interval 放行并更新 last。</summary>
        internal static bool PassDebounce(ref float last, float now, float interval)
        {
            if (now - last < interval) return false;
            last = now;
            return true;
        }

        // ==================== 状态条 ====================

        /// <summary>数值条 (底 Image + fill Image 子物体, 水平从左填); 返回 fill Image
        /// (调用方用 SetFill 设数值/color 设颜色)。纯装饰: 双 Image 都关 raycast。</summary>
        internal Image MakeBar(Transform parent, string name, float w, float h, Color fillColor)
        {
            var bg = MakePanel(parent, name, w, h, new Color(0.10f, 0.10f, 0.12f, 0.9f), false);
            var go = new GameObject("fill");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(bg, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = fillColor;
            img.raycastTarget = false;
            // v1.25.1 (bug: 条不刷新): 不再用 Image.Type.Filled — spriteless Image 在 Filled 模式下
            // fillAmount 不生效 (Unity 填充路径需要 sprite, 无 sprite 退化成整条满渲染)。
            // 改锚点式填充: SetFill 调 anchorMax.x (Simple 模式 spriteless 正常渲染)。
            return img;
        }

        /// <summary>锚点式数值条填充 (v1.25.1): fill 的 anchorMax.x = pct (0-1), 兼容 spriteless Image。</summary>
        internal static void SetFill(Image fill, float pct)
        {
            if (fill is null) return;
            var rt = fill.rectTransform;
            if (rt is null) return;
            float p = pct < 0f ? 0f : pct > 1f ? 1f : pct;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = new Vector2(p, 1f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // ==================== 滚动视图 (v1.37.0, picker 双栏左栏) ====================

        /// <summary>竖排滚动视图: viewport (RectMask2D 裁剪 + Image 底 raycast 拦穿透) + ScrollRect
        /// (竖向 Clamped) + content (锚顶满宽, 调用方自管子元素 SeatTop 累高并回填 sizeDelta.y —
        /// 不引 LayoutGroup, 防 Il2Cpp 重建时序坑)。返回 ScrollRect; content 经 out 给子元素父级。</summary>
        internal ScrollRect MakeScrollView(Transform parent, string name, float w, float h, Color bg,
            out RectTransform content)
        {
            var vpGo = new GameObject(name);
            var vpRt = vpGo.AddComponent<RectTransform>();
            vpRt.SetParent(parent, false);
            vpRt.sizeDelta = new Vector2(w, h);
            var img = vpGo.AddComponent<Image>();
            img.color = bg;
            img.raycastTarget = true;
            vpGo.AddComponent<RectMask2D>();
            var scroll = vpGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            var cGo = new GameObject("content");
            var cRt = cGo.AddComponent<RectTransform>();
            cRt.SetParent(vpRt, false);
            cRt.anchorMin = new Vector2(0f, 1f);
            cRt.anchorMax = new Vector2(1f, 1f);
            cRt.pivot = new Vector2(0.5f, 1f);
            cRt.anchoredPosition = Vector2.zero;
            cRt.sizeDelta = new Vector2(0f, h);
            scroll.viewport = vpRt;
            scroll.content = cRt;
            content = cRt;
            return scroll;
        }

        // ==================== 钉位 ====================

        /// <summary>中心锚钉位: rel 0-1 (原点左下) → 相对 root 中心的 anchoredPosition
        /// (overlay canvas 无 scaler 时 root.rect = 屏幕像素)。</summary>
        internal static void AnchorCenter(RectTransform rt, RectTransform root, float relX, float relY)
        {
            if (rt is null || root is null) return;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var rect = root.rect;
            rt.anchoredPosition = new Vector2((relX - 0.5f) * rect.width, (relY - 0.5f) * rect.height);
        }

        /// <summary>左下角锚钉位 (v1.25.1, 日志栏): rel 0-1 (原点左下) = 面板左下角的位置,
        /// anchor/pivot 全 (0,0) — 任意分辨率下面板左下角距屏幕左下角比例恒定, 不会沉出屏幕。</summary>
        internal static void AnchorBottomLeft(RectTransform rt, RectTransform root, float relX, float relY)
        {
            if (rt is null || root is null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            var rect = root.rect;
            rt.anchoredPosition = new Vector2(relX * rect.width, relY * rect.height);
        }

        /// <summary>左上角锚钉位 (v1.30.0, 场景标题面板): rel 0-1 (原点左下) = 面板左上角的位置,
        /// anchor/pivot 全 (0,1) — 任意分辨率下面板左上角距屏幕左上角比例恒定。</summary>
        internal static void AnchorTopLeft(RectTransform rt, RectTransform root, float relX, float relY)
        {
            if (rt is null || root is null) return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            var rect = root.rect;
            rt.anchoredPosition = new Vector2(relX * rect.width, (relY - 1f) * rect.height);
        }

        /// <summary>面板内顶对齐竖排: 子元素锚 (0.5,1) pivot (0.5,1), y = -offsetY (从上到下累计)。</summary>
        internal static void SeatTop(RectTransform rt, float offsetY)
        {
            if (rt is null) return;
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -offsetY);
        }
    }
}
