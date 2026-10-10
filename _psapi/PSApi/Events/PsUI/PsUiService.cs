using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using PSApi.Events.PsScript;
using UnityEngine;

namespace PSApi.Events.PsUI
{
    /// <summary>
    /// PSUI 运行时 (ui/11 §5): 面板注册表 (解析期) + 惰性实例化 (ui.open 时才建 GameObject)。
    /// 双后端分派: 面板树含 slot/grid_slot → 整面板图元树后端 (PsUiPixelBackend, PixelWindow.Show() 独立开窗,
    /// U4); 否则官方 CustomUIManager (路线 B)。幂等: 重复 open 同 id 只置顶 (BringToFront/ToFront)。
    /// on_click 绑定本包函数: 启动期 ValidateBindings 校验存在性; 点击时经 CallCallable 调用,
    /// 异常隔离 (单个按钮炸不影响窗口/其他回调)。所有 Il2Cpp 委托 pin 在 OpenPanel.Pins 防 GC,
    /// 窗口关闭 (脚本 close 或玩家 X/Esc) 后随 OpenPanel 移除释放。
    /// </summary>
    internal sealed class PsUiService
    {
        private sealed class OpenPanel
        {
            internal CustomUIWindow Window;
            internal string PackId;
            internal readonly List<object> Pins = new List<object>();
            /// <summary>M3: 静态树里带 id 的容器(row/column/grid/scroll) → 其 RectTransform, 供 build_begin 定位。</summary>
            internal readonly Dictionary<string, RectTransform> Containers = new Dictionary<string, RectTransform>(StringComparer.Ordinal);
            /// <summary>v1.16.0: 带 on_submit 的 input 元素 (元素 id, 本包函数名); PollSubmitInputs 回车检测用。</summary>
            internal readonly List<(string ElemId, string Fn)> SubmitInputs = new List<(string, string)>();
        }

        /// <summary>M3 构建期上下文 (on_build 回调执行期间非 null): ui.build_* 的操作目标。</summary>
        private sealed class BuildCtx
        {
            internal CustomUIBuilder B;
            internal OpenPanel Op;
            internal string PackId;
            internal string FullId;      // 构建中面板全名 (FindElem 构建期解析用, v1.2.1)
            internal PsUiPanel Panel;
            internal int Depth;      // build_begin/row/column/scroll 未配对数
            internal int AutoId;     // 匿名动态元素自增 id
        }

        private readonly MelonLogger.Instance _logger;
        private readonly Dictionary<string, PsUiPanel> _panels = new Dictionary<string, PsUiPanel>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _panelPack = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, IPackSource> _packSources = new Dictionary<string, IPackSource>(StringComparer.Ordinal);
        private readonly Dictionary<string, OpenPanel> _open = new Dictionary<string, OpenPanel>(StringComparer.Ordinal);
        /// <summary>U4: 打开的图元面板 (含 slot/grid_slot 的面板整面板走 PsUiPixelBackend, 不进 _open)。</summary>
        private readonly Dictionary<string, PsUiPixelBackend.PixelPanel> _openPixel = new Dictionary<string, PsUiPixelBackend.PixelPanel>(StringComparer.Ordinal);
        /// <summary>v1.4.0: 机器绑定面板 (machines/*.json ui="psui"): key = 机器 GameItem.Pointer。
        /// 窗口 = 机器 contentWindow, 开关由原生双击管理; 此处登记供回调解析/槽位轮询/脚本 API。</summary>
        private readonly Dictionary<IntPtr, PsUiPixelBackend.PixelPanel> _machinePanels = new Dictionary<IntPtr, PsUiPixelBackend.PixelPanel>();
        private readonly PsUiPixelBackend _pixel;
        private readonly Dictionary<string, string> _lastOpenedByPack = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Vector2> _posMem = new Dictionary<string, Vector2>(StringComparer.Ordinal); // M3: 窗口位置记忆(进程内)
        private BuildCtx _build;
        private readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private readonly List<object> _spriteKeepAlive = new List<object>();
        private Dictionary<string, Interpreter> _packItps = new Dictionary<string, Interpreter>(StringComparer.Ordinal);

        internal PsUiService(MelonLogger.Instance logger) { _logger = logger; _pixel = new PsUiPixelBackend(logger, this); }

        internal int PanelCount => _panels.Count;

        /// <summary>测试/诊断用: 查已注册面板。</summary>
        internal bool TryGetPanel(string fullId, out PsUiPanel panel) => _panels.TryGetValue(fullId, out panel);

        // ==================== 扫包 (解析期, 纯托管) ====================

        /// <summary>扫全部包的 ui/*.psui 注册面板 (id = 包id:window短名, 短名缺省=文件名)。解析警告只记日志不致命。返回警告数。
        /// P1 (v1.26.0): 场景文件夹资源 scenes/&lt;id&gt;/ui/*.psui 走同一注册表 (场景 json ui 键直引短名)。</summary>
        internal int ScanPacks(List<PackInfo> packs, List<string> errors)
        {
            int warnCount = 0;
            _panels.Clear();
            _panelPack.Clear();
            _packSources.Clear();
            foreach (var pack in packs)
            {
                if (!pack.Valid) continue;
                _packSources[pack.Id] = pack.Source;
                // 原为 TopDirectoryOnly: ListFiles 是递归的, 过滤掉子目录文件保持行为一致
                var files = pack.Source.HasDir("ui")
                    ? pack.Source.ListFiles("ui", ".psui")
                        .Where(f => !f.Substring("ui/".Length).Contains('/')).ToArray()
                    : Array.Empty<string>();
                // P1 (v1.26.0): 场景文件夹 scenes/<id>/ui/*.psui 同表注册 (递归全收, 面板 id 规则相同)
                var sceneFiles = pack.Source.HasDir("scenes")
                    ? pack.Source.ListFiles("scenes", ".psui")
                    : Array.Empty<string>();
                foreach (var file in files.Concat(sceneFiles))
                {
                    string rel = $"{pack.Id}/{file}";
                    string fallbackId = Path.GetFileNameWithoutExtension(file);
                    var warnings = new List<string>();
                    PsUiPanel panel;
                    try { panel = PsUiParser.Parse(rel, pack.Source.ReadText(file), fallbackId, warnings); }
                    catch (PsCompileError ce)
                    {
                        errors.Add(ce.Message + " (该面板未注册)");
                        PsApi.Warn(_logger, "[psui] compile error: " + ce.Message);
                        continue;
                    }
                    catch (Exception e)
                    {
                        errors.Add($"{rel}: 读取失败: {e.Message}");
                        continue;
                    }
                    foreach (var w in warnings) { PsApi.Warn(_logger, "[psui] " + w); warnCount++; }

                    string fullId = pack.Id + ":" + panel.ShortId;
                    if (_panels.ContainsKey(fullId))
                    {
                        PsApi.Warn(_logger, $"[psui] 面板 id 冲突 '{fullId}' ({rel}), 后者跳过");
                        warnCount++;
                        continue;
                    }
                    _panels[fullId] = panel;
                    _panelPack[fullId] = pack.Id;
                }
            }
            return warnCount;
        }

        /// <summary>Engine 阶段 2b 后调用: 记录包解释器, 并校验全部 on_click 引用 (启动期警告, 不等到点击才炸)。返回警告数。</summary>
        internal int BindAndValidate(List<Interpreter> packInterpreters)
        {
            int warnCount = 0;
            var map = new Dictionary<string, Interpreter>(StringComparer.Ordinal);
            foreach (var itp in packInterpreters)
                if (itp != null) map[itp.PackId] = itp;
            _packItps = map;

            foreach (var kv in _panels)
            {
                string packId = _panelPack[kv.Key];
                if (!_packItps.TryGetValue(packId, out var itp)) continue;   // 包无脚本: 面板可静态展示, on_click 点击时才警告
                foreach (var (fn, line) in kv.Value.OnClickRefs)
                {
                    if (!itp.Global.TryGet(fn, out var v) || v is not PsCallable)
                    {
                        PsApi.Warn(_logger, $"[psui] {kv.Value.File}:{line}: on_click 函数 '{fn}' 在包 '{packId}' 的 events/*.pss 里未定义, 点击时不生效");
                        warnCount++;
                    }
                }
                foreach (var (fn, line) in kv.Value.OnChangeRefs)
                {
                    if (!itp.Global.TryGet(fn, out var v) || v is not PsCallable)
                    {
                        PsApi.Warn(_logger, $"[psui] {kv.Value.File}:{line}: on_change 函数 '{fn}' 在包 '{packId}' 的 events/*.pss 里未定义, 触发时不生效");
                        warnCount++;
                    }
                }
                foreach (var (fn, line) in kv.Value.OnSubmitRefs)
                {
                    if (!itp.Global.TryGet(fn, out var v) || v is not PsCallable)
                    {
                        PsApi.Warn(_logger, $"[psui] {kv.Value.File}:{line}: on_submit 函数 '{fn}' 在包 '{packId}' 的 events/*.pss 里未定义, 回车提交时不生效");
                        warnCount++;
                    }
                }
                foreach (var (fn, line) in kv.Value.OnOpenRefs)
                {
                    if (!itp.Global.TryGet(fn, out var v) || v is not PsCallable)
                    {
                        PsApi.Warn(_logger, $"[psui] {kv.Value.File}:{line}: on_open 函数 '{fn}' 在包 '{packId}' 的 events/*.pss 里未定义, 机器面板开窗时不生效");
                        warnCount++;
                    }
                }
                var buildFn = kv.Value.GetString("on_build");
                if (buildFn != null && (!itp.Global.TryGet(buildFn, out var bv) || bv is not PsCallable))
                {
                    PsApi.Warn(_logger, $"[psui] {kv.Value.File}: on_build 函数 '{buildFn}' 在包 '{packId}' 的 events/*.pss 里未定义, 打开时跳过动态填充");
                    warnCount++;
                }
                if (buildFn != null && kv.Value.HasSlots())
                {
                    PsApi.Warn(_logger, $"[psui] {kv.Value.File}: 面板含 slot/grid_slot 走图元树后端, on_build 构建期填充暂不支持, 已忽略");
                    warnCount++;
                }
            }
            return warnCount;
        }

        // ==================== U2: 动态修改 (set_text/set_progress/set_value/get_value) ====================

