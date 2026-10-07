using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using PSApi.Events.PsScript;
using PSApi.Items;
using UnityEngine;

namespace PSApi.Events.PsUI
{
    /// <summary>
    /// U4 图元树后端 (ui/11 §5): 面板含 slot/grid_slot 时整面板走原版图元树
    /// (PixelWindow + GridPixelElement + TagElement/ButtonElement + GameSlotInventory/GameGridInventory),
    /// 装配序列以 PSApi.Items.CustomMachineFactory 为蓝本 (ISIL 还原自 MachineFurnace):
    ///   new PixelWindow → GridPixelElement 网格布局 → AttachPos 入座 (TryCast&lt;PixelElement&gt;)
    ///   → window.Attach(root) → window.Center().Show() (独立开窗, 无宿主物品)。
    /// 控件映射: label→TagElement / button→ButtonElement / slot→GameSlotInventory / grid_slot→GameGridInventory /
    ///   row→GridPixelElement(n,1) / column→(1,n) / grid→(columns,ceil(n/c)) / spacer→空 GridPixelElement(1,1) 占位;
    ///   无对应物的控件 (progress/toggle/slider/input/dropdown/image/scroll) = 警告跳过。
    /// v1.4.1 布局能力: button size WxH → ButtonElement 定宽定高 (forceWidth/forceHeight);
    ///   label size → TagElement fixedWidth (只用 W); window spacing → 顶层竖排网格间距;
    ///   spacer (可选 size WxH) → 网格空单元占位, 带 size 时入座走 Attach(el,x,y,minW,minH) 预留像素,
    ///   用于对角线/错落布局 (匠魂式) 撑开空单元。
    /// v1.5.0 美观能力: button icon:"pack:名" → AdvButtonElement (Sprite 面+文本, SetSprite 走
    ///   RenderHandler.LoadFromAtlas — IconService 图集拦截点, pack 键直解; AdvButton 非 ButtonElement
    ///   子类, 登记进平行字典 AdvButtons, set_text 双查); image src:"pack:名" → GameItemElement
    ///   (atlas="PSApi/icons" 标记, spritePath=完整命名空间键; size 尽力 ResizePixels);
    ///   label/button font_size = TMP fontSize 倍率 (1.0=原生默认, 0.65≈65% 小字),
    ///   构建时应用并记入 FontScales, 脚本 set_text 后重放 (防 SetText 重建文本节点丢缩放)。
    /// v1.5.2 修复: font_size 重放必须幂等 — 首次接触元素时捕获原始 fontSize 作 base
    ///   (FontBaseSizes), 之后每次重放 = base×倍率; v1.5.0/v1.5.1 是 tmp.fontSize *= factor
    ///   叠乘, 每次 set_text 刷新就缩一档 (bench_apply 全量 set_text → 连点按钮文字指数缩小
    ///   至针尖、自动布局随之逐次漂移错位)。
    /// v1.5.1: image 的 GameItemElement identifier 传键名 (v1.5.0 传空串会渲染未知物品"?"占位);
    ///   注意 image/AdvButton 图标渲染尺寸不受 size 控制 (实测过大裁切) — 图标式选择器请改用
    ///   "展示槽"模式 (双锁 slot + 真物品: 原生物品格大小 + 原生悬停 tooltip, 见 ui/11 §4.4)。
    /// 槽位红线 (AI_HANDOFF §2.1): 自装配槽位零委托 — 不给槽位赋任何 interop 委托;
    ///   过滤走 ItemsFacade.RegisterSlotWhitelist → SlotFilterRegistry 排他注册;
    ///   on_change 用轮询差分 (逐帧比对槽内容签名), 不挂原生槽位回调。
    /// 关窗行为: 任何路径关闭 (X/Esc/脚本 ui.close/重建) 时槽内物品自动退回玩家后仓
    ///   (ItemsFacade.ReturnToPlayer, F12 实证可见路径), 防物品随窗口对象丢失;
    ///   v1.4.0 起 window persistent: true 或机器绑定面板跳过退回 (物品归属面板使用者/机器存档)。
    /// v1.4.0 机器绑定面板 (BuildMachine): 同一棵图元树不 Show, 改作机器 contentWindow
    ///   (SetContentWindow; machines/*.json ui="psui" + panel) — 双击原生开窗, 槽内容随机器存档;
    ///   结构恒定铁律: 面板元素树不得增删 (存档 BFS 节点索引对不上会串槽), 动态布局 = 固定槽位+改锁/白名单。
    /// 幂等: 每次 open 新建全套对象, 不依赖"只跑一次"。
    /// </summary>
    internal sealed class PsUiPixelBackend
    {
        /// <summary>打开的图元面板记录 (元素查找/槽位轮询/关闭清理用)。</summary>
        internal sealed class PixelPanel
        {
            internal PixelWindow Window;
            internal string PackId;
            internal string FullId;
            internal readonly Dictionary<string, TagElement> Labels = new Dictionary<string, TagElement>(StringComparer.Ordinal);
            /// <summary>v1.38.0: rich_label 注册表 (RichTextElement 非 TagElement 子类, 平行字典;
            /// ui.machine_set_text 可查 — 与 label 同权可被脚本改文本/着色)。</summary>
            internal readonly Dictionary<string, RichTextElement> RichLabels = new Dictionary<string, RichTextElement>(StringComparer.Ordinal);
            internal readonly Dictionary<string, ButtonElement> Buttons = new Dictionary<string, ButtonElement>(StringComparer.Ordinal);
            /// <summary>v1.5.0: 图标按钮 (button icon: → AdvButtonElement; 非 ButtonElement 子类, set_text 双查)。</summary>
            internal readonly Dictionary<string, AdvButtonElement> AdvButtons = new Dictionary<string, AdvButtonElement>(StringComparer.Ordinal);
            /// <summary>v1.5.0: 元素字号倍率 (font_size 属性); set_text 后重放防丢。</summary>
            internal readonly Dictionary<string, float> FontScales = new Dictionary<string, float>(StringComparer.Ordinal);
            /// <summary>v1.5.2: font_size 重放的原始字号基准 (首次接触捕获未缩放值; 重放 = base×倍率, 幂等不叠乘)。</summary>
            internal readonly Dictionary<string, float> FontBaseSizes = new Dictionary<string, float>(StringComparer.Ordinal);
            internal readonly Dictionary<string, GameInventory> Slots = new Dictionary<string, GameInventory>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> SlotKinds = new Dictionary<string, string>(StringComparer.Ordinal);   // slot|grid_slot
            internal readonly Dictionary<string, string> SlotChangeFn = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> SlotSigs = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly List<object> Pins = new List<object>();
            /// <summary>v1.4.0: window persistent: true — 关窗不把槽内物品退回玩家 (物品归属由面板使用者负责)。</summary>
            internal bool Persistent;
            /// <summary>v1.4.0: 面板定义引用 (on_open 查找等); 构建时登记。</summary>
            internal PsUiPanel Panel;
            /// <summary>v1.4.0: 机器绑定面板 — 窗口是机器的 contentWindow (双击原生开窗), 非 null 时
            /// 不进 _openPixel: 关窗由原生管理 (不退物品), 回调带 machine 句柄, 轮询走 PollMachinePanels。</summary>
            internal GameItem Machine;
            /// <summary>v1.4.0: 机器面板上一帧可见性 (false→true 跳变触发 on_open)。</summary>
            internal bool WasVisible;
            /// <summary>v1.19.3: 镜像格链接 — grid_slot mirror: <源槽id> (源槽内容器物品的 contentWindow
            /// 借来显示在占位格位置; 元素级重挂会改图节点父系弄乱存档 BFS, 故只 Show/Hide+钉位置, 不动图)。</summary>
            internal readonly Dictionary<string, string> MirrorLinks = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, MirrorState> MirrorStates = new Dictionary<string, MirrorState>(StringComparer.Ordinal);
        }

