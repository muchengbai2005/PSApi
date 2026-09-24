using System;
using System.Collections.Generic;
using MelonLoader;
using PSApi;
using PSApi.Events.PsScript;

[assembly: MelonInfo(typeof(PSApi.Events.EventsPlugin), "PSApi.Events", "1.13.1", "Research")]
[assembly: MelonGame("Questing Goose Studio", "Probably Stolen")]
[assembly: MelonPriority(20)]

namespace PSApi.Events
{
    /// <summary>
    /// PSApi.Events — 事件宿主: 启动扫包 → PSScript 内核(E1 语言 + E2 状态/时间/数值 API)加载
    /// events/*.pss → on 块登记到事件总线; ModHook 官方事件桥接(GameHooks); 存档槽状态隔离。
    /// MelonPriority 20: 在 PSApi.Items(10) 之后, 事件可安全引用物品。
    /// </summary>
    public class EventsPlugin : MelonMod
    {
        internal static EventsPlugin Instance;

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
        private List<PackInfo> _packs = new List<PackInfo>();
        private float _tick;                // M3: 1Hz tick 事件计时
        private MelonPreferences_Entry<string> _hotkeyEntry; // M3: 管理面板热键(默认 F6)

        /// <summary>E4 NPC 服务(IsClientSelling/OpenUI 补丁经此回调)。</summary>
        internal NpcService Npcs => _npcs;

        /// <summary>M1 注入服务(补丁(static)侧经此回调)。</summary>
        internal InjectService Inject => _inject;

        public EventsPlugin()
        {
            Instance = this;
        }

        public override void OnInitializeMelon()
        {
            _bus = new EventBus(LoggerInstance);
            Bus = _bus;
            _storeEvents = new StoreEventService(LoggerInstance, _bus);
            _npcs = new NpcService(LoggerInstance, _bus);
            _inject = new InjectService(LoggerInstance, _npcs);
            _inject.Subscribe(_bus);
            _loot = new LootPoolService(LoggerInstance);
            _ui = new PsUI.PsUiService(LoggerInstance);
            // v1.4.0: 机器绑定 PSUI 面板 — Items 侧 ui="psui" 机器经此 hook 转交 Events 装配图元树窗口
            try { PSApi.Items.ItemsFacade.PsuiMachineAttach = (machine, itemId, panelRef) => _ui.AttachMachinePanel(machine, itemId, panelRef); }
            catch (Exception e) { PsApi.Warn(LoggerInstance, "PsuiMachineAttach hook 注册失败: " + e.Message); }
            _engine = new PsScriptEngine(LoggerInstance, _bus, _storeEvents, _npcs, _ui, _inject, _loot);
            _hooks = new GameHooks(LoggerInstance, _bus, _storeEvents, _npcs);
            _hooks.Subscribe();
            try { HarmonyInstance.PatchAll(); } // 自定义犯罪显示名(CrimeNamePatch) + E4 摆货/出售资格(NpcService)
            catch (Exception e) { PsApi.Warn(LoggerInstance, "Harmony PatchAll failed: " + e.Message); }
            PsApi.EnsureDirs();
            var cat = MelonPreferences.CreateCategory("PSApi.Events", "PSApi 事件与脚本");
            _hotkeyEntry = cat.CreateEntry("ManagerHotkey", "F6", "管理面板热键(KeyCode 名, 如 F6/F7)");
            Rescan("init");
            SelfTest();
            PsApi.Log(LoggerInstance, $"PSApi.Events v{PsApi.Version} loaded. packs={_packs.Count}");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName != "EmporiumMenu")
                SaveStates.EnsureSlot(LoggerInstance);
            // WP4: 主菜单弹内容包冲突窗 (游戏无独立主菜单场景, 启动即 EmporiumMenu, 主菜单 UI 在其中;
            // 进程级只弹一次, 无冲突内部直接返回)
            if (sceneName == "EmporiumMenu")
                PackConflictPopup.MaybeShow(LoggerInstance);
            _npcs?.OnSceneLeft(); // E7: 场景销毁后旧 Dialogue 指针/选项委托失效, 清注册表与 pin
            // 场景事件是触发器(on 事件)的天然信号源之一, P3 接入完整事件集
            _bus.Publish("psapi.scene.loaded", sceneName);
        }

        public override void OnUpdate()
        {
            _slicer.Drive();
            _inject?.Poll();
            _loot?.Poll(); // v1.10.0: loot_pool 表就绪轮询入池
            _ui?.PollPositions(); // M3: 窗口位置记忆(拖动后重开/重建恢复)
            _ui?.PollPixelPanels(); // U4: 图元面板关窗检测 + 槽位 on_change 差分 + 位置记忆
            _ui?.PollMachinePanels(); // v1.4.0: 机器绑定面板 on_open 跳变 + 槽位 on_change 差分 + 失效清理

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

        public override void OnDeinitializeMelon()
        {
            SaveStates.FlushAll(LoggerInstance);
        }

        /// <summary>重扫内容包(将来供控制台/热重载命令用): 重新加载全部 .pss 脚本。</summary>
        internal void Rescan(string why)
        {
            // WP2: 与 Items 同规则合并 (描述符读 Items public 注册表; Events 自造 EmbeddedPackSource)
            var mergeConflicts = new List<PackMerger.Conflict>();
            _packs = PackMerger.Merge(PackScanner.Scan(LoggerInstance), SnapshotEmbedded(), mergeConflicts, LoggerInstance);
            foreach (var c in mergeConflicts)
                PsApi.Warn(LoggerInstance, $"pack conflict [{c.Kind}]: {c.Detail}");
            int errors = _engine.LoadAll(_packs);
            PsApi.Log(LoggerInstance, $"rescan({why}): {_packs.Count} pack(s), {_engine.LoadedFileCount} pss file(s), {_engine.HandlerCount} handler(s), {errors} compile error(s)");
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
