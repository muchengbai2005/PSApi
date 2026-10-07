using System;
using Il2Cpp;
using UnityEngine;

namespace PSApi.Events.UI
{
    /// <summary>
    /// P3 (v1.28.0) 图元窗钉位静态助手 — 提取自已实证的钉位写法 (v1.25.1 实测),
    /// 供 script 驱动场景的能力服务 (ScriptGridService 等) 复用。
    /// 屏比 (0-1, 原点左下) 经窗口所在 canvas 的 RectTransform 局部坐标系换算
    /// (ScreenPointToLocalPointInRectangle → TransformPoint), 不用 Screen 像素/scaleFactor
    /// 手算 (换分辨率/窗口化时手算与 canvas 实际缩放失配, 窗口整只沉出屏幕底的根因);
    /// 中心钉位 pivot 无关 + 屏内钳制 (任何分辨率下整窗不离开屏幕)。
    /// </summary>
    internal static class PixelPin
    {
        /// <summary>图元窗口钉位: rel (0-1, 原点左下) → 窗口矩形中心钉到该屏比位置 (屏内钳制)。</summary>
        internal static void PinWindow(PixelWindow w, float relX, float relY)
        {
            if (w == null) return;
            try
            {
                var rt = w.rectTransform;
                if (rt is null) return;
                if (!ScreenRelToWorld(rt, relX, relY, out Vector3 center)) return;
                SetWindowCenterWorld(rt, ClampCenterOnScreen(rt, center));
            }
            catch { }
        }

        /// <summary>物品箱钉位 (v1.24.1 语义): relY = 底边锚点 — 窗口底缘贴 relY,
        /// 中心 = 底边 + 窗口实际半高 (rect.height × lossyScale, 不写死数值)。</summary>
        internal static void PinWindowBottom(PixelWindow w, float relX, float relY)
        {
            if (w == null) return;
            try
            {
                var rt = w.rectTransform;
                if (rt is null) return;
                if (!ScreenRelToWorld(rt, relX, relY, out Vector3 bottom)) return;
                float halfH = rt.rect.height * rt.lossyScale.y * 0.5f;
                SetWindowCenterWorld(rt, ClampCenterOnScreen(rt,
                    new Vector3(bottom.x, bottom.y + halfH, bottom.z)));
            }
            catch { }
        }

        /// <summary>地面窗钉位 (v1.24.1 语义): 左缘贴物品箱右缘 + gap 像素, 底边与箱底对齐;
        /// 箱缺失 → false (调用方回退绝对钉位)。</summary>
        internal static bool PinRightOf(PixelWindow win, PixelWindow anchorWin, float gapPx)
        {
            if (win == null || anchorWin == null) return false;
            try
            {
                var grt = win.rectTransform;
                var brt = anchorWin.rectTransform;
                if (grt is null || brt is null) return false;
                float gapW = gapPx / WorldScale(grt);
                var bc = WindowCenterWorld(brt);
                float boxRight = bc.x + brt.rect.width * brt.lossyScale.x * 0.5f;
                float boxBottom = bc.y - brt.rect.height * brt.lossyScale.y * 0.5f;
                var center = new Vector3(
                    boxRight + gapW + grt.rect.width * grt.lossyScale.x * 0.5f,
                    boxBottom + grt.rect.height * grt.lossyScale.y * 0.5f,
                    grt.position.z);
                SetWindowCenterWorld(grt, ClampCenterOnScreen(grt, center));
                return true;
            }
            catch { return false; }
        }

        /// <summary>窗口所在根 canvas 的 RectTransform + 相机 (overlay = null)。</summary>
        private static RectTransform CanvasRectOf(RectTransform rt, out Camera cam)
        {
            cam = null;
            try
            {
                var c = rt.gameObject.GetComponentInParent<Canvas>();
                if (c is null) return null;
                try { var root = c.rootCanvas; if (!(root is null)) c = root; } catch { }
                if (c.renderMode == RenderMode.ScreenSpaceCamera) cam = c.worldCamera;
                return c.transform.TryCast<RectTransform>();
            }
            catch { return null; }
        }