        /// <summary>v1.19.3: 镜像格运行时状态 (每镜像格一份)。</summary>
        internal sealed class MirrorState
        {
            internal IntPtr LastItem;      // 源槽当前物品指针 (Zero=空)
            internal PixelWindow Win;      // 借来的容器窗 (背包 contentWindow)
        }

        private readonly MelonLogger.Instance _logger;
        private readonly PsUiService _owner;

        internal PsUiPixelBackend(MelonLogger.Instance logger, PsUiService owner)
        {
            _logger = logger;
            _owner = owner;
        }

        /// <summary>Il2Cpp 运行时可用性探针: 读 CustomUIManager.Instance 静态属性 (不建对象, 结果缓存)。
        /// 无头测试台 = IL2CPP cctor 抛 TypeInitializationException = false; 游戏内不在对局返回 null 也算 true。</summary>
        private static bool Il2CppAvailable
        {
            get
            {
                if (_il2cppAvailable.HasValue) return _il2cppAvailable.Value;
                try { _ = CustomUIManager.Instance; _il2cppAvailable = true; }
                catch { _il2cppAvailable = false; }
                return _il2cppAvailable.Value;
            }
        }
        private static bool? _il2cppAvailable;

        // ==================== 构建 ====================

        /// <summary>装配并显示图元面板。失败抛异常 (调用方包成 PsRuntimeError)。成功返回 PixelPanel。</summary>
        internal PixelPanel Build(string fullId, PsUiPanel panel, string packId)
        {
            var pp = BuildCore(fullId, panel, packId, null);
            try { pp.Window.Center(); } catch { }
            try { pp.Window.Show(); }
            catch (Exception e) { throw new Exception("window.Show failed: " + e.Message); }
            PsApi.Log(_logger, $"[psui:pixel] opened: {fullId} (slots={pp.Slots.Count}, labels={pp.Labels.Count}, buttons={pp.Buttons.Count})");
            return pp;
        }

        /// <summary>v1.4.0: 装配机器绑定面板 — 同样的图元树, 但不 Center/Show, 改作机器的
        /// contentWindow (item.SetContentWindow 内部处理图节点链接; 双击原生开窗, 关窗原生管理不退物品)。
        /// 结构恒定要求: 同一面板定义每次构建出完全一致的图节点树 (存档 BFS 节点索引对得上, 读档不串槽)。
        /// 失败抛异常 (调用方判失败回退 plain item)。</summary>
        internal PixelPanel BuildMachine(string fullId, PsUiPanel panel, string packId, GameItem machine)
        {
            if (machine == null) throw new Exception("machine is null");
            var pp = BuildCore(fullId, panel, packId, machine);
            try { machine.SetContentWindow(pp.Window); }
            catch (Exception e) { throw new Exception("SetContentWindow failed: " + e.Message); }
            PsApi.Log(_logger, $"[psui:pixel] machine panel attached: {fullId} (slots={pp.Slots.Count}, labels={pp.Labels.Count}, buttons={pp.Buttons.Count})");
            return pp;
        }

        /// <summary>M1.5: 场景窗口构建 (场景 UI psui 驱动) — 不 Show/Center/登记 open 表, 返回 PixelPanel
        /// 由 SceneService 全权接管窗口生命周期。sceneClick: 按钮换线钩子, 按 elem_id 判定 → 非 null 的
        /// 返回值替换该按钮的 psui on_click (调用方负责委托双 pin); 返回 null = 保留 psui 声明。</summary>
        internal PixelPanel BuildScenePanel(string fullId, PsUiPanel panel, string packId, Func<string, Il2CppSystem.Action> sceneClick)
        {
            var pp = BuildCore(fullId, panel, packId, null, sceneClick);
            PsApi.Log(_logger, $"[psui:pixel] scene panel built: {fullId} (labels={pp.Labels.Count}, buttons={pp.Buttons.Count})");
            return pp;
        }