        /// <summary>label/button 文本。图元面板: label→TagElement.SetText, button→ButtonElement.Set,
        /// 图标按钮→AdvButtonElement.Set (v1.5.0 平行字典); set 后按 FontScales 重放字号倍率
        /// (v1.5.2 起经 FontBaseSizes 基线, base×倍率幂等, 不叠乘)。</summary>
        internal void SetText(string elemRef, string text, string packId, int line)
        {
            if (TryResolvePixelPanel(elemRef, packId, out var pixFullId, out var pixElemId, out var pixPanel)
                && PixelHasElem(pixPanel, pixElemId)) // v1.13.4: 元素不在图元面板 = 回退 CustomUI(双面板并存)
            {
                pixPanel.FontScales.TryGetValue(pixElemId, out var fs);
                try
                {
                    if (pixPanel.Labels.TryGetValue(pixElemId, out var tag) && !(tag is null))
                    {
                        // v2.0.5: 空文本直清 (原生钳制对空文本双向累加)
                        if (PsUiPixelBackend.TagTextIsEmpty(text)) { PsUiPixelBackend.ClearTagText(tag, _logger, $"{pixFullId}:{pixElemId}"); return; }
                        tag.SetText(text ?? "", 10000, RenderHandler.ColorPalette.White);
                        if (fs > 0f) PsUiPixelBackend.ApplyFontScale(tag.textNode, fs, pixPanel.FontBaseSizes, pixElemId, _logger, $"{pixFullId}:{pixElemId}");
                        PsUiPixelBackend.ResetTagHeight(tag, _logger, $"{pixFullId}:{pixElemId}"); // v2.0.4: 原生高度累加修复
                        return;
                    }
                    if (pixPanel.Buttons.TryGetValue(pixElemId, out var btn) && !(btn is null))
                    {
                        btn.Set(text ?? "", -1, -1);
                        if (fs > 0f) PsUiPixelBackend.ApplyFontScale(btn.textNode, fs, pixPanel.FontBaseSizes, pixElemId, _logger, $"{pixFullId}:{pixElemId}");
                        return;
                    }
                    if (pixPanel.AdvButtons.TryGetValue(pixElemId, out var ab) && !(ab is null))
                    {
                        ab.Set(text ?? "", -1, -1);
                        if (fs > 0f) PsUiPixelBackend.ApplyFontScale(ab.textNode, fs, pixPanel.FontBaseSizes, pixElemId, _logger, $"{pixFullId}:{pixElemId}");
                        return;
                    }
                }
                catch (Exception e) { throw new PsRuntimeError($"ui.set_text '{elemRef}' 失败: {e.Message}", line); }
                throw new PsRuntimeError($"ui.set_text: 图元面板 '{pixFullId}' 里没有元素 '{pixElemId}' (或该元素不支持 set_text; 仅 label/button 支持)", line);
            }
            var el = FindElem(elemRef, packId, line, out _);
            try { el.SetText(text ?? ""); }
            catch (Exception e) { throw new PsRuntimeError($"ui.set_text '{elemRef}' 失败: {e.Message}", line); }
        }

        /// <summary>progress 数值 (0..1) + 可选标签 (label null = 不动标签)。</summary>
        internal void SetProgress(string elemRef, double value, string label, string packId, int line)
        {
            var el = FindElem(elemRef, packId, line, out _);
            float v = (float)(value < 0 ? 0 : value > 1 ? 1 : value);
            try { el.SetProgress(v, string.IsNullOrEmpty(label) ? null : label); }
            catch (Exception e) { throw new PsRuntimeError($"ui.set_progress '{elemRef}' 失败: {e.Message}", line); }
        }

        /// <summary>通用值写 (别名行为: progress=value, label/button=text, toggle/slider/input/dropdown 预留 U3)。
        /// notify=false: 脚本回写不触发 on_change, 防循环。图元面板: label/button=text 别名, 其余不支持。</summary>
        internal void SetValue(string elemRef, object value, string packId, int line)
        {
            if (TryResolvePixelPanel(elemRef, packId, out var pixFullId, out var pixElemId, out var pixPanel)
                && PixelHasElem(pixPanel, pixElemId)) // v1.13.4: 元素不在图元面板 = 回退 CustomUI(双面板并存)
            {
                if (pixPanel.Labels.ContainsKey(pixElemId) || pixPanel.Buttons.ContainsKey(pixElemId) || pixPanel.AdvButtons.ContainsKey(pixElemId))
                { SetText(elemRef, PsValues.Fmt(value), packId, line); return; }
                throw new PsRuntimeError($"ui.set_value: 图元面板 '{pixFullId}' 的元素 '{pixElemId}' 不支持 set_value (label/button 以外请用 ui.get_slot_item 等专用 API)", line);
            }
            var el = FindElem(elemRef, packId, line, out _);
            try
            {
                if (!(el.progressBar is null))
                {
                    double d = value is long l ? l : value is double dd ? dd : 0;
                    el.SetProgress((float)(d < 0 ? 0 : d > 1 ? 1 : d));
                }
                else if (!(el.toggle is null)) el.SetBool(PsValues.Truthy(value), false);
                else if (!(el.slider is null))
                {
                    double d = value is long l2 ? l2 : value is double d2 ? d2
                        : throw new PsRuntimeError($"ui.set_value: slider 的值须为数字, 实为 {PsValues.TypeName(value)}", line);
                    el.SetFloat((float)d, false);
                }
                else if (!(el.input is null)) el.SetString(PsValues.Fmt(value), false);
                else if (!(el.dropdown is null))
                {
                    int idx = value is long li ? (int)li : value is double di ? (int)di
                        : throw new PsRuntimeError($"ui.set_value: dropdown 的值须为整数序号, 实为 {PsValues.TypeName(value)}", line);
                    el.SetIndex(idx, false);
                }
                else el.SetText(PsValues.Fmt(value));   // label/button/兜底
            }
            catch (PsRuntimeError) { throw; }
            catch (Exception e) { throw new PsRuntimeError($"ui.set_value '{elemRef}' 失败: {e.Message}", line); }
        }

        /// <summary>通用值读: progress→0..1 double; toggle→bool; slider→double; input→string; dropdown→序号 long;
        /// label/button→text 字符串; 无值组件 = null。图元面板: label→text, button→不支持, 槽位→请用 ui.get_slot_item。</summary>
        internal object GetValue(string elemRef, string packId, int line)
        {
            if (TryResolvePixelPanel(elemRef, packId, out var pixFullId, out var pixElemId, out var pixPanel)
                && PixelHasElem(pixPanel, pixElemId)) // v1.13.4: 元素不在图元面板 = 回退 CustomUI(双面板并存)
            {
                if (pixPanel.Labels.TryGetValue(pixElemId, out var tag) && !(tag is null))
                {
                    try { return tag.GetText(); }
                    catch (Exception e) { throw new PsRuntimeError($"ui.get_value '{elemRef}' 失败: {e.Message}", line); }
                }
                if (pixPanel.Slots.ContainsKey(pixElemId))
                    throw new PsRuntimeError($"ui.get_value: '{pixElemId}' 是槽位, 请用 ui.get_slot_item / ui.get_slot_items", line);
                throw new PsRuntimeError($"ui.get_value: 图元面板 '{pixFullId}' 的元素 '{pixElemId}' 不支持 get_value", line);
            }
            var el = FindElem(elemRef, packId, line, out _);
            try
            {
                if (!(el.progressBar is null))
                    return !(el.progressBar.fillImage is null) ? (object)(double)el.progressBar.fillImage.fillAmount : 0.0;
                if (!(el.toggle is null)) return el.toggle.isOn;
                if (!(el.slider is null)) return (double)el.slider.value;
                if (!(el.input is null)) return el.input.text;
                if (!(el.dropdown is null)) return (long)el.dropdown.value;
                if (!(el.text is null)) return el.text.text;
                return null;
            }
            catch (Exception e) { throw new PsRuntimeError($"ui.get_value '{elemRef}' 失败: {e.Message}", line); }
        }

        /// <summary>元素定位: elemRef 形如 "[pack:]elem_id" (无前缀补本包); 窗口 = 该包当前面板 (_lastOpenedByPack),
        /// 无当前则该包任一打开面板。包无打开面板/元素不存在 = 带行号友好错误。</summary>
        private CustomUIElement FindElem(string elemRef, string packId, int line, out string windowId)
        {
            string pid = packId;
            string elemId = (elemRef ?? "").Trim();
            int ci = elemId.IndexOf(':');
            if (ci > 0) { pid = elemId.Substring(0, ci); elemId = elemId.Substring(ci + 1); }
            if (elemId.Length == 0)
                throw new PsRuntimeError($"ui.*: 元素 id 不能为空 ('{elemRef}')", line);

            // M3(v1.2.1 修复): on_build 构建期面板尚未 Show/登记为已打开, 直接从构建上下文解析
            // (builder 建窗时 CustomUIWindow 已存在, Spawn 即注册 elements; 不碰 _open, 零半开状态)。
            if (_build != null && _build.Op.PackId == pid && !(_build.Op.Window is null))
            {
                windowId = _build.FullId;
                CustomUIElement bel = null;
                try { bel = _build.Op.Window.Get(elemId); } catch { }
                if (bel is null)
                    throw new PsRuntimeError($"ui.*: 面板 '{_build.FullId}'(构建中) 里没有元素 '{elemId}'", line);
                return bel;
            }

            CustomUIManager m = null;
            try { m = CustomUIManager.Instance; } catch { }
            if (m is null)
                throw new PsRuntimeError("ui.*: CustomUIManager 不可用 (需在对局内)", line);
            PruneDead(m);

            windowId = null;
            if (_lastOpenedByPack.TryGetValue(pid, out var cur) && _open.ContainsKey(cur)) windowId = cur;
            if (windowId == null)
                foreach (var kv in _open)
                    if (_panelPack.TryGetValue(kv.Key, out var pk) && pk == pid) { windowId = kv.Key; break; }
            if (windowId == null)
                throw new PsRuntimeError($"ui.*: 包 '{pid}' 没有打开的面板 (先 ui.open)", line);

            CustomUIElement el = null;
            try { el = m.GetElement(windowId, elemId); } catch { }
            if (el is null)
                throw new PsRuntimeError($"ui.*: 面板 '{windowId}' 里没有元素 '{elemId}'", line);
            return el;
        }

        // ==================== ui.open / ui.close ====================

        /// <summary>打开面板 (惰性实例化)。重复 open 同 id 只置顶。返回 true = 已打开/已置顶。
        /// U4 后端分派: 面板含 slot/grid_slot → 整面板走图元树后端 (PsUiPixelBackend), 不需要 CustomUIManager。</summary>
        internal bool Open(string panelId, string packId, int line)
        {
            string fullId = ResolveId(panelId, packId, line);
            if (!_panels.TryGetValue(fullId, out var panel))
                throw new PsRuntimeError($"ui.open: 未知面板 '{fullId}' (已注册: {(_panels.Count == 0 ? "无" : string.Join(", ", _panels.Keys))})", line);

            if (panel.HasSlots()) return OpenPixel(fullId, panel, packId, line);

            // 注意: CustomUIManager/Sprite 是 UnityEngine.Object 子类, == null 重载会触 Il2Cpp 调用
            // (无头环境直接 TypeInitializationException); 一律用 is null 纯引用判空, Unity 侧"假 null"
            // 包装由后续调用的 try/catch 兜底。
            CustomUIManager m = null;
            try { m = CustomUIManager.Instance; } catch { }
            if (m is null)
                throw new PsRuntimeError("ui.open: CustomUIManager 不可用 (需在对局内打开面板)", line);

            PruneDead(m);
            if (_open.TryGetValue(fullId, out var existing))
            {
                try { existing.Window.BringToFront(); } catch { }
                return true;
            }

            try
            {
                BuildWindow(m, fullId, panel, _panelPack[fullId]);
                return true;
            }
            catch (Exception e)
            {
                throw new PsRuntimeError($"ui.open '{fullId}' 构建失败: {e.Message}", line);
            }
        }

        /// <summary>关闭面板。panelId null/空 = "当前" (本包最近一次打开)。未打开 = no-op 返回 false。
        /// 图元面板: Hide + 槽内物品退回玩家后仓 + on_window_closed。</summary>
        internal bool Close(string panelId, string packId)
        {
            string fullId;
            if (string.IsNullOrWhiteSpace(panelId))
            {
                if (!_lastOpenedByPack.TryGetValue(packId, out fullId)) return false;
            }
            else fullId = ResolveId(panelId, packId, 0);

            if (_openPixel.TryGetValue(fullId, out var pp))
            {
                RememberPos(fullId);
                _pixel.Shutdown(pp);
                FireWindowClosed(fullId, _panelPack.TryGetValue(fullId, out var ppk) ? ppk : packId);
                return true;
            }

            CustomUIManager m = null;
            try { m = CustomUIManager.Instance; } catch { }
            if (!(m is null))
            {
                PruneDead(m);
                try { m.CloseWindow(fullId); } catch { }
            }
            bool was = _open.Remove(fullId);
            if (_lastOpenedByPack.TryGetValue(packId, out var cur) && cur == fullId)
                _lastOpenedByPack.Remove(packId);
            return was;
        }