        /// <summary>屏比 (0-1, 原点左下) → 世界坐标 (经 canvas 局部坐标系, 任意分辨率/缩放一致)。</summary>
        private static bool ScreenRelToWorld(RectTransform rt, float relX, float relY, out Vector3 world)
        {
            world = Vector3.zero;
            var crt = CanvasRectOf(rt, out Camera cam);
            if (crt is null) return false;
            Vector2 lp;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(crt,
                    new Vector2(relX * Screen.width, relY * Screen.height), cam, out lp))
                return false;
            world = crt.TransformPoint(new Vector3(lp.x, lp.y, 0f));
            return true;
        }

        /// <summary>v1.30.0 (M 键写回): 窗口矩形中心的屏比 (0-1, 原点左下) —
        /// ScreenRelToWorld 的逆运算 (世界坐标 → WorldToScreenPoint → ÷屏幕像素; overlay cam=null 同款)。</summary>
        internal static bool WindowCenterScreenRel(PixelWindow w, out float relX, out float relY)
        {
            relX = relY = 0f;
            if (w == null) return false;
            try
            {
                var rt = w.rectTransform;
                if (rt is null) return false;
                return WorldToScreenRel(rt, WindowCenterWorld(rt), out relX, out relY);
            }
            catch { return false; }
        }

        /// <summary>v1.30.0 (M 键写回): 窗口底边中点的屏比 (box.y 底边锚语义的逆运算)。</summary>
        internal static bool WindowBottomScreenRel(PixelWindow w, out float relX, out float relY)
        {
            relX = relY = 0f;
            if (w == null) return false;
            try
            {
                var rt = w.rectTransform;
                if (rt is null) return false;
                var bottom = WindowCenterWorld(rt);
                bottom.y -= rt.rect.height * rt.lossyScale.y * 0.5f;
                return WorldToScreenRel(rt, bottom, out relX, out relY);
            }
            catch { return false; }
        }