        /// <summary>窗口 + 元素树装配 (Build/BuildMachine/BuildScenePanel 共用; 不显示不绑定)。
        /// sceneClick (M1.5 场景路径): 按钮换线钩子, null = 全部按 psui 声明接线。</summary>
        private PixelPanel BuildCore(string fullId, PsUiPanel panel, string packId, GameItem machine, Func<string, Il2CppSystem.Action> sceneClick = null)
        {
            // 无头(测试台)防御: Il2Cpp 不可用时 PixelWindow 构造必抛, 且 ctor 半途失败会留下
            // 带 finalizer 的半构造僵尸对象 (进程退出时 finalizer 再触发 IL2CPP cctor 失败 = 未处理异常崩退出码);
            // 探针失败即早退, 根本不造对象。游戏内探针恒 true, 行为零变化。
            if (!Il2CppAvailable) throw new Exception("Il2Cpp 运行时不可用(无头环境), 图元面板无法构建");
            var pp = new PixelPanel { PackId = packId, FullId = fullId, Panel = panel, Persistent = panel.GetBool("persistent"), Machine = machine };
            string title = panel.GetString("title", fullId);
            bool draggable = panel.GetBool("draggable", true);

            PixelWindow window = null;
            var size = GetPanelSize(panel);
            if (size.HasValue)
            {
                try { window = new PixelWindow((int)size.Value.W, (int)size.Value.H, draggable, title); }
                catch (Exception e) { PsApi.Warn(_logger, $"[psui:pixel] {panel.File}: sized window ctor failed, fallback: {e.Message}"); }
            }
            if (window == null)
            {
                try { window = new PixelWindow(2, 2, draggable, title); }
                catch { try { window = new PixelWindow(draggable, title); } catch (Exception e2) { throw new Exception("window ctor failed: " + e2.Message); } }
            }
            if (window == null) throw new Exception("window ctor returned null");
            pp.Window = window;

            var root = BuildChildrenVertical(panel.Children, pp, panel, packId, sceneClick);
            if (root == null)
            {
                PsApi.Warn(_logger, $"[psui:pixel] {panel.File}: 没有可实例化的元素, 开窗为空面板");
                try { var g = new GridPixelElement(1, 1, false); root = g == null ? null : g.TryCast<PixelElement>(); } catch { }
            }
            if (root != null)
            {
                try { window.Attach(root); }
                catch (Exception e) { PsApi.Warn(_logger, $"[psui:pixel] {panel.File}: window.Attach failed: {e.Message}"); }
            }

            // 初始槽位签名 (空槽), 防打开瞬间误报 on_change
            foreach (var kv in pp.Slots)
                pp.SlotSigs[kv.Key] = SlotSignature(kv.Value);
            return pp;
        }

        internal static (float W, float H)? GetPanelSize(PsUiPanel panel)
        {
            if (!panel.Props.TryGetValue("size", out var v)) return null;
            string s = v as string ?? v.ToString();
            var parts = s.ToLowerInvariant().Split('x');
            if (parts.Length != 2) return null;
            if (!float.TryParse(parts[0], out float w) || !float.TryParse(parts[1], out float h) || w <= 0 || h <= 0) return null;
            return (w, h);
        }

        /// <summary>顶层子元素竖排: 单元素直用, 多元素包一层 1 列网格 (v1.4.1: 间距取 window spacing 属性)。</summary>
        private PixelElement BuildChildrenVertical(List<PsUiElem> children, PixelPanel pp, PsUiPanel panel, string packId, Func<string, Il2CppSystem.Action> sceneClick)
        {
            if (children.Count == 1) return BuildNode(children[0], pp, panel, packId, sceneClick);
            var grid = NewGrid(1, children.Count, (int)panel.GetNumber("spacing", 0), panel);
            if (grid == null) return null;
            int y = 0;
            foreach (var c in children)
            {
                var el = BuildNode(c, pp, panel, packId, sceneClick);
                if (el != null) SeatChild(grid, el, 0, y, panel, c);
                y++;
            }
            return grid.TryCast<PixelElement>();
        }