        // ==================== U4: 图元树后端 (含 slot/grid_slot 的面板) ====================

        private bool OpenPixel(string fullId, PsUiPanel panel, string packId, int line)
        {
            if (_openPixel.TryGetValue(fullId, out var existing))
            {
                try { existing.Window?.ToFront(false); } catch { }
                return true;
            }
            PsUiPixelBackend.PixelPanel pp;
            try { pp = _pixel.Build(fullId, panel, packId); }
            catch (Exception e) { throw new PsRuntimeError($"ui.open '{fullId}' 图元面板构建失败: {e.Message}", line); }
            _openPixel[fullId] = pp;
            _lastOpenedByPack[packId] = fullId;
            // 位置记忆 (与 CustomUI 面板共用 _posMem, 键 = 面板全名)
            if (_posMem.TryGetValue(fullId, out var memPos))
            {
                try { var r = pp.Window?.rectTransform; if (!(r is null)) r.anchoredPosition = memPos; } catch { }
            }
            return true;
        }

        /// <summary>图元面板是否拥有该元素 (v1.13.4: set_*/get_value 路径归属判定 —— 旧逻辑"包有任一
        /// 图元面板开着就走图元路径, 元素查不到直接报错", 同包同时开 CustomUI+图元两面板时 CustomUI
        /// 元素全不可达; 现元素不在图元面板 = 回退 CustomUI 路径)。</summary>
        private static bool PixelHasElem(PsUiPixelBackend.PixelPanel pp, string elemId)
            => pp.Labels.ContainsKey(elemId) || pp.Buttons.ContainsKey(elemId)
            || pp.AdvButtons.ContainsKey(elemId) || pp.Slots.ContainsKey(elemId);

        /// <summary>元素引用 → 打开的图元面板: 本包当前面板 (_lastOpenedByPack) 优先, 否则该包任一图元面板。
        /// 该包无打开图元面板 = false (调用方回退 CustomUI 路径或报错)。</summary>
        private bool TryResolvePixelPanel(string elemRef, string packId,
            out string fullId, out string elemId, out PsUiPixelBackend.PixelPanel pp)
        {
            fullId = null; pp = null;
            string pid = packId;
            elemId = (elemRef ?? "").Trim();
            int ci = elemId.IndexOf(':');
            if (ci > 0) { pid = elemId.Substring(0, ci); elemId = elemId.Substring(ci + 1); }
            if (_lastOpenedByPack.TryGetValue(pid, out var cur) && _openPixel.TryGetValue(cur, out pp))
            {
                fullId = cur;
                return true;
            }
            foreach (var kv in _openPixel)
            {
                if (_panelPack.TryGetValue(kv.Key, out var pk) && pk == pid)
                {
                    fullId = kv.Key; pp = kv.Value;
                    return true;
                }
            }
            return false;
        }

        /// <summary>ui.get_slot_item(elem_id): 槽内首个物品句柄; 槽空 = null (不抛错)。
        /// 面板/槽位不存在 = 带行号友好错误。grid_slot 多物品请用 ui.get_slot_items。</summary>
        internal object GetSlotItem(string elemRef, string packId, int line)
        {
            if (!TryResolvePixelPanel(elemRef, packId, out var fullId, out var elemId, out var pp))
                throw new PsRuntimeError($"ui.get_slot_item: 没有打开的图元面板 (slot/grid_slot 面板才可用; 元素引用 '{elemRef}')", line);
            if (!pp.Slots.TryGetValue(elemId, out var slot))
                throw new PsRuntimeError($"ui.get_slot_item: 面板 '{fullId}' 里没有槽位 '{elemId}' (已有: {(pp.Slots.Count == 0 ? "无" : string.Join(", ", pp.Slots.Keys))})", line);
            List<GameItem> items = null;
            try { items = PSApi.Items.ItemsFacade.SlotItems(slot); }
            catch (Exception e) { throw new PsRuntimeError($"ui.get_slot_item '{elemRef}' 失败: {e.Message}", line); }
            return items != null && items.Count > 0 && items[0] != null ? new PsItemHandle(items[0]) : null;
        }

        /// <summary>ui.get_slot_items(elem_id): 槽内全部物品句柄列表 (空槽 = 空表)。</summary>
        internal List<object> GetSlotItems(string elemRef, string packId, int line)
        {
            if (!TryResolvePixelPanel(elemRef, packId, out var fullId, out var elemId, out var pp))
                throw new PsRuntimeError($"ui.get_slot_items: 没有打开的图元面板 (slot/grid_slot 面板才可用; 元素引用 '{elemRef}')", line);
            if (!pp.Slots.TryGetValue(elemId, out var slot))
                throw new PsRuntimeError($"ui.get_slot_items: 面板 '{fullId}' 里没有槽位 '{elemId}' (已有: {(pp.Slots.Count == 0 ? "无" : string.Join(", ", pp.Slots.Keys))})", line);
            var result = new List<object>();
            List<GameItem> items = null;
            try { items = PSApi.Items.ItemsFacade.SlotItems(slot); }
            catch (Exception e) { throw new PsRuntimeError($"ui.get_slot_items '{elemRef}' 失败: {e.Message}", line); }
            if (items != null)
                foreach (var item in items)
                    if (item != null) result.Add(new PsItemHandle(item));
            return result;
        }

        // ==================== M1.5: 场景 UI psui 驱动 (SceneService) ====================

        /// <summary>场景窗口构建: 按已注册的 ui/*.psui 面板 (panelRef = 短名, 或 "pack:panel" 全限定)
        /// 构建图元窗口 — 不 Show/Center, 不登记 _openPixel (SceneService 全权接管生命周期/按钮接线,
        /// 不进存档 BFS 索引, 与机器绑定面板的结构恒定铁律无关)。onClickOverride: 按按钮 elem_id 判定
        /// → 非 null 返回值替换该按钮的 psui on_click (调用方双 pin), null = 保留 psui 声明 (pss 全自定义)。
        /// 面板缺失/构建失败 = 抛异常 (调用方告警回退自动网格)。</summary>
        internal PsUiPixelBackend.PixelPanel BuildSceneWindow(string panelRef, string packId, Func<string, Il2CppSystem.Action> onClickOverride)
        {
            string refId = (panelRef ?? "").Trim();
            if (refId.Length == 0) throw new Exception("场景 ui 引用为空");
            string fullId = refId.Contains(":") ? refId : packId + ":" + refId;
            PsUiPanel panel;
            if (!_panels.TryGetValue(fullId, out panel))
                throw new Exception($"场景面板 '{fullId}' 未注册 (packs/{packId}/ui/{refId}.psui 缺失或解析失败)");
            return _pixel.BuildScenePanel(fullId, panel, _panelPack[fullId], onClickOverride);
        }

        // ==================== v1.4.0: 机器绑定面板 (machines/*.json ui="psui") ====================

        /// <summary>ItemsFacade.PsuiMachineAttach hook 实现: 给 ui="psui" 机器装配 PSUI 图元树窗口。
        /// 面板引用 panelRef = 面板短名 (包取自物品 id 前缀) 或 "pack:panel" 全限定。
        /// 永不抛出 (Items 侧失败回退 plain item): 任何异常/面板缺失 = 告警 + false。</summary>
        internal bool AttachMachinePanel(GameItem machine, string itemId, string panelRef)
        {
            try
            {
                if (machine == null || string.IsNullOrWhiteSpace(itemId)) return false;
                string packId = itemId.Contains(":") ? itemId.Substring(0, itemId.IndexOf(':')) : null;
                string refId = (panelRef ?? "").Trim();
                if (refId.Length == 0)
                {
                    PsApi.Warn(_logger, $"[psui:machine] {itemId}: machines/*.json 缺 panel 字段, 无法装配面板");
                    return false;
                }
                string fullId = refId.Contains(":") ? refId : (packId != null ? packId + ":" + refId : refId);
                if (!_panels.TryGetValue(fullId, out var panel))
                {
                    PsApi.Warn(_logger, $"[psui:machine] {itemId}: 面板 '{fullId}' 未注册 (packs/{packId}/ui/{refId}.psui 缺失?)");
                    return false;
                }
                var pp = _pixel.BuildMachine(fullId, panel, _panelPack[fullId], machine);
                _machinePanels[machine.Pointer] = pp;
                return true;
            }
            catch (Exception e)
            {
                PsApi.Warn(_logger, $"[psui:machine] {itemId}: 面板装配失败: {e.Message}");
                return false;
            }
        }

        /// <summary>机器句柄 → 面板记录; 未登记 (非 psui 机器/装配失败) = 带行号友好错误。</summary>
        private PsUiPixelBackend.PixelPanel NeedMachinePanel(GameItem machine, string api, int line)
        {
            if (machine == null)
                throw new PsRuntimeError($"{api}: 机器句柄已失效 (机器对象不存在)", line);
            IntPtr ptr;
            try { ptr = machine.Pointer; } catch { throw new PsRuntimeError($"{api}: 机器句柄已失效 (指针不可读)", line); }
            if (!_machinePanels.TryGetValue(ptr, out var pp))
                throw new PsRuntimeError($"{api}: 该机器没有绑定的 PSUI 面板 (machines/*.json 需 ui: \"psui\" + panel 字段)", line);
            return pp;
        }

