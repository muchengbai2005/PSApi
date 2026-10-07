using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using PSApi;
using PSApi.Events.PsScript;

namespace PSApi.Events
{
    /// <summary>
    /// PSApi.Events — 事件模块: 启动扫包 → PSScript 内核(E1 语言 + E2 状态/时间/数值 API)加载
    /// events/*.pss → on 块登记到事件总线; ModHook 官方事件桥接(GameHooks); 存档槽状态隔离。
    /// v2.0.0: 合并为单 PSApi.dll (方案 A) — 本类不再是 MelonMod, 由根 PsApiPlugin 两段式初始化驱动
    /// (段 2 = 本模块, 等价原 MelonPriority 20 在 Items 10 之后, 事件可安全引用物品);
    /// Harmony PatchAll 收拢到根插件全程序集一次 (防 Items/Events 各一次双挂)。
    /// </summary>
    internal sealed class EventsPlugin
    {
        internal static EventsPlugin Instance;

        private readonly MelonLogger.Instance _logger;
        /// <summary>兼容属性: 模块内沿用 LoggerInstance 写法 (v2.0.0 前是 MelonMod 成员)。</summary>
        internal MelonLogger.Instance LoggerInstance => _logger;

        /// <summary>E7 补丁类(static)发事件的通道; OnInitializeMelon 后非空。</summary>
        internal static EventBus Bus { get; private set; }

        /// <summary>补丁(static)侧的告警通道; LoggerInstance 未就绪时静默。</summary>
        internal static void LogWarn(string msg)
        {
            try { if (Instance?.LoggerInstance != null) PsApi.Warn(Instance.LoggerInstance, msg); } catch { }
        }

        private readonly FrameSlicer _slicer = new FrameSlicer();
        private EventBus _bus;              // LoggerInstance 构造期尚未赋值, 一律 OnInitializeMelon 才建
        private PsScriptEngine _engine;
        private GameHooks _hooks;
        private StoreEventService _storeEvents;
        private NpcService _npcs;
        private InjectService _inject;
        private LootPoolService _loot;
        private PsUI.PsUiService _ui;
        private Scenes.SceneService _scenes;
        private UI.SceneUguiService _sceneUgui;
        private Scenes.ScriptGridService _scriptGrid;
        private Scenes.ScriptCombatService _scriptCombat;
        private Scenes.SceneLogService _sceneLog;
        private Scenes.RtCombatService _rtCombat;
        private List<PackInfo> _packs = new List<PackInfo>();
        private float _tick;                // M3: 1Hz tick 事件计时
        private MelonPreferences_Entry<string> _hotkeyEntry; // M3: 管理面板热键(默认 F6)

        /// <summary>E4 NPC 服务(IsClientSelling/OpenUI 补丁经此回调)。</summary>
        internal NpcService Npcs => _npcs;

        /// <summary>M1 注入服务(补丁(static)侧经此回调)。</summary>
        internal InjectService Inject => _inject;

        /// <summary>自定义外出场景服务(SceneMapPatch 经此回调)。</summary>
        internal Scenes.SceneService Scenes => _scenes;

        /// <summary>P3 (v1.28.0): script 场景 grid 能力服务 (QuickTransferPatches 快捷转移重定向门控)。</summary>
        internal Scenes.ScriptGridService ScriptGridSvc => _scriptGrid;

        internal EventsPlugin(MelonLogger.Instance logger)
        {
            Instance = this;
            _logger = logger;
        }

