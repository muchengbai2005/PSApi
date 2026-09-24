using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using PSApi;
using UnityEngine;

[assembly: MelonInfo(typeof(PSApi.Items.ItemsPlugin), "PSApi.Items", "0.9.3", "Research")]
[assembly: MelonGame("Questing Goose Studio", "Probably Stolen")]
[assembly: MelonPriority(10)]

namespace PSApi.Items
{
    /// <summary>
    /// PSApi.Items — 物品/品质/图标/本地化内容宿主 (P1)。
    /// 流程：启动扫包 + 解析 (纯托管) → 目录就绪信号 (ModHook 优先，轮询兜底) → 同步注册物品。
    /// 挂载：LocHelper 文本注入 / 图标双入口 (GameItemElement.ResolveSpriteByName + RenderHandler.LoadFromAtlas) /
    ///       StringToDisplayString 品质名 / GetFinalOfferValue 品质定价 / ModHook.OnCreateTooltipLate 品质 tooltip。
    /// MelonPriority 10: 先于 PSApi.Events(20) 完成目录注入。
    /// </summary>
    public class ItemsPlugin : MelonMod
    {
        internal static ItemsPlugin Instance;

        private readonly PsRegistry _registry;
        private readonly List<object> _pinned = new List<object>();   // 防 Il2Cpp 委托被 GC
        private List<PackInfo> _packs = new List<PackInfo>();
        private bool _directoryReady;      // 物品目录已就绪 (每局重建，需重注册)
        private float _poll;
        private MelonPreferences_Entry<KeyCode> _grantHotkey;


        public ItemsPlugin()
        {
            Instance = this;
            _registry = new PsRegistry(LoggerInstance);
        }

        public override void OnInitializeMelon()
        {
            PsApi.EnsureDirs();
            LocService.Init(LoggerInstance);
            IconService.Init(LoggerInstance);
            ShapeFactory.Init(LoggerInstance);
            QualityService.Init(LoggerInstance);
            RecipeService.Init(LoggerInstance);
            ModulePrinterService.Init(LoggerInstance);
            SlotFilterService.Init(LoggerInstance);
            SlotFilterRegistry.Init(LoggerInstance);
            ProgressRecipeService.Init(LoggerInstance);
            CustomMachineFactory.Init(LoggerInstance);
            ItemStore.Init(LoggerInstance);
            SaveLoadPatches.Init(LoggerInstance);

            var cat = MelonPreferences.CreateCategory("PSApi.Items");
            _grantHotkey = cat.CreateEntry("GrantHotkey", KeyCode.F12, "Grant Hotkey", "发放全部 PS-API 物品的快捷键 (调试用)");


            Rescan("init");
            SubscribeHooks();
            // ★ 必须显式 PatchAll —— MelonLoader 不自动应用 Harmony patch
            // (此前遗漏导致 StringToDisplayString/GetFinalOfferValue/Loc/图标/Labeler patch 全部未生效，
            //  早期测试通过纯属 feature 模式走原生管线)
            HarmonyInstance.PatchAll();
            var ver = Info?.Version ?? "unknown";
            PsApi.Log(LoggerInstance, $"PSApi.Items v{ver} loaded. packs={_packs.Count} items={ItemStore.DefCount} qualities={QualityService.Count}");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName != "EmporiumMenu")
            {
                _directoryReady = false;   // 目录每局重建，回对局需重新注入
                SaveStates.EnsureSlot(LoggerInstance);
            }
        }

        public override void OnUpdate()
        {
            if (!_directoryReady)
            {
                _poll += Time.deltaTime;
                if (_poll > 3f)
                {
                    _poll = 0f;
                    TryPollDirectory();
                }
            }
            SaveLoadPatches.Drive();
            try { RecipeLockWindow.Update(); }
            catch { }
            if (Input.GetKeyDown(_grantHotkey.Value))
            {
                try
                {
                    int n = ItemStore.GrantAllToPlayer();
                    PsApi.Log(LoggerInstance, n > 0 ? $"grant hotkey: {n} item(s) to back inventory" : "grant hotkey: nothing granted (not in store yet?)");
                }
                catch (Exception e) { PsApi.Err(LoggerInstance, "grant failed: " + e.Message); }
            }

        }


        public override void OnDeinitializeMelon()
        {
            SaveStates.FlushAll(LoggerInstance);
        }

        // ==================== 扫描与解析 ====================

