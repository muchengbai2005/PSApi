using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace PSApi.Events
{
    /// <summary>
    /// 内容包冲突弹窗 (编译 DLL 化 WP4): 进入主菜单时读取
    /// PSApi.Items.EmbeddedPackRegistry.Conflicts, 有冲突则用 CustomUIManager 弹窗
    /// (与 F6 管理面板同路线), 进程级只弹一次。两栏: 缺少前置模组 / 重复加载。
    /// 主菜单判定: 游戏无独立主菜单场景 (构建仅 EmporiumMenu + InventoryScene,
    /// 启动即 EmporiumMenu, 主菜单 UI 在其中), 故以 EmporiumMenu 首次加载为触发点。
    /// BuildView 是纯函数 (internal 镜像结构输入, 无头可测); UI 本体需游戏内 CustomUIManager。
    /// </summary>
    internal static class PackConflictPopup
    {
        internal const string WindowId = "psapi_pack_conflicts";

        private static bool _shown;                              // 每次启动只弹一次 (进程级)
        private static readonly List<object> _pins = new List<object>();   // 防 Il2Cpp 委托被 GC

        /// <summary>弹窗视图 (两栏显示行)。</summary>
        internal sealed class View
        {
            internal string Title = "PS-API 内容包冲突";
            internal readonly List<string> MissingPrereqLines = new List<string>();
            internal readonly List<string> DuplicateLines = new List<string>();
            internal bool Empty => MissingPrereqLines.Count == 0 && DuplicateLines.Count == 0;
        }

        /// <summary>冲突条目 internal 镜像 (与 Items.PackConflictInfo 字段对应; 隔 public 类型便于测试台只编本文件)。</summary>
        internal sealed class ConflictItem
        {
            internal string Kind;
            internal string Detail;
        }

        /// <summary>冲突快照 → 两栏显示行 (纯函数)。未知 kind / 空详情跳过; null 输入 = 空视图 (不弹)。</summary>
        internal static View BuildView(IEnumerable<ConflictItem> conflicts)
        {
            var view = new View();
            if (conflicts == null) return view;
            foreach (var c in conflicts)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.Detail)) continue;
                if (string.Equals(c.Kind, "missing_prereq", StringComparison.OrdinalIgnoreCase))
                    view.MissingPrereqLines.Add(c.Detail);
                else if (string.Equals(c.Kind, "duplicate", StringComparison.OrdinalIgnoreCase))
                    view.DuplicateLines.Add(c.Detail);
            }
            return view;
        }

        /// <summary>主菜单场景入口 (Plugin.OnSceneWasLoaded 调用)。无冲突/已弹过 = 直接返回。</summary>
        internal static void MaybeShow(MelonLogger.Instance logger)
        {
            if (_shown) return;

            // 硬引用 PSApi.Items + try/catch 防御 (与 Plugin.SnapshotEmbedded 同款)
            List<ConflictItem> items;
            try
            {
                var src = PSApi.Items.EmbeddedPackRegistry.Conflicts;
                if (src == null || src.Count == 0) return;
                items = src.Select(c => new ConflictItem { Kind = c.Kind, Detail = c.Detail }).ToList();
            }
            catch (Exception e)
            {
                PsApi.Warn(logger, "pack conflict popup: conflicts 读取失败: " + e.Message);
                return;
            }

            var view = BuildView(items);
            if (view.Empty) return;
            _shown = true;   // 先置位: 弹窗构建失败也不重试刷屏 (冲突明细 Rescan 已逐条 Warn, 此处不重复)
            try { Show(logger, view); }
            catch (Exception e) { PsApi.Warn(logger, "pack conflict popup 构建失败: " + e.Message); }
        }

        private static void Show(MelonLogger.Instance logger, View view)
        {
            CustomUIManager m;
            try { m = CustomUIManager.Instance; } catch { m = null; }
            if (m == null)
            {
                PsApi.Warn(logger, "pack conflict popup: CustomUIManager 不可用, 冲突明细见启动日志");
                return;
            }

            int lines = view.MissingPrereqLines.Count + view.DuplicateLines.Count;
            int sections = (view.MissingPrereqLines.Count > 0 ? 1 : 0) + (view.DuplicateLines.Count > 0 ? 1 : 0);
            float height = 130f + sections * 28f + lines * 26f;   // 标题/留白/确定按钮 ≈ 130

            var b = m.CreateWindow(WindowId, view.Title, CustomUIManager.LAYER_MENU);
            b.SetSize(460f, height);
            b.SetDraggable(true);
            b.SetCloseOnEscape(true);
            b.Center();
            if (view.MissingPrereqLines.Count > 0)
            {
                b.AddLabel("缺少前置模组:");
                foreach (var line in view.MissingPrereqLines) b.AddLabel("  " + line);
            }
            if (view.DuplicateLines.Count > 0)
            {
                b.AddLabel("重复加载:");
                foreach (var line in view.DuplicateLines) b.AddLabel("  " + line);
            }
            Action managed = () => { try { m.CloseWindow(WindowId); } catch { } };
            var act = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(managed);
            _pins.Add(managed);
            _pins.Add(act);
            b.AddButton("确定", act);
            b.Show();
            PsApi.Log(logger, $"pack conflict popup shown: missing_prereq={view.MissingPrereqLines.Count} duplicate={view.DuplicateLines.Count}");
        }
    }
}
