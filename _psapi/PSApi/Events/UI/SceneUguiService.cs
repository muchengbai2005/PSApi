using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using MelonLoader;
using PSApi.Events.PsScript;
using UnityEngine;
using UnityEngine.UI;

namespace PSApi.Events.UI
{
    /// <summary>P2 (v1.27.0) 场景 uGUI 元素记录 (句柄表行)。Unity 对象只存引用, 表本身纯托管可测。</summary>
    internal sealed class UguiElemRec
    {
        internal long Handle;
        internal string SceneId;
        internal string Kind;            // panel | text | button | bar
        internal GameObject Go;
        internal RectTransform Rt;
        internal TextMeshProUGUI Tmp;    // text/button 的文本 (ui.set_text 目标)
        internal Image Fill;             // bar 的填充 (ui.set_fill 目标)
        internal float W, H;             // panel 像素尺寸 (子元素定位基准)
    }

    /// <summary>P2 句柄表 (纯托管, pss_test 可测): 分配/查询/删除/按场景整组摘除。
    /// 句柄从 1 自增, 0 = 无效 (不在场景时创建 API 的返回值)。</summary>
    internal sealed class UguiHandleTable
    {
        private long _next = 1;
        private readonly Dictionary<long, UguiElemRec> _map = new Dictionary<long, UguiElemRec>();

        internal int Count => _map.Count;

        internal long Add(UguiElemRec rec)
        {
            rec.Handle = _next++;
            _map[rec.Handle] = rec;
            return rec.Handle;
        }

        internal bool TryGet(long handle, out UguiElemRec rec) => _map.TryGetValue(handle, out rec);

        internal bool Remove(long handle) => _map.Remove(handle);

        /// <summary>摘除该场景全部记录 (从表移除并返回, 调用方负责 Destroy Unity 对象)。</summary>
        internal List<UguiElemRec> DrainScene(string sceneId)
        {
            var outList = new List<UguiElemRec>();
            if (sceneId == null) return outList;
            var keys = new List<long>();
            foreach (var kv in _map)
                if (kv.Value.SceneId == sceneId) keys.Add(kv.Key);
            foreach (var k in keys)
            {
                outList.Add(_map[k]);
                _map.Remove(k);
            }
            return outList;
        }

        internal List<UguiElemRec> DrainAll()
        {
            var outList = new List<UguiElemRec>(_map.Values);
            _map.Clear();
            return outList;
        }

        internal int CountScene(string sceneId)
        {
            int n = 0;
            foreach (var kv in _map)
                if (kv.Value.SceneId == sceneId) n++;
            return n;
        }
    }

    /// <summary>
    /// P2 (v1.27.0) 场景 uGUI 创建服务 — pss ui.panel/text/button/bar 句柄式 API 的后端:
    /// script 驱动场景的第二条 UI 路线 (psui 图元面板之外的自由布局)。
    /// 坐标语义与场景布局 (RaidLayout) 一致: x/y 屏比 0-1 (顶层面板相对屏幕左下, 子元素相对父面板左下),
    /// w/h 像素。元素挂在场景壳的自建 overlay canvas 下 (层级铁律: UguiBuilder 探测
    /// FloatingWindowCanvas order, 不写死魔法数; opts.order 可强制)。
    /// 生命周期硬要求: 句柄表按场景 FullId 归组, 场景撤离 (SceneService.DoExit → OnSceneExit)
    /// 自动销毁整组, 防泄漏防孤儿; Unity 场景卸载只清引用; 重进/F10 防御性清残留。
    /// 每个场景进入期一个 UguiBuilder (其委托 pins 随 canvas 销毁整体弃 — 按钮已死, 无 GC 回调风险)。
    /// 不在任何自定义场景里调用创建 API = Warn + 返回 0 (别崩); 中文/去抖/填充全走 UguiBuilder 现有机制。
    /// </summary>
    internal sealed class SceneUguiService
    {
        private readonly MelonLogger.Instance _logger;
        /// <summary>场景服务反向引用 (Plugin 接线, 与 Raid 同款双向)。</summary>
        internal Scenes.SceneService Scenes;

        private readonly UguiHandleTable _table = new UguiHandleTable();
        private UguiBuilder _builder;            // 每个场景进入期一个
        private GameObject _canvasGo;
        private RectTransform _canvasRoot;
        private string _canvasSceneId;
        private int _nameSeq;