        /// <summary>ui.machine_set_text(machine, elem_id, text [, color]): 机器面板 label/rich_label/button 文本
        /// (v1.5.0: 图标按钮 AdvButtons 双查 + FontScales 字号重放; v1.5.2: 重放经 FontBaseSizes 基线幂等;
        ///  v1.38.0: 可选第 4 参 color "#RRGGBB"/"#RRGGBBAA" — SetText 只吃 ColorPalette 枚举, 故 SetText
        ///  之后直设下游 TMP .color (PsUiPixelBackend.ApplyTextColor); **省略 color = 回调色板白**
        ///  (GetColorFromPalette(White), 与 SetText(White) 同色值而非 Color.white) — 语义简单可预期;
        ///  rich_label 自此可被寻址 (RichLabels 注册修复, 与 label 同权)。</summary>
        internal void MachineSetText(GameItem machine, string elemId, string text, int line, (float r, float g, float b, float a)? color = null)
        {
            var pp = NeedMachinePanel(machine, "ui.machine_set_text", line);
            pp.FontScales.TryGetValue(elemId, out var fs);
            // 省略 color = 回调色板白 (取游戏调色板实际色值; 取不到兜底纯白)
            float wr = 1f, wg = 1f, wb = 1f, wa = 1f;
            if (!color.HasValue)
            {
                try
                {
                    var c32 = RenderHandler.GetColorFromPalette(RenderHandler.ColorPalette.White);
                    wr = c32.r / 255f; wg = c32.g / 255f; wb = c32.b / 255f; wa = c32.a / 255f;
                }
                catch { }
            }
            float cr = color.HasValue ? color.Value.r : wr, cg = color.HasValue ? color.Value.g : wg;
            float cb = color.HasValue ? color.Value.b : wb, ca = color.HasValue ? color.Value.a : wa;
            try
            {
                if (pp.Labels.TryGetValue(elemId, out var tag) && !(tag is null))
                {
                    // v2.0.5: 空文本直清 (原生钳制对空文本双向累加, 见 PsUiPixelBackend.ClearTagText)
                    if (PsUiPixelBackend.TagTextIsEmpty(text)) { PsUiPixelBackend.ClearTagText(tag, _logger, $"{pp.FullId}:{elemId}"); return; }
                    tag.SetText(text ?? "", 10000, RenderHandler.ColorPalette.White);
                    if (fs > 0f) PsUiPixelBackend.ApplyFontScale(tag.textNode, fs, pp.FontBaseSizes, elemId, _logger, $"{pp.FullId}:{elemId}");
                    PsUiPixelBackend.ResetTagHeight(tag, _logger, $"{pp.FullId}:{elemId}"); // v2.0.4: 原生高度累加修复
                    PsUiPixelBackend.ApplyTextColor(tag.textNode, cr, cg, cb, ca, _logger, $"{pp.FullId}:{elemId}");
                    return;
                }
                if (pp.RichLabels.TryGetValue(elemId, out var rt) && !(rt is null))
                {
                    if (PsUiPixelBackend.TagTextIsEmpty(text)) { PsUiPixelBackend.ClearRichText(rt, _logger, $"{pp.FullId}:{elemId}"); return; } // v2.0.5
                    rt.SetText(text ?? "", 10000, RenderHandler.ColorPalette.White);
                    if (fs > 0f) PsUiPixelBackend.ApplyFontScale(rt.handler, fs, pp.FontBaseSizes, elemId, _logger, $"{pp.FullId}:{elemId}");
                    PsUiPixelBackend.ResetRichHeight(rt, _logger, $"{pp.FullId}:{elemId}"); // v2.0.4: 原生高度累加修复
                    PsUiPixelBackend.ApplyTextColor(rt.handler, cr, cg, cb, ca, _logger, $"{pp.FullId}:{elemId}");
                    return;
                }
                if (pp.Buttons.TryGetValue(elemId, out var btn) && !(btn is null))
                {
                    btn.Set(text ?? "", -1, -1);
                    if (fs > 0f) PsUiPixelBackend.ApplyFontScale(btn.textNode, fs, pp.FontBaseSizes, elemId, _logger, $"{pp.FullId}:{elemId}");
                    PsUiPixelBackend.ApplyTextColor(btn.textNode, cr, cg, cb, ca, _logger, $"{pp.FullId}:{elemId}");
                    return;
                }
                if (pp.AdvButtons.TryGetValue(elemId, out var ab) && !(ab is null))
                {
                    ab.Set(text ?? "", -1, -1);
                    if (fs > 0f) PsUiPixelBackend.ApplyFontScale(ab.textNode, fs, pp.FontBaseSizes, elemId, _logger, $"{pp.FullId}:{elemId}");
                    PsUiPixelBackend.ApplyTextColor(ab.textNode, cr, cg, cb, ca, _logger, $"{pp.FullId}:{elemId}");
                    return;
                }
            }
            catch (Exception e) { throw new PsRuntimeError($"ui.machine_set_text '{elemId}' 失败: {e.Message}", line); }
            throw new PsRuntimeError($"ui.machine_set_text: 机器面板 '{pp.FullId}' 里没有元素 '{elemId}' (仅 label/rich_label/button 支持)", line);
        }

        /// <summary>v1.25.0 (M3 战斗): 非抛出机器槽位读取 — 场景能力服务读旅行物品箱装备槽
        /// (s_main/s_melee/s_throw*/s_chest) 用。机器未登记/槽位不存在/槽空/异常 = null
        /// (调用方走武器链/瀑布回退并自记日志, 此处不告警)。</summary>
        internal GameItem TryMachineSlotItem(GameItem machine, string slotId)
        {
            try
            {
                if (machine == null || string.IsNullOrEmpty(slotId)) return null;
                if (!_machinePanels.TryGetValue(machine.Pointer, out var pp)) return null;
                if (!pp.Slots.TryGetValue(slotId, out var slot) || slot == null) return null;
                var items = PSApi.Items.ItemsFacade.SlotItems(slot);
                return items != null && items.Count > 0 ? items[0] : null;
            }
            catch { return null; }
        }

        /// <summary>v2.0.3: 非抛出机器槽位库存读取 — 场景能力服务救回外出栏背包
        /// (grid.rescue_backpack 需对槽本体 UncheckedAccept) 用。机器未登记/槽位不存在/异常 = null
        /// (调用方自记日志, 此处不告警)。</summary>
        internal GameInventory TryMachineSlotInventory(GameItem machine, string slotId)
        {
            try
            {
                if (machine == null || string.IsNullOrEmpty(slotId)) return null;
                if (!_machinePanels.TryGetValue(machine.Pointer, out var pp)) return null;
                return pp.Slots.TryGetValue(slotId, out var slot) ? slot : null;
            }
            catch { return null; }
        }

        /// <summary>ui.machine_slot_item(machine, slot_id): 槽内首个物品句柄; 槽空 = null。</summary>
        internal object MachineSlotItem(GameItem machine, string slotId, int line)
        {
            var pp = NeedMachinePanel(machine, "ui.machine_slot_item", line);
            if (!pp.Slots.TryGetValue(slotId, out var slot))
                throw new PsRuntimeError($"ui.machine_slot_item: 机器面板 '{pp.FullId}' 里没有槽位 '{slotId}' (已有: {(pp.Slots.Count == 0 ? "无" : string.Join(", ", pp.Slots.Keys))})", line);
            List<GameItem> items = null;
            try { items = PSApi.Items.ItemsFacade.SlotItems(slot); }
            catch (Exception e) { throw new PsRuntimeError($"ui.machine_slot_item '{slotId}' 失败: {e.Message}", line); }
            return items != null && items.Count > 0 && items[0] != null ? new PsItemHandle(items[0]) : null;
        }

        /// <summary>ui.machine_slot_items(machine, slot_id): 槽内全部物品句柄列表 (空槽 = 空表)。
        /// v1.6.0: grid_slot 多物品 (融化区 2x6 等) 遍历用, 同 ui.get_slot_items 语义。</summary>
        internal List<object> MachineSlotItems(GameItem machine, string slotId, int line)
        {
            var pp = NeedMachinePanel(machine, "ui.machine_slot_items", line);
            if (!pp.Slots.TryGetValue(slotId, out var slot))
                throw new PsRuntimeError($"ui.machine_slot_items: 机器面板 '{pp.FullId}' 里没有槽位 '{slotId}' (已有: {(pp.Slots.Count == 0 ? "无" : string.Join(", ", pp.Slots.Keys))})", line);
            var result = new List<object>();
            List<GameItem> items = null;
            try { items = PSApi.Items.ItemsFacade.SlotItems(slot); }
            catch (Exception e) { throw new PsRuntimeError($"ui.machine_slot_items '{slotId}' 失败: {e.Message}", line); }
            if (items != null)
                foreach (var item in items)
                    if (item != null) result.Add(new PsItemHandle(item));
            return result;
        }
        /// (覆盖旧注册; 切武器逐槽换白名单用)。id 自动归一化 (剥 game: 前缀)。</summary>
        internal void MachineSetWhitelist(GameItem machine, string slotId, List<object> ids, int line)
        {
            var pp = NeedMachinePanel(machine, "ui.machine_set_whitelist", line);
            if (!pp.Slots.TryGetValue(slotId, out var slot))
                throw new PsRuntimeError($"ui.machine_set_whitelist: 机器面板 '{pp.FullId}' 里没有槽位 '{slotId}'", line);
            var arr = new List<string>();
            if (ids != null)
                foreach (var o in ids) { var s = PsValues.Fmt(o); if (!string.IsNullOrWhiteSpace(s)) arr.Add(s); }
            if (arr.Count == 0)
                throw new PsRuntimeError($"ui.machine_set_whitelist: 白名单不能为空 (要禁用槽位请用 ui.machine_lock)", line);
            try
            {
                if (!PSApi.Items.ItemsFacade.RegisterSlotWhitelist(slot, arr.ToArray(), true))
                    throw new PsRuntimeError($"ui.machine_set_whitelist '{slotId}' 注册失败", line);
            }
            catch (PsRuntimeError) { throw; }
            catch (Exception e) { throw new PsRuntimeError($"ui.machine_set_whitelist '{slotId}' 失败: {e.Message}", line); }
        }

        /// <summary>ui.machine_spawn(machine, slot_id, item_id[, count=1]): 建全新物品直入槽位
        /// (输出槽武器预览用; 不过白名单, 与机器隔夜产出同路径)。返回物品句柄; 失败 = null。</summary>
        internal object MachineSpawn(GameItem machine, string slotId, string itemId, int count, int line)
        {
            var pp = NeedMachinePanel(machine, "ui.machine_spawn", line);
            if (!pp.Slots.TryGetValue(slotId, out var slot))
                throw new PsRuntimeError($"ui.machine_spawn: 机器面板 '{pp.FullId}' 里没有槽位 '{slotId}'", line);
            try
            {
                var item = PSApi.Items.ItemsFacade.CreateIntoSlot(slot, itemId, count);
                return item == null ? null : (object)new PsItemHandle(item);
            }
            catch (Exception e) { throw new PsRuntimeError($"ui.machine_spawn '{slotId}' 失败: {e.Message}", line); }
        }

        /// <summary>ui.machine_lock(machine, slot_id, locked): 槽位双锁 — 插入锁 (SlotFilter 三 patch 拒放入)
        /// + 取出锁 (原生 LockInv 拒取出)。组装中锁部件/输出槽预览"取不下来"/成品未取走拒新部件用。</summary>
        internal void MachineLock(GameItem machine, string slotId, bool locked, int line)
        {
            var pp = NeedMachinePanel(machine, "ui.machine_lock", line);
            if (!pp.Slots.TryGetValue(slotId, out var slot))
                throw new PsRuntimeError($"ui.machine_lock: 机器面板 '{pp.FullId}' 里没有槽位 '{slotId}'", line);
            try
            {
                PSApi.Items.ItemsFacade.SetSlotInsertLock(slot, locked);
                PSApi.Items.ItemsFacade.SetSlotRemovalLock(slot, locked);
            }
            catch (Exception e) { throw new PsRuntimeError($"ui.machine_lock '{slotId}' 失败: {e.Message}", line); }
        }