        /// <summary>世界坐标 → 屏比 (0-1, 原点左下); overlay/camera 双 renderMode 都走 WorldToScreenPoint。</summary>
        private static bool WorldToScreenRel(RectTransform rt, Vector3 world, out float relX, out float relY)
        {
            relX = relY = 0f;
            var crt = CanvasRectOf(rt, out Camera cam);
            if (crt is null) return false;
            if (Screen.width <= 0 || Screen.height <= 0) return false;
            Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, world);
            relX = sp.x / Screen.width;
            relY = sp.y / Screen.height;
            return true;
        }

        /// <summary>窗口矩形中心的世界坐标 (pivot 无关: position 是 pivot 点, 中心 = pivot + (0.5-pivot)×rect×lossy)。</summary>
        private static Vector3 WindowCenterWorld(RectTransform rt)
        {
            var p = rt.pivot;
            var r = rt.rect;
            var ls = rt.lossyScale;
            var pos = rt.position;
            return new Vector3(pos.x + (0.5f - p.x) * r.width * ls.x,
                               pos.y + (0.5f - p.y) * r.height * ls.y, pos.z);
        }

        /// <summary>把窗口矩形中心钉到世界坐标 (pivot 无关, 保持 z)。</summary>
        private static void SetWindowCenterWorld(RectTransform rt, Vector3 center)
        {
            var p = rt.pivot;
            var r = rt.rect;
            var ls = rt.lossyScale;
            var pos = rt.position;
            rt.position = new Vector3(center.x - (0.5f - p.x) * r.width * ls.x,
                                      center.y - (0.5f - p.y) * r.height * ls.y, pos.z);
        }

        /// <summary>屏内钳制: 窗口中心钳在 [半尺寸, 屏-半尺寸] 内 (窗比屏大时该轴不钳)。</summary>
        private static Vector3 ClampCenterOnScreen(RectTransform rt, Vector3 center)
        {
            var crt = CanvasRectOf(rt, out _);
            if (crt is null) return center;
            var r = rt.rect;
            var ls = rt.lossyScale;
            float hw = r.width * ls.x * 0.5f, hh = r.height * ls.y * 0.5f;
            var cr = crt.rect;
            var minW = crt.TransformPoint(new Vector3(cr.xMin, cr.yMin, 0f));
            var maxW = crt.TransformPoint(new Vector3(cr.xMax, cr.yMax, 0f));
            float cx = center.x, cy = center.y;
            if (minW.x + hw <= maxW.x - hw) cx = Math.Max(minW.x + hw, Math.Min(maxW.x - hw, cx));
            if (minW.y + hh <= maxW.y - hh) cy = Math.Max(minW.y + hh, Math.Min(maxW.y - hh, cy));
            return new Vector3(cx, cy, center.z);
        }

        /// <summary>窗口所在 overlay canvas 的 scaleFactor (取不到回退 1; gap 像素换算用)。</summary>
        private static float WorldScale(RectTransform rt)
        {
            try
            {
                var c = rt.gameObject.GetComponentInParent<Canvas>();
                if (c != null && c.scaleFactor > 0.01f) return c.scaleFactor;
            }
            catch { }
            return 1f;
        }

        /// <summary>v1.29.1: 图元窗标题栏文本像素宽估算 — PixelWindow 标题栏截断阈值按拉丁
        /// 字符宽算 (SetTitle 原生逻辑), 中文按约 2 倍宽渲染 → 长中文标题溢出压到 X 按钮
        /// (实证: "废弃军械库·脚本版" 8 字在 ~120px 宽场景壳窗上与 X 互叠; "地面 (前进时自动
        /// 清空)" 在 6 格地面窗上 "清" 字被 X 盖住)。调用方用本估算撑宽窗口内容使标题全显。
        /// 每字: ASCII 12px, 其余 (CJK/全角) 24px (标题字号实测 ≈24px/中文字);
        /// +40px 标题栏镶边 (X 按钮 ~30 + 左右边距 ~10)。纯函数可无头测。</summary>
        internal static int TitleBarContentWidth(string title)
        {
            if (string.IsNullOrEmpty(title)) return 0;
            int w = 0;
            foreach (char c in title) w += c < 0x80 ? 12 : 24;
            return w + 40;
        }

        /// <summary>v1.29.1: 内容入座 + 标题栏宽度保障 — PixelWindow 随内容自适应宽度, 标题栏
        /// 文本不参测 → 长中文标题溢出压 X 按钮 (地面窗 "地面 (前进时自动清空)" 的 "清" 字被盖
        /// 实证)。内容外包一层 1x2 居中网格, 顶行垫空占位按标题估算宽撑列 (PsUi spacer size
        /// 同款 Attach(el,x,y,minW,minH) 预留像素; 列宽取各单元最大值, 内容本身更宽时无效果)。
        /// 任何一步失败回退直接 Attach 内容 (标题可能溢出但窗可用)。</summary>
        internal static bool AttachWithTitleWidth(PixelWindow win, PixelElement content, string title)
        {
            if (win == null || content == null) return false;
            int titleW = TitleBarContentWidth(title);
            if (titleW <= 0) return win.Attach(content);
            try
            {
                var wrap = new GridPixelElement(1, 2, true);
                var spacer = new GridPixelElement(1, 1);
                if (wrap == null || spacer == null) return win.Attach(content);
                wrap.Attach(spacer.TryCast<PixelElement>(), 0, 0, titleW, 1);
                wrap.Attach(content, 0, 1);
                return win.Attach(wrap.TryCast<PixelElement>());
            }
            catch { return win.Attach(content); }
        }
    }
}