        internal void Init()
        {
            _bus = new EventBus(LoggerInstance);
            Bus = _bus;
            _storeEvents = new StoreEventService(LoggerInstance, _bus);
            _npcs = new NpcService(LoggerInstance, _bus);
            _inject = new InjectService(LoggerInstance, _npcs);
            _inject.Subscribe(_bus);
            _loot = new LootPoolService(LoggerInstance);
            _ui = new PsUI.PsUiService(LoggerInstance);
            _scenes = new Scenes.SceneService(LoggerInstance, _bus, _ui);
            // v1.33.0 (波 3): C# raid 服务已随 builtin_raid 退役删除 (raid 玩法只剩 _raid pss 库)
            _sceneUgui = new UI.SceneUguiService(LoggerInstance);      // P2 (v1.27.0): 场景 uGUI 创建服务 (ui.panel/text/button/bar 后端)
            _sceneUgui.Scenes = _scenes;
            _scenes.SceneUgui = _sceneUgui;
            // P3 (v1.28.0): script 场景能力服务 (grid.*/combat.*/scene.log 后端)
            _scriptGrid = new Scenes.ScriptGridService(LoggerInstance);
            _scriptGrid.Scenes = _scenes;
            _scriptGrid.Ui = _ui;
            _scriptCombat = new Scenes.ScriptCombatService(LoggerInstance);
            _scriptCombat.Scenes = _scenes;
            _scriptCombat.Grid = _scriptGrid;
            _sceneLog = new Scenes.SceneLogService(LoggerInstance);
            _sceneLog.Scenes = _scenes;
            _scriptGrid.LogSvc = _sceneLog;
            _scenes.ScriptGrid = _scriptGrid;
            _scenes.ScriptCombat = _scriptCombat;
            _scenes.SceneLog = _sceneLog;
            // v1.29.0: 实时动作战斗会话服务 (combat.rt_start 后端; Plugin.OnUpdate 每帧 Tick)
            _rtCombat = new Scenes.RtCombatService(LoggerInstance);
            _rtCombat.Scenes = _scenes;
            _rtCombat.Grid = _scriptGrid;
            _rtCombat.LogSvc = _sceneLog;
            _rtCombat.Combat = _scriptCombat;
            _rtCombat.PackSourceOf = id =>
            {
                foreach (var p in _packs) if (p != null && p.Id == id) return p.Source;
                return null;
            };
            _scriptCombat.Rt = _rtCombat;
            _scenes.RtCombat = _rtCombat;
            _slicer.Enqueue(() => _rtCombat?.Prewarm());   // 程序化 sprite 三件套预热 (首战斗零卡顿)
            // v1.4.0: 机器绑定 PSUI 面板 — Items 侧 ui="psui" 机器经此 hook 转交 Events 装配图元树窗口
            try { PSApi.Items.ItemsFacade.PsuiMachineAttach = (machine, itemId, panelRef) => _ui.AttachMachinePanel(machine, itemId, panelRef); }
            catch (Exception e) { PsApi.Warn(LoggerInstance, "PsuiMachineAttach hook 注册失败: " + e.Message); }
            _engine = new PsScriptEngine(LoggerInstance, _bus, _storeEvents, _npcs, _ui, _inject, _loot, _scenes);
            _hooks = new GameHooks(LoggerInstance, _bus, _storeEvents, _npcs);
            _hooks.Subscribe();
            // v2.0.0: Harmony PatchAll 收拢到根 PsApiPlugin 全程序集一次 (自定义犯罪显示名(CrimeNamePatch) +
            // E4 摆货/出售资格(NpcService) 等本模块 patch 一并覆盖); 下方补丁自检保留
            // v1.13.7: 补丁自检 —— 溢价闸门补丁是否真实挂上, 启动即见分晓(排查"补丁没生效"类假象)
            try
            {
                var m = AccessTools.Method(typeof(GameItem), "AddClientItemBuyingFeature");
                var info = m == null ? null : HarmonyLib.Harmony.GetPatchInfo(m);
                PsApi.Log(LoggerInstance, $"[premium-gate] 补丁自检: target={(m == null ? "MISSING" : "ok")} prefixes={(info == null ? 0 : info.Prefixes.Count)}");
                var m2 = AccessTools.Method(typeof(GameItem), "AddItemFeature");
                var info2 = m2 == null ? null : HarmonyLib.Harmony.GetPatchInfo(m2);
                PsApi.Log(LoggerInstance, $"[premium-watch] 补丁自检: target={(m2 == null ? "MISSING" : "ok")} prefixes={(info2 == null ? 0 : info2.Prefixes.Count)}");
                // v1.13.9: 曝光闸门+路径追踪补丁一并自检
                foreach (var pair in new[]
                {
                    ("StoreClient.CanClientExposeAnyFeature", AccessTools.Method(typeof(StoreClient), "CanClientExposeAnyFeature")),
                    ("StoreClient.ClientExposeFeature", AccessTools.Method(typeof(StoreClient), "ClientExposeFeature")),
                    ("StoreClient.OnItemPlaced", AccessTools.Method(typeof(StoreClient), "OnItemPlaced")),
                    ("PlayerStore.PlacedItemForSelling", AccessTools.Method(typeof(PlayerStore), "PlacedItemForSelling")),
                })
                {
                    var pi = pair.Item2 == null ? null : HarmonyLib.Harmony.GetPatchInfo(pair.Item2);
                    PsApi.Log(LoggerInstance, $"[pm-self] {pair.Item1}: target={(pair.Item2 == null ? "MISSING" : "ok")} patches={(pi == null ? 0 : pi.Prefixes.Count + pi.Postfixes.Count)}");
                }
                // v1.25.1: ctrl+左键快捷转移重定向补丁自检 (bug5 防物品进隐藏 afterhour 窗)
                var mqt = AccessTools.Method(typeof(ItemQuickTransferHandler), "OnEventRelease");
                var iqt = mqt == null ? null : HarmonyLib.Harmony.GetPatchInfo(mqt);
                PsApi.Log(LoggerInstance, $"[quick-transfer] 补丁自检: target={(mqt == null ? "MISSING" : "ok")} prefixes={(iqt == null ? 0 : iqt.Prefixes.Count)}");
                // v1.48.3: 锁交互槽补丁自检 — v1.48.2 教训: 补丁挂了但 Traverse.Field 读 interop 属性静默失败,
                // 只验 target 不够, 顺带把成员形态打出来 (field=无/property=有 即为根因实证)
                PsApi.Log(LoggerInstance, $"[lock-self] ItemContextHandler.currentItem 成员形态: field={(AccessTools.Field(typeof(ItemContextHandler), "currentItem") == null ? "无" : "有")} property={(AccessTools.Property(typeof(ItemContextHandler), "currentItem") == null ? "无" : "有")}");
                foreach (var pair in new[]
                {
                    ("ItemContextHandler.UpdateConditions", AccessTools.Method(typeof(ItemContextHandler), "UpdateConditions")),
                    ("ItemContextHandler.TriggerButton", AccessTools.Method(typeof(ItemContextHandler), "TriggerButton")),
                    ("ItemDefaultActivateHandler.OnEventPress", AccessTools.Method(typeof(ItemDefaultActivateHandler), "OnEventPress")),
                    ("ItemMouseDoubleClickHandler.OpenContentAction", AccessTools.Method(typeof(ItemMouseDoubleClickHandler), "OpenContentAction")),
                    ("ItemMouseDoubleClickHandler.OpenExamineAction", AccessTools.Method(typeof(ItemMouseDoubleClickHandler), "OpenExamineAction")),
                    ("GeneralHelper.MayPlayerInsertInto(GameItem)", AccessTools.Method(typeof(GeneralHelper), "MayPlayerInsertInto", new Type[] { typeof(GameItem) })),
                    ("GraphUtils.CanInsertToActiveContainer", AccessTools.Method(typeof(GraphUtils), "CanInsertToActiveContainer")),
                    ("GraphUtils.InsertToActiveContainer", AccessTools.Method(typeof(GraphUtils), "InsertToActiveContainer")),
                    ("ItemSelectHandler.TrySelectItem", AccessTools.Method(typeof(ItemSelectHandler), "TrySelectItem")),
                    ("ItemSelectHandler.TryEquipItem", AccessTools.Method(typeof(ItemSelectHandler), "TryEquipItem")),
                })
                {
                    var pl = pair.Item2 == null ? null : HarmonyLib.Harmony.GetPatchInfo(pair.Item2);
                    PsApi.Log(LoggerInstance, $"[lock-self] {pair.Item1}: target={(pair.Item2 == null ? "MISSING" : "ok")} patches={(pl == null ? 0 : pl.Prefixes.Count + pl.Postfixes.Count)}");
                }
            }
            catch (Exception e) { PsApi.Warn(LoggerInstance, "补丁自检失败: " + e.Message); }
            PsApi.EnsureDirs();
            // v1.31.0: 布局三层覆写 — 旧全局文件迁移提示 + 包文件读取委托接线 (数据源随 Rescan 的 _packs 惰性读)
            Raid.RaidLayout.CheckLegacyGlobalFile(LoggerInstance);
            Raid.RaidLayout.PackLayoutTextOf = id => ReadPackText(id, "scenes/layout.json");
            Raid.RaidLayout.SceneLayoutTextOf = (id, sid) => ReadPackText(id, "scenes/" + sid + "/layout.json");
            // v1.48.8: 第四层用户覆写 (物理文件, 内嵌 DLL 包也能读; M 写回内嵌包时落这里)
            Raid.RaidLayout.UserLayoutTextOf = Raid.RaidLayout.ReadUserOverrideText;
            Raid.RaidLayout.PackLoadOrder = () =>
            {
                var ids = new List<string>();
                foreach (var p in _packs) if (p != null && p.Valid) ids.Add(p.Id);
                return ids;
            };
            Raid.RaidLayout.PackDirOf = id =>
            {
                foreach (var p in _packs) if (p != null && p.Valid && p.Id == id) return p.Dir;
                return null;
            };
            Raid.RaidLayout.RefreshActive(null, null, LoggerInstance);   // 初始 = 代码默认 (进场景时 SceneService 刷新)
            // v2.0.0 修复: 根 PsApiPlugin.MigratePreferences 已建 [PSApi] 类目与 ManagerHotkey 条目,
            // MelonLoader 0.7.x 的 CreateEntry 对已存在条目会抛 "Already Exists" (而非返回旧实例,
            // 实测 2026-10-07 首启即炸) — 改为 get-or-create, 与根插件共用同一实例。
            var cat = MelonPreferences.GetCategory("PSApi") ?? MelonPreferences.CreateCategory("PSApi", "PSApi 统一宿主");
            _hotkeyEntry = cat.GetEntry<string>("ManagerHotkey")
                ?? cat.CreateEntry("ManagerHotkey", "F6", "管理面板热键(KeyCode 名, 如 F6/F7)");
            Rescan("init");
            SelfTest();
            PsApi.Log(LoggerInstance, $"[events] 模块初始化完成 (PSApi v{PsApi.Version}). packs={_packs.Count}");
        }