        /// <summary>ui.machine_set_shape(machine, slot_id, shape): grid_slot 逐格禁放 — 原生
        /// GameGridInventory.SetShape (行优先格态串, 0=可放 1=永久洞; v1.41.0 起 2=锁定格暗色渲染,
        /// 调原生前 ToNative 映射 2→1) + Window.Validate() 重布局 + 锁定格 overlay 重建。
        /// 放置判定三条路径 (CheckShapePlacement/TryInventorySlot/CheckInventorySlotStillValid) 逐格
        /// 现读 inventoryShape, 改后即时生效不用重建; 已在格内的物品不自动弹 (1→0 解锁方向无此问题)。
        /// 错误语义照 machine_lock 惯例: 机器无面板/槽位不存在/槽非网格 = 带行号抛错。</summary>
        internal void MachineSetShape(GameItem machine, string slotId, string shape, int line)
        {
            var pp = NeedMachinePanel(machine, "ui.machine_set_shape", line);
            if (!pp.Slots.TryGetValue(slotId, out var slot))
                throw new PsRuntimeError($"ui.machine_set_shape: 机器面板 '{pp.FullId}' 里没有槽位 '{slotId}' (已有: {(pp.Slots.Count == 0 ? "无" : string.Join(", ", pp.Slots.Keys))})", line);
            var ginv = slot.TryCast<GameGridInventory>();
            if (ginv == null)
                throw new PsRuntimeError($"ui.machine_set_shape: 槽位 '{slotId}' 不是 grid_slot (shape 只对 GameGridInventory 生效)", line);
            try
            {
                int gw = ginv.inventoryShape.width, gh = ginv.inventoryShape.height;
                if (shape.Length != gw * gh)
                    throw new PsRuntimeError($"ui.machine_set_shape 的 shape 长度 {shape.Length} ≠ 槽位 {gw}x{gh}={gw * gh} (行优先, 每格一个字符)", line);
                ginv.SetShape(ShapeUtil.ToNative(shape), gw);
                PsUiPixelBackend.RebuildLockOverlay(ginv, shape, gw, gh, _logger, $"{pp.FullId}:{slotId}");
                if (!(pp.Window is null)) pp.Window.Validate();
            }
            catch (PsRuntimeError) { throw; }
            catch (Exception e) { throw new PsRuntimeError($"ui.machine_set_shape '{slotId}' 失败: {e.Message}", line); }
        }

        /// <summary>ui.machine_clear(machine, slot_id): 清空槽位 — 槽内物品全部取出销毁
        /// (换武器清输出槽预览用; 玩家部件请让玩家自行取回, 别用这个)。</summary>
        internal void MachineClear(GameItem machine, string slotId, int line)
        {
            var pp = NeedMachinePanel(machine, "ui.machine_clear", line);
            if (!pp.Slots.TryGetValue(slotId, out var slot))
                throw new PsRuntimeError($"ui.machine_clear: 机器面板 '{pp.FullId}' 里没有槽位 '{slotId}'", line);
            List<GameItem> items = null;
            try { items = PSApi.Items.ItemsFacade.SlotItems(slot); } catch { }
            if (items == null) return;
            foreach (var item in items)
            {
                if (item == null) continue;
                try { PSApi.Items.ItemsFacade.ConsumeItem(item); } catch { }
            }
        }

        /// <summary>ui.machine_is_open(machine): 机器面板窗口当前是否开着 (原生双击开/关)。</summary>
        internal bool MachineIsOpen(GameItem machine, int line)
        {
            var pp = NeedMachinePanel(machine, "ui.machine_is_open", line);
            try { return !(pp.Window is null) && pp.Window.IsVisible(); }
            catch { return false; }
        }

        /// <summary>v2.0.5 诊断: ui.machine_dump_layout(machine [, tag="psui"]) — 输出面板窗口子元素数
        /// + 全部 label/rich_label 的 widthPixels×heightPixels 一行日志 («[tag] refresh: children=N labels=[id=WxH ...]»)。
        /// 诊断专用: 无头/句柄失效/无面板 = 打一行 «无面板» 并返回 null, 永不抛错。
        /// 用途: 区分「元素数在涨 (孤儿累积)」还是「单元素尺寸在涨 (钳制累加)」——v0.49.1 预览黑块排查。</summary>
        internal object MachineDumpLayout(GameItem machine, string logTag)
        {
            try
            {
                IntPtr ptr = machine is null ? IntPtr.Zero : machine.Pointer;
                if (ptr == IntPtr.Zero || !_machinePanels.TryGetValue(ptr, out var pp) || pp is null)
                {
                    PsApi.Log(_logger, $"[{logTag}] refresh: 无面板 (无头或句柄失效)");
                    return null;
                }
                int children = -1;
                try
                {
                    var rt = pp.Window is null ? null : pp.Window.handler is null ? null : pp.Window.handler.rectTransform;
                    if (!(rt is null)) children = rt.childCount;
                }
                catch { }
                var sb = new System.Text.StringBuilder();
                foreach (var kv in pp.Labels)
                    try { sb.Append(kv.Key).Append('=').Append(kv.Value._widthPixels_k__BackingField).Append('x').Append(kv.Value._heightPixels_k__BackingField).Append(' '); }
                    catch { sb.Append(kv.Key).Append("=? "); }
                foreach (var kv in pp.RichLabels)
                    try { sb.Append(kv.Key).Append("(rich)=").Append(kv.Value._widthPixels_k__BackingField).Append('x').Append(kv.Value._heightPixels_k__BackingField).Append(' '); }
                    catch { sb.Append(kv.Key).Append("(rich)=? "); }
                PsApi.Log(_logger, $"[{logTag}] refresh: children={children} labels=[{sb.ToString().TrimEnd()}]");
            }
            catch (Exception e) { PsApi.Warn(_logger, $"[{logTag}] dump 失败: {e.Message}"); }
            return null;
        }

        /// <summary>ui.machine_find_uid(uid): 按 uniqueId (存档稳定) 找已登记的 psui 机器 → 物品句柄; 未找到 = null。
        /// 隔夜事件从 state 恢复机器引用用 (句柄不能进 state, uid 可以)。</summary>
        internal object MachineFindUid(long uid, int line)
        {
            foreach (var kv in _machinePanels)
            {
                var m = kv.Value.Machine;
                if (m == null) continue;
                try { if (m.uniqueId == (int)uid) return new PsItemHandle(m); }
                catch { }
            }
            return null;
        }

        /// <summary>机器面板按钮点击 → 本包函数, 参数 {elem, machine} (哪台机器的哪个按钮)。
        /// 机器引用经 pp 迟取 (点击时实时包句柄, 不提前缓存句柄对象)。</summary>
        internal Il2CppSystem.Action PinMachineClick(List<object> pins, string packId, string fnName, string elemId, PsUiPixelBackend.PixelPanel pp)
        {
            Action managed = () =>
            {
                try { FireMachineClick(pp, packId, fnName, elemId); }
                catch (Exception e) { PsApi.Err(_logger, $"[psui:machine] on_click '{fnName}' (elem '{elemId}') 出错: {e.Message}"); }
            };
            var act = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(managed);
            pins.Add(managed);
            pins.Add(act);
            return act;
        }

        private void FireMachineClick(PsUiPixelBackend.PixelPanel pp, string packId, string fnName, string elemId)
        {
            if (!_packItps.TryGetValue(packId, out var itp))
            {
                PsApi.Warn(_logger, $"[psui:machine] on_click '{fnName}': 包 '{packId}' 无脚本环境");
                return;
            }
            if (!itp.Global.TryGet(fnName, out var v) || v is not PsCallable fn)
            {
                PsApi.Warn(_logger, $"[psui:machine] on_click '{fnName}' (elem '{elemId}'): 函数未定义");
                return;
            }
            var dict = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["elem"] = elemId ?? "",
                ["machine"] = pp.Machine != null ? new PsItemHandle(pp.Machine) : null,
            };
            itp.BeginRun();
            itp.CallCallable(fn, ArgsFor(fn, dict), 0);
        }

