using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace PSApi.Items
{
    /// <summary>
    /// v0.5.10: 配方锁定 UI — 用游戏原生 CustomUI 系统替代 IMGUI。
    /// OnUpdate 轮询 WindowsHandler.current.visibleWindows + PixelWindow.parentItems：
    /// 检测到双击机器打开的库存 UI 属于 PSApi 机器时，在机器窗口右下角显示"设置"按钮，
    /// 点击后弹出机器设置面板（含"固定配方"一键锁定当前输入匹配的配方）。
    /// 按钮跟随机器窗口拖动，支持多机器同时打开。机器 UI 关闭时自动清理。
    /// 按钮大小/位置可通过 UserData/PSApi/ui/button_config.json 自定义。
    /// </summary>
    internal static class RecipeLockWindow
    {
        private const string PanelWindowId = "psapi_lock_panel";

        private static readonly Dictionary<long, MachineEntry> _entries = new();
        private static readonly List<object> _panelPinned = new();
        private static GameItem _panelMachine;
        private static string _panelMachineId;

        private static bool _suppressReopen;

        private static ButtonConfig Config => LoadConfig();

        private class ButtonConfig
        {
            public float Width { get; set; } = 100;
            public float Height { get; set; } = 60;
            public float AnchorX { get; set; } = 0.9f;
            public float AnchorY { get; set; } = 0.9f;
        }

        private static readonly JsonSerializerOptions CfgOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        private static ButtonConfig LoadConfig()
        {
            var def = new ButtonConfig();
            try
            {
                var dir = Path.Combine(PsApi.RootDir, "ui");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "button_config.json");
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, JsonSerializer.Serialize(def, CfgOpts));
                    return def;
                }
                var cfg = JsonSerializer.Deserialize<ButtonConfig>(File.ReadAllText(path), CfgOpts) ?? def;
                if (cfg.Width < 40) cfg.Width = 40;
                if (cfg.Height < 30) cfg.Height = 30;
                return cfg;
            }
            catch { return def; }
        }

        private class MachineEntry
        {
            public GameItem Machine;
            public string MachineId;
            public PixelWindow MachineWindow;
            public CustomUIWindow BtnWindow;
            public string WindowId;
            public List<object> Pinned = new();
        }

        // ==================== 每帧轮询 (Plugin.OnUpdate 调用) ====================

        /// <summary>每帧调用：遍历可见 PixelWindow，为每个 PSApi 机器创建/更新"设置"按钮。</summary>
        internal static void Update()
        {
            var visible = new Dictionary<long, (GameItem, string, PixelWindow)>();
            try
            {
                var handler = WindowsHandler.current;
                if (handler != null)
                {
                    var windows = handler.visibleWindows;
                    if (windows != null)
                    {
                        for (int i = 0; i < windows.Count; i++)
                        {
                            var win = windows[i];
                            if (win == null) continue;
                            var parents = win.parentItems;
                            if (parents == null || parents.Count == 0) continue;
                            for (int j = 0; j < parents.Count; j++)
                            {
                                var item = parents[j];
                                if (item == null) continue;
                                string id = TryGetId(item);
                                if (id != null && RecipeService.HasRecipesFor(id))
                                {
                                    long ptr = item.Pointer.ToInt64();
                                    if (!visible.ContainsKey(ptr))
                                        visible[ptr] = (item, id, win);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            var toRemove = new List<long>();
            foreach (var kv in _entries)
                if (!visible.ContainsKey(kv.Key)) toRemove.Add(kv.Key);
            foreach (var ptr in toRemove)
            {
                CloseButtonWindow(_entries[ptr]);
                _entries.Remove(ptr);
            }

            foreach (var kv in visible)
            {
                long ptr = kv.Key;
                var (machine, machineId, machineWindow) = kv.Value;
                bool panelIsForThis = _panelMachine != null && _panelMachine.Pointer == machine.Pointer;

                if (!_entries.TryGetValue(ptr, out var entry))
                {
                    if (panelIsForThis) continue;
                    entry = new MachineEntry
                    {
                        Machine = machine,
                        MachineId = machineId,
                        MachineWindow = machineWindow,
                        WindowId = "psapi_lock_btn_" + ptr,
                    };
                    ShowButtonWindow(entry);
                    _entries[ptr] = entry;
                }
                else
                {
                    entry.Machine = machine;
                    entry.MachineId = machineId;
                    entry.MachineWindow = machineWindow;
                    if (panelIsForThis)
                    {
                        if (entry.BtnWindow != null) CloseButtonWindow(entry);
                    }
                    else if (entry.BtnWindow == null)
                    {
                        ShowButtonWindow(entry);
                    }
                    else
                    {
                        FollowMachineWindow(entry);
                    }
                }
            }
        }

        // ==================== 位置跟随 ====================

        private static void FollowMachineWindow(MachineEntry entry)
        {
            if (entry.BtnWindow == null || entry.MachineWindow == null) return;
            var log = ItemsPlugin.Instance?.LoggerInstance;
            try
            {
                var machineRect = entry.MachineWindow.rectTransform;
                if (machineRect == null) return;
                var pos = machineRect.position;
                var scale = machineRect.lossyScale;
                var rect = machineRect.rect;
                var sd = machineRect.sizeDelta;
                int pw = entry.MachineWindow.widthPixels;
                int ph = entry.MachineWindow.heightPixels;
                float worldW = rect.width * scale.x;
                float worldH = rect.height * scale.y;
                if (worldW < 1f) worldW = sd.x * scale.x;
                if (worldH < 1f) worldH = sd.y * scale.y;
                if (worldW < 1f) worldW = pw / scale.x;
                if (worldH < 1f) worldH = ph / scale.y;
                var cfg = Config;
                var buttonPos = new Vector3(pos.x + (cfg.AnchorX - 0.5f) * worldW, pos.y + (0.5f - cfg.AnchorY) * worldH, pos.z);
                var btnRect = entry.BtnWindow.Rect;
                if (btnRect != null) btnRect.position = buttonPos;

            }
            catch { }
        }

        // ==================== 按钮窗口 ====================

        private static void ShowButtonWindow(MachineEntry entry)
        {
            var log = ItemsPlugin.Instance?.LoggerInstance;
            var mgr = CustomUIManager.Instance;
            if (mgr == null) return;
            try
            {
                if (mgr.IsOpen(entry.WindowId)) return;

                var capturedEntry = entry;
                var onClick = (Action)(() => ShowPanelWindow(capturedEntry));
                var delOnClick = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(onClick);
                entry.Pinned.Add(onClick);
                entry.Pinned.Add(delOnClick);

                entry.BtnWindow = CustomUIManager.Window(entry.WindowId, "", CustomUIManager.LAYER_MENU)
                    .SetSize(Config.Width, Config.Height)
                    .SetDraggable(false)
                    .SetCloseOnEscape(false)
                    .AddButton("设置", delOnClick, "lock_btn")
                    .Show();
                PsApi.Log(log, $"btn window created: {entry.WindowId} machine={entry.MachineId}");

                try
                {
                    var frame = entry.BtnWindow.frame;
                    if (frame != null)
                    {
                        if (frame.closeButton != null)
                        {
                            frame.closeButton.interactable = false;
                            var cbImg = frame.closeButton.image;
                            if (cbImg != null) cbImg.color = new Color(0, 0, 0, 0);
                        }
                        var frameImg = frame.GetComponent<UnityEngine.UI.Image>();
                        if (frameImg != null) frameImg.color = new Color(0, 0, 0, 0);
                    }
                }
                catch (Exception e) { PsApi.Warn(log, "frame style failed: " + e.Message); }

                FollowMachineWindow(entry);
                PsApi.Log(log, $"btn window positioned: {entry.WindowId}");
            }
            catch (Exception e) { PsApi.Err(log, "lock btn window failed: " + e.Message); }
        }

        // ==================== 设置面板 ====================

        private static void ShowPanelWindow(MachineEntry entry)
        {
            var mgr = CustomUIManager.Instance;
            if (mgr == null) return;
            if (entry.Machine == null || entry.MachineId == null) return;
            try
            {
                if (mgr.IsOpen(PanelWindowId)) return;
                if (mgr.IsOpen(entry.WindowId)) mgr.CloseWindow(entry.WindowId);

                _panelMachine = entry.Machine;
                _panelMachineId = entry.MachineId;

                var machine = entry.Machine;
                var machineId = entry.MachineId;

                string currentRecipe = RecipeService.TryMatchCurrentRecipe(machine, machineId, out var curId) ? curId : "无匹配";

                var onFix = (Action)FixCurrentRecipe;
                var delOnFix = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(onFix);
                _panelPinned.Add(onFix);
                _panelPinned.Add(delOnFix);

                var window = CustomUIManager.Window(PanelWindowId, "PSApi 机器设置", CustomUIManager.LAYER_MENU)
                    .SetSize(300, 160)
                    .SetDraggable(true)
                    .SetCloseOnEscape(true)
                    .AddLabel($"机器: {machineId}")
                    .AddLabel($"当前配方: {currentRecipe}")
                    .AddButton("固定配方 (锁定为当前输入)", delOnFix, "fix_recipe_btn")
                    .Show();

                var onClose = (Action)(() =>
                {
                    if (!_suppressReopen)
                    {
                        _panelMachine = null;
                        _panelMachineId = null;
                        _panelPinned.Clear();
                    }
                });
                var delOnClose = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(onClose);
                _panelPinned.Add(onClose);
                _panelPinned.Add(delOnClose);
                window.onClose = delOnClose;
            }
            catch (Exception e) { PsApi.Err(ItemsPlugin.Instance?.LoggerInstance, "lock panel window failed: " + e.Message); }
        }

        // ==================== 固定配方 ====================

        private static void FixCurrentRecipe()
        {
            if (_panelMachine == null || _panelMachineId == null) return;
            var log = ItemsPlugin.Instance?.LoggerInstance;
            try
            {
                if (RecipeService.TryMatchCurrentRecipe(_panelMachine, _panelMachineId, out var matchedId))
                {
                    var allInfos = RecipeService.GetRecipeInfosForMachine(_panelMachineId);
                    var locked = new HashSet<string>();
                    foreach (var info in allInfos)
                        if (!string.Equals(info.Id, matchedId, StringComparison.OrdinalIgnoreCase))
                            locked.Add(info.Id);
                    RecipeLockService.SetLocked(_panelMachine, locked);
                    PsApi.Log(log, $"fix recipe: {matchedId} locked {locked.Count} others");
                }
                long ptr = _panelMachine.Pointer.ToInt64();
                _suppressReopen = true;
                try { CustomUIManager.Instance?.CloseWindow(PanelWindowId); } catch { }
                _suppressReopen = false;
                if (_entries.TryGetValue(ptr, out var entry))
                    ShowPanelWindow(entry);
            }
            catch (Exception e) { PsApi.Err(log, "fix recipe failed: " + e.Message); }
        }

        // ==================== 清理 ====================

        private static void CloseButtonWindow(MachineEntry entry)
        {
            try
            {
                var mgr = CustomUIManager.Instance;
                if (mgr != null && mgr.IsOpen(entry.WindowId))
                    mgr.CloseWindow(entry.WindowId);
            }
            catch { }
            entry.Pinned.Clear();
            entry.BtnWindow = null;
        }

        internal static void CloseAll()
        {
            _suppressReopen = true;
            try
            {
                var mgr = CustomUIManager.Instance;
                if (mgr != null)
                {
                    foreach (var entry in _entries.Values)
                        if (mgr.IsOpen(entry.WindowId)) mgr.CloseWindow(entry.WindowId);
                    if (mgr.IsOpen(PanelWindowId)) mgr.CloseWindow(PanelWindowId);
                }
            }
            catch { }
            finally
            {
                _suppressReopen = false;
                _entries.Clear();
                _panelMachine = null;
                _panelMachineId = null;

                _panelPinned.Clear();
            }
        }

        // ==================== 工具 ====================

        private static string TryGetId(GameItem item)
        {
            try { return item?.identifier; }
            catch { return null; }
        }
    }
}