        internal void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName != "EmporiumMenu")
                SaveStates.EnsureSlot(LoggerInstance);
            // WP4: 主菜单弹内容包冲突窗 (游戏无独立主菜单场景, 启动即 EmporiumMenu, 主菜单 UI 在其中;
            // 进程级只弹一次, 无冲突内部直接返回)
            if (sceneName == "EmporiumMenu")
                PackConflictPopup.MaybeShow(LoggerInstance);
            _npcs?.OnSceneLeft(); // E7: 场景销毁后旧 Dialogue 指针/选项委托失效, 清注册表与 pin
            _scenes?.OnSceneLeft(); // 外出场景运行时对象随 Unity 场景销毁, 清引用
            // 场景事件是触发器(on 事件)的天然信号源之一, P3 接入完整事件集
            _bus.Publish("psapi.scene.loaded", sceneName);
        }

        internal void OnUpdate()
        {
            _slicer.Drive();
            _inject?.Poll();
            _loot?.Poll(); // v1.10.0: loot_pool 表就绪轮询入池
            _ui?.PollPositions(); // M3: 窗口位置记忆(拖动后重开/重建恢复)
            _ui?.PollPixelPanels(); // U4: 图元面板关窗检测 + 槽位 on_change 差分 + 位置记忆
            _ui?.PollMachinePanels(); // v1.4.0: 机器绑定面板 on_open 跳变 + 槽位 on_change 差分 + 失效清理
            _ui?.PollKeyBinds(); // v1.16.0: ui.bind_key 按键-面板绑定轮询 (按下 toggle)
            _ui?.PollSubmitInputs(); // v1.16.0: input on_submit 回车提交检测 (Return/KeypadEnter)
            _scenes?.Poll(); // 外出场景: raid 窗口被关重开 + 商店根意外激活兜底撤离
            _rtCombat?.Tick(); // v1.29.0: 实时动作战斗每帧驱动 (输入轮询/预警/准星; 含 X 收武器)

            // M3: 1Hz tick 事件(管理面板刷新等低频周期任务; 脚本不走每帧)
            _tick += UnityEngine.Time.deltaTime;
            if (_tick >= 1f)
            {
                _tick = 0f;
                _bus?.Publish("psapi.tick", GameHooks.GameDay.Payload(withDayFields: true));
            }

            // M3: 管理面板热键(默认 F6, MelonPreferences 可改)
            if (_hotkeyEntry != null)
            {
                UnityEngine.KeyCode key;
                try { key = Hotkey; } catch { return; }
                if (UnityEngine.Input.GetKeyDown(key))
                {
                    try { _ui?.TogglePanel("psapi_manager:manager"); }
                    catch (Exception e) { PsApi.Warn(LoggerInstance, "管理面板热键: " + e.Message); }
                }
            }

            // v1.24.0: F10 布局热重载 (v1.31.0 三层覆写 / v1.48.8 第四层用户覆写: 重读当前场景 默认+包级+场景级+用户覆写 → 重建 uGUI HUD
            // + 重钉图元网格窗 + 选图场景/场景标题重建 + rt 战斗 UI 战斗中重建)
            bool f10 = false;
            try { f10 = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F10); } catch { }
            if (f10)
            {
                try
                {
                    Raid.RaidLayout.RefreshActive(_scenes?.Active?.PackId, _scenes?.Active?.ShortId, LoggerInstance);
                    _scenes?.RelayoutPicker();
                    _scenes?.RelayoutTitle();   // v1.30.0: 无互动物场景标题面板按新布局重建
                    _rtCombat?.Relayout();      // v1.31.0: rt 战斗 UI 战斗中按新 rt 区重建
                    PsApi.Log(LoggerInstance, "[layout] F10 布局热重载完成");
                }
                catch (Exception e) { PsApi.Warn(LoggerInstance, "[layout] F10 热重载失败: " + e.Message); }
            }

            // v1.30.0: M 键写回 — 场景内拖好的图元窗 (箱/地面/容器) 当前屏比位置写进布局
            // (v1.31.0 起目标 = 当前场景的场景级 packs/<pack>/scenes/<id>/layout.json, ground.mode 置 free;
            // v1.48.8 内嵌包改写用户覆写层 layout_overrides/<pack>/<sceneId>.json)
            // 并即时生效; 仅在自定义场景激活时生效 (与 X 键 InScene 判断同规)
            bool mKey = false;
            try { mKey = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.M); } catch { }
            if (mKey && _scenes != null && _scenes.Active != null)
            {
                try { _scenes.WriteBackLayoutPins(); }
                catch (Exception e) { PsApi.Warn(LoggerInstance, "[layout] M 写回失败: " + e.Message); }
            }
        }

        private UnityEngine.KeyCode _hotkeyCache = UnityEngine.KeyCode.F6;
        private string _hotkeyRaw = "F6";
        private UnityEngine.KeyCode Hotkey
        {
            get
            {
                string raw = _hotkeyEntry.Value ?? "F6";
                if (raw == _hotkeyRaw) return _hotkeyCache;
                _hotkeyRaw = raw;
                if (!Enum.TryParse(raw.Trim(), true, out _hotkeyCache))
                {
                    _hotkeyCache = UnityEngine.KeyCode.F6;
                    PsApi.Warn(LoggerInstance, $"ManagerHotkey '{raw}' 无法解析, 回退 F6");
                }
                return _hotkeyCache;
            }
        }

        // v1.14.2: OnDeinitializeMelon 不再 FlushAll —— 状态只随游戏真实存档落盘(SaveSyncPatch),
        // 退出游戏时不落盘未存档的变更, 与游戏回档语义严格一致。

        /// <summary>重扫内容包(将来供控制台/热重载命令用): 重新加载全部 .pss 脚本。</summary>
        internal void Rescan(string why)
        {
            // WP2: 与 Items 同规则合并 (描述符读 Items public 注册表; Events 自造 EmbeddedPackSource)
            var mergeConflicts = new List<PackMerger.Conflict>();
            _packs = PackMerger.Merge(PackScanner.Scan(LoggerInstance), SnapshotEmbedded(), mergeConflicts, LoggerInstance);
            foreach (var c in mergeConflicts)
                PsApi.Warn(LoggerInstance, $"pack conflict [{c.Kind}]: {c.Detail}");
            int errors = _engine.LoadAll(_packs);
            errors += _scenes?.ScanPacks(_packs) ?? 0; // scenes/*.json (自定义外出场景)
            PsApi.Log(LoggerInstance, $"rescan({why}): {_packs.Count} pack(s), {_engine.LoadedFileCount} pss file(s), {_engine.HandlerCount} handler(s), {_scenes?.SceneCount ?? 0} scene(s), {errors} compile error(s)");
        }

        // 硬引用 PSApi.Items (csproj Private=false); Items 缺失时 JIT 异常, 按空列表降级 (同 ItemsFacade 现状防御)
        private static List<PackMerger.EmbeddedDescriptor> SnapshotEmbedded()
        {
            try
            {
                var descs = new List<PackMerger.EmbeddedDescriptor>();
                foreach (var p in PSApi.Items.EmbeddedPackRegistry.Packs)
                    descs.Add(new PackMerger.EmbeddedDescriptor
                    {
                        Id = p.Id,
                        Version = p.Version,
                        Prerequisites = p.Prerequisites,
                        ResourceAssembly = p.ResourceAssembly,
                        ResourcePrefix = p.ResourcePrefix,
                    });
                return descs;
            }
            catch (Exception e)
            {
                LogWarn("EmbeddedPackRegistry 读取失败, 按无内嵌包降级: " + e.Message);
                return new List<PackMerger.EmbeddedDescriptor>();
            }
        }

        /// <summary>v1.31.0: 包内文本文件读取 (布局委托用): 按 id 找有效包, HasFile 才读; 任何失败 = null。</summary>
        private string ReadPackText(string packId, string rel)
        {
            try
            {
                foreach (var p in _packs)
                    if (p != null && p.Valid && p.Id == packId && p.Source != null && p.Source.HasFile(rel))
                        return p.Source.ReadText(rel);
            }
            catch (Exception e) { PsApi.Warn(LoggerInstance, $"[layout] 读取 {packId}/{rel} 失败: {e.Message}"); }
            return null;
        }

        /// <summary>总线自检: 订阅→发布→once 语义验证(仅日志, 无游戏副作用)。</summary>
        private void SelfTest()
        {
            int hits = 0;
            _bus.Subscribe("psapi.selftest", _ => hits++, owner: "selftest");
            _bus.Subscribe("psapi.selftest", _ => hits += 10, once: true, owner: "selftest-once");
            _bus.Publish("psapi.selftest");
            _bus.Publish("psapi.selftest");
            // 期望: hits = (1+10) + 1 = 12; once 第二次不触发
            PsApi.Log(LoggerInstance, $"event bus selftest hits={hits} (expect 12) sceneSubs={_bus.SubscriberCount("psapi.scene.loaded")}");
        }
    }
}