        internal SceneUguiService(MelonLogger.Instance logger) { _logger = logger; }

        internal int ElementCount => _table.Count;
        internal int CountForScene(string sceneId) => _table.CountScene(sceneId);
        /// <summary>句柄表本体 (pss_test 注入假记录测归组清理逻辑用)。</summary>
        internal UguiHandleTable Table => _table;

        // ==================== 参数钳制 / 颜色解析 (纯函数, pss_test 可测) ====================

        internal static float Clamp01(double v) => v < 0 ? 0f : v > 1 ? 1f : (float)v;
        internal static float ClampPx(double v) => v < 1 ? 1f : v > 2000 ? 2000f : (float)v;
        internal static float ClampFont(double v) => v < 6 ? 6f : v > 72 ? 72f : (float)v;

        /// <summary>#RGB / #RRGGBB / #RRGGBBAA → 0-1 分量; 非法返回 false。纯函数。</summary>
        internal static bool TryParseHexColor(string s, out float r, out float g, out float b, out float a)
        {
            r = g = b = 1f; a = 1f;
            if (string.IsNullOrWhiteSpace(s) || s[0] != '#') return false;
            string hex = s.Substring(1);
            try
            {
                if (hex.Length == 3)
                    hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
                if (hex.Length == 6) hex += "FF";
                if (hex.Length != 8) return false;
                uint v = Convert.ToUInt32(hex, 16);
                r = ((v >> 24) & 0xFF) / 255f;
                g = ((v >> 16) & 0xFF) / 255f;
                b = ((v >> 8) & 0xFF) / 255f;
                a = (v & 0xFF) / 255f;
                return true;
            }
            catch { return false; }
        }

        private static double AsOptNum(object v, string fn, string what, int line)
        {
            if (v is long l) return l;
            if (v is double d) return d;
            throw new PsRuntimeError($"{fn} 的 {what} 须为数字, 实为 {PsValues.TypeName(v)}", line);
        }

        private static Color ParseColorOpts(Dictionary<string, object> opts, float defR, float defG, float defB, float defA, string fn, int line)
        {
            float r = defR, g = defG, b = defB, a = defA;
            object cv;
            if (opts != null && opts.TryGetValue("color", out cv) && cv != null)
            {
                if (!(cv is string cs) || !TryParseHexColor(cs, out r, out g, out b, out a))
                    throw new PsRuntimeError($"{fn} 的 color 须为 \"#rrggbb\" 形式, 实为 {PsValues.Fmt(cv)}", line);
            }
            object av;
            if (opts != null && opts.TryGetValue("alpha", out av) && av != null)
                a = Clamp01(AsOptNum(av, fn, "alpha", line));
            return new Color(r, g, b, a);
        }

        // ==================== 场景守卫 / canvas ====================

        private string ActiveSceneId()
        {
            try { return Scenes?.Active?.FullId; } catch { return null; }
        }

        /// <summary>创建类 API 共用守卫: 不在任何自定义场景 = Warn + false (调用方返回 0, 别崩)。</summary>
        private bool NeedScene(string fn, out string sceneId)
        {
            sceneId = ActiveSceneId();
            if (sceneId != null) return true;
            PsApi.Warn(_logger, $"[scene-ugui] {fn} 不在任何自定义场景里调用, 已忽略 (返回 0)");
            return false;
        }

        private bool EnsureCanvas(string sceneId, int? order, string fn)
        {
            if (_canvasGo is not null && _canvasSceneId == sceneId)
            {
                if (order.HasValue)
                {
                    try { var c = _canvasGo.GetComponent<Canvas>(); if (c is not null) c.sortingOrder = order.Value; } catch { }
                }
                return true;
            }
            // 场景切换/残留保险 (正常路径 OnSceneExit 已清)
            CleanupRuntime("canvas-stale");
            _builder = new UguiBuilder(_logger, new List<object>());
            int ord = order ?? _builder.DetectPixelCanvasOrder(3, "场景 uGUI");
            _canvasGo = _builder.MakeCanvas("PSApiSceneUgui", ord, true);
            if (_canvasGo is null) { PsApi.Warn(_logger, $"[scene-ugui] {fn}: canvas 创建失败"); return false; }
            _canvasRoot = _canvasGo.transform.TryCast<RectTransform>();   // Il2Cpp 红线: 不用 as
            if (_canvasRoot is null)
            {
                PsApi.Warn(_logger, $"[scene-ugui] {fn}: canvas RectTransform 获取失败");
                try { UnityEngine.Object.Destroy(_canvasGo); } catch { }
                _canvasGo = null;
                return false;
            }
            _canvasSceneId = sceneId;
            PsApi.Log(_logger, $"[scene-ugui] 场景 uGUI canvas 已建 (scene={sceneId}, order={ord})");
            return true;
        }