        /// <summary>单元素构建; 无图元对应物 = 警告跳过返回 null (其子树同样跳过)。</summary>
        private PixelElement BuildNode(PsUiElem elem, PixelPanel pp, PsUiPanel panel, string packId, Func<string, Il2CppSystem.Action> sceneClick)
        {
            switch (elem.Type)
            {
                case "label":
                {
                    try
                    {
                        // v1.4.1: size 属性 → TagElement fixedWidth (只用 W; 定宽防折行/撑开列宽)
                        var lsz = elem.GetSize("size");
                        int fixedW = lsz.HasValue ? (int)lsz.Value.W : -1;
                        var tag = new TagElement(fixedW, false).SetText(elem.GetString("text", ""), 10000, PaletteOf(elem, panel, _logger));
                        if (tag == null) return null;
                        // v1.5.0: font_size = TMP fontSize 倍率 (构建应用 + FontScales 登记, set_text 后重放)
                        float lfs = FontScaleOf(elem);
                        if (lfs > 0f) { ApplyFontScale(tag.textNode, lfs, pp.FontBaseSizes, elem.Id, _logger, $"{panel.File}:{elem.Line}"); if (elem.Id != null) pp.FontScales[elem.Id] = lfs; }
                        if (elem.Id != null) pp.Labels[elem.Id] = tag;
                        return tag.TryCast<PixelElement>();
                    }
                    catch (Exception e) { WarnSkipped(panel, elem, "label 创建失败: " + e.Message); return null; }
                }
                case "button":
                {
                    try
                    {
                        string fn = elem.GetString("on_click");
                        Il2CppSystem.Action act = null;
                        // M1.5: 场景路径换线钩子 — elem_id 命中场景互动物点的按钮由 SceneService 接管
                        // (psui 声明的 on_click 不生效); 钩子返回 null = 保留 psui 声明
                        if (sceneClick != null && elem.Id != null) act = sceneClick(elem.Id);
                        // v1.4.0: 机器绑定面板的按钮回调带 {elem, machine} 参数 (哪台机器的按钮被点)
                        if (act == null && fn != null) act = pp.Machine != null
                            ? _owner.PinMachineClick(pp.Pins, packId, fn, elem.Id ?? "", pp)
                            : _owner.PinClick(pp.Pins, packId, fn, panel.File, elem.Line);
                        // v1.4.1: size 属性 → 定宽定高 (长文本按钮防折行)
                        var bsz = elem.GetSize("size");
                        int bw = bsz.HasValue ? (int)bsz.Value.W : -1;
                        int bh = bsz.HasValue ? (int)bsz.Value.H : -1;
                        float bfs = FontScaleOf(elem);
                        // v1.5.0: icon 属性 → AdvButtonElement (Sprite 面按钮); pack:名 经 LoadFromAtlas 拦截解析
                        string icon = elem.GetString("icon");
                        if (icon != null)
                        {
                            var ab = new AdvButtonElement(null, elem.GetString("text", ""), bw, bh, act);
                            if (ab is null) return null;
                            TrySetAdvIcon(ab, icon, packId, panel, elem);
                            if (bfs > 0f) { ApplyFontScale(ab.textNode, bfs, pp.FontBaseSizes, elem.Id, _logger, $"{panel.File}:{elem.Line}"); if (elem.Id != null) pp.FontScales[elem.Id] = bfs; }
                            if (elem.Id != null) pp.AdvButtons[elem.Id] = ab;
                            return ab.TryCast<PixelElement>();
                        }
                        var btn = new ButtonElement(elem.GetString("text", elem.Id ?? "button"), bw, bh, act);
                        if (btn == null) return null;
                        if (bfs > 0f) { ApplyFontScale(btn.textNode, bfs, pp.FontBaseSizes, elem.Id, _logger, $"{panel.File}:{elem.Line}"); if (elem.Id != null) pp.FontScales[elem.Id] = bfs; }
                        if (elem.Id != null) pp.Buttons[elem.Id] = btn;
                        string tip = elem.GetString("tooltip");
                        if (tip != null) PsApi.Warn(_logger, $"[psui:pixel] {panel.File}:{elem.Line}: 图元按钮不支持 tooltip, 已忽略");
                        return btn.TryCast<PixelElement>();
                    }
                    catch (Exception e) { WarnSkipped(panel, elem, "button 创建失败: " + e.Message); return null; }
                }
                case "image":
                {
                    // v1.19.2: image 改走 SpritePixelElement (原生精灵图元, canResize + ResizePixels 真缩放 —
                    // 修复 v1.5.x GameItemElement 路径"尺寸不受 size 控制、过大裁切"问题); ctor 失败回退旧路径。
                    string src = elem.GetString("src");
                    if (src == null || !src.StartsWith("pack:"))
                    {
                        WarnSkipped(panel, elem, "图元后端 image 仅支持 src:\"pack:名\" (game:/mod: 请走 CustomUIManager 后端面板)");
                        return null;
                    }
                    string key = ResolvePackKey(src, packId);
                    var isz = elem.GetSize("size");
                    try
                    {
                        var sp = new SpritePixelElement("PSApi/icons", key);
                        if (sp != null)
                        {
                            if (isz.HasValue) { try { sp.ResizePixels((int)isz.Value.W, (int)isz.Value.H); } catch { } }
                            return sp.TryCast<PixelElement>();
                        }
                    }
                    catch (Exception e) { PsApi.Warn(_logger, $"[psui:pixel] {panel.File}:{elem.Line}: SpritePixelElement 失败, 回退 GameItemElement: {e.Message}"); }
                    try
                    {
                        var ge = new GameItemElement(key, "PSApi/icons", key);
                        if (ge is null) return null;
                        if (isz.HasValue) { try { ge.ResizePixels((int)isz.Value.W, (int)isz.Value.H); } catch { } }
                        return ge.TryCast<PixelElement>();
                    }
                    catch (Exception e) { WarnSkipped(panel, elem, "image 创建失败: " + e.Message); return null; }
                }
                case "rich_label":
                {
                    // v1.19.2: 富文本标签 (RichTextElement) — 与 TagElement 同链路但支持富文本;
                    // size WxH → fixedWidth/fixedHeight 定尺寸; 不吃点击 (hasRaycast=false)。
                    try
                    {
                        var rsz = elem.GetSize("size");
                        int fw = rsz.HasValue ? (int)rsz.Value.W : -1;
                        int fh = rsz.HasValue ? (int)rsz.Value.H : -1;
                        var rt = new RichTextElement(false, fw, fh, false);
                        if (rt == null) return null;
                        rt.SetText(elem.GetString("text", ""), 10000, PaletteOf(elem, panel, _logger));
                        float rfs = FontScaleOf(elem);
                        if (rfs > 0f) { ApplyFontScale(rt.handler, rfs, pp.FontBaseSizes, elem.Id, _logger, $"{panel.File}:{elem.Line}"); if (elem.Id != null) pp.FontScales[elem.Id] = rfs; }
                        // v1.38.0: rich_label 注册进 RichLabels — ui.machine_set_text 可寻址 (此前漏注册只能静态显示)
                        if (elem.Id != null) pp.RichLabels[elem.Id] = rt;
                        return rt.TryCast<PixelElement>();
                    }
                    catch (Exception e) { WarnSkipped(panel, elem, "rich_label 创建失败: " + e.Message); return null; }
                }
                case "spacer":
                {
                    // v1.4.1: 网格空单元占位 — 空 1x1 网格 (零尺寸, 不参与渲染);
                    // 带 size 时由入座方用 Attach(el,x,y,minW,minH) 预留像素 (见 SeatChild)。
                    try
                    {
                        var g = new GridPixelElement(1, 1, false);
                        return g == null ? null : g.TryCast<PixelElement>();
                    }
                    catch (Exception e) { WarnSkipped(panel, elem, "spacer 创建失败: " + e.Message); return null; }
                }
                case "slot":
                case "grid_slot":
                case "scroll_grid_slot":
                {
                    bool isGrid = elem.Type != "slot";
                    var sz = elem.GetSize("size");
                    int w = (int)(sz?.W ?? (isGrid ? 3f : 2f));
                    int h = (int)(sz?.H ?? (isGrid ? 4f : 2f));
                    GameInventory inv = null;
                    try
                    {
                        if (elem.Type == "scroll_grid_slot")
                        {
                            // v1.19.2: 可滚动格网 — size=可视区格数 WxH, content_height=内容总高(像素, 默认 400)
                            int ch = (int)elem.GetNumber("content_height", 400);
                            inv = new GameGridScrollableInventory(w, h, ch, 0.5f);
                        }
                        else
                        {
                            inv = isGrid
                                ? (GameInventory)new GameGridInventory(w, h)
                                : new GameSlotInventory(w, h, false, false);
                        }
                    }
                    catch (Exception e) { WarnSkipped(panel, elem, "槽位创建失败: " + e.Message); return null; }
                    if (inv == null) { WarnSkipped(panel, elem, "槽位创建返回 null"); return null; }

                    // v1.40.0: grid_slot shape — 行优先格态串 (0=可放 1=永久洞),
                    // 原生 GameGridInventory.SetShape (与原版扩容套件 MachineHelper.ExpandModule 同 API,
                    // 禁放格不画底格视觉上是洞); 格式/长度解析期已校验。放置判定逐格现读
                    // inventoryShape, 之后 ui.machine_set_shape 改动即时生效。
                    // v1.41.0: 第三态 '2'=锁定格 — 原生只认 0/1, ToNative 映射 2→1 后调 SetShape,
                    // 原串交 RebuildLockOverlay 画暗色底格 (看得到"这里以后解锁")。
                    var shapeStr = elem.GetString("shape");
                    if (shapeStr != null)
                    {
                        var ginv = inv.TryCast<GameGridInventory>();
                        if (ginv != null)
                        {
                            try
                            {
                                ginv.SetShape(ShapeUtil.ToNative(shapeStr), w);
                                RebuildLockOverlay(ginv, shapeStr, w, h, _logger, $"{panel.File}:{elem.Line}");
                            }
                            catch (Exception e) { WarnSkipped(panel, elem, "shape 应用失败: " + e.Message); }
                        }
                    }

                    var wl = elem.GetList("whitelist");
                    if (wl == null || wl.Count == 0)
                    {
                        PsApi.Warn(_logger, $"[psui:pixel] {panel.File}:{elem.Line}: 槽位 '{elem.Id ?? "?"}' 缺 whitelist — 槽位不受管, 放入判定不可靠 (建议补 whitelist [id...])");
                    }
                    else
                    {
                        var ids = new List<string>();
                        foreach (var o in wl) ids.Add(PsValues.Fmt(o));
                        if (!ItemsFacade.RegisterSlotWhitelist(inv, ids.ToArray(), true))
                            PsApi.Warn(_logger, $"[psui:pixel] {panel.File}:{elem.Line}: 槽位 '{elem.Id ?? "?"}' whitelist 注册失败");
                    }

                    // v1.48.0: lock_interactions: true — 槽内物品锁死除移动外一切交互
                    // (InteractionLockRegistry, 双击/on_target 目标端全拦; 判定=位于该槽, 不持久化)
                    if (elem.GetBool("lock_interactions"))
                    {
                        try { PsScript.InteractionLockRegistry.Register(inv.Pointer); }
                        catch (Exception e) { WarnSkipped(panel, elem, "lock_interactions 注册失败: " + e.Message); }
                    }

                    // v1.48.1: strict_footprint: true (仅 slot 有意义) — 固定槽默认不验 footprint
                    // (一格任意大小), 声明后由 GridPlacementGuard 验外接矩形≤槽格数
                    if (elem.GetBool("strict_footprint"))
                    {
                        try { ItemsFacade.RegisterSlotStrictFootprint(inv, true); }
                        catch (Exception e) { WarnSkipped(panel, elem, "strict_footprint 注册失败: " + e.Message); }
                    }

                    if (elem.Id != null)
                    {
                        pp.Slots[elem.Id] = inv;
                        pp.SlotKinds[elem.Id] = elem.Type;
                        string fn = elem.GetString("on_change");
                        if (fn != null) pp.SlotChangeFn[elem.Id] = fn;
                        string mir = elem.GetString("mirror");
                        if (mir != null) pp.MirrorLinks[elem.Id] = mir;
                    }
                    else PsApi.Warn(_logger, $"[psui:pixel] {panel.File}:{elem.Line}: 槽位无 id, 脚本无法访问 (ui.get_slot_item/on_change 都需要 id)");
                    return inv.TryCast<PixelElement>();
                }
                case "row":
                case "column":
                case "grid":
                {
                    bool isRow = elem.Type == "row";
                    int cols = isRow ? Math.Max(1, elem.Children.Count)
                        : elem.Type == "column" ? 1
                        : Math.Max(1, (int)elem.GetNumber("columns", 2));
                    int rows = (int)Math.Ceiling(Math.Max(1, elem.Children.Count) / (double)cols);
                    var g = NewGrid(cols, rows, (int)elem.GetNumber("spacing", 0), panel);
                    if (g == null) return null;
                    for (int i = 0; i < elem.Children.Count; i++)
                    {
                        var el = BuildNode(elem.Children[i], pp, panel, packId, sceneClick);
                        if (el != null) SeatChild(g, el, i % cols, i / cols, panel, elem.Children[i]);
                    }
                    return g.TryCast<PixelElement>();
                }
                default:
                    WarnSkipped(panel, elem, "图元树后端不支持该控件 (支持: label/button/slot/grid_slot/row/column/grid/spacer/image)");
                    return null;
            }
        }