        /// <summary>机器面板槽位 on_change: 参数 {elem, value, machine}。</summary>
        internal void FireMachineChange(PsUiPixelBackend.PixelPanel pp, string fnName, string elemId, object value)
        {
            if (!_packItps.TryGetValue(pp.PackId, out var itp))
            {
                PsApi.Warn(_logger, $"[psui:machine] on_change '{fnName}': 包 '{pp.PackId}' 无脚本环境");
                return;
            }
            if (!itp.Global.TryGet(fnName, out var v) || v is not PsCallable fn)
            {
                PsApi.Warn(_logger, $"[psui:machine] on_change '{fnName}' (elem '{elemId}'): 函数未定义");
                return;
            }
            var dict = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["elem"] = elemId ?? "",
                ["value"] = value,
                ["machine"] = pp.Machine != null ? new PsItemHandle(pp.Machine) : null,
            };
            // v1.13.4: 异常隔离(同 FireChange, 差分轮询路径无兜底)
            try
            {
                itp.BeginRun();
                itp.CallCallable(fn, ArgsFor(fn, dict), 0);
            }
            catch (Exception e) { PsApi.Err(_logger, $"[psui:machine] on_change '{fnName}' (elem '{elemId}') 出错: {e.Message}"); }
        }

        /// <summary>机器面板 on_open (window on_open 属性): 每次窗口 false→true 跳变触发, 参数 {machine}。</summary>
        private void FireMachineOpen(PsUiPixelBackend.PixelPanel pp)
        {
            string fnName = pp.Panel?.GetString("on_open");
            if (fnName == null) return;
            if (!_packItps.TryGetValue(pp.PackId, out var itp)) return;
            if (!itp.Global.TryGet(fnName, out var v) || v is not PsCallable fn) return;
            var dict = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["machine"] = pp.Machine != null ? new PsItemHandle(pp.Machine) : null,
            };
            try
            {
                itp.BeginRun();
                itp.CallCallable(fn, ArgsFor(fn, dict), 0);
            }
            catch (Exception e) { PsApi.Err(_logger, $"[psui:machine] on_open '{fnName}' ({pp.FullId}) 出错: {e.Message}"); }
        }

        /// <summary>宿主 OnUpdate 调用: 机器绑定面板轮询 — 失效机器清理 + on_open 可见性跳变 + 槽位 on_change 差分。
        /// 关窗由原生管理 (不退物品, 槽内容随机器存档)。</summary>
        internal void PollMachinePanels()
        {
            if (_machinePanels.Count == 0) return;
            List<IntPtr> dead = null;
            // v1.13.4: 快照迭代 —— on_open/on_change 回调里脚本可调 ui.close/rebuild 改表, 直接枚举会炸帧循环
            foreach (var kv in _machinePanels.ToArray())
            {
                var pp = kv.Value;
                // 活性: 机器被毁/卖店/场景卸载 → 指针包装失效, 读 identifier 探测
                bool alive;
                try { alive = pp.Machine != null && pp.Machine.identifier != null; }
                catch { alive = false; }
                if (!alive) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }

                bool visible;
                try { visible = !(pp.Window is null) && pp.Window.IsVisible(); }
                catch { visible = false; }
                if (visible && !pp.WasVisible)
                {
                    // false→true: 重置槽位签名基准 (关窗期间的变化不开窗补报) + on_open 刷新回调
                    foreach (var skv in pp.Slots)
                    {
                        try { pp.SlotSigs[skv.Key] = PsUiPixelBackend.SlotSignature(skv.Value); } catch { }
                    }
                    FireMachineOpen(pp);
                }
                pp.WasVisible = visible;
                if (visible) _pixel.PollSlots(pp);
                else _pixel.HideMirrors(pp);   // v1.19.3: 机器窗关闭时镜像窗跟着藏 (PollSlots 不跑)
            }
            if (dead != null)
                foreach (var ptr in dead)
                {
                    if (_machinePanels.TryGetValue(ptr, out var pp)) pp.Pins.Clear();
                    _machinePanels.Remove(ptr);
                }
        }

        /// <summary>宿主 OnUpdate 调用: 图元面板关窗检测 (玩家 X/Esc) + 槽位 on_change 逐帧差分 + 位置记忆。
        /// 零委托红线: on_change 不挂原生槽位回调, 全靠签名比对。</summary>
        internal void PollPixelPanels()
        {
            if (_openPixel.Count == 0) return;
            var dead = _pixel.Poll(_openPixel);
            if (dead != null)
            {
                foreach (var id in dead)
                {
                    if (!_openPixel.TryGetValue(id, out var pp)) continue;
                    RememberPos(id);
                    _pixel.Shutdown(pp);
                    FireWindowClosed(id, _panelPack.TryGetValue(id, out var pk) ? pk : "");
                }
            }
            foreach (var kv in _openPixel)
            {
                try
                {
                    var r = kv.Value.Window?.rectTransform;
                    if (!(r is null)) _posMem[kv.Key] = r.anchoredPosition;
                }
                catch { }
            }
        }

        private string ResolveId(string panelId, string packId, int line)
        {
            if (string.IsNullOrWhiteSpace(panelId))
                throw new PsRuntimeError("ui.open: 面板 id 不能为空", line);
            string id = panelId.Trim();
            return id.Contains(":") ? id : packId + ":" + id;
        }

        /// <summary>清理已被玩家 X/Esc 关闭的残留窗口记录。</summary>
        private void PruneDead(CustomUIManager m)
        {
            List<string> dead = null;
            foreach (var kv in _open)
            {
                bool alive;
                try { alive = !(kv.Value.Window is null) && m.IsOpen(kv.Key); }
                catch { alive = false; }
                if (!alive) (dead ??= new List<string>()).Add(kv.Key);
            }
            if (dead != null) foreach (var id in dead) _open.Remove(id);
        }

        // ==================== 实例化 ====================

        private void BuildWindow(CustomUIManager m, string fullId, PsUiPanel panel, string packId)
        {
            var op = new OpenPanel { PackId = packId };
            var b = m.CreateWindow(fullId, panel.GetString("title", fullId), panel.GetString("layer", "overlay") ?? "overlay");
            // v1.2.1: CustomUIWindow 在 CreateWindow 时已存在(Show 仅收尾), 提前挂上 —
            // on_build 里 ui.set_text 等经 FindElem 构建期分支直接解析本窗口的元素
            try { op.Window = b.window; } catch { }
            var size = GetPanelSize(panel);
            if (size.HasValue) b.SetSize(size.Value.W, size.Value.H);
            b.SetDraggable(panel.GetBool("draggable", true));
            b.SetCloseOnEscape(panel.GetBool("close_on_escape", true));
            bool positioned = false;
            string pos = panel.GetString("position");
            if (pos != null)
            {
                var parts = pos.Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0].Trim(), out float px) && float.TryParse(parts[1].Trim(), out float py))
                {
                    b.SetPosition(new Vector2(px, py));
                    positioned = true;
                }
                else PsApi.Warn(_logger, $"[psui] {panel.File}: position '{pos}' 须为 \"x,y\", 忽略");
            }
            // M3: 位置记忆 — 无显式 position 且有拖动记忆时恢复(NpcManager _savedPos 平移)
            if (!positioned && _posMem.TryGetValue(fullId, out var memPos))
            {
                try { b.SetPosition(memPos); positioned = true; } catch { }
            }
            if (!positioned) b.Center();

            foreach (var child in panel.Children) BuildElement(b, child, packId, op, panel);
            RunOnBuild(b, op, panel, packId, fullId);   // M3: 静态骨架建完后 on_build 命令式填充动态区
            op.Window = b.Show();
            HookOnClose(op, fullId, packId);
            _open[fullId] = op;
            _lastOpenedByPack[packId] = fullId;
            PsApi.Log(_logger, $"[psui] opened: {fullId} ({panel.Children.Count} root element(s))");
        }

        private static (float W, float H)? GetPanelSize(PsUiPanel panel)
        {
            if (!panel.Props.TryGetValue("size", out var v)) return null;
            string s = v as string ?? v.ToString();
            var parts = s.ToLowerInvariant().Split('x');
            if (parts.Length != 2) return null;
            if (!float.TryParse(parts[0], out float w) || !float.TryParse(parts[1], out float h) || w <= 0 || h <= 0) return null;
            return (w, h);
        }

        private void BuildElement(CustomUIBuilder b, PsUiElem elem, string packId, OpenPanel op, PsUiPanel panel)
        {
            if (!PsUiParser.IsInstantiable(elem.Type))
            {
                PsApi.Warn(_logger, $"[psui] {panel.File}:{elem.Line}: 元素 '{elem.Type}' 属后续批次(U3+), 本次跳过(含子树)");
                return;
            }
            switch (elem.Type)
            {
                case "label":
                    b.AddLabel(elem.GetString("text", ""), elem.Id);
                    break;
                case "button":
                {
                    string fn = elem.GetString("on_click");
                    Il2CppSystem.Action act = null;
                    if (fn != null) act = PinClick(op.Pins, packId, fn, panel.File, elem.Line);
                    var after = b.AddButton(elem.GetString("text", elem.Id ?? "button"), act, elem.Id);
                    string tip = elem.GetString("tooltip");
                    if (tip != null) after.WithTooltip(tip);
                    break;
                }
                case "progress":
                {
                    string lbl = elem.GetString("label");
                    if (lbl != null) b.AddLabel(lbl);
                    b.AddProgressBar(Mathf.Clamp01((float)elem.GetNumber("value", 0)), elem.Id);
                    break;
                }
                case "toggle":
                {
                    string fn = elem.GetString("on_change");
                    Il2CppSystem.Action<bool> act = null;
                    if (fn != null) act = PinChange<bool>(op, packId, fn, elem, v => v);
                    b.AddToggle(elem.GetString("label", elem.Id ?? "toggle"), elem.GetBool("checked", false), act, elem.Id);
                    break;
                }
                case "slider":
                {
                    string lbl = elem.GetString("label");
                    if (lbl != null) b.AddLabel(lbl);
                    string fn = elem.GetString("on_change");
                    Il2CppSystem.Action<float> act = null;
                    if (fn != null) act = PinChange<float>(op, packId, fn, elem, v => (double)v);
                    b.AddSlider((float)elem.GetNumber("min", 0), (float)elem.GetNumber("max", 100),
                        (float)elem.GetNumber("value", 0), act, elem.GetNumber("step", 0) >= 1, elem.Id);
                    break;
                }
                case "input":
                {
                    string lbl = elem.GetString("label");
                    if (lbl != null) b.AddLabel(lbl);
                    string fn = elem.GetString("on_change");
                    Il2CppSystem.Action<string> act = null;
                    if (fn != null) act = PinChange<string>(op, packId, fn, elem, v => v ?? "");
                    b.AddInput(elem.GetString("placeholder", ""), elem.GetString("text", ""), act, elem.Id);
                    // v1.16.0: on_submit = 回车提交 (PollSubmitInputs 轮询 Return/KeypadEnter); 无 id 无法定位清空, 警告跳过
                    string submitFn = elem.GetString("on_submit");
                    if (submitFn != null)
                    {
                        if (elem.Id == null)
                            PsApi.Warn(_logger, $"[psui] {panel.File}:{elem.Line}: input 的 on_submit 需要元素 id (定位/清空用), 已跳过");
                        else
                            op.SubmitInputs.Add((elem.Id, submitFn));
                    }
                    break;
                }
                case "dropdown":
                {
                    string lbl = elem.GetString("label");
                    if (lbl != null) b.AddLabel(lbl);
                    string fn = elem.GetString("on_change");
                    Il2CppSystem.Action<int> act = null;
                    if (fn != null) act = PinChange<int>(op, packId, fn, elem, v => (long)v);
                    var opts = new Il2CppSystem.Collections.Generic.List<string>();
                    var rawOpts = elem.GetList("options");
                    if (rawOpts != null) foreach (var o in rawOpts) opts.Add(PsValues.Fmt(o));
                    b.AddDropdown(opts.Cast<Il2CppSystem.Collections.Generic.IList<string>>(),
                        (int)elem.GetNumber("index", 0), act, elem.Id);
                    break;
                }
                case "image":
                {
                    var sprite = ResolveSprite(elem.GetString("src"), packId, panel, elem.Line);
                    if (sprite is null) break;
                    var sz = elem.GetSize("size");
                    b.AddImage(sprite, sz?.W ?? -1f, sz?.H ?? -1f, elem.Id);
                    break;
                }
                case "row":
                case "column":
                {
                    if (elem.Type == "row") b.BeginRow((float)elem.GetNumber("spacing", 4));
                    else b.BeginColumn((float)elem.GetNumber("spacing", 4));
                    CaptureContainer(b, op, elem);
                    foreach (var c in elem.Children) BuildElement(b, c, packId, op, panel);
                    b.End();
                    break;
                }
                case "grid":
                {
                    var cell = elem.GetSize("cell") ?? (64f, 64f);
                    b.BeginGrid((int)elem.GetNumber("columns", 2), cell.W, cell.H, (float)elem.GetNumber("spacing", 4));
                    CaptureContainer(b, op, elem);
                    foreach (var c in elem.Children) BuildElement(b, c, packId, op, panel);
                    b.End();
                    break;
                }
                case "scroll":
                {
                    b.BeginScroll((float)elem.GetNumber("height", 200));
                    CaptureContainer(b, op, elem);
                    foreach (var c in elem.Children) BuildElement(b, c, packId, op, panel);
                    b.End();
                    break;
                }
            }
        }

        /// <summary>M3: 带 id 的容器登记进 OpenPanel.Containers, 供 on_build 里 ui.build_begin(id) 定位。</summary>
        private void CaptureContainer(CustomUIBuilder b, OpenPanel op, PsUiElem elem)
        {
            if (elem.Id == null) return;
            try
            {
                var t = b.CurrentParent;
                if (!(t is null)) op.Containers[elem.Id] = t;
            }
            catch (Exception e) { PsApi.Warn(_logger, $"[psui] 容器 '{elem.Id}' 捕获失败: {e.Message}"); }
        }

        // ==================== M3: on_build 构建期动态填充 (ui.build_*) ====================

        /// <summary>静态骨架建完后调 on_build(Show 之前, builder 仍可用)。函数未定义已在 BindAndValidate 警告, 此处静默跳过。
        /// v1.2.1: on_build 期间 _build 非空, FindElem 走构建期分支直解本窗口元素; 脚本抛错仅记日志,
        /// finally 保证 _build 清空 + 未配对容器闭合, 不留半开状态(面板不进 _open, Show 照常)。</summary>
        private void RunOnBuild(CustomUIBuilder b, OpenPanel op, PsUiPanel panel, string packId, string fullId)
        {
            string fnName = panel.GetString("on_build");
            if (fnName == null) return;
            if (!_packItps.TryGetValue(packId, out var itp)) return;
            if (!itp.Global.TryGet(fnName, out var v) || v is not PsCallable fn) return;
            var ctx = new BuildCtx { B = b, Op = op, PackId = packId, FullId = fullId, Panel = panel };
            _build = ctx;
            try
            {
                itp.BeginRun();
                itp.CallCallable(fn, new List<object>(), 0);
            }
            catch (Exception e) { PsApi.Err(_logger, $"[psui] on_build '{fnName}' ({panel.File}) 出错: {e.Message}"); }
            finally
            {
                while (ctx.Depth > 0) { try { b.End(); } catch { } ctx.Depth--; } // 未配对 build_end 防御
                _build = null;
            }
        }

        private BuildCtx NeedBuild(string api, int line)
        {
            if (_build == null)
                throw new PsRuntimeError($"{api} 仅可在面板的 on_build 构建期调用(动态内容变化请 ui.rebuild 重建)", line);
            return _build;
        }

        private string DynId(BuildCtx ctx, string id)
        {
            if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
            return "__dyn" + (++ctx.AutoId);
        }

        /// <summary>ui.build_begin(container_id): 推进一个静态树带 id 容器, 后续 build_* 落入其中; 须配 build_end。</summary>
        internal void BuildBegin(string containerId, int line)
        {
            var ctx = NeedBuild("ui.build_begin", line);
            if (string.IsNullOrWhiteSpace(containerId) || !ctx.Op.Containers.TryGetValue(containerId.Trim(), out var t) || t is null)
                throw new PsRuntimeError($"ui.build_begin: 面板 '{ctx.Panel.File}' 没有带 id 的容器 '{containerId}'(row/column/grid/scroll 写 id 才可定位)", line);
            try { ctx.B.parents.Push(t); }
            catch (Exception e) { throw new PsRuntimeError($"ui.build_begin '{containerId}' 失败: {e.Message}", line); }
            ctx.Depth++;
        }

        /// <summary>ui.build_row/column/scroll: 开动态容器; 须配 build_end。</summary>
        internal void BuildContainer(string kind, double a, int line)
        {
            var ctx = NeedBuild("ui.build_" + kind, line);
            try
            {
                switch (kind)
                {
                    case "row": ctx.B.BeginRow((float)a); break;
                    case "column": ctx.B.BeginColumn((float)a); break;
                    case "scroll": ctx.B.BeginScroll((float)a); break;
                }
            }
            catch (Exception e) { throw new PsRuntimeError($"ui.build_{kind} 失败: {e.Message}", line); }
            ctx.Depth++;
        }

        internal void BuildEnd(int line)
        {
            var ctx = NeedBuild("ui.build_end", line);
            if (ctx.Depth <= 0)
                throw new PsRuntimeError("ui.build_end: 没有未闭合的 build_begin/build_row/column/scroll", line);
            try { ctx.B.End(); }
            catch (Exception e) { throw new PsRuntimeError($"ui.build_end 失败: {e.Message}", line); }
            ctx.Depth--;
        }

        /// <summary>ui.build_label(text, {id?, tooltip?}) → 元素 id。</summary>
        internal string BuildLabel(string text, Dictionary<string, object> opts, int line)
        {
            var ctx = NeedBuild("ui.build_label", line);
            string id = DynId(ctx, OptStr(opts, "id"));
            try
            {
                ctx.B.AddLabel(text ?? "", id);
                string tip = OptStr(opts, "tooltip");
                if (tip != null) ctx.B.WithTooltip(tip);
            }
            catch (Exception e) { throw new PsRuntimeError($"ui.build_label 失败: {e.Message}", line); }
            return id;
        }

        /// <summary>ui.build_button(text, fn, {id?, arg?, tooltip?}) → 元素 id。
        /// 点击时 fn 为 1 参函数则收 {elem=元素id, arg=arg}, 0 参则空调用(同 on_change 的 ArgsFor 约定)。</summary>
        internal string BuildButton(string text, string fnName, Dictionary<string, object> opts, int line, string packId)
        {
            var ctx = NeedBuild("ui.build_button", line);
            if (string.IsNullOrWhiteSpace(fnName))
                throw new PsRuntimeError("ui.build_button 的 fn 须为非空字符串(本包函数名)", line);
            string id = DynId(ctx, OptStr(opts, "id"));
            object arg = opts != null && opts.TryGetValue("arg", out var av) ? av : null;
            Action managed = () =>
            {
                try { FireClickArg(packId, fnName, id, arg); }
                catch (Exception e) { PsApi.Err(_logger, $"[psui] build_button '{fnName}' (elem '{id}') 出错: {e.Message}"); }
            };
            try
            {
                var act = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(managed);
                ctx.Op.Pins.Add(managed);
                ctx.Op.Pins.Add(act);
                ctx.B.AddButton(string.IsNullOrEmpty(text) ? id : text, act, id);
                string tip = OptStr(opts, "tooltip");
                if (tip != null) ctx.B.WithTooltip(tip);
            }
            catch (Exception e) { throw new PsRuntimeError($"ui.build_button 失败: {e.Message}", line); }
            return id;
        }

        /// <summary>动态按钮点击: 1 参函数收 {elem, arg} dict, 0 参空调用; 函数未定义 = 警告不炸。</summary>
        internal void FireClickArg(string packId, string fnName, string elemId, object arg)
        {
            if (!_packItps.TryGetValue(packId, out var itp))
            {
                PsApi.Warn(_logger, $"[psui] build_button '{fnName}': 包 '{packId}' 无脚本环境");
                return;
            }
            if (!itp.Global.TryGet(fnName, out var v) || v is not PsCallable fn)
            {
                PsApi.Warn(_logger, $"[psui] build_button '{fnName}' (elem '{elemId}'): 函数未定义");
                return;
            }
            var dict = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["elem"] = elemId ?? "",
                ["arg"] = arg,
            };
            itp.BeginRun();
            itp.CallCallable(fn, ArgsFor(fn, dict), 0);
        }

        private static string OptStr(Dictionary<string, object> opts, string key)
            => opts != null && opts.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;

        // ==================== M3: 重建 / 位置记忆 / 尺寸 / 开关 ====================

        /// <summary>ui.rebuild([panel_id]): 记忆位置 → 关闭 → 按 .psui+on_build 重建。未打开 = no-op 返回 false。</summary>
        internal bool Rebuild(string panelId, string packId, int line)
        {
            string fullId;
            if (string.IsNullOrWhiteSpace(panelId))
            {
                if (!_lastOpenedByPack.TryGetValue(packId, out fullId))
                    return false;
            }
            else fullId = ResolveId(panelId, packId, line);

            CustomUIManager m = null;
            try { m = CustomUIManager.Instance; } catch { }
            if (m is null && !_openPixel.ContainsKey(fullId))
                throw new PsRuntimeError("ui.rebuild: CustomUIManager 不可用 (需在对局内)", line);
            if (!(m is null)) PruneDead(m);
            if (!_open.ContainsKey(fullId) && !_openPixel.ContainsKey(fullId)) return false;
            RememberPos(fullId);
            Close(fullId, packId);
            return Open(fullId, packId, line);
        }

        /// <summary>ui.is_open([panel_id]): 面板当前是否打开。</summary>
        internal bool IsOpen(string panelId, string packId, int line)
        {
            string fullId;
            if (string.IsNullOrWhiteSpace(panelId))
            {
                if (!_lastOpenedByPack.TryGetValue(packId, out fullId)) return false;
            }
            else fullId = ResolveId(panelId, packId, line);
            if (_openPixel.ContainsKey(fullId)) return true;
            CustomUIManager m = null;
            try { m = CustomUIManager.Instance; } catch { }
            if (m is null) return false;
            PruneDead(m);
            return _open.ContainsKey(fullId);
        }

        /// <summary>F6 热键用: 按全名切换面板(开→关, 关→开)。面板未注册 = 警告。</summary>
        internal void TogglePanel(string fullId)
        {
            if (!_panels.TryGetValue(fullId, out _) || !_panelPack.TryGetValue(fullId, out var packId))
            {
                PsApi.Warn(_logger, $"[psui] 热键面板 '{fullId}' 未注册( packs/psapi_manager 缺失? )");
                return;
            }
            if (IsOpen(fullId, packId, 0)) Close(fullId, packId);
            else
            {
                try { Open(fullId, packId, 0); }
                catch (PsRuntimeError re) { PsApi.Warn(_logger, "[psui] " + re.Message); }
            }
        }

        // ==================== v1.16.0: ui.bind_key 按键绑定 + input on_submit 回车提交 ====================

        /// <summary>ui.bind_key 绑定表 (KeyCode → 面板全名; 进程级, 不随包卸载清理)。</summary>
        private readonly List<(KeyCode Key, string FullId)> _keyBinds = new List<(KeyCode, string)>();

        /// <summary>测试/诊断用: 已注册按键绑定数。</summary>
        internal int KeyBindCount => _keyBinds.Count;

        /// <summary>ui.bind_key(key_name, panel_id) 顶层注册: 按键按下 = TogglePanel(开→关/关→开)。
        /// KeyCode 解析照抄管理面板热键的 Enum.TryParse 模式 (大小写不敏感); 键名非法/面板未注册 = 带行号报错。
        /// 重复注册同键同面板幂等; 一键可绑多面板 (按下全部 toggle)。</summary>
        internal void BindKey(string keyName, string panelId, string packId, int line)
        {
            if (string.IsNullOrWhiteSpace(keyName))
                throw new PsRuntimeError("ui.bind_key 的 key_name 须为非空字符串(UnityEngine.KeyCode 名, 如 \"Slash\"/\"F6\")", line);
            if (!Enum.TryParse(keyName.Trim(), true, out KeyCode key))
                throw new PsRuntimeError($"ui.bind_key: 无法识别的按键名 '{keyName}' (须为 UnityEngine.KeyCode 名, 如 Slash/F6/Return)", line);
            string fullId = ResolveId(panelId, packId, line);
            if (!_panels.ContainsKey(fullId))
                throw new PsRuntimeError($"ui.bind_key: 未知面板 '{fullId}' (面板在 ui/*.psui 注册后才可绑定; 顶层调用时全部包已扫完)", line);
            foreach (var kb in _keyBinds)
                if (kb.Key == key && kb.FullId == fullId) return; // 幂等
            _keyBinds.Add((key, fullId));
            PsApi.Log(_logger, $"[psui] bind_key: {key} → {fullId}");
        }

        /// <summary>宿主 OnUpdate 调用: 轮询 bind_key 绑定表, GetKeyDown → TogglePanel。</summary>
        internal void PollKeyBinds()
        {
            if (_keyBinds.Count == 0) return;
            foreach (var (key, fullId) in _keyBinds)
            {
                bool down;
                try { down = Input.GetKeyDown(key); } catch { continue; }
                if (!down) continue;
                try { TogglePanel(fullId); }
                catch (Exception e) { PsApi.Warn(_logger, $"[psui] bind_key {key} → '{fullId}' 切换失败: {e.Message}"); }
            }
        }

        /// <summary>宿主 OnUpdate 调用: 对"开着且含 on_submit input 的 CustomUI 面板"检测回车
        /// (Return/KeypadEnter) → 回调 {elem, value=当前文本} 并清空该 input (SetString("", false) 不触发 on_change)。
        /// 焦点无关: 面板开着即生效 (自定义 UI 无可靠焦点 API, 指令台场景够用); 图元面板不支持。
        /// 异常隔离同 FireChange (回调炸不影响宿主帧循环)。
        /// v1.16.2: _open/SubmitInputs 先快照再枚举 — 回调脚本可 ui.rebuild (关+重开面板改 _open),
        /// 直接 foreach 会在下个 MoveNext 抛 InvalidOperationException (实测: psconsole 每条指令崩一次)。</summary>
        internal void PollSubmitInputs()
        {
            if (_open.Count == 0) return;
            bool enter;
            try { enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter); }
            catch { return; }
            if (!enter) return;
            foreach (var kv in _open.ToArray())
            {
                var op = kv.Value;
                if (op.SubmitInputs.Count == 0) continue;
                foreach (var (elemId, fn) in op.SubmitInputs.ToArray())
                {
                    try
                    {
                        var el = op.Window?.Get(elemId);
                        if (el is null || el.input is null) continue;
                        string text;
                        try { text = el.input.text; } catch { text = ""; }
                        FireChange(op.PackId, fn, elemId, text ?? "");
                        // v1.16.2: 回调内 rebuild 会关掉旧面板换新 OpenPanel — 旧 op 上的元素已随窗口销毁,
                        // 重新校验面板仍开着且还是同一个 op, 元素重新取, 取不到就跳过清空
                        if (!_open.TryGetValue(kv.Key, out var cur) || !ReferenceEquals(cur, op)) continue;
                        op.Window?.Get(elemId)?.SetString("", false);
                    }
                    catch (Exception e) { PsApi.Err(_logger, $"[psui] on_submit '{fn}' (elem '{elemId}') 出错: {e.Message}"); }
                }
            }
        }

        /// <summary>ui.set_pref_size(elem_id, w, h): 显式撑开元素优选尺寸(多行文本防遮挡; w/h 传 -1 = 不指定)。
        /// 对齐 NpcManager ApplyClientLabelHeight。图元面板无优选尺寸概念 = 运行错误。</summary>
        internal void SetPrefSize(string elemRef, double w, double h, string packId, int line)
        {
            if (TryResolvePixelPanel(elemRef, packId, out _, out var psElemId, out var psPanel)
                && PixelHasElem(psPanel, psElemId)) // v1.13.4: 元素不在图元面板 = 回退 CustomUI(双面板并存)
                throw new PsRuntimeError($"ui.set_pref_size: 图元面板 (含 slot/grid_slot) 不支持 set_pref_size", line);
            var el = FindElem(elemRef, packId, line, out _);
            try { el.SetPreferredSize((float)w, (float)h); }
            catch (Exception e) { throw new PsRuntimeError($"ui.set_pref_size '{elemRef}' 失败: {e.Message}", line); }
        }

        /// <summary>位置记忆: 读当前 Rect 存档(重建/关闭前调用, 或由宿主 PollPositions 周期调用)。</summary>
        internal void RememberPos(string fullId)
        {
            if (!_open.TryGetValue(fullId, out var op)) return;
            try
            {
                var r = op.Window?.Rect;
                if (!(r is null)) _posMem[fullId] = r.anchoredPosition;
            }
            catch { }
        }

        /// <summary>宿主 OnUpdate 周期调用: 持续记录全部打开面板的位置(用户拖动后重开/重建恢复,
        /// NpcManager Update 先例; 每帧几次属性读, 开销可忽略)。</summary>
        internal void PollPositions()
        {
            if (_open.Count == 0) return;
            foreach (var kv in _open)
            {
                try
                {
                    var r = kv.Value.Window?.Rect;
                    if (!(r is null)) _posMem[kv.Key] = r.anchoredPosition;
                }
                catch { }
            }
        }

        /// <summary>按钮点击 → 本包全局函数。委托 pin 在传入的 pins 表防 GC (CustomUI 面板 = OpenPanel.Pins,
        /// U4 图元面板 = PixelPanel.Pins; 均随窗口关闭释放)。</summary>
        internal Il2CppSystem.Action PinClick(List<object> pins, string packId, string fnName, string file, int srcLine)
        {
            Action managed = () =>
            {
                try
                {
                    if (!_packItps.TryGetValue(packId, out var itp))
                    {
                        PsApi.Warn(_logger, $"[psui] on_click '{fnName}': 包 '{packId}' 无脚本环境");
                        return;
                    }
                    if (!itp.Global.TryGet(fnName, out var v) || v is not PsCallable fn)
                    {
                        PsApi.Warn(_logger, $"[psui] on_click '{fnName}' ({file}:{srcLine}): 函数未定义");
                        return;
                    }
                    itp.BeginRun();
                    itp.CallCallable(fn, new List<object>(), 0);
                }
                catch (Exception e) { PsApi.Err(_logger, $"[psui] on_click '{fnName}' 出错: {e.Message}"); }
            };
            var act = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(managed);
            pins.Add(managed);
            pins.Add(act);
            return act;
        }

        /// <summary>控件值变化 → 本包全局函数 (on_change)。函数可 0 或 1 参: 1 参收 dict
        /// {elem=元素id, value=新值} (toggle=bool/slider=double/input=string/dropdown=序号 long)。
        /// 委托双 pin 在 OpenPanel.Pins 防 GC (窗口关闭时释放)。</summary>
        private Il2CppSystem.Action<T> PinChange<T>(OpenPanel op, string packId, string fnName, PsUiElem elem, Func<T, object> toValue)
        {
            string elemId = elem.Id ?? "";
            int srcLine = elem.Line;
            Action<T> managed = v =>
            {
                try { FireChange(packId, fnName, elemId, toValue(v)); }
                catch (Exception e) { PsApi.Err(_logger, $"[psui] on_change '{fnName}' (elem '{elemId}' 行{srcLine}) 出错: {e.Message}"); }
            };
            var act = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<T>>(managed);
            op.Pins.Add(managed);
            op.Pins.Add(act);
            return act;
        }

        /// <summary>触发 on_change: 查包函数并调用 (0 参函数传空表, 否则传 1 个 dict)。
        /// 函数未定义 = 警告不炸 (BindAndValidate 启动期已报过一次)。</summary>
        internal void FireChange(string packId, string fnName, string elemId, object value)
        {
            if (!_packItps.TryGetValue(packId, out var itp))
            {
                PsApi.Warn(_logger, $"[psui] on_change '{fnName}': 包 '{packId}' 无脚本环境");
                return;
            }
            if (!itp.Global.TryGet(fnName, out var v) || v is not PsCallable fn)
            {
                PsApi.Warn(_logger, $"[psui] on_change '{fnName}' (elem '{elemId}'): 函数未定义");
                return;
            }
            var arg = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["elem"] = elemId ?? "",
                ["value"] = value,
            };
            // v1.13.4: 异常隔离 —— 差分轮询路径(PollSlots)调进来没有兜底, 脚本抛错会直穿宿主
            // OnUpdate 跳过当帧其余面板轮询; 与按钮点击路径同规: 报错日志, 不外抛
            try
            {
                itp.BeginRun();
                itp.CallCallable(fn, ArgsFor(fn, arg), 0);
            }
            catch (Exception e) { PsApi.Err(_logger, $"[psui] on_change '{fnName}' (elem '{elemId}') 出错: {e.Message}"); }
        }

        /// <summary>ScriptFunc 0 参 = 空表 (直传 dict 会触发"实传多于参数"错), 否则传 1 个实参; 非 ScriptFunc 一律传。</summary>
        private static List<object> ArgsFor(PsCallable fn, object arg)
            => fn is ScriptFunc sf && sf.Params.Count == 0
                ? new List<object>()
                : new List<object> { arg };

        /// <summary>窗口 onClose (任何关闭路径: X/Esc/脚本 close) → 链调原回调 + 本包 on_window_closed(panel_id 全名)。
        /// 该回调是可选约定: 包未定义 = 静默。prev 防御性链调, 避免覆盖游戏自建窗口逻辑。</summary>
        private void HookOnClose(OpenPanel op, string fullId, string packId)
        {
            Il2CppSystem.Action prev = null;
            try { prev = op.Window.onClose; } catch { }
            Action managed = () =>
            {
                try { prev?.Invoke(); } catch { }
                try { FireWindowClosed(fullId, packId); }
                catch (Exception e) { PsApi.Err(_logger, $"[psui] on_window_closed '{fullId}' 出错: {e.Message}"); }
            };
            var act = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(managed);
            op.Pins.Add(managed);
            op.Pins.Add(act);
            try { op.Window.onClose = act; }
            catch (Exception e) { PsApi.Warn(_logger, $"[psui] {fullId}: 挂 onClose 失败: {e.Message}"); }
        }

        /// <summary>窗口关闭收尾: 从 _open/_openPixel/_lastOpenedByPack 移除 + 回调包内 on_window_closed(fullId) (未定义 = 静默)。</summary>
        internal void FireWindowClosed(string fullId, string packId)
        {
            RememberPos(fullId); // M3: 关窗前最后记一次位置
            _open.Remove(fullId);
            _openPixel.Remove(fullId);
            if (_lastOpenedByPack.TryGetValue(packId, out var cur) && cur == fullId)
                _lastOpenedByPack.Remove(packId);
            if (!_packItps.TryGetValue(packId, out var itp)) return;
            if (!itp.Global.TryGet("on_window_closed", out var v) || v is not PsCallable fn) return;
            itp.BeginRun();
            itp.CallCallable(fn, ArgsFor(fn, fullId), 0);
        }

        // ==================== image src ====================

        /// <summary>src 三种写法: game: → CustomUIManager.GetSprite; pack: → 本包 icons/&lt;key&gt;.png 自加载; mod: → 暂不支援(警告跳过)。</summary>
        private Sprite ResolveSprite(string src, string packId, PsUiPanel panel, int line)
        {
            if (string.IsNullOrWhiteSpace(src))
            {
                PsApi.Warn(_logger, $"[psui] {panel.File}:{line}: image 缺 src, 跳过");
                return null;
            }
            if (src.StartsWith("game:", StringComparison.OrdinalIgnoreCase))
            {
                string key = src.Substring(5);
                try
                {
                    var m = CustomUIManager.Instance;
                    var sp = !(m is null) ? m.GetSprite(key) : null;
                    if (sp is null) PsApi.Warn(_logger, $"[psui] {panel.File}:{line}: game sprite '{key}' 未找到, 跳过");
                    return sp;
                }
                catch (Exception e) { PsApi.Warn(_logger, $"[psui] GetSprite('{key}') failed: {e.Message}"); return null; }
            }
            if (src.StartsWith("mod:", StringComparison.OrdinalIgnoreCase))
            {
                // U2: mod:<modId>/<key> → 官方 mod 资源字典; 查不到 = 警告跳过
                string rest = src.Substring(4);
                int slash = rest.IndexOf('/');
                if (slash <= 0 || slash == rest.Length - 1)
                {
                    PsApi.Warn(_logger, $"[psui] {panel.File}:{line}: mod: src 须为 mod:<modId>/<key>, 实为 '{src}', 跳过");
                    return null;
                }
                string modId = rest.Substring(0, slash), spriteKey = rest.Substring(slash + 1);
                try
                {
                    var m = CustomUIManager.Instance;
                    var sp = !(m is null) ? m.GetModSprite(modId, spriteKey) : null;
                    if (sp is null) PsApi.Warn(_logger, $"[psui] {panel.File}:{line}: mod sprite '{modId}/{spriteKey}' 未找到, 跳过");
                    return sp;
                }
                catch (Exception e) { PsApi.Warn(_logger, $"[psui] GetModSprite('{modId}','{spriteKey}') failed: {e.Message}"); return null; }
            }
            // pack: 或无前缀 (无前缀按包内处理)
            string name = src.StartsWith("pack:", StringComparison.OrdinalIgnoreCase) ? src.Substring(5) : src;
            return LoadPackSprite(packId, name, panel, line);
        }

        private Sprite LoadPackSprite(string packId, string name, PsUiPanel panel, int line)
        {
            string key = packId + ":" + name;
            if (_spriteCache.TryGetValue(key, out var cached)) return cached;
            if (!_packSources.TryGetValue(packId, out var src)) return null;
            string relPath = "icons/" + name + ".png";
            if (!src.HasFile(relPath))
            {
                PsApi.Warn(_logger, $"[psui] {panel.File}:{line}: 包图标 '{key}' 不存在 ({src.Describe()}), 跳过");
                return null;
            }
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                if (!ImageConversion.LoadImage(tex, src.ReadBytes(relPath)))
                {
                    PsApi.Warn(_logger, "[psui] icon decode failed: " + key);
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                sprite.hideFlags = HideFlags.HideAndDontSave;
                _spriteCache[key] = sprite;
                _spriteKeepAlive.Add(sprite);
                _spriteKeepAlive.Add(tex);
                return sprite;
            }
            catch (Exception e) { PsApi.Warn(_logger, "[psui] icon load failed: " + key + " " + e.Message); return null; }
        }
    }
}