        // ==================== 创建 ====================

        internal long CreatePanel(double x, double y, double w, double h, Dictionary<string, object> opts, int line)
        {
            string sceneId;
            if (!NeedScene("ui.panel", out sceneId)) return 0L;
            int? order = null;
            object ov;
            if (opts != null && opts.TryGetValue("order", out ov) && ov != null)
                order = (int)AsOptNum(ov, "ui.panel", "order", line);
            bool raycast = false;
            if (opts != null && opts.TryGetValue("raycast", out ov) && ov != null) raycast = PsValues.Truthy(ov);
            // P3 (v1.28.0): anchor="center" → x/y = 面板中心点屏比 (AnchorCenter; raid 状态/行动区同款);
            // 缺省 "bl" = 左下角屏比 (旧行为一字不改)
            bool center = false;
            if (opts != null && opts.TryGetValue("anchor", out ov) && ov != null)
            {
                string av = PsValues.Fmt(ov);
                if (av == "center") center = true;
                else if (av != "bl") throw new PsRuntimeError($"ui.panel 的 anchor 仅支持 \"bl\"/\"center\", 实为 '{av}'", line);
            }
            var color = ParseColorOpts(opts, 0.07f, 0.07f, 0.10f, 0.97f, "ui.panel", line);
            if (!EnsureCanvas(sceneId, order, "ui.panel")) return 0L;
            float pw = ClampPx(w), ph = ClampPx(h);
            var rt = _builder.MakePanel(_canvasRoot, "p2_panel_" + (++_nameSeq), pw, ph, color, raycast);
            if (center) UguiBuilder.AnchorCenter(rt, _canvasRoot, Clamp01(x), Clamp01(y));
            else UguiBuilder.AnchorBottomLeft(rt, _canvasRoot, Clamp01(x), Clamp01(y));
            long handle = _table.Add(new UguiElemRec { SceneId = sceneId, Kind = "panel", Go = rt.gameObject, Rt = rt, W = pw, H = ph });
            PsApi.Log(_logger, $"[scene-ugui] panel #{handle} ({pw:0}x{ph:0} @ {Clamp01(x):0.00},{Clamp01(y):0.00})");
            return handle;
        }

        /// <summary>子元素父句柄校验: 必须是存活的面板。</summary>
        private UguiElemRec NeedParent(long handle, string fn, int line)
        {
            UguiElemRec rec;
            if (!_table.TryGet(handle, out rec))
                throw new PsRuntimeError($"{fn} 的父句柄 #{handle} 不存在 (未创建/已销毁/场景已退出)", line);
            if (rec.Kind != "panel")
                throw new PsRuntimeError($"{fn} 的父元素须为 ui.panel 句柄, 实为 {rec.Kind} (#{handle})", line);
            if (rec.Rt is null)
                throw new PsRuntimeError($"{fn} 的父面板 #{handle} 已被销毁", line);
            return rec;
        }