        // ==================== v1.5.0: 图标与字号 ====================

        /// <summary>v1.20.0: label/rich_label color 属性 → ColorPalette (枚举名, 大小写不敏感, 如 "orange");
        /// 缺省/非法 = White (非法告警一次)。</summary>
        private static RenderHandler.ColorPalette PaletteOf(PsUiElem elem, PsUiPanel panel, MelonLogger.Instance log)
        {
            string s = elem.GetString("color");
            if (string.IsNullOrWhiteSpace(s)) return RenderHandler.ColorPalette.White;
            RenderHandler.ColorPalette c;
            if (Enum.TryParse(s.Trim(), true, out c)) return c;
            PsApi.Warn(log, $"[psui:pixel] {panel.File}:{elem.Line}: 未知颜色 '{s}', 用白色 (取 ColorPalette 枚举名, 如 White/Orange/Red/Green)");
            return RenderHandler.ColorPalette.White;
        }

        /// <summary>font_size 属性 → 倍率 (合法域 0.2..3; 缺省/非法 = -1 不应用)。</summary>
        private static float FontScaleOf(PsUiElem elem)
        {
            double v = elem.GetNumber("font_size", 1.0);
            if (v <= 0.0 || Math.Abs(v - 1.0) < 0.001) return -1f;
            if (v < 0.2 || v > 3.0) return -1f;
            return (float)v;
        }

