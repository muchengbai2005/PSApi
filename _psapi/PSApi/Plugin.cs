using System;
using System.IO;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(PSApi.PsApiPlugin), "PSApi", "2.0.8", "Research")]
[assembly: MelonGame("Questing Goose Studio", "Probably Stolen")]
[assembly: MelonPriority(10)]

namespace PSApi
{
    /// <summary>
    /// PSApi — 统一宿主 (v2.0.0, 方案 A: 原 PSApi.Items + PSApi.Events 合并为单 DLL, 见 _temp/doc_archive_20261007/PSApi合并方案.md)。
    /// 单 MelonMod 两段式初始化: 段 1 Items (物品目录/图标/品质, 等价原 priority 10) → 段 2 Events
    /// (PSScript/NPC/场景, 等价原 priority 20 在 Items 之后); 命名空间保持 PSApi.Items.*/PSApi.Events.* 不变。
    /// Harmony PatchAll 全程序集只调一次 (合并前两宿主各一次, 同程序集内会双挂 — 必须显式 PatchAll
    /// 且仅一次: MelonLoader 不自动应用 Harmony patch, 历史教训见原 Items/Plugin.cs 注释)。
    /// 内容包引导壳 (PSPack.*, priority 5) 仍先于本插件 Register, 合并扫描时内嵌包已就位。
    /// </summary>
    public class PsApiPlugin : MelonMod
    {
        private Items.ItemsPlugin _items;
        private Events.EventsPlugin _events;

        public override void OnInitializeMelon()
        {
            PsApi.EnsureDirs();
            CheckLegacyDlls();
            MigratePreferences();
            _items = new Items.ItemsPlugin(LoggerInstance);
            _items.Init();                       // 段 1: Items (原 priority 10)
            _events = new Events.EventsPlugin(LoggerInstance);
            _events.Init();                      // 段 2: Events (原 priority 20, 可安全引用物品)
            try { HarmonyInstance.PatchAll(); }  // 全程序集 Harmony patch 一次 (Items+Events 全部补丁类)
            catch (Exception e) { PsApi.Warn(LoggerInstance, "Harmony PatchAll failed: " + e.Message); }
            PsApi.Log(LoggerInstance, $"PSApi v{PsApi.Version} loaded (统一宿主: items+events)");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // 两模块原是两个独立 MelonMod, 异常互不影响 — 合并后保留该隔离语义
            try { _items?.OnSceneWasLoaded(buildIndex, sceneName); }
            catch (Exception e) { PsApi.Warn(LoggerInstance, "[items] OnSceneWasLoaded: " + e.Message); }
            try { _events?.OnSceneWasLoaded(buildIndex, sceneName); }
            catch (Exception e) { PsApi.Warn(LoggerInstance, "[events] OnSceneWasLoaded: " + e.Message); }
        }

        public override void OnUpdate()
        {
            try { _items?.OnUpdate(); }
            catch (Exception e) { PsApi.Warn(LoggerInstance, "[items] OnUpdate: " + e.Message); }
            try { _events?.OnUpdate(); }
            catch (Exception e) { PsApi.Warn(LoggerInstance, "[events] OnUpdate: " + e.Message); }
        }

        /// <summary>v2.0.0: 旧两件套残留检测 — Mods/ 下旧 DLL 与新 PSApi.dll 并存会双挂补丁/双写状态, 启动即告警。</summary>
        private void CheckLegacyDlls()
        {
            try
            {
                foreach (var f in new[] { "PSApi.Events.dll", "PSApi.Items.dll" })
                {
                    string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", f);
                    if (File.Exists(p))
                        PsApi.Warn(LoggerInstance, $"[v2.0.0] 检测到旧两件套残留 Mods/{f} — 合并后由 PSApi.dll 统一承载, 请删除旧文件防双挂");
                }
            }
            catch { }
        }

        /// <summary>v2.0.0: 偏好类目合并迁移 — 旧 PSApi.Events/PSApi.Items 类目里用户自定义过的
        /// ManagerHotkey/GrantHotkey 搬进新 "PSApi" 类目 (仅非默认值才迁移; 旧类目留档不删)。
        /// 模块 Init 内用 GetCategory/GetEntry 取这里已建的实例 (v2.0.0 修复: CreateEntry 对已存在条目
        /// 会抛 "Already Exists", 模块侧不可再裸 CreateEntry 同名键)。</summary>
        private void MigratePreferences()
        {
            try
            {
                var cat = MelonPreferences.CreateCategory("PSApi", "PSApi 统一宿主");
                var mgr = cat.CreateEntry("ManagerHotkey", "F6", "管理面板热键(KeyCode 名, 如 F6/F7)");
                var grant = cat.CreateEntry("GrantHotkey", KeyCode.F12, "Grant Hotkey", "发放全部 PS-API 物品的快捷键 (调试用)");
                bool migrated = false;
                var oldEvents = MelonPreferences.GetCategory("PSApi.Events");
                var oldMgr = oldEvents?.GetEntry<string>("ManagerHotkey");
                if (oldMgr != null && !string.IsNullOrEmpty(oldMgr.Value) && oldMgr.Value != "F6" && mgr.Value == "F6")
                {
                    mgr.Value = oldMgr.Value;
                    migrated = true;
                }
                var oldItems = MelonPreferences.GetCategory("PSApi.Items");
                var oldGrant = oldItems?.GetEntry<KeyCode>("GrantHotkey");
                if (oldGrant != null && oldGrant.Value != KeyCode.F12 && grant.Value == KeyCode.F12)
                {
                    grant.Value = oldGrant.Value;
                    migrated = true;
                }
                if (migrated)
                {
                    MelonPreferences.Save();
                    PsApi.Log(LoggerInstance, "偏好迁移: 旧 PSApi.Events/PSApi.Items 类目自定义键值已并入 [PSApi] 类目");
                }
            }
            catch (Exception e) { PsApi.Warn(LoggerInstance, "偏好迁移失败 (不影响运行): " + e.Message); }
        }
    }
}
