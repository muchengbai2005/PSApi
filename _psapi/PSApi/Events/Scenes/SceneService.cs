using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using MelonLoader;
using PSApi.Items;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PSApi.Events.Scenes
{
    /// <summary>
    /// 自定义外出场景服务 (搜打撤 M1) — v1.19.0 起改为 <b>直接复用原版切场景状态机</b>
    /// (Cpp2IL ISIL 指令级实证, 不再自己维护隐藏清单):
    ///   进入 = 黑屏过场 (StoreUIManager.StartBlackscreenSequence) 回调里复刻 MapUIManager.OnGoingOutside:
    ///          MapUIManager.CloseUI → PaperUIManager.CloseAllPaperUI → EmporiumEntry.CloseItemWindowUIWindow
    ///          → <b>EmporiumEntry.OnLeaveStore()</b> (原版一手包办: 藏 12 个库存窗 GameObject + 关 ~19 块面板
    ///            + 全部可见 PixelWindow.Hide 扫荡 + ClearFrontInv + storeInteractables/storePhysical 失活
    ///            + storeCreditDisplay/storeCalendar/clientCounter 失活 + ModHook 前后事件)
    ///          → 藏卷帘门按钮 (StoreShutterButton.blockerGameObject.transform.parent, 原版同款)
    ///          → <b>EmporiumEntry.ShowAfterhourInv() + TransferBackpackToInv()</b> (原版外出背包, 替代旧自建
    ///            raid GameGridInventory — 战利品直接进 EmporiumEntry.afterhourInventory)
    ///          → MapUIManager.isOutside = true → EmporiumEntry.Validate(true)
    ///          + 自建场景暗幕 + 原生图元场景窗口 (互动物按钮列)。
    ///   撤离 = 黑屏回调里复刻 MapUIManager.OnReturning:
    ///          销毁场景 UI → <b>EmporiumEntry.OnArriveStore(false)</b> (原版对称恢复, 含 visitLeftTonight-1)
    ///          → CloseAllPaperUI/CloseItemWindowUIWindow → <b>HideAfterhourInv + TransferAfterhourToInv</b>
    ///            (外出背包物品原版自动转回玩家背包) → isOutside=false → 卷帘门恢复 → PlayStoreAmbient。
    ///   旧版手工清单 (_hiddenRoots/_hiddenWindows/BlockerMask/世界TMP) 全部删除 — 窗口位置丢失/相机错位
    ///   的根因就是绕过原版自己 Show/Hide; OnLeaveStore/OnArriveStore 走 GameObject 失活路径不动窗口位置。
    ///   SweepLeftovers 仅做体检: OnLeaveStore 后若 BlockerMask/世界TMP 仍激活才补刀并记录 (正常为空)。
    /// v1.33.0 (波 3, 用户拍板): builtin_raid 驱动退役 — C# raid 服务/状态 POCO 删除,
    ///   场景内循环只剩 script 一套 (pss 经能力 API 驱动; raid 玩法 = scenes/_raid pss 库)。
    ///   driver 合法值: script (缺省一律推断 script) / picker。
    ///   包布局双结构: 旧式扁平 scenes/&lt;id&gt;.json + 新式文件夹 scenes/&lt;id&gt;/scene.json
    ///   (ui/scripts 子目录由 PsUiService/PsScriptEngine 同管线递归扫描)。
    /// v1.32.0 场景导航原语 + 选图面板场景化 (波 2, 用户拍板):
    ///   scene.json "entry" 扩展 "hidden" (不注入不进 picker 列表, 可 scenes.enter 进入, 修死场景);
    ///   场景栈 (_navStack, 元素=场景短 id 或 "map") + scenes.enter/back/leave(reason)/list 原语;
    ///   第三驱动 driver=picker — 选图面板成为注册场景 (轻量壳: 不黑屏不离店, 藏 mapPanel+选图 UI;
    ///   全注册表唯一, 无包注册时框架内置 psapi:picker), 入口按钮/面板标题读 picker 场景 name;
    ///   读档自动重进壳层 (SaveStates("scenes") "active" 键, PollRestoreScene)。
    /// 事件: psapi.scene.enter / psapi.scene.leave / psapi.scene.interact / psapi.scene.loot
    ///   (payload: scene/name/danger/point/type/item/count/day; 两种驱动都发 enter/leave)。
    ///   战斗预留: psapi.scene.combat.* (无实现)。
    /// 安全: 场景搜索状态仅内存 (当天有效), 不落盘。
    /// </summary>
    internal sealed class SceneService
    {
        private readonly MelonLogger.Instance _logger;
        private readonly EventBus _bus;
        private readonly PsUI.PsUiService _ui;   // M1.5: 场景 UI psui 驱动 (BuildSceneWindow), 可空 = 仅自动网格
        /// <summary>P2 (v1.27.0): 场景 uGUI 创建服务 (ui.panel/text/button/bar 后端; Plugin 接线, 无头为 null)。
        /// 生命周期: DoEnter 防御清残留 / DoExit 自动销毁该场景整组 / OnSceneLeft 清引用。</summary>
        internal UI.SceneUguiService SceneUgui;
        /// <summary>P3 (v1.28.0): script 场景能力服务 (grid.*/combat.*/scene.log 后端; Plugin 接线, 无头为 null)。
        /// 生命周期同 SceneUgui: DoEnter 防御清理 / DoExit 销毁容器+地面遗留并恢复外出栏 / OnSceneLeft 清引用;
        /// Poll 驱动容器 X 关窗检测与窗口健康。</summary>
        internal ScriptGridService ScriptGrid;
        internal ScriptCombatService ScriptCombat;
        internal SceneLogService SceneLog;
        /// <summary>v1.29.0: 实时动作战斗会话服务 (combat.rt_start 后端; Plugin 接线, 无头为 null)。
        /// 生命周期: DoEnter 防御清理 / DoExit 中止战斗(abort, 不回调) / OnSceneLeft 清引用+光标兜底。</summary>
        internal RtCombatService RtCombat;
        private readonly Dictionary<string, SceneDef> _scenes = new Dictionary<string, SceneDef>(StringComparer.Ordinal);
        private readonly List<object> _pins = new List<object>();
        private readonly System.Random _rng = new System.Random();

        // ---- 运行期状态 ----
        internal SceneDef Active { get; private set; }
        private GameObject _dimGo;                // 场景暗幕 (纯视觉, 不吃点击)
        private PixelWindow _sceneWindow;         // 原生图元场景窗口 (互动物按钮列)
        private GameObject _titleHud;             // v1.30.0: 无互动物场景的左上角标题 uGUI 根 (替代图元标题窗)
        private TextMeshProUGUI _statusUgui;      // v1.30.0: 标题面板状态行 (空=不渲染, 无 v1.29.1 小白块)
        private GameObject _pickerHud;            // v1.24.0: 独立选图场景 uGUI 根 (canvas+暗幕+面板, 唯一引用)
        // ---- v1.37.0: picker 双栏改版选中态 (关闭/卸载随 _pickerHud 同点复位) ----
        private string _pickerSelected;           // 选中场景 FullId (null=未选中 → 「开始搜索」置灰)
        private TextMeshProUGUI _pickerDetailTitle, _pickerDetailBody;   // 右栏详情 (选中即刷新)
        private Button _pickerStartBtn;
        private readonly List<KeyValuePair<string, Image>> _pickerRowImgs =
            new List<KeyValuePair<string, Image>>();   // 左栏行按钮底色 (选中高亮; FullId → Image)
        private float _pickerToggleGuardUntil;    // v1.21.1: 入口按钮双触发去抖 (实测单次点击 onClick 派发 2 次, 间隔 ~0.19s = 按下/抬起)
        private UI.UguiBuilder _ugui;             // v1.24.0: uGUI 基础设施 (选图场景; 懒初始化)
        private TagElement _statusTag;            // 状态行
        private readonly Dictionary<string, ButtonElement> _pointButtons = new Dictionary<string, ButtonElement>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, int>> _used =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);  // "day|scene" → point → 已搜次数
        private readonly HashSet<int> _injectedMaps = new HashSet<int>();          // 已注入按钮的 mapPanel 实例
        /// <summary>v1.19.4: mapPanel 实例 → 注入前面板高度 (自愈重注入时先归位, 防重复 +72)。</summary>
        private readonly Dictionary<int, float> _panelBaseH = new Dictionary<int, float>();
        private float _mapHealthTimer;
        private readonly List<GameObject> _extraHidden = new List<GameObject>();   // SweepLeftovers 补刀记录 (正常为空)
        /// <summary>v1.32.0: 场景导航栈 (元素 = 场景短 id 或 SceneNav.MapToken "map") —
        /// scenes.enter 压返回点 / scenes.back 弹栈返回 / scenes.leave 清空。仅内存, 读档不恢复。</summary>
        private readonly List<string> _navStack = new List<string>();
        /// <summary>v1.32.0: DoEnter 进行中 (黑屏过场窗口期 Active 仍为 null) 重入守卫。</summary>
        private bool _enterInFlight;
        /// <summary>v1.32.0: 读档自动重进 — 每次回商店只尝试一次 (OnSceneLeft 复位)。</summary>
        private bool _restoreChecked;
        /// <summary>v1.32.0: 生效的 picker 场景 (选图面板场景化; ScanPacks 填充 — 包注册先加载者,
        /// 全注册表唯一; 无包注册时框架内置 psapi:picker)。</summary>
        internal SceneDef PickerScene { get; private set; }

        internal SceneService(MelonLogger.Instance logger, EventBus bus, PsUI.PsUiService ui = null)
        {
            _logger = logger;
            _bus = bus;
            _ui = ui;
        }

        internal int SceneCount => _scenes.Count;

        // ==================== 包加载 ====================

        internal int ScanPacks(List<PackInfo> packs)
        {
            _scenes.Clear();
            PickerScene = null;
            int errors = 0;
            foreach (var pack in packs)
            {
                if (!pack.Valid) continue;
                if (!pack.Source.HasDir("scenes")) continue;
                foreach (var file in pack.Source.ListFiles("scenes", ".json"))
                {
                    // P1 (v1.26.0): 双结构并存 — 旧式扁平 scenes/<id>.json; 新式文件夹
                    // scenes/<id>/scene.json (资源相对场景文件夹: ui/scripts 子目录同管线路由)。
                    // ListFiles 是递归的, 其余布局 (更深层级/文件夹内别名 json) 不认, 告警跳过。
                    string rest = file.Substring("scenes/".Length);
                    // v1.28.1 (用户拍板): scenes/ 下名字以 "_" 开头的文件夹 = 库目录 (公用玩法
                    // 脚本/资源, 如 _raid 搜打撤系统库), 不注册为场景 — 静默跳过, 连布局告警都不打。
                    // 脚本管线 (scenes/**/*.pss) 照常递归覆盖库目录, 不受此排除影响。
                    int firstSlash = rest.IndexOf('/');
                    if (firstSlash > 0 && rest[0] == '_') continue;
                    // v1.31.0: 布局文件不是场景 — 包级 scenes/layout.json 与场景级 scenes/<id>/layout.json
                    // (M 键写回会创建后者), 静默跳过 (走 RaidLayout 三层覆写管线, 不注册不告警)
                    if (string.Equals(rest, "layout.json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (firstSlash > 0 && rest.EndsWith("/layout.json", StringComparison.OrdinalIgnoreCase)
                        && rest.IndexOf('/') == rest.LastIndexOf('/')) continue;
                    bool legacyFlat = !rest.Contains("/");
                    bool folderScene = !legacyFlat
                        && rest.EndsWith("/scene.json", StringComparison.OrdinalIgnoreCase)
                        && rest.IndexOf('/') == rest.LastIndexOf('/');
                    if (!legacyFlat && !folderScene)
                    {
                        errors++;
                        PsApi.Warn(_logger, $"[scenes] 无法识别的场景文件布局 '{pack.Id}/{file}' (支持 scenes/<id>.json 或 scenes/<id>/scene.json), 跳过");
                        continue;
                    }
                    string rel = $"{pack.Id}/{file}";
                    SceneDef def;
                    try { def = SceneJson.Parse(rel, pack.Source.ReadText(file)); }
                    catch (Exception e) { errors++; PsApi.Warn(_logger, "[scenes] " + e.Message); continue; }
                    def.PackId = pack.Id;
                    // v1.32.0: 场景文件夹 scripts/ 子目录探测 (picker 包自绘判定用; 扁平结构恒 false)
                    if (folderScene)
                    {
                        string sceneDir = file.Substring(0, file.Length - "/scene.json".Length);
                        try { def.HasScripts = pack.Source.HasDir(sceneDir + "/scripts"); } catch { }
                    }
                    if (def.DriverInferred)
                        PsApi.Log(_logger, $"[scenes] 场景 {def.FullId} 未声明 driver, 按内容推断为 {def.Driver}");
                    // v1.32.0: picker 场景全注册表唯一生效 — 先加载者胜, 多余告警跳过 (不算错误)
                    if (def.IsPicker && PickerScene != null)
                    {
                        PsApi.Warn(_logger, $"[scenes] picker 场景唯一生效: '{def.FullId}' ({rel}) 跳过, 已生效 '{PickerScene.FullId}' (先加载者)");
                        continue;
                    }
                    if (_scenes.ContainsKey(def.FullId))
                    {
                        errors++;
                        PsApi.Warn(_logger, $"[scenes] 场景 id 冲突 '{def.FullId}' ({rel}), 后者跳过");
                        continue;
                    }
                    _scenes[def.FullId] = def;
                    if (def.IsPicker) PickerScene = def;
                }
            }
            // v1.32.0: 框架内置默认 picker — 无任何包注册 driver=picker 场景时自动注册
            // (id psapi:picker, name=外出拾荒, driver=picker, entry=hidden — 自身不进选图列表)
            if (PickerScene == null)
            {
                var builtin = new SceneDef
                {
                    PackId = "psapi",
                    ShortId = "picker",
                    Name = "外出拾荒",
                    Entry = "hidden",
                    Driver = "picker",
                    Danger = 1,
                    File = "<builtin>",
                };
                _scenes[builtin.FullId] = builtin;
                PickerScene = builtin;
                PsApi.Log(_logger, "[scenes] 无包注册 picker 场景, 已注册框架内置 psapi:picker (外出拾荒)");
            }
            return errors;
        }

        internal IEnumerable<SceneDef> All => _scenes.Values;

        // ==================== 地图按钮注入 (SceneMapPatch 调用) ====================

        /// <summary>离开店铺窗口结构 (F9 dump 实证): _Map > Image(窗口面板) > Image(HorizontalLayoutGroup
        /// 按钮行) > Inventor/SuperMarket/Scavenging 三个 uGUI Button。旧实现把按钮塞进原版行当第 4 个
        /// → 窗口定宽溢出到框外; 且自设深色无精灵+拉丁字体 → 黑条无文字。
        /// v1.21.0 (用户拍板): 单入口 — 不再每场景一个按钮, 只注入 1 个「外出拾荒」入口按钮
        /// (自建 "PSApiSceneRow_0" 第二行的第一个, 复制原行锚定/尺寸/HLG 设置, 窗口面板加高一行),
        /// 点击在地图面板右侧弹出模组选图面板 (OpenPicker, 由场景注册表自动生成)。注册表无场景
        /// → 不注入不加高。按钮视觉 (Image 精灵/颜色/尺寸) 与文字 (字体/字号/颜色/对齐) 全部从原版
        /// 模板按钮取样 — 中文字形由模板字体保证 (垃圾倾倒场同款)。
        /// 按钮本体仍自建零继承 (不克隆 — 克隆曾连原版持久化监听一起复制致双触发)。</summary>
        internal void InjectMapButtons(MapUIManager mgr)
        {
            if (mgr == null) return;
            GameObject panel = null;
            try { panel = mgr.mapPanel; } catch { }
            if (panel == null) return;
            int key;
            try { key = panel.GetInstanceID(); } catch { return; }
            if (_injectedMaps.Contains(key)) return;

            GameObject template = null;
            try { template = mgr.dumpingGroundButton; } catch { }
            if (template == null) { PsApi.Warn(_logger, "[scenes] 地图模板按钮 (dumpingGroundButton) 为空, 注入跳过"); return; }

            var mapScenes = new List<SceneDef>();
            foreach (var def in _scenes.Values)
                if (def.Entry == "map") mapScenes.Add(def);
            if (mapScenes.Count == 0) return;

            // 模板视觉取样
            Image tImg = null; TMP_Text tTmp = null; RectTransform tRt = null;
            try { tImg = template.GetComponent<Image>(); } catch { }
            try { tTmp = template.GetComponentInChildren<TMP_Text>(true); } catch { }
            // Il2Cpp 红线: C# as/cast 对 interop 包装必败 (继承被压平), 只能 TryCast 走原生类型检查
            try { tRt = template.transform.TryCast<RectTransform>(); } catch { }
            if (tImg == null) PsApi.Warn(_logger, "[scenes] 模板按钮无 Image, 按钮用保底样式");
            if (tTmp == null) PsApi.Warn(_logger, "[scenes] 模板按钮无 TMP, 文字字体用保底链");

            // 行容器 (HorizontalLayoutGroup) 与窗口面板
            Transform row = null;
            try { row = template.transform.parent; } catch { }
            if (row == null) return;
            Transform panelT = row.parent;
            if (panelT == null) return;
            var rowRt = row.TryCast<RectTransform>();
            var panelRt = panelT.TryCast<RectTransform>();
            HorizontalLayoutGroup rowHlg = null;
            try { rowHlg = row.GetComponent<HorizontalLayoutGroup>(); } catch { }

            // v1.21.0 (用户拍板): 单入口 — 流式布局机制保留 (左缘对齐/行高/面板加高), 但只放 1 个
            // 「外出拾荒」入口按钮 (原 per-row 布局第一行第一个, 即废弃军械库位), 点击切换选图面板。
            // 左缘对齐: 原行 HLG MiddleCenter, 内容左缘 = (行宽-内容总宽)/2 → 我们行的 padding.left。
            const float rowGap = 12f;
            float spacing = 2f;
            try { if (rowHlg != null) spacing = rowHlg.spacing; } catch { }
            int origCount = 3;
            try { origCount = Math.Max(1, row.childCount); } catch { }
            float origContentW = spacing * Math.Max(0, origCount - 1);
            for (int i = 0; i < origCount; i++)
            {
                try
                {
                    var crt = row.GetChild(i).TryCast<RectTransform>();
                    if (crt != null) origContentW += crt.sizeDelta.x;
                }
                catch { }
            }
            float rowW = 0f;
            try { if (rowRt != null && rowRt.rect.width > 1f) rowW = rowRt.rect.width; } catch { }
            float leftPad = rowW > origContentW ? (rowW - origContentW) / 2f : 0f;
            float rowH = 60f;
            try { if (rowRt != null && rowRt.rect.height > 1f) rowH = rowRt.rect.height; } catch { }

            var myRow = new GameObject("PSApiSceneRow_0");
            var myRt = myRow.AddComponent<RectTransform>();
            myRt.SetParent(panelT, false);
            if (rowRt != null)
            {
                myRt.anchorMin = rowRt.anchorMin;
                myRt.anchorMax = rowRt.anchorMax;
                myRt.pivot = rowRt.pivot;
                myRt.sizeDelta = rowRt.sizeDelta;
                myRt.anchoredPosition = rowRt.anchoredPosition + new Vector2(0f, -(rowH + rowGap));
            }
            var myHlg = myRow.AddComponent<HorizontalLayoutGroup>();
            if (rowHlg != null)
            {
                try
                {
                    myHlg.spacing = rowHlg.spacing;
                    var p = rowHlg.padding;
                    if (p != null) myHlg.padding = new RectOffset(p.left, p.right, p.top, p.bottom);
                    myHlg.childControlWidth = rowHlg.childControlWidth;
                    myHlg.childControlHeight = rowHlg.childControlHeight;
                    myHlg.childForceExpandWidth = rowHlg.childForceExpandWidth;
                    myHlg.childForceExpandHeight = rowHlg.childForceExpandHeight;
                    myHlg.childScaleWidth = rowHlg.childScaleWidth;
                    myHlg.childScaleHeight = rowHlg.childScaleHeight;
                }
                catch { }
            }
            // 左对齐 + 左缘缩进对齐杰克逊博士 (不抄原行的 MiddleCenter)
            try { myHlg.childAlignment = TextAnchor.MiddleLeft; } catch { }
            try
            {
                var p = myHlg.padding;
                myHlg.padding = new RectOffset((int)leftPad, p != null ? p.right : 0, p != null ? p.top : 0, p != null ? p.bottom : 0);
            }
            catch { }

            bool added = false;
            // v1.32.0: 入口按钮文字读生效 picker 场景的 name (破除「外出拾荒」写死)
            string pickerLabel = PickerScene != null && !string.IsNullOrEmpty(PickerScene.Name)
                ? PickerScene.Name : "外出拾荒";
            try
            {
                added = AddMapButton(myRt, tImg, tTmp, tRt, "PSApiSceneBtn_Picker", pickerLabel, TogglePicker);
            }
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] 地图入口按钮注入失败: " + e.Message); }
            if (added)
            {
                // 窗口面板加高一行 (v1.19.4: 自愈重注入时先归位到基线, 防重复加高)
                try
                {
                    if (panelRt != null)
                    {
                        float baseH;
                        if (!_panelBaseH.TryGetValue(key, out baseH))
                        {
                            baseH = panelRt.sizeDelta.y;
                            _panelBaseH[key] = baseH;
                        }
                        panelRt.sizeDelta = new Vector2(panelRt.sizeDelta.x, baseH + (rowH + rowGap));
                    }
                }
                catch { }
                _injectedMaps.Add(key);
                PsApi.Log(_logger, $"[scenes] 地图注入 picker 入口按钮 '{pickerLabel}' (场景数 {mapScenes.Count}, 面板加高 {rowH + rowGap:0}px)");
            }
            else
            {
                try { UnityEngine.Object.Destroy(myRow); } catch { }
            }
        }

        /// <summary>自建 uGUI 按钮 (视觉/文字全部从原版模板取样, 行为零继承)。</summary>
        private bool AddMapButton(Transform parent, Image tImg, TMP_Text tTmp, RectTransform tRt, string btnName, string label, System.Action onClick)
        {
            var go = new GameObject(btnName);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            try { rt.sizeDelta = tRt != null ? tRt.sizeDelta : new Vector2(190f, 44f); } catch { }
            var img = go.AddComponent<Image>();
            if (tImg != null)
            {
                try { img.sprite = tImg.sprite; } catch { }
                try { img.color = tImg.color; } catch { }
                try { img.type = tImg.type; } catch { }
                try { img.pixelsPerUnitMultiplier = tImg.pixelsPerUnitMultiplier; } catch { }
            }
            else img.color = new Color(0.16f, 0.10f, 0.08f, 0.95f);
            var btn = go.AddComponent<Button>();

            var tgo = new GameObject("txt");
            var trt = tgo.AddComponent<RectTransform>();
            trt.SetParent(go.transform, false);
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            var tmp = tgo.AddComponent<TextMeshProUGUI>();
            tmp.raycastTarget = false;
            // v1.19.3: 先抄字体再设文本 (旧顺序文本先用默认字体排版, 中文可能已定型失败);
            // 连自动字号三件套一起抄 — F12 实证模板按钮 enableAutoSizing, 只抄 fontSize 会冻住
            // 注入瞬间的计算值 (19.15 vs 布局稳定后的 23), 按钮显得更小。
            if (tTmp != null)
            {
                try { tmp.font = tTmp.font; } catch (Exception e) { PsApi.Warn(_logger, "[scenes] 字体取样失败: " + e.Message); }
                try { tmp.fontSharedMaterial = tTmp.fontSharedMaterial; } catch { }
                try { tmp.enableAutoSizing = tTmp.enableAutoSizing; } catch { }
                try { tmp.fontSizeMin = tTmp.fontSizeMin; } catch { }
                try { tmp.fontSizeMax = tTmp.fontSizeMax; } catch { }
                try { tmp.fontSize = tTmp.fontSize; } catch { }
                try { tmp.color = tTmp.color; } catch { }
                try { tmp.alignment = tTmp.alignment; } catch { }
            }
            if (tmp.font == null)
            {
                try { tmp.font = TMP_Settings.defaultFontAsset; } catch { }
                tmp.fontSize = 20;
                tmp.alignment = TextAlignmentOptions.Center;
            }
            // 验证日志: 字体链路出问题时下轮一眼定位 (模板 vs 实得)
            try
            {
                PsApi.Log(_logger, $"[scenes] 按钮 '{btnName}' 字体: 模板={(tTmp != null && tTmp.font != null ? tTmp.font.name : "null")} auto={(tTmp != null && tTmp.enableAutoSizing)} → 实得={(tmp.font != null ? tmp.font.name : "null")} size={tmp.fontSize}");
            }
            catch { }
            tmp.text = label;

            var uact = DelegateSupport.ConvertDelegate<UnityAction>(onClick);
            _pins.Add(onClick); _pins.Add(uact);
            btn.onClick.AddListener(uact);
            return true;
        }

        /// <summary>v1.19.4: 地图按钮健康检查 (Poll 节流调用) —
        /// ① PSApiSceneRow 被面板重建销毁 → 摘 key 自愈重注入;
        /// ② 字体同步: 中文化 mod 在 OpenUI 后换字体的时点不定, 我们按钮的 TMP 跟着模板当前字体走
        ///    (字体/材质/自动字号三件套; 字号不同步差 >0.5 才写, 防每帧写引发 TMP 重排)。</summary>
        private void MapButtonsHealth() => MapButtonsHealth(false);

        private void MapButtonsHealth(bool heartbeat)
        {
            MapUIManager mgr = null;
            try { mgr = MapUIManager.Instance; } catch (Exception e) { if (heartbeat) PsApi.Log(_logger, "[scenes] 健康心跳: Instance 异常 " + e.Message); }
            if (mgr == null) { if (heartbeat) PsApi.Log(_logger, "[scenes] 健康心跳: MapUIManager.Instance=null"); return; }
            GameObject panel = null;
            try { panel = mgr.mapPanel; } catch { }
            if (panel == null) { if (heartbeat) PsApi.Log(_logger, "[scenes] 健康心跳: mapPanel=null"); return; }
            bool panelActive;
            try { panelActive = panel.activeInHierarchy; } catch { return; }
            if (!panelActive)
            {
                _mapHealthDiagDone = false;
                if (heartbeat) PsApi.Log(_logger, "[scenes] 健康心跳: 地图未开 (panel 不活跃)");
                return;
            }
            GameObject template = null;
            try { template = mgr.dumpingGroundButton; } catch { }
            if (template == null) { MapHealthDiag("dumpingGroundButton 为空"); return; }
            Transform row = null;
            try { row = template.transform.parent; } catch { }
            Transform panelT = row == null ? null : row.parent;
            if (panelT == null) { if (heartbeat) PsApi.Log(_logger, "[scenes] 健康心跳: panelT=null"); return; }
            int key;
            try { key = panel.GetInstanceID(); } catch { return; }

            // v1.19.9: 流式多行 — 行名前缀 "PSApiSceneRow_" (一排满自动换行), 逐行收集
            var rows = new List<Transform>();
            try
            {
                for (int i = 0; i < panelT.childCount; i++)
                {
                    var c = panelT.GetChild(i);
                    if (c != null && c.name.StartsWith("PSApiSceneRow", StringComparison.Ordinal)) rows.Add(c);
                }
            }
            catch (Exception e) { if (heartbeat) PsApi.Log(_logger, "[scenes] 健康心跳: 行收集异常 " + e.Message); }
            if (rows.Count == 0)
            {
                if (heartbeat) PsApi.Log(_logger, $"[scenes] 健康心跳: 地图开着但 PSApiSceneRow 未找到 (injected={_injectedMaps.Contains(key)})");
                MapHealthDiag($"PSApiSceneRow 未找到 (injected={_injectedMaps.Contains(key)})");
                if (_injectedMaps.Contains(key))
                {
                    _injectedMaps.Remove(key);
                    PsApi.Log(_logger, "[scenes] 地图自定义行丢失 (面板被重建?), 自愈重注入");
                    try { InjectMapButtons(mgr); } catch { }
                }
                return;
            }

            TMP_Text tTmp = null;
            try { tTmp = template.GetComponentInChildren<TMP_Text>(true); } catch { }
            if (tTmp == null || tTmp.font == null) { MapHealthDiag("模板 TMP/字体为空, 同步跳过"); return; }
            int idx = 0;
            foreach (var myRow in rows)
            {
                for (int ci = 0; ci < myRow.childCount; ci++)
                {
                    idx++;
                    Transform child = null;
                    try { child = myRow.GetChild(ci); } catch { }
                    if (child == null) continue;
                    TMP_Text tmp = null;
                    // 注意: 用 GameObject 版 GetComponentInChildren (模板同款已实证可用);
                    // Component(Transform) 版在 interop 下取不到我们自己 AddComponent 的 TMP
                    try { tmp = child.gameObject.GetComponentInChildren<TMP_Text>(true); } catch { }
                    if (tmp == null) continue;
                    if (heartbeat && idx == 1)
                    {
                        string bf = null;
                        try { bf = tmp.font != null ? tmp.font.name : "null"; } catch { }
                        PsApi.Log(_logger, $"[scenes] 健康心跳: 模板={tTmp.font.name} 按钮={bf ?? "?"} 行数={rows.Count}");
                    }
                    string beforeName = null;
                    try { beforeName = tmp.font != null ? tmp.font.name : "null"; } catch { }
                    bool needSync = false;
                    try { needSync = tmp.font != tTmp.font; }
                    catch (Exception e) { MapHealthDiag($"字体比较有异常: {e.GetType().Name}: {e.Message}"); }
                    MapHealthDiag($"btn{idx} 字体={beforeName ?? "?"} 模板={tTmp.font.name} 需同步={needSync}");
                    if (!needSync) continue;
                    try
                    {
                        tmp.font = tTmp.font;
                        try { tmp.fontSharedMaterial = tTmp.fontSharedMaterial; } catch { }
                        PsApi.Log(_logger, $"[scenes] 地图按钮字体同步 → {tTmp.font.name}");
                    }
                    catch (Exception e) { PsApi.Warn(_logger, $"[scenes] 字体同步写入失败: {e.GetType().Name}: {e.Message}"); }
                    try
                    {
                        bool auto = tTmp.enableAutoSizing;
                        if (auto)
                        {
                            if (!tmp.enableAutoSizing) tmp.enableAutoSizing = true;
                            tmp.fontSizeMin = tTmp.fontSizeMin; tmp.fontSizeMax = tTmp.fontSizeMax;
                        }
                        else if (System.Math.Abs(tmp.fontSize - tTmp.fontSize) > 0.5f) tmp.fontSize = tTmp.fontSize;
                    }
                    catch { }
                }
            }
        }

        /// <summary>v1.19.5: 健康检查诊断 — 每次地图打开只打第一趟 (防刷屏), 地图关闭后重置。</summary>
        private void MapHealthDiag(string msg)
        {
            if (_mapHealthDiagDone) return;
            _mapHealthDiagDone = true;
            PsApi.Log(_logger, $"[scenes] 地图健康诊断: {msg}");
        }
        private bool _mapHealthDiagDone;
        private float _mapHbTimer;

        // ==================== 选图场景 (v1.21.2) ====================

        /// <summary>选图面板条目 — 解锁态预留三元组: 构建器只组数据 (pss_test 可测), 渲染按 Enabled 分流。</summary>
        internal sealed class ScenePickEntry
        {
            internal SceneDef Def;
            internal bool Enabled;
            internal string LockReason;   // Enabled=false 时的锁定原因 (展示用)
        }

        /// <summary>选图面板数据组装 (纯逻辑): 按注册顺序逐项; lockRule 返回 null=解锁, 否则返回锁定
        /// 原因串。v1.47.0: scenes.set_gate 门禁经此接入 (OpenPicker 传 gateReasons 查表 rule; hide 在调用方剔除)。</summary>
        internal static List<ScenePickEntry> BuildPickEntries(IEnumerable<SceneDef> scenes, Func<SceneDef, string> lockRule = null)
        {
            var list = new List<ScenePickEntry>();
            foreach (var def in scenes)
            {
                if (def == null) continue;
                string reason = lockRule == null ? null : lockRule(def);
                list.Add(new ScenePickEntry { Def = def, Enabled = reason == null, LockReason = reason });
            }
            return list;
        }

        /// <summary>入口按钮点击 (v1.32.0 picker 场景化): 当前场景 = picker → scenes.back() 返回;
        /// 非场景态 → scenes.enter(picker) 进入 (栈压 "map")。toggle 语义保留 (再点入口 = back)。
        /// v1.21.1: 0.3s 去抖 — 游戏内实测单次物理点击 onClick 被派发两次 (间隔 ~0.19s, 按下/抬起
        /// 各一次; 旧版场景按钮被 TryEnter 的 Active 守卫掩盖, 翻转语义下表现为闪现即关)。</summary>
        private void TogglePicker()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (now < _pickerToggleGuardUntil)
            {
                PsApi.Log(_logger, $"[scenes] 重复 toggle 已忽略 (去抖 0.3s, 距上次 {now - (_pickerToggleGuardUntil - 0.3f):0.00}s)");
                return;
            }
            _pickerToggleGuardUntil = now + 0.3f;
            var picker = PickerScene;
            if (picker == null) { PsApi.Warn(_logger, "[scenes] 无生效 picker 场景 (注册表异常), 入口无响应"); return; }
            if (Active == picker)
            {
                PsApi.Log(_logger, "[scenes] 入口按钮 toggle → scenes.back() 返回");
                BackScene();
            }
            else if (Active == null)
            {
                PsApi.Log(_logger, $"[scenes] 入口按钮 toggle → 进入 picker 场景 {picker.FullId}");
                EnterScene(picker.FullId);
            }
            // 在别的场景内时入口按钮不可达 (地图已关), 防御 no-op
        }

        /// <summary>v1.21.2 (用户拍板): 独立选图场景 — 嵌入地图面板右侧两轮失败 (钉出屏幕/被盖住),
        /// 改为视觉层场景: 隐藏原版 mapPanel + 全屏暗幕 + 居中选图面板。
        /// v1.24.0 uGUI 化 (方案 C, 用户拍板): 图元 PixelWindow 选图窗 → 单根 uGUI canvas
        /// (UguiBuilder): 全屏暗幕 Image (raycastTarget=true 模态吞杂点) + 居中面板 (标题 TMP +
        /// 每场景 uGUI 按钮 + 危险度★/desc 灰字 + 底部「返回地图」按钮)。层级: canvas order =
        /// 原版图元窗 canvas (FloatingWindowCanvas, 运行时探测+日志钉死, 探测失败回退 3) —
        /// 即旧图元窗所在的层; 商店 UI 在其下, mapPanel 已藏无竞争者。layout.canvas_order > 0
        /// 可强制指定。不离开商店: 不调 OnGoingOutside/OnLeaveStore/CloseUI, 游戏地图状态保持"开"
        /// (我们只 SetActive(false) 了面板), 商店根/BGM/visitLeftTonight 全不动。
        /// 数据由 BuildPickEntries 从场景注册表自动生成 (锁定态渲染路径保留 = 灰字不可点 + 原因)。
        /// v1.37.0 双栏改版 (用户拍板): 左栏 ScrollRect 场景列表 (行=名字+★, 点击=选中高亮不进场) +
        /// 右栏详情面板 (名字/★/desc/loot_hint) + 底部「开始搜索」(未选中置灰) /「返回地图」;
        /// 布局新键 picker.list_w/detail_w/detail_font/rows (旧键保留兼容)。
        /// v1.32.0: 本方法成为 picker 场景的<b>默认渲染</b> (由 EnterPickerShell 调用, Active 已是 picker
        /// 场景; 包自绘 picker 不建)。点场景按钮 → scenes.enter (picker id 入栈) → 目标场景现有全流程;
        /// 「返回地图」与 Esc → scenes.back()。
        /// Il2Cpp 红线: TryCast / 委托双 pin 进 _pins; 按钮 onClick UguiBuilder 统一 0.3s 去抖。</summary>
        private void OpenPicker()
        {
            if (_pickerHud is not null) return;   // 防叠窗 (双派发第二次被吞)
            GameObject mapPanel = null;
            try { var m = MapUIManager.Instance; if (m != null) mapPanel = m.mapPanel; } catch { }
            if (mapPanel == null) { PsApi.Warn(_logger, "[scenes] 选图场景: mapPanel 为空, 展开跳过"); return; }

            var mapScenes = new List<SceneDef>();
            var gateReasons = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var def in _scenes.Values)
            {
                if (def.Entry != "map") continue;
                // v1.47.0: scenes.set_gate 门禁 — "hide" 前缀 = 条目剔除; 其他字符串 = 锁定原因 (灰字不可点)
                string g = SceneGateState.Eval(def.FullId);
                if (SceneGateState.IsHidden(g)) continue;
                mapScenes.Add(def);
                if (g != null) gateReasons[def.FullId] = g;
            }
            if (mapScenes.Count == 0) return;
            var entries = BuildPickEntries(mapScenes,
                def => gateReasons.TryGetValue(def.FullId, out var r) ? r : null);

            if (_ugui == null) _ugui = new UI.UguiBuilder(_logger, _pins);
            // v1.31.0: picker 区无场景上下文 = 代码默认 + 全部包级 layout.json 按包加载序合并 (后者胜)
            var L = PSApi.Events.Raid.RaidLayout.ResolvePickerLayout(w => PsApi.Warn(_logger, w));
            var P = L.Picker;
            float titleFont = PSApi.Events.Raid.RaidLayout.ClampFont(P.TitleFont);
            float font = PSApi.Events.Raid.RaidLayout.ClampFont(P.Font);

            // 层级铁律: 运行时探测图元窗 canvas 层级 (v1.21.3 实证 = FloatingWindowCanvas order=3),
            // 我们的 uGUI canvas 用同一层 (商店 UI 在其下, mapPanel 已藏) — 探测失败回退 3。
            int pixelOrder = _ugui.DetectPixelCanvasOrder(3, "选图场景");
            int order = L.CanvasOrder > 0 ? L.CanvasOrder : pixelOrder;
            _pickerHud = _ugui.MakeCanvas("PSApiPickerHud", order);
            if (_pickerHud is null) { PsApi.Warn(_logger, "[scenes] 选图场景: canvas 创建失败"); return; }
            var root = _pickerHud.transform.TryCast<RectTransform>();   // Il2Cpp 红线: 不用 as
            if (root == null)
            {
                PsApi.Warn(_logger, "[scenes] 选图场景: canvas RectTransform 获取失败");
                ClosePickerScene("open-fail", true);
                return;
            }

            // ① 全屏暗幕 (模态吞杂点)
            var dim = _ugui.MakePanel(root, "dim", 0, 0, new Color(0.04f, 0.04f, 0.07f, 1f), true);
            dim.anchorMin = Vector2.zero;
            dim.anchorMax = Vector2.one;
            dim.offsetMin = Vector2.zero;
            dim.offsetMax = Vector2.zero;

            // ② 居中面板 — v1.37.0 双栏改版: 顶部标题 / 左栏 ScrollRect 场景列表 (每行按钮 = 名字+危险度★,
            // 点击 = 选中高亮不进场) / 右栏详情面板 (标题+★+desc+loot_hint) / 底部「开始搜索」(未选中置灰)
            // +「返回地图」。行按钮交互仍走 UguiBuilder.MakeButton 内置 0.3s 去抖。
            float pad = P.Spacing + 6f;
            float titleH = titleFont + 10f;
            float detailFont = PSApi.Events.Raid.RaidLayout.ClampFont(P.DetailFont);
            float listW = P.ListW, detailW = P.DetailW;
            int visRows = Math.Max(1, P.Rows);
            float rowPitch = P.BtnH + P.Spacing;
            float bodyH = visRows * rowPitch;
            float panelW = pad * 3f + listW + detailW;
            float panelH = pad + titleH + bodyH + pad + P.BtnH + pad;
            var panel = _ugui.MakePanel(root, "panel", panelW, panelH, new Color(0.07f, 0.07f, 0.10f, 0.97f), true);
            UI.UguiBuilder.AnchorCenter(panel, root, 0.5f, 0.5f);

            // v1.32.0: 面板标题读生效 picker 场景 name (与入口按钮同源)
            string pickerName = Active != null && Active.IsPicker && !string.IsNullOrEmpty(Active.Name)
                ? Active.Name : "外出拾荒";
            var title = _ugui.MakeText(panel, "title", pickerName, titleFont,
                new Color(0.95f, 0.88f, 0.72f), Il2CppTMPro.TextAlignmentOptions.Center);
            title.rectTransform.sizeDelta = new Vector2(panelW - 2 * pad, titleH);
            UI.UguiBuilder.SeatTop(title.rectTransform, pad);

            float bodyY = pad + titleH;
            float listCx = -(panelW / 2f) + pad + listW / 2f;
            float detailCx = listCx + listW / 2f + pad + detailW / 2f;

            // ---- 左栏: 场景列表 ScrollRect ----
            var scroll = _ugui.MakeScrollView(panel, "list", listW, bodyH, new Color(0.05f, 0.05f, 0.08f, 0.9f),
                out RectTransform content);
            var vpRt = scroll.transform.TryCast<RectTransform>();
            UI.UguiBuilder.SeatTop(vpRt, bodyY);
            vpRt.anchoredPosition = new Vector2(listCx, vpRt.anchoredPosition.y);

            _pickerSelected = null;
            _pickerRowImgs.Clear();
            float ry = 4f;
            foreach (var e in entries)
            {
                int danger = Math.Max(1, Math.Min(5, e.Def.Danger));
                string rowLabel = e.Def.Name + "  " + new string('★', danger);
                if (e.Enabled)
                {
                    string id = e.Def.FullId;
                    // v1.37.0: 行点击 = 选中 (高亮+详情刷新), 不再直接进场 — 进场走底部「开始搜索」
                    var btn = _ugui.MakeButton(content, "scene_" + e.Def.ShortId, rowLabel, listW - 8f, P.BtnH, font,
                        () => PickerSelect(id), out _);
                    var brt = btn.transform.TryCast<RectTransform>();
                    UI.UguiBuilder.SeatTop(brt, ry);
                    Image bimg = null;
                    try { bimg = btn.GetComponent<Image>(); } catch { }
                    if (!(bimg is null)) _pickerRowImgs.Add(new KeyValuePair<string, Image>(id, bimg));
                }
                else
                {
                    // 锁定项渲染路径 (v1.47.0 set_gate 门禁): 灰字不可点 + 原因
                    var lockTxt = _ugui.MakeText(content, "lock_" + e.Def.ShortId,
                        e.Def.Name + " (未解锁: " + e.LockReason + ")", font,
                        new Color(0.45f, 0.45f, 0.45f), Il2CppTMPro.TextAlignmentOptions.Center);
                    lockTxt.rectTransform.sizeDelta = new Vector2(listW - 8f, P.BtnH);
                    UI.UguiBuilder.SeatTop(lockTxt.rectTransform, ry);
                }
                ry += rowPitch;
            }
            try { content.sizeDelta = new Vector2(0f, Math.Max(bodyH, ry)); } catch { }

            // ---- 右栏: 详情面板 (选中即刷新; 未选中 = 引导文案) ----
            var detail = _ugui.MakePanel(panel, "detail", detailW, bodyH, new Color(0.09f, 0.09f, 0.13f, 0.95f), false);
            UI.UguiBuilder.SeatTop(detail, bodyY);
            detail.anchoredPosition = new Vector2(detailCx, detail.anchoredPosition.y);
            _pickerDetailTitle = _ugui.MakeText(detail, "d_title", "—", titleFont,
                new Color(0.95f, 0.88f, 0.72f), Il2CppTMPro.TextAlignmentOptions.Center);
            _pickerDetailTitle.rectTransform.sizeDelta = new Vector2(detailW - 2 * pad, titleH);
            UI.UguiBuilder.SeatTop(_pickerDetailTitle.rectTransform, pad);
            _pickerDetailBody = _ugui.MakeText(detail, "d_body", "选择左侧地点查看详情", detailFont,
                new Color(0.78f, 0.78f, 0.78f), Il2CppTMPro.TextAlignmentOptions.TopLeft);
            _pickerDetailBody.rectTransform.sizeDelta = new Vector2(detailW - 2 * pad, bodyH - titleH - pad * 2f - 8f);
            UI.UguiBuilder.SeatTop(_pickerDetailBody.rectTransform, pad + titleH + 8f);

            // ---- 底部: 「开始搜索」(未选中置灰不可点) + 「返回地图」 ----
            float bottomY = bodyY + bodyH + pad;
            float halfBtnW = (panelW - 2 * pad - P.Spacing) / 2f;
            _pickerStartBtn = _ugui.MakeButton(panel, "start", "开始搜索", halfBtnW, P.BtnH, font, () =>
            {
                string id = _pickerSelected;
                if (id == null) return;
                EnterScene(id);   // v1.32.0: scenes.enter (picker id 入栈, back 可返回本面板)
            }, out _);
            try { _pickerStartBtn.interactable = false; } catch { }
            var srt = _pickerStartBtn.transform.TryCast<RectTransform>();
            UI.UguiBuilder.SeatTop(srt, bottomY);
            srt.anchoredPosition = new Vector2(-(halfBtnW / 2f + P.Spacing / 2f), srt.anchoredPosition.y);
            // v1.32.0: 「返回地图」改走 scenes.back() (弹栈 = picker 轻量壳退出 + 裸恢复官方地图)
            var back = _ugui.MakeButton(panel, "back", "返回地图", halfBtnW, P.BtnH, font,
                () => BackScene(), out _);
            var krt = back.transform.TryCast<RectTransform>();
            UI.UguiBuilder.SeatTop(krt, bottomY);
            krt.anchoredPosition = new Vector2(halfBtnW / 2f + P.Spacing / 2f, krt.anchoredPosition.y);

            // ③ 藏官方地图面板 (恢复只能裸 SetActive — OpenUI 含黑屏/CloseUI 会调 OnArriveStore)
            try { mapPanel.SetActive(false); } catch { }
            PsApi.Log(_logger, $"[scenes] 选图场景展开 (场景数 {entries.Count}, canvas order={order}{(L.CanvasOrder > 0 ? ", 布局强制" : "")})");
        }

        /// <summary>v1.37.0 双栏改版: 左栏行点击 = 选中 (行高亮 + 右栏详情刷新 + 「开始搜索」解禁),
        /// 不直接进场 — 进场收敛到底部「开始搜索」按钮 (误点进场实测根治)。</summary>
        private void PickerSelect(string fullId)
        {
            if (_pickerHud is null || string.IsNullOrEmpty(fullId)) return;
            _pickerSelected = fullId;
            var normal = new Color(0.16f, 0.12f, 0.09f, 0.95f);   // 与 UguiBuilder.MakeButton 底色一致
            var sel = new Color(0.38f, 0.30f, 0.16f, 0.97f);
            foreach (var kv in _pickerRowImgs)
                try { if (!(kv.Value is null)) kv.Value.color = kv.Key == fullId ? sel : normal; } catch { }
            SceneDef def = null;
            try { _scenes.TryGetValue(fullId, out def); } catch { }
            if (def != null)
            {
                int danger = Math.Max(1, Math.Min(5, def.Danger));
                try { if (!(_pickerDetailTitle is null)) _pickerDetailTitle.text = def.Name; } catch { }
                string body = "危险度 " + new string('★', danger);
                if (!string.IsNullOrWhiteSpace(def.Desc)) body += "\n\n" + def.Desc;
                if (!string.IsNullOrWhiteSpace(def.LootHint)) body += "\n\n产出: " + def.LootHint;
                try { if (!(_pickerDetailBody is null)) _pickerDetailBody.text = body; } catch { }
            }
            try { if (!(_pickerStartBtn is null)) _pickerStartBtn.interactable = true; } catch { }
        }

        /// <summary>picker 场景健康检查 (Poll 每帧, 节流区外; v1.32.0 picker 场景化: 键于 Active 是
        /// picker 场景, 不再键于 _pickerHud — 包自绘 picker 无默认 uGUI 也生效):
        /// ① Esc → scenes.back() (ISIL 实证: 游戏 ResolveEscape 的 Esc 目标不含 MapUIManager,
        ///    mapPanel 状态不受扰);
        /// ② 兜底: mapPanel 没了 (外部力量) → scenes.back() (恢复路径对 null 面板天然容忍,
        ///    等价旧"关选图场景不恢复")。
        /// (关闭路径 = Esc / 「返回地图」按钮 / 入口 toggle / 本兜底, 全部收敛到 scenes.back。)</summary>
        private void PickerHealth()
        {
            var def = Active;
            if (def == null || !def.IsPicker) return;
            bool esc = false;
            try { esc = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape); } catch { }
            if (esc) { BackScene(); return; }
            GameObject panel = null;
            try { var m = MapUIManager.Instance; if (m != null) panel = m.mapPanel; } catch { }
            if (panel == null) BackScene();
        }

        /// <summary>picker 默认渲染关闭: uGUI 根销毁 + 引用清空。restoreMap=true → mapPanel
        /// SetActive(true) 返回官方地图界面 (游戏地图状态从未关闭, 不需要 OpenUI — 那会重放黑屏
        /// 过场, ISIL 实证); goto 路径传 false (紧随的 TryEnter 自己处理 mapPanel)。幂等。
        /// v1.32.0: 由 ExitPickerShell 统一调用 (picker 轻量壳退出的一半), 不再直接对外。</summary>
        private void ClosePickerScene(string reason, bool restoreMap)
        {
            bool had = _pickerHud is not null;
            try { if (_pickerHud is not null) UnityEngine.Object.Destroy(_pickerHud); } catch { }
            _pickerHud = null;
            _pickerSelected = null; _pickerDetailTitle = null; _pickerDetailBody = null; _pickerStartBtn = null;   // v1.37.0 双栏选中态复位
            _pickerRowImgs.Clear();
            if (restoreMap)
            {
                try
                {
                    var m = MapUIManager.Instance;
                    var p = m == null ? null : m.mapPanel;
                    if (p != null && !p.activeSelf) p.SetActive(true);
                }
                catch { }
            }
            if (had) PsApi.Log(_logger, $"[scenes] 选图场景关闭 ({reason}{(restoreMap ? ", 已恢复地图" : "")})");
        }

        /// <summary>v1.24.0: F10 布局热重载 (Plugin.OnUpdate → RaidLayout.RefreshActive 之后调用) —
        /// 选图场景开着 = 关并按新布局重开 (地图保持隐藏, 不恢复); 关着 = 下次展开自动用新布局。</summary>
        internal void RelayoutPicker()
        {
            if (_pickerHud is null) return;
            ClosePickerScene("relayout", false);
            OpenPicker();
            PsApi.Log(_logger, "[scenes] 选图场景已按新布局重建 (F10)");
        }

        /// <summary>v1.30.0: M 键写回 (Plugin.OnUpdate, 仅在自定义场景激活时调用) —
        /// 把拖好的图元窗 (物品箱/地面/容器) 当前屏比位置写进布局:
        ///   箱: box.x=箱中心屏比 x, box.y=箱底边屏比 (底边锚语义);
        ///   地面: ground.x/y=窗中心屏比 + ground.mode="free" (不再贴箱右缘);
        ///   容器 (开着时): container.x/y=窗中心屏比 (下次开窗钉新位置)。
        /// v1.31.0: 写回目标改为<b>当前场景的场景级文件</b> packs/&lt;pack&gt;/scenes/&lt;id&gt;/layout.json
        /// (没有就创建, 只写钉位键 box/ground/container+ground.mode, 其它键经 JsonNode 合并原样保留)。
        /// v1.48.8: 内嵌包 (PackDirOf=null) 不再放弃 — 写回目标 = 用户覆写层
        /// UserData/PSApi/layout_overrides/&lt;pack&gt;/&lt;sceneId&gt;.json (RaidLayout.WriteBackPathFor 决策;
        /// 覆写层是读取层栈最高层, 写完即生效; junction 文件夹包写穿落开发主库, 行为不变)。
        /// v1.33.0 (波 3): builtin_raid 退役后只剩 script 单路由
        /// (ScriptGridService); 写回后重解析当前场景布局 + 重钉让玩家立即看到生效。</summary>
        internal void WriteBackLayoutPins()
        {
            if (Active == null) return;
            if (Active.IsPicker) return;   // v1.32.0: picker 轻量壳无图元窗可钉, M 写回不适用
            var pins = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            if (ScriptGrid == null) { PsApi.Warn(_logger, "[layout] M 写回: ScriptGridService 未接线"); return; }
            ScriptGrid.CollectLayoutPins(pins);
            if (pins.Count == 0)
            {
                PsApi.Log(_logger, "[layout] M 写回: 没有可写回的图元窗 (箱/地面/容器都不在)");
                return;
            }
            string packDir = null;
            try { packDir = PSApi.Events.Raid.RaidLayout.PackDirOf?.Invoke(Active.PackId); } catch { }
            string path = PSApi.Events.Raid.RaidLayout.WriteBackPathFor(Active.PackId, Active.ShortId, packDir);
            bool toOverride = string.IsNullOrEmpty(packDir);   // v1.48.8: 内嵌包 → 用户覆写层
            string text = null;
            try { if (File.Exists(path)) text = File.ReadAllText(path); }
            catch (Exception e) { PsApi.Warn(_logger, "[layout] M 写回读取失败: " + e.Message); return; }
            string merged = PSApi.Events.Raid.RaidLayout.MergePinsJson(text, pins, out string err);
            if (merged == null) { PsApi.Warn(_logger, "[layout] M 写回合并失败: " + err); return; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, merged);
            }
            catch (Exception e) { PsApi.Warn(_logger, "[layout] M 写回写盘失败: " + e.Message); return; }
            PSApi.Events.Raid.RaidLayout.RefreshActive(Active.PackId, Active.ShortId, _logger);
            ScriptGrid.RepinFromLayout();
            var sb = new System.Text.StringBuilder();
            foreach (var sec in pins)
                foreach (var kv in sec.Value)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(sec.Key).Append('.').Append(kv.Key).Append('=');
                    sb.Append(kv.Value is float f ? f.ToString("0.###") : kv.Value is double d ? d.ToString("0.###") : kv.Value?.ToString());
                }
            PsApi.Log(_logger, $"[layout] M 写回完成 (script) → {(toOverride ? "用户覆写层 " : "")}{path}: {sb} → 已重解析并重钉");
        }

        // ==================== 进入 / 撤离 ====================

        internal bool TryEnter(string sceneId)
        {
            // v1.32.0: _enterInFlight 重入守卫 — 黑屏过场窗口期 Active 仍为 null, 防 DoEnter 叠进
            if (Active != null || _enterInFlight) return false;
            SceneDef def;
            if (!_scenes.TryGetValue(sceneId, out def)) return false;

            // v1.32.0: picker 场景轻量壳 — 同步直入, 不黑屏不关地图杂窗
            // (v1.21.2 教训: CloseUI 调 OnArriveStore 副作用, picker 是地图延伸层不能走标准壳前置)
            if (def.IsPicker)
            {
                _enterInFlight = true;
                DoEnter(def);
                return Active == def;
            }

            _enterInFlight = true;
            // 关地图与杂窗 (仿原版 OnGoingOutside 前置; CloseUI 后强制 mapPanel 失活兜底, 防点击穿透)
            CloseMapAndPanels();

            StoreUIManager sui = null;
            try { sui = StoreUIManager.Instance; } catch { }
            if (sui == null) { DoEnter(def); return true; }
            System.Action act = () => DoEnter(def);
            var during = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(act);
            _pins.Add(act); _pins.Add(during);
            try { sui.StartBlackscreenSequence(true, during, null, 0.25f, 0.2f); }
            catch (Exception e)
            {
                PsApi.Warn(_logger, "[scenes] 黑屏过场失败, 直接进场景: " + e.Message);
                DoEnter(def);
            }
            return true;
        }

        private static void CloseMapAndPanels()
        {
            try
            {
                var m = MapUIManager.Instance;
                if (m != null)
                {
                    m.CloseUI();
                    var p = m.mapPanel;
                    if (p != null && p.activeSelf) p.SetActive(false);
                }
            }
            catch { }
            try { PaperUIManager.Instance?.CloseAllPaperUI(); } catch { }
            try { EmporiumEntry.Instance?.CloseItemWindowUIWindow(); } catch { }
        }

        /// <summary>复刻 MapUIManager.OnGoingOutside (ISIL 实证行 2404): 全部走原版公开方法。
        /// v1.32.0: picker 场景分流轻量壳 (EnterPickerShell) — 不黑屏/不离店; 重入守卫在此复位。</summary>
        private void DoEnter(SceneDef def)
        {
            _enterInFlight = false;   // v1.32.0: 过场窗口结束 (黑屏回调/直进都在此收口)
            if (def.IsPicker) { EnterPickerShell(def); return; }
            Active = def;
            // v1.31.0: 三层覆写 — 进场景即解析当前场景布局缓存 (默认+包级+场景级), 后续 HUD/网格/标题全走它
            try { PSApi.Events.Raid.RaidLayout.RefreshActive(def.PackId, def.ShortId, _logger); }
            catch (Exception e) { PsApi.Warn(_logger, "[layout] 场景布局刷新异常: " + e.Message); }
            try { SceneUgui?.OnSceneEnter(def.FullId); }   // P2: 防御性清同名残留 (重进保险)
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] 场景 uGUI 进入清理异常: " + e.Message); }
            try { ScriptGrid?.OnSceneEnter(); }   // P3: grid 能力防御性清残留
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] script grid 进入清理异常: " + e.Message); }
            try { RtCombat?.OnSceneExit(); }   // v1.29.0: 防御性中止残留实时战斗 (重进保险)
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] rt combat 进入清理异常: " + e.Message); }
            bool leftStore = false;
            try
            {
                CloseMapAndPanels();   // 幂等 (黑屏前已关一遍)

                // ① 原版离店: 藏窗/藏店/关面板/藏信用日历计数器, 一手包办
                EmporiumEntry ee = null;
                try { ee = EmporiumEntry.Instance; } catch { }
                if (ee == null) throw new Exception("EmporiumEntry.Instance 为空");
                ee.OnLeaveStore();
                leftStore = true;
                PsApi.Log(_logger, "[scenes] OnLeaveStore 完成 (原版离店)");

                // ② 原版外出背包: afterhourWindow.Show + 背包内容转入
                try { ee.ShowAfterhourInv(); } catch (Exception e) { PsApi.Warn(_logger, "[scenes] ShowAfterhourInv 失败: " + e.Message); }
                try { ee.TransferBackpackToInv(); } catch (Exception e) { PsApi.Warn(_logger, "[scenes] TransferBackpackToInv 失败: " + e.Message); }

                // (v1.33.0 波 3: builtin_raid 的外出栏隐藏随 C# raid 服务退役删除 — script 场景
                //  需要隐藏时由 pss 经 grid.hide_afterhour 自行处理, 外出栏默认保留)

                // ③ 原版状态位 + 卷帘门按钮隐藏
                try { MapUIManager.Instance.isOutside = true; } catch { }
                SetShutterHidden(true);

                // ④ 体检: 原版没盖到的残留 (BlockerMask/世界TMP) 才补刀
                SweepLeftovers();

                try { ee.Validate(true); } catch { }

                // ⑤ 原版外出环境音 (ISIL 实证: 原版每个外出点都切 ambient, 撤离 PlayStoreAmbient 恢复)
                try
                {
                    var ssp = StoreSoundPlayer.Instance;
                    if (ssp != null)
                    {
                        switch ((def.Bgm ?? "").Trim().ToLowerInvariant())
                        {
                            case "none": break;
                            case "store": ssp.PlayStoreAmbient(); break;
                            case "commissary": ssp.PlayCommissaryAmbient(); break;
                            case "inventor": ssp.PlayInventorAmbient(); break;
                            default: ssp.PlayDumpingGroundAmbient(); break;
                        }
                    }
                }
                catch { }

                BuildSceneUI(def);
            }
            catch (Exception e)
            {
                // 任一步失败: 走原版回店路径回滚, 防黑屏卡死
                PsApi.Err(_logger, "[scenes] 进入场景失败, 已回滚: " + e.Message);
                try { if (_sceneWindow != null) _sceneWindow.Hide(); } catch { }
                _sceneWindow = null; _statusTag = null; _pointButtons.Clear();
                try { if (_titleHud is not null) UnityEngine.Object.Destroy(_titleHud); } catch { }
                _titleHud = null; _statusUgui = null;
                try { if (_dimGo is not null) UnityEngine.Object.Destroy(_dimGo); } catch { }
                _dimGo = null;
                RestoreLeftovers();
                try { MapUIManager.Instance.isOutside = false; } catch { }
                SetShutterHidden(false);
                if (leftStore)
                {
                    try { EmporiumEntry.Instance?.OnArriveStore(true); } catch { }   // fromMap=true: 回滚不扣 visitLeftTonight
                    try { EmporiumEntry.Instance?.HideAfterhourInv(); } catch { }
                }
                Active = null;
                try { PSApi.Events.Raid.RaidLayout.RefreshActive(null, null, _logger); } catch { }   // v1.31.0: 回退默认布局
                ClearSavedScene();   // v1.32.0: 进入失败不留下次读档重进记录
                return;
            }
            PersistActiveScene();   // v1.32.0: 读档自动重进记录 (场景 id+当天 day → SaveStates)
            Publish("psapi.scene.enter", def, null, null, 0);
            PsApi.Log(_logger, $"[scenes] 进入场景 {def.FullId} ({def.Name})");
        }

        internal bool TryExit(string reason)
        {
            if (Active == null) return false;
            var def = Active;
            // v1.32.0: picker 轻量壳退出同步直出, 不黑屏 (picker 是地图延伸层, 与进入对称)
            if (def.IsPicker) { DoExit(def, reason); return true; }
            StoreUIManager sui = null;
            try { sui = StoreUIManager.Instance; } catch { }
            if (sui == null) { DoExit(def, reason); return true; }
            System.Action act = () => DoExit(def, reason);
            var during = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(act);
            _pins.Add(act); _pins.Add(during);
            try { sui.StartBlackscreenSequence(true, during, null, 0.25f, 0.2f); }
            catch (Exception e)
            {
                PsApi.Warn(_logger, "[scenes] 黑屏过场失败, 直接撤离: " + e.Message);
                DoExit(def, reason);
            }
            return true;
        }

        /// <summary>复刻 MapUIManager.OnReturning (ISIL 实证行 2701): 原版对称恢复。
        /// v1.32.0: picker 场景分流轻量壳退出 (ExitPickerShell; goto 不恢复地图, 其余恢复)。</summary>
        private void DoExit(SceneDef def, string reason)
        {
            if (def.IsPicker) { ExitPickerShell(def, reason, reason != "goto"); return; }
            // (v1.33.0 波 3: raid 收尾随 C# raid 服务退役删除 — script 场景收尾由 pss
            //  on scene_leave 自行处理, 外出栏由 ScriptGrid.OnSceneExit 配对恢复)
            // 销毁场景 UI (战利品在原版外出背包里, 由 TransferAfterhourToInv 带回, 不会丢)
            try { if (_sceneWindow != null) _sceneWindow.Hide(); } catch { }
            _sceneWindow = null;
            try { if (_pickerHud is not null) UnityEngine.Object.Destroy(_pickerHud); } catch { }
            _pickerHud = null;
            _pickerSelected = null; _pickerDetailTitle = null; _pickerDetailBody = null; _pickerStartBtn = null;
            _pickerRowImgs.Clear();
            try { if (_titleHud is not null) UnityEngine.Object.Destroy(_titleHud); } catch { }
            _titleHud = null;
            _statusTag = null;
            _statusUgui = null;
            _pointButtons.Clear();
            try { if (_dimGo is not null) UnityEngine.Object.Destroy(_dimGo); } catch { }
            _dimGo = null;
            // P2 (v1.27.0): 自动销毁该场景创建的全部 uGUI 元素 — 硬要求 (防泄漏防孤儿)
            try { SceneUgui?.OnSceneExit(def.FullId); }
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] 场景 uGUI 撤离清理异常: " + e.Message); }
            // P3 (v1.28.0): script 场景能力收尾 (容器/地面遗留销毁+外出栏恢复 → 信息栏销毁)
            // v1.35.0: 经典按钮面板删除后 ScriptCombat 无场景期状态, 无收尾调用
            try { RtCombat?.OnSceneExit(); }   // v1.29.0: 实时战斗中 if any → abort (光标/战斗锁配对归还)
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] rt combat 撤离清理异常: " + e.Message); }
            try { ScriptGrid?.OnSceneExit(); }
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] script grid 撤离清理异常: " + e.Message); }
            try { SceneLog?.OnSceneExit(); }
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] 场景信息栏撤离清理异常: " + e.Message); }

            EmporiumEntry ee = null;
            try { ee = EmporiumEntry.Instance; } catch { }
            if (ee != null)
            {
                // ① 原版回店: 全对称恢复 (含 visitLeftTonight-1, 与原版外出一次一致)
                try { ee.OnArriveStore(false); }
                catch (Exception e) { PsApi.Warn(_logger, "[scenes] OnArriveStore 失败: " + e.Message); }
                // ② 外出背包收口: 隐藏 + 物品原版自动转回玩家背包
                try { ee.HideAfterhourInv(); } catch { }
                try { ee.TransferAfterhourToInv(); }
                catch (Exception e) { PsApi.Warn(_logger, "[scenes] TransferAfterhourToInv 失败: " + e.Message); }
            }
            try { PaperUIManager.Instance?.CloseAllPaperUI(); } catch { }
            try { ee?.CloseItemWindowUIWindow(); } catch { }
            try { MapUIManager.Instance.isOutside = false; } catch { }
            SetShutterHidden(false);
            RestoreLeftovers();
            try { StoreSoundPlayer.Instance?.PlayStoreAmbient(); } catch { }
            if (Active != null && Active.FullId == def.FullId) Active = null;
            try { PSApi.Events.Raid.RaidLayout.RefreshActive(null, null, _logger); } catch { }   // v1.31.0: 出场景回退默认布局
            ClearSavedScene();   // v1.32.0: 出场景清读档重进记录 (goto 链上紧随的 DoEnter 会重写)
            Publish("psapi.scene.leave", def, reason, null, 0);
            PsApi.Log(_logger, $"[scenes] 离开场景 {def.FullId} (reason={reason})");
        }

        // ==================== 场景导航原语 (v1.32.0) ====================

        /// <summary>scenes.enter(id): id 解析 (短名/带包前缀全 id, SceneNav.Resolve) →
        /// 场景不存在 warn 返 false。从场景内调用 = 跳转: 当前场景 DoExit(reason="goto") 走标准壳退出
        /// (壳恢复与回店路径一致) → TryEnter(新场景) → 压当前场景短 id。从非场景态调用 = 直入:
        /// TryEnter → 压 "map" (返回点 = 商店/地图层)。重入守卫: DoEnter 进行中不再入;
        /// 无头/主菜单 (游戏壳不可用) 降级 false。</summary>
        internal bool EnterScene(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (_enterInFlight) return false;
            var def = SceneNav.Resolve(_scenes, id.Trim(), out bool ambiguous);
            if (def == null)
            {
                PsApi.Warn(_logger, $"[scenes] scenes.enter: 场景不存在 '{id}'");
                return false;
            }
            if (ambiguous) PsApi.Warn(_logger, $"[scenes] scenes.enter: 短名 '{id}' 多义, 取先加载者 {def.FullId}");
            // v1.47.0: scenes.set_gate 硬门禁 — 锁定(hide 或原因串)一律拒绝进入
            string gate = SceneGateState.Eval(def.FullId);
            if (gate != null)
            {
                PsApi.Warn(_logger, $"[scenes] scenes.enter: '{def.FullId}' 被门禁拒绝 ({(SceneGateState.IsHidden(gate) ? "hide" : gate)})");
                return false;
            }
            if (Active == def) return false;   // 已在该场景, no-op
            if (!InGameShell()) return false;  // 无头/主菜单降级 (Il2Cpp 不可用 → false)
            if (Active != null)
            {
                var cur = Active;
                DoExit(cur, "goto");   // 标准壳退出 (藏/还配对与回店路径一致)
                if (!TryEnter(def.FullId)) return false;
                _navStack.Add(SceneNav.EnterPushToken(cur.ShortId));
                PsApi.Log(_logger, $"[scenes] 场景跳转 {cur.FullId} → {def.FullId} (goto, 栈深 {_navStack.Count})");
                return true;
            }
            if (!TryEnter(def.FullId)) return false;
            _navStack.Add(SceneNav.EnterPushToken(null));   // "map"
            PsApi.Log(_logger, $"[scenes] 进入场景 {def.FullId} (压栈 map, 栈深 {_navStack.Count})");
            return true;
        }

        /// <summary>scenes.back(): 弹栈返回。空栈 = 回店 (等价 scenes.leave 回店路径);
        /// 栈顶 "map" = 回店恢复 (DoExit) + 裸 mapPanel.SetActive(true) 重开官方地图
        /// (v1.21.2 教训: 不走 OpenUI 黑屏/CloseUI 副作用); 栈顶场景 id = 退出当前场景后进入该场景
        /// (不再压栈)。弹出的场景已不存在 → 告警, 停回店态。无头/主菜单降级 false。</summary>
        internal bool BackScene()
        {
            if (_enterInFlight) return false;
            if (_navStack.Count == 0)
                return TryExit("back");   // 空栈 = 回店 (不在场景 = false, no-op)
            if (!InGameShell()) return false;   // 无头/主菜单降级
            string token = SceneNav.PopBack(_navStack);
            if (token == SceneNav.MapToken)
            {
                if (Active != null) DoExit(Active, "back");
                RestoreMapPanelBare();
                PsApi.Log(_logger, $"[scenes] scenes.back → 回店+重开官方地图 (栈深 {_navStack.Count})");
                return true;
            }
            // 弹出场景 id = 进入该场景 (不再压栈)
            if (Active != null) DoExit(Active, "back");
            var def = SceneNav.Resolve(_scenes, token, out bool _);
            if (def == null)
            {
                PsApi.Warn(_logger, $"[scenes] scenes.back: 栈内场景 '{token}' 已不存在, 停回店态");
                return true;
            }
            PsApi.Log(_logger, $"[scenes] scenes.back → 进入场景 {def.FullId} (栈深 {_navStack.Count})");
            return TryEnter(def.FullId);
        }

        /// <summary>scenes.leave(reason?): 清空场景栈 + 黑屏回店 (不在场景 = false, no-op)。
        /// psapi.scene.leave 事件的 reason 取该值 (修"reason 固定 script"硬编码)。</summary>
        internal bool LeaveScene(string reason)
        {
            if (string.IsNullOrEmpty(reason)) reason = "script";
            _navStack.Clear();
            return TryExit(reason);
        }

        /// <summary>scenes.list(): entry==map 场景数组 [{id,name,danger,desc,loot_hint,locked,lock_reason}...]
        /// (v1.47.0: scenes.set_gate 门禁生效 — "hide" 前缀场景不列出; 锁定场景 locked=true + lock_reason=原因;
        /// 无头/不在商店 = 空表)。</summary>
        internal List<object> ListScenes()
        {
            var list = new List<object>();
            if (!InGameShell()) return list;
            foreach (var def in _scenes.Values)
            {
                if (def.Entry != "map") continue;
                string g = SceneGateState.Eval(def.FullId);
                if (SceneGateState.IsHidden(g)) continue;
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["id"] = def.FullId,
                    ["name"] = def.Name,
                    ["danger"] = (long)def.Danger,
                    ["desc"] = def.Desc ?? "",
                    ["loot_hint"] = def.LootHint ?? "",   // v1.37.0: 产出提示 (picker 双栏详情同源)
                    ["locked"] = g != null,             // v1.47.0: 门禁锁定态 (原恒 false 预留位启用)
                    ["lock_reason"] = g ?? "",
                });
            }
            return list;
        }

        /// <summary>游戏壳可用 (Il2Cpp 就绪且在商店场景): 无头测试/主菜单 = false → 导航原语降级。
        /// (game.save 同款先例: Il2Cpp 静态访问 try/catch + null 判定双保险。)</summary>
        private bool InGameShell()
        {
            try { return EmporiumEntry.Instance != null; } catch { return false; }
        }

        /// <summary>裸恢复官方地图面板 (v1.21.2 实证: 恢复只能 mapPanel.SetActive(true) —
        /// OpenUI 含黑屏过场, CloseUI 调 OnArriveStore 副作用, 都不能走)。幂等。</summary>
        private static void RestoreMapPanelBare()
        {
            try
            {
                var m = MapUIManager.Instance;
                var p = m == null ? null : m.mapPanel;
                if (p != null && !p.activeSelf) p.SetActive(true);
            }
            catch { }
        }

        /// <summary>v1.32.0: picker 场景轻量壳进入 — 与标准壳的差异点: 不黑屏 (picker 是地图的
        /// 延伸层)、不离店 (不调 OnLeaveStore/ShowAfterhourInv/CloseUI, isOutside/visitLeftTonight/
        /// 卷帘门/环境音全不动)、不藏外出栏 (_afterhourHiddenShell 不涉及)。只做: 裸藏 mapPanel
        /// + 默认渲染 (OpenPicker: 暗幕+选图 uGUI) 或包自绘 (ui 键/scripts 任一注册 → 默认渲染不建,
        /// 只藏地图+发 enter 事件, 包用 pss 经 scenes.list + scenes.enter/back 自制)。</summary>
        private void EnterPickerShell(SceneDef def)
        {
            try { SceneUgui?.OnSceneEnter(def.FullId); }   // 防御性清同名残留 (与标准壳同款)
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] picker uGUI 进入清理异常: " + e.Message); }
            bool custom = def.Ui != null || def.HasScripts;
            Active = def;
            if (custom)
            {
                // 包自绘: 只裸藏地图发事件 (v1.21.2 同款: 不动游戏地图状态, 不黑屏)
                try
                {
                    var m = MapUIManager.Instance;
                    var p = m == null ? null : m.mapPanel;
                    if (p != null && p.activeSelf) p.SetActive(false);
                }
                catch { }
            }
            else
            {
                OpenPicker();   // 默认渲染: 建暗幕+选图 uGUI + 裸藏 mapPanel (失败自清理, _pickerHud 保持 null)
                if (_pickerHud is null)
                {
                    PsApi.Warn(_logger, "[scenes] picker 场景进入失败 (选图 UI 未建起)");
                    Active = null;
                    return;
                }
            }
            PersistActiveScene();   // 读档自动重进记录 (picker 也记 — 恢复回选图面板)
            Publish("psapi.scene.enter", def, null, null, 0);
            PsApi.Log(_logger, $"[scenes] 进入 picker 场景 {def.FullId} ({def.Name}{(custom ? ", 包自绘" : "")})");
        }

        /// <summary>EnterPickerShell 对称退出: 包自绘 uGUI 自动销毁 + 默认渲染销毁 (ClosePickerScene)
        /// + restoreMap=true 时裸 mapPanel.SetActive(true)。restoreMap=false 仅 goto 链 (紧随的
        /// TryEnter 标准壳自己关地图)。轻量壳不动商店状态, 故无 OnArriveStore/afterhour 对称动作。</summary>
        private void ExitPickerShell(SceneDef def, string reason, bool restoreMap)
        {
            try { SceneUgui?.OnSceneExit(def.FullId); }   // 包自绘 uGUI 自动销毁 (与标准壳同款硬要求)
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] picker uGUI 撤离清理异常: " + e.Message); }
            ClosePickerScene(reason, restoreMap);   // 默认渲染销毁 + 按需裸恢复地图 (幂等)
            if (Active == def) Active = null;
            ClearSavedScene();
            Publish("psapi.scene.leave", def, reason, null, 0);
            PsApi.Log(_logger, $"[scenes] 离开 picker 场景 {def.FullId} (reason={reason})");
        }

        // ---- 读档自动重进 (v1.32.0 壳层通用化) ----
        // v1.33.0 (波 3): builtin 侧 Poll 那份已随退役删除, 壳层为唯一实现;
        // script 场景恢复后由 pss 自己的 raid_enter 逻辑还原状态。跨天丢弃照现有语义。

        private sealed class SavedSceneRef
        {
            public string Scene { get; set; }
            public long Day { get; set; }
        }

        /// <summary>进场景时把 (场景 id+当天 day) 写 SaveStates("scenes") "active" 键。</summary>
        private void PersistActiveScene()
        {
            try
            {
                var def = Active;
                if (def == null) return;
                long day = 0;
                try { if (GameHooks.GameDay.TryRead(out long d, out _, out _)) day = d; } catch { }
                SaveStates.For("scenes", _logger).Set("active",
                    JsonSerializer.Serialize(new SavedSceneRef { Scene = def.FullId, Day = day }));
            }
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] 场景重进记录持久化失败: " + e.Message); }
        }

        private SavedSceneRef LoadSavedScene()
        {
            try
            {
                var json = SaveStates.For("scenes", _logger).Get("active");
                if (string.IsNullOrEmpty(json)) return null;
                return JsonSerializer.Deserialize<SavedSceneRef>(json);
            }
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] 场景重进记录读取失败: " + e.Message); return null; }
        }

        private void ClearSavedScene()
        {
            try { SaveStates.For("scenes", _logger).Set("active", null); }
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] 场景重进记录清除失败: " + e.Message); }
        }

        /// <summary>读档自动重进 (Poll 驱动): 当天有记录 + Active==null
        /// + 在对局 (商店根激活) → TryEnter 恢复; 跨天丢弃。每次回商店只尝试一次。</summary>
        private void PollRestoreScene()
        {
            if (_restoreChecked || Active != null || _enterInFlight) return;
            bool inShop = false;
            try
            {
                var ee = EmporiumEntry.Instance;
                inShop = ee != null && ee.storePhysical != null && ee.storePhysical.activeSelf;
            }
            catch { }
            if (!inShop) return;
            _restoreChecked = true;
            var saved = LoadSavedScene();
            if (saved == null || string.IsNullOrEmpty(saved.Scene)) return;
            long day = 0;
            try { if (GameHooks.GameDay.TryRead(out long d, out _, out _)) day = d; } catch { }
            if (saved.Day == day)
            {
                PsApi.Log(_logger, $"[scenes] 检测到存档中的场景记录 ({saved.Scene}), 自动重进恢复");
                try { TryEnter(saved.Scene); }
                catch (Exception e) { PsApi.Warn(_logger, "[scenes] 读档重进失败: " + e.Message); }
            }
            else
            {
                PsApi.Log(_logger, "[scenes] 存档中的场景记录已跨天, 丢弃");
                ClearSavedScene();
            }
        }

        // ==================== 原版复刻辅助 ====================

        /// <summary>卷帘门按钮显隐 (原版 OnGoingOutside/OnReturning 同款:
        /// StoreShutterButton.blockerGameObject.transform.parent.gameObject.SetActive)。</summary>
        private void SetShutterHidden(bool hidden)
        {
            try
            {
                var ssb = StoreShutterButton.Instance;
                var blocker = ssb == null ? null : ssb.blockerGameObject;
                var parent = blocker == null ? null : blocker.transform.parent;
                var go = parent == null ? null : parent.gameObject;
                if (go != null && go.activeSelf == hidden) go.SetActive(!hidden);
            }
            catch (Exception e) { PsApi.Warn(_logger, "[scenes] 卷帘门按钮 " + (hidden ? "隐藏" : "恢复") + " 失败: " + e.Message); }
        }

        /// <summary>OnLeaveStore 后的残留体检: 原版进 scav 时 BlockerMask (卷帘门遮罩 UI) 与世界浮动 TMP
        /// (金钱/租金/顾客计数) 都处于失活 (scene-watch 实证)。若我们调完原版方法后它们仍激活,
        /// 说明原版是别处关的 — 补刀并记录, 撤离恢复; 日志可验证原版覆盖度。</summary>
        private void SweepLeftovers()
        {
            _extraHidden.Clear();
            try
            {
                var wuc = FindRootByName("WindowUICanvas");
                var bm = wuc == null ? null : wuc.transform.Find("WindowRootObject/Node/BlockerMask");
                if (bm != null && bm.gameObject.activeSelf)
                {
                    bm.gameObject.SetActive(false);
                    _extraHidden.Add(bm.gameObject);
                    PsApi.Log(_logger, "[scenes] 残留补刀: BlockerMask (原版未覆盖)");
                }
            }
            catch { }
            try
            {
                var wsi = FindRootByName("CanvasWorldStoreInteract");
                if (wsi != null)
                {
                    foreach (var tmpName in new[] { "clientCounterTMP", "moneyTeshMeshPro", "rentTMP" })
                    {
                        try
                        {
                            var t = wsi.transform.Find(tmpName);
                            if (t == null || !t.gameObject.activeSelf) continue;
                            t.gameObject.SetActive(false);
                            _extraHidden.Add(t.gameObject);
                            PsApi.Log(_logger, $"[scenes] 残留补刀: {tmpName} (原版未覆盖)");
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void RestoreLeftovers()
        {
            foreach (var go in _extraHidden)
            {
                try { if (go != null && !go.activeSelf) go.SetActive(true); }
                catch { }
            }
            _extraHidden.Clear();
        }

        /// <summary>按名字找场景根级 GameObject (active 与否都找)。找不到返回 null。</summary>
        private GameObject FindRootByName(string name)
        {
            try
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                var roots = scene.GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                    if (roots[i] != null && roots[i].name == name) return roots[i];
            }
            catch { }
            return null;
        }

        // ==================== 帧轮询兜底 ====================

        internal void Poll()
        {
            // v1.19.4: 地图按钮健康检查 (节流 ~0.5s) — 中文化 mod 在 OpenUI 之后才换中文字体
            // (日志实证: 注入瞬间模板字体=NotoSans-Medium 拉丁字体), 且可能重建面板销毁第二行;
            // 注入时抄字体永远慢半拍 → 改为常驻同步 + 丢失自愈。
            _mapHealthTimer += UnityEngine.Time.deltaTime;
            if (_mapHealthTimer >= 0.5f)
            {
                _mapHealthTimer = 0f;
                // v1.19.6: 心跳诊断 — 每 10s 一行全门径结果 (v1.19.5 无声门径全灭, 需先定位死在哪一环)
                _mapHbTimer += 0.5f;
                bool doHb = _mapHbTimer >= 10f;
                if (doHb) _mapHbTimer = 0f;
                try { MapButtonsHealth(doHb); }
                catch (Exception e) { PsApi.Warn(_logger, $"[scenes] 健康检查异常: {e.GetType().Name}: {e.Message}"); }
            }
            // v1.21.2: picker 场景健康检查 (每帧, 节流区外; v1.32.0: Esc/map-gone → scenes.back)
            try { PickerHealth(); }
            catch (Exception e) { PsApi.Warn(_logger, $"[scenes] 选图场景健康检查异常: {e.GetType().Name}: {e.Message}"); }
            // v1.32.0: 读档自动重进 (壳层通用化; script 场景恢复后由 pss raid_enter 还原状态)
            try { PollRestoreScene(); }
            catch (Exception e) { PsApi.Warn(_logger, $"[scenes] 读档重进探测异常: {e.GetType().Name}: {e.Message}"); }
            // P3 (v1.28.0): script 场景 grid 能力轮询 (容器 X 关窗检测 + 窗口健康 + 外出栏保持隐藏)
            try { ScriptGrid?.Poll(); }
            catch (Exception e) { PsApi.Warn(_logger, $"[scenes] script grid Poll 异常: {e.GetType().Name}: {e.Message}"); }
            if (Active == null) return;
            // 场景窗口被 Esc/X 关掉 → 重开 (状态仍在, 只是不可见)
            if (_sceneWindow != null)
            {
                try { if (!_sceneWindow.IsVisible()) _sceneWindow.Show(); } catch { }
            }
            // 补刀物被游戏逻辑复活 → 压回去
            try
            {
                foreach (var go in _extraHidden)
                    if (go != null && go.activeSelf) go.SetActive(false);
            }
            catch { }
            // 商店意外回活 (外部力量回店/读档) → 无过场清理
            // v1.32.0: picker 轻量壳不离店, 商店根恒激活属正常态 — 本兜底不适用于 picker
            try
            {
                var ee = EmporiumEntry.Instance;
                var physical = ee == null ? null : ee.storePhysical;
                if (physical != null && physical.activeSelf && !Active.IsPicker) DoExit(Active, "abort");
            }
            catch { }
        }

        /// <summary>Unity 场景卸载: 所有运行时对象已销毁, 只清引用 (商店由游戏重建)。</summary>
        internal void OnSceneLeft()
        {
            if (Active == null && _dimGo is null && _sceneWindow == null && _pickerHud is null && _titleHud is null) return;
            Active = null;
            _dimGo = null;
            _sceneWindow = null;
            _pickerHud = null;
            _pickerSelected = null; _pickerDetailTitle = null; _pickerDetailBody = null; _pickerStartBtn = null;
            _pickerRowImgs.Clear();
            _titleHud = null;
            _statusTag = null;
            _statusUgui = null;
            _pointButtons.Clear();
            _extraHidden.Clear();
            _injectedMaps.Clear();
            _navStack.Clear();            // v1.32.0: 导航栈仅内存, 随 Unity 场景卸载重置
            _enterInFlight = false;
            _restoreChecked = false;      // v1.32.0: 读档重进探测复位 (持久化不清 — 恢复靠它)
            try { PSApi.Events.Raid.RaidLayout.RefreshActive(null, null, _logger); } catch { }   // v1.31.0: 布局缓存回退默认
            try { SceneUgui?.OnSceneLeft(); } catch { }   // P2: 场景 uGUI 引用随场景销毁, 摘表清引用
            try { RtCombat?.OnSceneLeft(); } catch { }         // v1.29.0: rt 战斗引用清 + 光标计数兜底
            try { ScriptGrid?.OnSceneLeft(); } catch { }     // P3: grid 窗引用随场景销毁
            try { SceneLog?.OnSceneLeft(); } catch { }       // P3: 信息栏引用随场景销毁
        }

        // ==================== 场景 UI ====================

        /// <summary>场景 UI = 纯视觉暗幕 (低层级 Canvas, raycastTarget=false 不吃任何点击)
        ///   + 原生图元场景窗口 (PixelWindow + GridPixelElement 列 + TagElement/ButtonElement) —
        ///   与 PSUI 机器面板同一条渲染链路, 中文文本/字体由游戏原生管线处理 (自制 TMP 曾整板不渲染)。
        ///   交互点击只认原生图元系统, 从根上杜绝 uGUI 点击穿透到下层原版 UI。
        ///   M1.5 双路: def.Ui != null → psui 驱动 (PsUiService.BuildSceneWindow, 按钮换线见
        ///   BuildSceneUiFromPsui; 失败回退下方自动网格); def.Ui == null → 自动网格按钮列 (原样)。</summary>
        private void BuildSceneUI(SceneDef def)
        {
            // ---- 环境根: 不透明暗幕 (复刻原版 scavengingScene 的"独立环境"角色) ----
            _dimGo = new GameObject("PSApiSceneDim_" + def.FullId);
            var canvas = _dimGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -10;
            var bgGo = new GameObject("bg");
            var brt = bgGo.AddComponent<RectTransform>();
            brt.SetParent(_dimGo.transform, false);
            brt.anchorMin = Vector2.zero;
            brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero;
            brt.offsetMax = Vector2.zero;
            var bg = bgGo.AddComponent<Image>();
            // pack 背景图 (scene JSON "bg": 图标键, 走 IconService 同 pack icons/*.png 链路)
            Sprite bgSprite = null;
            if (!string.IsNullOrWhiteSpace(def.Bg))
            {
                string key = def.Bg.Contains(":") ? def.Bg : def.PackId + ":" + def.Bg;
                try { ItemsFacade.TryGetIcon(key, out bgSprite); } catch { }
                if (bgSprite == null) PsApi.Warn(_logger, $"[scenes] 场景背景图 '{key}' 未找到 (pack icons/ 下需有对应 png), 用纯色底");
            }
            if (bgSprite != null) { bg.sprite = bgSprite; bg.color = Color.white; }
            else bg.color = new Color(0.04f, 0.04f, 0.07f, 1f);
            bg.raycastTarget = false;   // 商店已由原版 OnLeaveStore 隐藏, 无需挡点击; 原生图元窗口照常交互

            // (v1.33.0 波 3: builtin_raid 分流 (raid 四区 HUD 接管) 已随 C# raid 服务退役删除,
            //  全部场景走统一 script 路径 — psui / 自动网格 / 无互动物标题面板)

            // ---- 原生图元场景窗口 ----
            // M1.5: scene JSON "ui" 键 → psui 驱动 (原版图元链路); 缺失/失败告警回退自动网格 (旧路径)
            if (def.Ui != null)
            {
                try
                {
                    BuildSceneUiFromPsui(def);
                    return;
                }
                catch (Exception e)
                {
                    PsApi.Warn(_logger, $"[scenes] 场景 psui '{def.PackId}:{def.Ui}' 构建失败, 回退自动网格: {e.Message}");
                    try { _sceneWindow?.Hide(); } catch { }
                    _sceneWindow = null; _statusTag = null; _pointButtons.Clear();
                    try { if (_titleHud is not null) UnityEngine.Object.Destroy(_titleHud); } catch { }
                    _titleHud = null; _statusUgui = null;
                }
            }
            // v1.30.0: 无互动物 points 的场景 (script/自动网格; 窗口实际只有标题+危险度+状态行)
            // → 不建图元标题窗 (位置被游戏窗口系统摆布不受控), 改 UguiBuilder 左上角 uGUI 标题面板
            // (位置/字号走布局 title 区, F10 热重载重建见 RelayoutTitle)。有 points 的 legacy 场景走原图元窗。
            if (def.Points.Count == 0)
            {
                BuildSceneTitleUgui(def);
                return;
            }
            int n = def.Points.Count;
            var grid = new GridPixelElement(1, n + 2, false);
            if (grid == null) throw new Exception("scene grid ctor null");
            int y = 0;
            // v1.29.1: 窗口随内容自适应宽度, 标题栏文本不参测 → 长中文场景名溢出压 X 按钮
            // (实测 "废弃军械库·脚本版" 8 字与 X 互叠)。用标题估算宽定住信息行 (fixedWidth
            // 撑开列宽, PsUi label size 同款机制); 阈值 150 = "危险度 ★★★★★" 自然宽上限,
            // 低于它不设定宽防信息行折行。
            int titleW = UI.PixelPin.TitleBarContentWidth(def.Name);
            var info = new TagElement(titleW > 150 ? titleW : -1, false).SetText(
                "危险度 " + new string('★', Math.Max(1, Math.Min(5, def.Danger))),
                10000, RenderHandler.ColorPalette.Orange);
            Seat(grid, info == null ? null : info.TryCast<PixelElement>(), 0, y++);
            foreach (var p in def.Points)
            {
                var be = NewPointButton(def, p);
                Seat(grid, be == null ? null : be.TryCast<PixelElement>(), 0, y++);
            }
            _statusTag = new TagElement(-1, false).SetText(" ", 10000, RenderHandler.ColorPalette.White);
            Seat(grid, _statusTag == null ? null : _statusTag.TryCast<PixelElement>(), 0, y++);

            _sceneWindow = new PixelWindow(420, 110 + (n + 2) * 46, true, def.Name);
            if (_sceneWindow == null) throw new Exception("scene window ctor null");
            _sceneWindow.Attach(grid.TryCast<PixelElement>());
            _sceneWindow.Center();
            _sceneWindow.Show();
        }

        private static void Seat(GridPixelElement grid, PixelElement el, int x, int y)
        {
            if (grid == null || el == null) return;
            try { grid.AttachPos(el, x, y); }
            catch { try { grid.Attach(el, x, y); } catch { } }
        }

        /// <summary>v1.30.0: 无互动物场景的左上角标题 uGUI 面板 (替代原图元 PixelWindow 标题窗) —
        /// 三行 = 场景名 (title.font×1.5 较大字号) / 危险度 ★ / 状态行 (替代 _statusTag;
        /// 空状态不渲染任何占位 — 顺带收掉 v1.29.1 遗留的空状态行小白块)。
        /// 位置/字号走布局 title 区 (x/y=左上角屏比); 层级与选图场景同规 (探测图元窗 canvas,
        /// canvas_order 可强制); 纯展示无 raycaster 不吃点击, pss HUD/图元窗交互不受影响。</summary>
        private void BuildSceneTitleUgui(SceneDef def)
        {
            if (_ugui == null) _ugui = new UI.UguiBuilder(_logger, _pins);
            var L = PSApi.Events.Raid.RaidLayout.Active;   // v1.31.0: 当前场景三层布局 (进场景时已刷新)
            var T = L.Title;
            float font = PSApi.Events.Raid.RaidLayout.ClampFont(T.Font);
            float nameFont = PSApi.Events.Raid.RaidLayout.ClampFont(font * 1.5f);
            int pixelOrder = _ugui.DetectPixelCanvasOrder(3, "场景标题");
            int order = L.CanvasOrder > 0 ? L.CanvasOrder : pixelOrder;
            _titleHud = _ugui.MakeCanvas("PSApiSceneTitle", order, false);
            if (_titleHud is null) { PsApi.Warn(_logger, "[scenes] 场景标题 canvas 创建失败"); return; }
            var root = _titleHud.transform.TryCast<RectTransform>();   // Il2Cpp 红线: 不用 as
            if (root is null)
            {
                PsApi.Warn(_logger, "[scenes] 场景标题 RectTransform 获取失败");
                try { UnityEngine.Object.Destroy(_titleHud); } catch { }
                _titleHud = null;
                return;
            }

            const float pad = 10f;
            float nameH = nameFont + 8f, lineH = font + 6f;
            string sceneName = string.IsNullOrEmpty(def.Name) ? def.FullId : def.Name;
            float w = sceneName.Length * nameFont + 2 * pad;
            if (w < 220f) w = 220f;
            if (w > 560f) w = 560f;
            float h = pad + nameH + lineH + lineH + pad;
            var panel = _ugui.MakePanel(root, "panel", w, h, new Color(0.07f, 0.07f, 0.10f, 0.92f), false);
            UI.UguiBuilder.AnchorTopLeft(panel, root, T.X, T.Y);

            float y = pad;
            var nameTmp = _ugui.MakeText(panel, "name", sceneName, nameFont,
                new Color(0.95f, 0.88f, 0.72f), Il2CppTMPro.TextAlignmentOptions.Left);
            nameTmp.rectTransform.sizeDelta = new Vector2(w - 2 * pad, nameH);
            SeatTopLeft(nameTmp.rectTransform, pad, y);
            y += nameH;
            var dangerTmp = _ugui.MakeText(panel, "danger",
                "危险度 " + new string('★', Math.Max(1, Math.Min(5, def.Danger))), font,
                new Color(0.90f, 0.60f, 0.25f), Il2CppTMPro.TextAlignmentOptions.Left);
            dangerTmp.rectTransform.sizeDelta = new Vector2(w - 2 * pad, lineH);
            SeatTopLeft(dangerTmp.rectTransform, pad, y);
            y += lineH;
            _statusUgui = _ugui.MakeText(panel, "status", "", font,
                new Color(0.92f, 0.92f, 0.92f), Il2CppTMPro.TextAlignmentOptions.Left);
            _statusUgui.rectTransform.sizeDelta = new Vector2(w - 2 * pad, lineH);
            SeatTopLeft(_statusUgui.rectTransform, pad, y);
            PsApi.Log(_logger, $"[scenes] 场景标题 uGUI 面板已建 (无互动物, 锚=({T.X:0.###},{T.Y:0.###}) font={font}, canvas order={order})");
        }

        /// <summary>面板内顶对齐竖排 (左上锚): 子元素锚/pivot (0,1), anchoredPosition = (x, -offsetY)。</summary>
        private static void SeatTopLeft(RectTransform rt, float x, float offsetY)
        {
            if (rt is null) return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -offsetY);
        }

        /// <summary>v1.30.0: F10 布局热重载 — 标题面板开着 = 销毁按新布局重建 (场景不变);
        /// 没开 = 下次进场景自动用新布局。</summary>
        internal void RelayoutTitle()
        {
            if (_titleHud is null) return;
            var def = Active;
            try { UnityEngine.Object.Destroy(_titleHud); } catch { }
            _titleHud = null;
            _statusUgui = null;
            if (def != null) BuildSceneTitleUgui(def);
            PsApi.Log(_logger, "[scenes] 场景标题已按新布局重建 (F10)");
        }

        /// <summary>M1.5: psui 驱动的场景窗口 — 复用 PsUI 图元后端构建 (不登记 open 表, SceneService 全权
        /// 管理生命周期)。按钮接线: elem_id 命中 def.Points 的按钮在构建期即被换上 OnPointClick 委托
        /// (自动获得 psapi.scene.interact 发布 + exit 撤离 + loot 搜索/次数/耗尽改名内置逻辑, 与自动网格
        /// 路径一致); 其余按钮保留 psui 声明的 on_click (pss 全自定义)。elem_id=status 的 TagElement
        /// → 状态行 (没有则 _statusTag=null, SetStatus 已有 null 容忍)。失败抛异常由调用方回退自动网格。</summary>
        private void BuildSceneUiFromPsui(SceneDef def)
        {
            if (_ui == null) throw new Exception("PsUiService 未接线 (场景 ui 键需要)");
            var pp = _ui.BuildSceneWindow(def.Ui, def.PackId, id => SceneClickOverride(def, id));
            _sceneWindow = pp.Window;
            if (_sceneWindow == null) throw new Exception("psui 构建未产生窗口");
            TagElement status;
            _statusTag = pp.Labels.TryGetValue("status", out status) && status != null ? status : null;
            // 次数耗尽改名用: 构建后 Buttons 字典已按 elem_id 登记, 挑出互动物按钮
            foreach (var p in def.Points)
            {
                ButtonElement be;
                if (pp.Buttons.TryGetValue(p.Id, out be) && be != null) _pointButtons[p.Id] = be;
            }
            try { _sceneWindow.Center(); } catch { }
            _sceneWindow.Show();
            PsApi.Log(_logger, $"[scenes] 场景窗口由 psui '{def.PackId}:{def.Ui}' 构建 (points={_pointButtons.Count}/{def.Points.Count}, status={_statusTag != null})");
        }

        /// <summary>psui 按钮换线钩子 (构建期逐按钮按 elem_id 判定): 命中互动物点 → OnPointClick 委托
        /// (双 pin 防 GC); 未命中 → null = 保留 psui 声明的 on_click。</summary>
        private Il2CppSystem.Action SceneClickOverride(SceneDef def, string elemId)
        {
            if (string.IsNullOrEmpty(elemId)) return null;
            ScenePointDef match = null;
            foreach (var p in def.Points) if (p.Id == elemId) { match = p; break; }
            if (match == null) return null;
            string pid = match.Id;
            System.Action act = () => OnPointClick(def, pid);
            var iact = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(act);
            _pins.Add(act); _pins.Add(iact);
            return iact;
        }

        private ButtonElement NewPointButton(SceneDef def, ScenePointDef p)
        {
            string pid = p.Id;
            System.Action act = () => OnPointClick(def, pid);
            var iact = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(act);
            _pins.Add(act); _pins.Add(iact);
            var be = new ButtonElement(p.Label, 300, 30, iact);   // v1.29.1: 去掉 ◄ 撤离前缀 (字体缺字形渲染成方框)
            if (be == null) return null;
            _pointButtons[pid] = be;
            return be;
        }

        private void OnPointClick(SceneDef def, string pointId)
        {
            ScenePointDef p = null;
            foreach (var q in def.Points) if (q.Id == pointId) { p = q; break; }
            if (p == null) return;
            Publish("psapi.scene.interact", def, null, p, 0);

            if (p.Type == "exit") { TryExit("extract"); return; }

            // ---- loot 点: 战利品直接进原版外出背包 (EmporiumEntry.afterhourInventory) ----
            string stateKey = StateKey(def);
            Dictionary<string, int> used;
            if (!_used.TryGetValue(stateKey, out used)) { used = new Dictionary<string, int>(StringComparer.Ordinal); _used[stateKey] = used; }
            int n;
            used.TryGetValue(p.Id, out n);
            if (n >= p.Times) { SetStatus("这里已经搜干净了"); return; }

            GameGridInventory afterhour = null;
            try { afterhour = EmporiumEntry.Instance?.afterhourInventory; } catch { }
            if (afterhour == null) { SetStatus("外出背包不可用"); return; }

            string itemId = p.Loot[_rng.Next(p.Loot.Count)];
            int count = SceneJson.ParseCount(p.LootCount, _rng);
            GameItem item = null;
            try { item = ItemsFacade.CreateIntoSlot(afterhour, itemId, count); } catch { }
            if (item == null)
            {
                SetStatus("外出背包已满, 装不下了");
                return;
            }
            used[p.Id] = n + 1;
            if (n + 1 >= p.Times)
            {
                try
                {
                    ButtonElement be;
                    if (_pointButtons.TryGetValue(p.Id, out be) && be != null) be.Set(p.Label + " (已搜完)");
                }
                catch { }
                SetStatus(p.Label + ": 搜完了");
            }
            else SetStatus(p.Label + ": 找到 " + ItemsFacade.DisplayName(itemId) + " x" + count);
            Publish("psapi.scene.loot", def, null, p, count, itemId);
        }

        private void SetStatus(string msg)
        {
            try
            {
                if (_statusTag != null)
                {
                    // v2.0.5: 空文本直清 (原生钳制空文本双向累加)
                    if (PsUI.PsUiPixelBackend.TagTextIsEmpty(msg)) PsUI.PsUiPixelBackend.ClearTagText(_statusTag, _logger, "scene:status");
                    else
                    {
                        _statusTag.SetText(msg, 10000, RenderHandler.ColorPalette.White);
                        PsUI.PsUiPixelBackend.ResetTagHeight(_statusTag, _logger, "scene:status"); // v2.0.4: 原生高度累加修复
                    }
                }
            }
            catch { }
            // v1.30.0: 标题 uGUI 面板状态行 (空串 = 不渲染, 无占位)
            try { if (_statusUgui is not null) _statusUgui.text = msg ?? ""; } catch { }
        }

        // ==================== 工具 ====================

        private string StateKey(SceneDef def)
        {
            long day = 0;
            try { if (GameHooks.GameDay.TryRead(out long d, out _, out _)) day = d; } catch { }
            return day + "|" + def.FullId;
        }

        private void Publish(string evt, SceneDef def, string reason, ScenePointDef p, int count, string itemId = null)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["scene"] = def.FullId;
            dict["name"] = def.Name;
            dict["danger"] = (long)def.Danger;
            if (reason != null) dict["reason"] = reason;
            if (p != null) { dict["point"] = p.Id; dict["type"] = p.Type; }
            if (itemId != null) dict["item"] = itemId;
            if (count > 0) dict["count"] = (long)count;
            try { if (GameHooks.GameDay.TryRead(out long d, out _, out _)) dict["day"] = d; } catch { }
            _bus.Publish(evt, dict);
        }
    }
}