        /// <summary>v1.38.0: "#RRGGBB"/"#RRGGBBAA" hex 颜色解析 (大小写不敏感) — 纯 C# 手写,
        /// 不用 ColorUtility.TryParseHtmlString: 零 Unity 依赖, pss_test 无头环境可直接断言
        /// (UnityEngine.Color 静态构造在 Il2Cpp 无头下会炸, 见 RtCombatService 注记)。
        /// 非法输入返回 false 不抛 (抛错带行号是调用方 builtin 的职责)。</summary>
        internal static bool TryParseHtmlColor(string s, out float r, out float g, out float b, out float a)
        {
            r = g = b = a = 0f;
            if (string.IsNullOrEmpty(s) || s[0] != '#') return false;
            int n = s.Length - 1;
            if (n != 6 && n != 8) return false;
            for (int i = 1; i < s.Length; i++)
                if (HexVal(s[i]) < 0) return false;
            r = (HexVal(s[1]) * 16 + HexVal(s[2])) / 255f;
            g = (HexVal(s[3]) * 16 + HexVal(s[4])) / 255f;
            b = (HexVal(s[5]) * 16 + HexVal(s[6])) / 255f;
            a = n == 8 ? (HexVal(s[7]) * 16 + HexVal(s[8])) / 255f : 1f;
            return true;
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>v1.38.0: TMP 运行期着色 (ui.machine_set_text 第 4 参 color 的落地路径) —
        /// TagElement/RichTextElement.SetText 只吃 ColorPalette 枚举 (游戏内仅枚举重载),
        /// 故 SetText 之后直设下游 TMP .color (TreeNodeRender.text = TextMeshProUGUI)。
        /// 文本节点懒初始化: textNode/text 任一不可达 = 静默跳过 (下次 set_text 自愈,
        /// 与 ApplyFontScale 同款容错); 原生调用异常才告警。</summary>
        internal static void ApplyTextColor(TreeNodeRender textNode, float r, float g, float b, float a, MelonLogger.Instance log, string ctx)
        {
            try
            {
                var tmp = textNode is null ? null : textNode.text;
                if (tmp is null) return; // 文本节点懒初始化: 尚未创建则跳过, 下次 set_text 自愈
                tmp.color = new Color(r, g, b, a);
            }
            catch (Exception e) { PsApi.Warn(log, $"[psui:pixel] {ctx}: 文本着色失败: {e.Message}"); }
        }

        /// <summary>font_size 幂等核心 (纯函数, 测试台可断言): 首次接触该元素捕获 base=当前(未缩放)
        /// 字号, 返回 base×factor; 之后每次重放都用已存 base, 不在已缩放值上叠乘。
        /// elemId 为 null (无 id 元素, 永不会重放) = 不缓存, 直接用当前值算一次。</summary>
        internal static float ScaledFontSize(Dictionary<string, float> baseSizes, string elemId, float currentFontSize, float factor)
        {
            float baseSize;
            if (elemId == null || !baseSizes.TryGetValue(elemId, out baseSize))
            {
                baseSize = currentFontSize;
                if (elemId != null) baseSizes[elemId] = baseSize;
            }
            return baseSize * factor;
        }

        /// <summary>TMP fontSize 倍率应用 (textNode/text 任一不可达 = 静默跳过, set_text 后重放兜底;
        /// 原生调用异常才告警)。构建时调一次; 脚本 set_text 后由 PsUiService 按 PixelPanel.FontScales 重放。
        /// v1.5.2: 经 ScaledFontSize + FontBaseSizes 幂等 (base×倍率), 不再叠乘。</summary>
        internal static void ApplyFontScale(TreeNodeRender textNode, float factor, Dictionary<string, float> baseSizes, string elemId, MelonLogger.Instance log, string ctx)
        {
            try
            {
                var tmp = textNode is null ? null : textNode.text;
                if (tmp is null) return; // 文本节点懒初始化: 尚未创建则跳过, 重放兜底 (首次重放捕获 base)
                tmp.fontSize = ScaledFontSize(baseSizes, elemId, tmp.fontSize, factor);
            }
            catch (Exception e) { PsApi.Warn(log, $"[psui:pixel] {ctx}: font_size 应用失败: {e.Message}"); }
        }

        /// <summary>"pack:名" → IconService 命名空间键 (缺省补本包前缀; 已含 ":" 视为全限定跨包键)。</summary>
        internal static string ResolvePackKey(string src, string packId)
        {
            string name = src.Substring("pack:".Length);
            return name.Contains(":") ? name : packId + ":" + name;
        }

        /// <summary>AdvButtonElement 图标: SetSprite("PSApi/icons", 完整键) — 与物品图标同走 LoadFromAtlas 拦截点。</summary>
        private void TrySetAdvIcon(AdvButtonElement ab, string icon, string packId, PsUiPanel panel, PsUiElem elem)
        {
            if (!icon.StartsWith("pack:"))
            {
                PsApi.Warn(_logger, $"[psui:pixel] {panel.File}:{elem.Line}: button icon 仅支持 \"pack:名\", 已忽略 '{icon}'");
                return;
            }
            try { ab.SetSprite("PSApi/icons", ResolvePackKey(icon, packId)); }
            catch (Exception e) { PsApi.Warn(_logger, $"[psui:pixel] {panel.File}:{elem.Line}: button icon 设置失败: {e.Message}"); }
        }

        private GridPixelElement NewGrid(int cols, int rows, int spacing, PsUiPanel panel)
        {
            try
            {
                var g = new GridPixelElement(Math.Max(1, cols), Math.Max(1, rows), false);
                if (spacing > 0) { try { g.SetSpacing(spacing, spacing); } catch { } }
                return g;
            }
            catch (Exception e)
            {
                PsApi.Warn(_logger, $"[psui:pixel] {panel.File}: grid 创建失败: {e.Message}");
                return null;
            }
        }

        /// <summary>AttachPos 入座 (interop 包装层类继承被压平, 须 TryCast 走原生类型检查)。
        /// v1.4.1: spacer 带 size 属性时走 Attach(el,x,y,minW,minH) 预留像素 (对角线布局撑开空单元)。</summary>
        private void SeatChild(GridPixelElement grid, PixelElement el, int x, int y, PsUiPanel panel, PsUiElem elem)
        {
            if (elem.Type == "spacer")
            {
                var sz = elem.GetSize("size");
                if (sz.HasValue)
                {
                    try
                    {
                        if (!grid.Attach(el, x, y, (int)sz.Value.W, (int)sz.Value.H))
                            PsApi.Warn(_logger, $"[psui:pixel] {panel.File}:{elem.Line}: spacer 入座失败 ({x},{y})");
                    }
                    catch (Exception e) { PsApi.Warn(_logger, $"[psui:pixel] {panel.File}:{elem.Line}: spacer 入座异常: {e.Message}"); }
                    return;
                }
            }
            Seat(grid, el, x, y, panel, elem);
        }

        /// <summary>AttachPos 入座 (interop 包装层类继承被压平, 须 TryCast 走原生类型检查)。</summary>
        private void Seat(GridPixelElement grid, PixelElement el, int x, int y, PsUiPanel panel, PsUiElem elem)
        {
            try
            {
                if (!grid.AttachPos(el, x, y))
                    PsApi.Warn(_logger, $"[psui:pixel] {panel.File}:{elem.Line}: 元素 '{elem.Type}' 入座失败 ({x},{y})");
            }
            catch (Exception e) { PsApi.Warn(_logger, $"[psui:pixel] {panel.File}:{elem.Line}: 元素 '{elem.Type}' 入座异常: {e.Message}"); }
        }

        private void WarnSkipped(PsUiPanel panel, PsUiElem elem, string why)
            => PsApi.Warn(_logger, $"[psui:pixel] {panel.File}:{elem.Line}: 元素 '{elem.Type}' 跳过 — {why}");

        // ==================== 关闭 / 物品退回 ====================

        /// <summary>关闭面板: Hide + 槽内物品退回玩家后仓 (任何关闭路径都走这里)。
        /// v1.4.0: persistent: true 或机器绑定面板 — 不退物品 (物品归属面板使用者/机器存档)。</summary>
        internal void Shutdown(PixelPanel pp)
        {
            if (pp == null) return;
            // v1.19.3: 镜像窗归还 (借来的容器窗 Hide, 占位格恢复)
            foreach (var mkv in pp.MirrorStates)
            {
                try { mkv.Value.Win?.Hide(); } catch { }
                SetPlaceholderVisible(pp, mkv.Key, true);
            }
            pp.MirrorStates.Clear();
            if (!pp.Persistent && pp.Machine == null)
                foreach (var kv in pp.Slots)
                {
                    List<GameItem> items = null;
                    try { items = ItemsFacade.SlotItems(kv.Value); } catch { }
                    if (items == null) continue;
                    foreach (var item in items)
                    {
                        try
                        {
                            if (ItemsFacade.ReturnToPlayer(item))
                                PsApi.Log(_logger, $"[psui:pixel] {pp.FullId}: 槽位 '{kv.Key}' 物品已退回玩家后仓");
                        }
                        catch { }
                    }
                }
            try { pp.Window?.Hide(); } catch { }
            pp.Pins.Clear();
        }

        // ==================== 轮询 (宿主 OnUpdate): 关窗检测 + 槽位 on_change 差分 ====================

        /// <summary>返回因玩家 X/Esc 关闭而需要收尾的面板全名列表 (无 = null); 对仍打开的面板做槽位差分触发 on_change。</summary>
        internal List<string> Poll(Dictionary<string, PixelPanel> open)
        {
            if (open.Count == 0) return null;
            List<string> dead = null;
            // v1.13.4: 快照迭代 —— PollSlots 触发的 on_change 回调里脚本可调 ui.close/rebuild 改 open 表
            foreach (var kv in open.ToArray())
            {
                var pp = kv.Value;
                bool visible;
                try { visible = !(pp.Window is null) && pp.Window.IsVisible(); }
                catch { visible = false; }
                if (!visible) { (dead ??= new List<string>()).Add(kv.Key); continue; }
                PollSlots(pp);
            }
            return dead;
        }

        /// <summary>槽位差分 → on_change (v1.4.0 internal: 机器面板由 PsUiService.PollMachinePanels 逐面板调用)。
        /// 机器绑定面板回调带 machine 句柄 ({elem, value, machine}), 独立面板走原 {elem, value}。</summary>
        internal void PollSlots(PixelPanel pp)
        {
            // v1.13.4: 快照迭代 —— on_change 回调里 ui.rebuild 会重建 Slots 表, 直接枚举会炸
            foreach (var kv in pp.Slots.ToArray())
            {
                string sig;
                try { sig = SlotSignature(kv.Value); }
                catch { continue; }
                if (pp.SlotSigs.TryGetValue(kv.Key, out var prev) && prev == sig) continue;
                pp.SlotSigs[kv.Key] = sig;
                if (pp.SlotChangeFn.TryGetValue(kv.Key, out var fn))
                {
                    object value = SlotValue(pp, kv.Key);
                    if (pp.Machine != null) _owner.FireMachineChange(pp, fn, kv.Key, value);
                    else _owner.FireChange(pp.PackId, fn, kv.Key, value);
                }
            }
            PollMirrors(pp);
        }

        // ==================== v1.41.0: 锁定格暗色 overlay (shape 第三态 '2') ====================
        // 原生 SetShape 只认 0/1 (1=禁放格不画底格), 但 UX 要求锁定格"暗色可见" (表达以后解锁)。
        // 红线: 不得往图元元素树加元素 (存档 BFS 索引铁律) — overlay 用树外 raw uGUI GameObject,
        // 挂在 grid handler 的 GameObject 下 (照镜像格伴侣窗思路, 不 Attach/不 SetRenderingParent),
        // 生命周期跟面板走 (父物体销毁即连带销毁, 关窗/机器销毁无泄漏)。
        // 渲染次序: 容器 SetAsLastSibling 在原生底格之上; 锁定格按定义无物品, 不挡物品图标;
        // raycastTarget 全关, 不吃拖拽事件。

        /// <summary>overlay 容器名 (查找旧容器销毁用)。</summary>
        internal const string LockOverlayName = "ps_lock_overlay";

        /// <summary>按三态 shape 串重建锁定格 overlay: 先销毁旧容器, 串里有 '2' 才逐格建暗色 Image。
        /// 格几何: widthPixels/heightPixels 恰为 w×h 整数格 (Validate(): widthPixels=shape.width×格常量,
        /// IL 实证无边框), 格左上原点, 1px 内缩仿原生格线。任何一步失败只告警不抛 (视觉增强不该炸面板)。</summary>
        internal static void RebuildLockOverlay(GameGridInventory ginv, string shape, int w, int h,
            MelonLogger.Instance logger, string tag)
        {
            try
            {
                Transform parent = null;
                var pe = ginv.TryCast<PixelElement>();
                if (pe != null && !(pe.handler is null)) parent = pe.handler.transform;
                if (parent == null) return;
                var old = parent.Find(LockOverlayName);
                if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                if (string.IsNullOrEmpty(shape) || shape.IndexOf('2') < 0) return;
                if (w <= 0 || h <= 0 || shape.Length != w * h) return;
                float cw = ginv.widthPixels / (float)w, ch = ginv.heightPixels / (float)h;
                if (cw <= 1f || ch <= 1f) return;

                var root = new GameObject(LockOverlayName);
                var rrt = root.AddComponent<RectTransform>();
                rrt.SetParent(parent, false);
                rrt.anchorMin = rrt.anchorMax = new Vector2(0f, 1f);
                rrt.pivot = new Vector2(0f, 1f);
                rrt.anchoredPosition = Vector2.zero;
                rrt.sizeDelta = new Vector2(w * cw, h * ch);
                rrt.SetAsLastSibling();
                for (int i = 0; i < shape.Length; i++)
                {
                    if (shape[i] != '2') continue;
                    int col = i % w, row = i / w;
                    var go = new GameObject("c" + i);
                    var rt = go.AddComponent<RectTransform>();
                    rt.SetParent(rrt, false);
                    rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                    rt.pivot = new Vector2(0f, 1f);
                    rt.anchoredPosition = new Vector2(col * cw + 1f, -(row * ch + 1f));
                    rt.sizeDelta = new Vector2(cw - 2f, ch - 2f);
                    var img = go.AddComponent<UnityEngine.UI.Image>();
                    img.color = new Color(0f, 0f, 0f, 0.55f);   // 暗色半透明: 看得出是格子, 一眼知不可用
                    img.raycastTarget = false;
                }
            }
            catch (Exception e) { PsApi.Warn(logger, $"[psui:pixel] {tag}: 锁定格 overlay 构建失败: {e.Message}"); }
        }

        // ==================== v1.19.3: 镜像格 (grid_slot mirror: <源槽id>) ====================
        // 源槽内容器物品 (背包) 的 contentWindow 借来钉在占位格位置 — 真库存零同步 (物品跟背包走,
        // 序列化在背包 item 子物品里, 与面板图无关)。元素级重挂 (Attach/SetRenderingParent) 会改
        // 图节点父系、弄乱存档 BFS 节点索引, 故只 Show/Hide + 逐帧钉位置, 元素树一行不动。

        /// <summary>镜像格逐帧差分: 源槽物品变化 → 换挂/卸载容器窗; 不变 → 钉位置 (本窗可拖动)。</summary>
        private void PollMirrors(PixelPanel pp)
        {
            if (pp.MirrorLinks.Count == 0) return;
            foreach (var kv in pp.MirrorLinks.ToArray())
            {
                string gridId = kv.Key;
                if (!pp.Slots.TryGetValue(kv.Value, out var srcSlot)) continue;
                GameItem item = null;
                try
                {
                    var items = ItemsFacade.SlotItems(srcSlot);
                    if (items != null && items.Count > 0) item = items[0];
                }
                catch { }
                IntPtr ptr = IntPtr.Zero;
                try { if (item != null) ptr = item.Pointer; } catch { item = null; }
                if (!pp.MirrorStates.TryGetValue(gridId, out var st)) { st = new MirrorState(); pp.MirrorStates[gridId] = st; }
                if (ptr != st.LastItem)
                {
                    st.LastItem = ptr;
                    DetachMirror(pp, gridId, st);
                    if (item != null) AttachMirror(pp, gridId, st, item);
                }
                if (st.Win != null) PinMirror(pp, gridId, st);
            }
        }

        private void AttachMirror(PixelPanel pp, string gridId, MirrorState st, GameItem item)
        {
            PixelWindow win = null;
            try { win = item.contentWindow; } catch { }
            if (win == null)
            {
                string id = null;
                try { id = item.identifier; } catch { }
                PsApi.Log(_logger, $"[psui:pixel] {pp.FullId}: 镜像格 '{gridId}' 源物品 {id ?? "?"} 无 contentWindow (不是容器), 跳过");
                return;
            }
            try { win.Show(); } catch (Exception e) { PsApi.Warn(_logger, $"[psui:pixel] {pp.FullId}: 镜像窗 Show 失败: {e.Message}"); return; }
            st.Win = win;
            SetPlaceholderVisible(pp, gridId, false);
            try
            {
                string id = item.identifier;
                PsApi.Log(_logger, $"[psui:pixel] {pp.FullId}: 镜像格 '{gridId}' 挂载容器窗 (物品 {id ?? "?"})");
            }
            catch { }
        }

        private void DetachMirror(PixelPanel pp, string gridId, MirrorState st)
        {
            if (st.Win != null)
            {
                try { st.Win.Hide(); } catch { }
                st.Win = null;
            }
            SetPlaceholderVisible(pp, gridId, true);
        }

        /// <summary>面板不可见时收编全部镜像窗 (机器面板关窗由原生管理, PollSlots 不跑, 镜像窗得跟着藏)。</summary>
        internal void HideMirrors(PixelPanel pp)
        {
            if (pp == null) return;
            foreach (var kv in pp.MirrorStates)
            {
                try { kv.Value.Win?.Hide(); } catch { }
                SetPlaceholderVisible(pp, kv.Key, true);
            }
        }

        /// <summary>占位格显隐 (借窗期间藏掉空格子框架, 归还后恢复)。</summary>
        private void SetPlaceholderVisible(PixelPanel pp, string gridId, bool visible)
        {
            try
            {
                if (!pp.Slots.TryGetValue(gridId, out var inv) || inv == null) return;
                var pe = inv.TryCast<PixelElement>();
                var h = pe?.handler;
                if (h == null) return;
                if (h.gameObject.activeSelf != visible) h.gameObject.SetActive(visible);
            }
            catch { }
        }

        /// <summary>把借来的容器窗钉到占位格位置 (占位格藏了但 transform 还在; 逐帧跟随面板拖动)。
        /// 被玩家 X 手动关掉的镜像窗下一帧重开 (它是面板的一部分, 不该独立关)。</summary>
        private void PinMirror(PixelPanel pp, string gridId, MirrorState st)
        {
            try
            {
                if (!st.Win.IsVisible()) st.Win.Show();
                var mrt = st.Win.rectTransform;
                if (mrt == null) return;
                if (!pp.Slots.TryGetValue(gridId, out var inv) || inv == null) return;
                var pe = inv.TryCast<PixelElement>();
                var h = pe?.handler;
                if (h == null) return;
                var anchor = h.transform.position;
                if ((mrt.position - anchor).sqrMagnitude > 4f) mrt.position = anchor;
            }
            catch { }
        }

        /// <summary>槽内容签名: "ptr:id×count;..." (逐帧差分用)。</summary>
        internal static string SlotSignature(GameInventory slot)
        {
            var items = ItemsFacade.SlotItems(slot);
            if (items.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                if (item == null) continue;
                try
                {
                    string id = null;
                    try { id = item.identifier; } catch { }
                    int count = 1;
                    try { count = item.unitCount; } catch { }
                    sb.Append(item.Pointer.ToInt64()).Append(':').Append(id ?? "?").Append('x').Append(count).Append(';');
                }
                catch { }
            }
            return sb.ToString();
        }

        /// <summary>on_change 的 value: slot → 首个物品句柄 (空 = null); grid_slot/scroll_grid_slot → 句柄列表。</summary>
        internal object SlotValue(PixelPanel pp, string slotId)
        {
            if (!pp.Slots.TryGetValue(slotId, out var slot)) return null;
            var items = ItemsFacade.SlotItems(slot);
            bool isGrid = !pp.SlotKinds.TryGetValue(slotId, out var kind) || kind != "slot";
            if (!isGrid)
                return items.Count > 0 && items[0] != null ? new PsItemHandle(items[0]) : null;
            var list = new List<object>();
            foreach (var item in items)
                if (item != null) list.Add(new PsItemHandle(item));
            return list;
        }
    }
}