        /// <summary>重扫内容包并全量解析 (qualities 先于 items, 供品质组校验)。</summary>
        internal void Rescan(string why)
        {
            // WP2: 文件夹包 + 内嵌包 (EmbeddedPackRegistry, 包 DLL priority=5 已先登记) 合并去重 + 前置检查
            var conflicts = new List<PackMerger.Conflict>();
            _packs = PackMerger.Merge(PackScanner.Scan(LoggerInstance), EmbeddedPackRegistry.SnapshotForMerge(), conflicts, LoggerInstance);
            EmbeddedPackRegistry.PublishConflicts(conflicts);
            foreach (var c in conflicts)
                PsApi.Warn(LoggerInstance, $"pack conflict [{c.Kind}]: {c.Detail}");
            var errors = new List<string>();

            QualityService.Clear();
            foreach (var p in _packs)
                if (p.Valid) LoadQualities(p, errors);

            ItemStore.LoadAll(_packs, errors);
            RecipeService.LoadAll(_packs, errors);

            PsApi.WriteErrorLog(LoggerInstance, errors);
            PsApi.Log(LoggerInstance, $"rescan({why}): {_packs.Count} pack(s), {ItemStore.DefCount} item def(s), {QualityService.Count} quality(ies), errors={errors.Count}");
        }

        private static void LoadQualities(PackInfo pack, List<string> errors)
        {
            if (!pack.Source.HasDir("qualities")) return;
            var opts = new JsonSerializerOptions
            {
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                PropertyNameCaseInsensitive = true,
            };
            foreach (var file in pack.Source.ListFiles("qualities", ".json"))
            {
                QualityFile parsed;
                try { parsed = JsonSerializer.Deserialize<QualityFile>(pack.Source.ReadText(file), opts); }
                catch (Exception e) { errors.Add($"[{pack.Id}] {Path.GetFileName(file)} parse failed: {e.Message}"); continue; }
                if (parsed?.Qualities == null) continue;
                foreach (var q in parsed.Qualities)
                    QualityService.Register(q, pack.Id, errors);
            }
        }

        // ==================== 钩子 ====================

        private void SubscribeHooks()
        {
            try
            {
                // 官方钩子：物品目录初始化 (即使官方 ModLoader 禁用，ModHook 仍存活 — ExtraItems 已验证)
                var onDir = (Action<ModItemDirectory>)(dir => OnDirectoryInit("ModHook.OnModItemDirectoryInit"));
                var delDir = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ModItemDirectory>>(onDir);
                ModHook.OnModItemDirectoryInit += delDir;
                _pinned.Add(onDir); _pinned.Add(delDir);

                // 品质 tooltip 行 (官方钩子，零 patch 风险)
                var onTip = (Action<RichTextBuilder, GameItem>)((builder, item) =>
                {
                    try
                    {
                        if (builder == null || item == null) return;
                        var line = QualityService.TooltipLine(item);
                        if (line != null) builder.AddLine(line);
                        if (ModulePrinterService.TryGetProgressLine(item, out var printerLine))
                            builder.AddLine(printerLine);
                        if (ProgressRecipeService.TryGetProgressLine(item, out var progressLine))
                            builder.AddLine(progressLine);
                        if (ProgressRecipeService.TryGetProducingLine(item, out var producingLine))
                            builder.AddLine(producingLine);
                    }
                    catch { }
                });
                var delTip = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<RichTextBuilder, GameItem>>(onTip);
                ModHook.OnCreateTooltipLate += delTip;
                _pinned.Add(onTip); _pinned.Add(delTip);
            }
            catch (Exception e)
            {
                PsApi.Warn(LoggerInstance, "ModHook subscription failed, will poll instead: " + e.Message);
            }
        }

        private void OnDirectoryInit(string from)
        {
            if (_directoryReady) return;
            if (!ItemStore.RegisterAllNow(from))
            {
                PsApi.Log(LoggerInstance, $"directory signal via {from} but directories not ready; poll will retry");
                return;
            }
            _directoryReady = true;
            PsApi.Log(LoggerInstance, $"item directory ready via {from}; {ItemStore.DefCount} item def(s) registered total");
        }

        private void TryPollDirectory()
        {
            try
            {
                // 目录就绪探针：能创建原版物品即视为目录可用 (未进对局会返回 null/抛异常)
                if (DirectoryMaster.Item("newspaper", true) != null) OnDirectoryInit("poll");
            }
            catch { /* 尚未进对局 */ }
        }
    }
}