        /// <summary>子元素左下角锚定位: rel 0-1 (相对父面板左下) + 父像素尺寸 → anchoredPosition。</summary>
        private static void SeatChild(RectTransform rt, double relX, double relY, float parentW, float parentH)
        {
            if (rt is null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(Clamp01(relX) * parentW, Clamp01(relY) * parentH);
        }

        /// <summary>P3 (v1.28.0): 父句柄 0 = 场景 canvas 根, x/y = 元素中心点屏比 (AnchorCenter;
        /// raid poi 按钮直挂 HUD 根同款)。非 0 = 存活的面板 (旧行为)。</summary>
        private RectTransform RootFor(string fn, out string sceneId, int line)
        {
            sceneId = ActiveSceneId();
            if (sceneId == null)
                throw new PsRuntimeError($"{fn} 的父句柄 0 (canvas 根) 需要在自定义场景里调用", line);
            if (!EnsureCanvas(sceneId, null, fn))
                throw new PsRuntimeError($"{fn}: 场景 uGUI canvas 创建失败", line);
            return _canvasRoot;
        }

        internal long CreateText(long parent, double x, double y, double w, string text, double fontSize, int line)
        {
            float fs = ClampFont(fontSize);
            if (parent == 0)
            {
                var root = RootFor("ui.text", out string sceneId0, line);
                var tmp0 = _builder.MakeText(root, "p2_text_" + (++_nameSeq), text, fs, new Color(0.92f, 0.92f, 0.92f));
                var rt0 = tmp0.rectTransform;
                rt0.sizeDelta = new Vector2(ClampPx(w), fs * 1.5f);
                UguiBuilder.AnchorCenter(rt0, root, Clamp01(x), Clamp01(y));
                return _table.Add(new UguiElemRec { SceneId = sceneId0, Kind = "text", Go = tmp0.gameObject, Rt = rt0, Tmp = tmp0 });
            }
            var rec = NeedParent(parent, "ui.text", line);
            var tmp = _builder.MakeText(rec.Rt, "p2_text_" + (++_nameSeq), text, fs, new Color(0.92f, 0.92f, 0.92f));
            var rt = tmp.rectTransform;
            rt.sizeDelta = new Vector2(ClampPx(w), fs * 1.5f);
            SeatChild(rt, x, y, rec.W, rec.H);
            return _table.Add(new UguiElemRec { SceneId = rec.SceneId, Kind = "text", Go = tmp.gameObject, Rt = rt, Tmp = tmp });
        }

        internal long CreateButton(long parent, double x, double y, double w, double h, string label, string fnName, double fontSize, Interpreter itp, int line)
        {
            float fs = ClampFont(fontSize);
            float bw = ClampPx(w), bh = ClampPx(h);
            // 句柄在 Add 后才知道: 先用 cell 占位, 回调闭包共拿 (委托双 pin 在 UguiBuilder.MakeButton 内)
            var cell = new long[1];
            System.Action onClick = () => FireClick(itp, fnName, cell[0]);
            if (parent == 0)
            {
                var root = RootFor("ui.button", out string sceneId0, line);
                var btn0 = _builder.MakeButton(root, "p2_btn_" + (++_nameSeq), label, bw, bh, fs, onClick, out var tmp0);
                var brt0 = btn0.transform.TryCast<RectTransform>();
                UguiBuilder.AnchorCenter(brt0, root, Clamp01(x), Clamp01(y));
                long h0 = _table.Add(new UguiElemRec { SceneId = sceneId0, Kind = "button", Go = btn0.gameObject, Rt = brt0, Tmp = tmp0, W = bw, H = bh });
                cell[0] = h0;
                PsApi.Log(_logger, $"[scene-ugui] button #{h0} '{label}' → {fnName}() (canvas 根, 中心 {Clamp01(x):0.00},{Clamp01(y):0.00})");
                return h0;
            }
            var rec = NeedParent(parent, "ui.button", line);
            var btn = _builder.MakeButton(rec.Rt, "p2_btn_" + (++_nameSeq), label, bw, bh, fs, onClick, out var tmp);
            var rt = btn.transform.TryCast<RectTransform>();
            SeatChild(rt, x, y, rec.W, rec.H);
            long handle = _table.Add(new UguiElemRec { SceneId = rec.SceneId, Kind = "button", Go = btn.gameObject, Rt = rt, Tmp = tmp, W = bw, H = bh });
            cell[0] = handle;
            PsApi.Log(_logger, $"[scene-ugui] button #{handle} '{label}' → {fnName}()");
            return handle;
        }

        /// <summary>按钮点击 → 创建者所在包解释器的 pss 函数 (0 参函数不传参, 否则收 {handle})。
        /// 异常由 UguiBuilder.MakeButton 的 guarded 包装捕获 Warn, 不穿透。</summary>
        private void FireClick(Interpreter itp, string fnName, long handle)
        {
            if (itp == null) { PsApi.Warn(_logger, $"[scene-ugui] 按钮回调 '{fnName}': 解释器已失效"); return; }
            object v;
            if (!itp.Global.TryGet(fnName, out v) || !(v is PsCallable fn))
            {
                PsApi.Warn(_logger, $"[scene-ugui] 按钮回调 '{fnName}': 函数未定义");
                return;
            }
            var dict = new Dictionary<string, object>(StringComparer.Ordinal) { ["handle"] = handle };
            itp.BeginRun();
            itp.CallCallable(fn, fn is ScriptFunc sf && sf.Params.Count == 0 ? new List<object>() : new List<object> { dict }, 0);
        }

        internal long CreateBar(long parent, double x, double y, double w, double h, double pct, string colorHex, int line)
        {
            var fillColor = new Color(0.35f, 0.70f, 0.40f, 1f);
            if (!string.IsNullOrWhiteSpace(colorHex))
            {
                if (!TryParseHexColor(colorHex, out float r, out float g, out float b, out float a))
                    throw new PsRuntimeError($"ui.bar 的 color 须为 \"#rrggbb\" 形式, 实为 '{colorHex}'", line);
                fillColor = new Color(r, g, b, a);
            }
            float bw = ClampPx(w), bh = ClampPx(h);
            if (parent == 0)
            {
                var root = RootFor("ui.bar", out string sceneId0, line);
                var fill0 = _builder.MakeBar(root, "p2_bar_" + (++_nameSeq), bw, bh, fillColor);
                var brt0 = fill0.rectTransform.parent.TryCast<RectTransform>();
                UguiBuilder.AnchorCenter(brt0, root, Clamp01(x), Clamp01(y));
                UguiBuilder.SetFill(fill0, Clamp01(pct));
                return _table.Add(new UguiElemRec { SceneId = sceneId0, Kind = "bar", Go = brt0 is null ? null : brt0.gameObject, Rt = brt0, Fill = fill0, W = bw, H = bh });
            }
            var rec = NeedParent(parent, "ui.bar", line);
            var fill = _builder.MakeBar(rec.Rt, "p2_bar_" + (++_nameSeq), bw, bh, fillColor);
            var bgRt = fill.rectTransform.parent.TryCast<RectTransform>();
            SeatChild(bgRt, x, y, rec.W, rec.H);
            UguiBuilder.SetFill(fill, Clamp01(pct));
            return _table.Add(new UguiElemRec { SceneId = rec.SceneId, Kind = "bar", Go = bgRt is null ? null : bgRt.gameObject, Rt = bgRt, Fill = fill, W = bw, H = bh });
        }

        // ==================== 句柄操作 ====================

        private UguiElemRec FindLive(long handle, string fn)
        {
            UguiElemRec rec;
            if (!_table.TryGet(handle, out rec) || rec.Go is null)
            {
                PsApi.Warn(_logger, $"[scene-ugui] {fn}: 句柄 #{handle} 不存在或已销毁, 已忽略");
                return null;
            }
            return rec;
        }

        internal void SetText(long handle, string text, int line)
        {
            var rec = FindLive(handle, "ui.set_text");
            if (rec == null) return;
            if (rec.Tmp is null)
                throw new PsRuntimeError($"ui.set_text: 句柄 #{handle} 是 {rec.Kind}, 只支持 text/button", line);
            try { rec.Tmp.text = text ?? ""; }
            catch (Exception e) { PsApi.Warn(_logger, $"[scene-ugui] set_text #{handle} 失败: {e.Message}"); }
        }

        internal void SetFill(long handle, double pct, int line)
        {
            var rec = FindLive(handle, "ui.set_fill");
            if (rec == null) return;
            if (rec.Fill is null)
                throw new PsRuntimeError($"ui.set_fill: 句柄 #{handle} 是 {rec.Kind}, 只支持 bar", line);
            UguiBuilder.SetFill(rec.Fill, Clamp01(pct));
        }

        /// <summary>P3 (v1.28.0): 动态改色 — bar = 填充色 (力竭/饥饿红色态等), text/button = 文本色。</summary>
        internal void SetColor(long handle, string colorHex, int line)
        {
            var rec = FindLive(handle, "ui.set_color");
            if (rec == null) return;
            if (!TryParseHexColor(colorHex, out float r, out float g, out float b, out float a))
                throw new PsRuntimeError($"ui.set_color 的 color 须为 \"#rrggbb\" 形式, 实为 '{colorHex}'", line);
            if (rec.Fill is not null)
            {
                try { rec.Fill.color = new Color(r, g, b, a); } catch { }
                return;
            }
            if (rec.Tmp is not null)
            {
                try { rec.Tmp.color = new Color(r, g, b, a); } catch { }
                return;
            }
            throw new PsRuntimeError($"ui.set_color: 句柄 #{handle} 是 {rec.Kind}, 只支持 bar/text/button", line);
        }

        internal void SetVisible(long handle, bool visible)
        {
            var rec = FindLive(handle, "ui.set_visible");
            if (rec == null) return;
            try { rec.Go.SetActive(visible); }
            catch (Exception e) { PsApi.Warn(_logger, $"[scene-ugui] set_visible #{handle} 失败: {e.Message}"); }
        }

        internal bool DestroyElem(long handle)
        {
            UguiElemRec rec;
            if (!_table.TryGet(handle, out rec))
            {
                PsApi.Warn(_logger, $"[scene-ugui] ui.destroy: 句柄 #{handle} 不存在, 已忽略");
                return false;
            }
            _table.Remove(handle);
            try { if (rec.Go is not null) UnityEngine.Object.Destroy(rec.Go); } catch { }
            return true;
        }

        /// <summary>ui.clear(): 清当前场景全部自建元素 → 清理数 (不在场景 = warn + 0)。</summary>
        internal long ClearCurrent()
        {
            string sceneId;
            if (!NeedScene("ui.clear", out sceneId)) return 0L;
            return CleanupScene(sceneId, "ui.clear");
        }

        // ==================== 清理 ====================

        /// <summary>整组销毁该场景元素 (句柄表摘除 + Unity Destroy + canvas 回收) → 清理数。</summary>
        private long CleanupScene(string sceneId, string why)
        {
            var drained = _table.DrainScene(sceneId);
            foreach (var rec in drained)
            {
                try { if (rec.Go is not null) UnityEngine.Object.Destroy(rec.Go); } catch { }
            }
            if (_canvasSceneId == sceneId) CleanupCanvasOnly();
            if (drained.Count > 0)
                PsApi.Log(_logger, $"[scene-ugui] 清理场景 {sceneId} 的 {drained.Count} 个 uGUI 元素 ({why})");
            return drained.Count;
        }

        private void CleanupCanvasOnly()
        {
            try { if (_canvasGo is not null) UnityEngine.Object.Destroy(_canvasGo); } catch { }
            _canvasGo = null; _canvasRoot = null; _canvasSceneId = null; _builder = null;
        }

        /// <summary>保险清理: 不论归组全部摘除 + canvas 销毁 (canvas 重建前/重进防御)。</summary>
        private void CleanupRuntime(string why)
        {
            var drained = _table.DrainAll();
            if (drained.Count > 0)
            {
                foreach (var rec in drained)
                {
                    try { if (rec.Go is not null) UnityEngine.Object.Destroy(rec.Go); } catch { }
                }
                PsApi.Warn(_logger, $"[scene-ugui] 保险清理 {drained.Count} 个残留元素 ({why})");
            }
            CleanupCanvasOnly();
        }

        // ==================== SceneService 生命周期钩子 ====================

        /// <summary>场景进入 (DoEnter): 防御性清残留 (崩溃/F10 后重进保险; 正常为空)。</summary>
        internal void OnSceneEnter(string sceneId)
        {
            try
            {
                if (_table.CountScene(sceneId) > 0 || (_canvasGo is not null && _canvasSceneId != sceneId))
                    CleanupRuntime("enter-stale:" + sceneId);
            }
            catch (Exception e) { PsApi.Warn(_logger, "[scene-ugui] OnSceneEnter 清理异常: " + e.Message); }
        }

        /// <summary>场景撤离 (DoExit): 自动销毁该场景创建的全部元素 — 硬要求 (防泄漏防孤儿)。</summary>
        internal void OnSceneExit(string sceneId)
        {
            try { CleanupScene(sceneId, "scene-leave"); }
            catch (Exception e) { PsApi.Warn(_logger, "[scene-ugui] OnSceneExit 清理异常: " + e.Message); }
        }

        /// <summary>Unity 场景卸载 (SceneService.OnSceneLeft): 对象已随场景销毁, 只摘表清引用。</summary>
        internal void OnSceneLeft()
        {
            _table.DrainAll();
            _canvasGo = null; _canvasRoot = null; _canvasSceneId = null; _builder = null;
        }
    }
}
