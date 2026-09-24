using System;
using System.Collections.Generic;
using PSApi.Events.PsUI;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// E6 ui.* 命名空间 (ui/11 §4): ui.open(panel_id) / ui.close([panel_id])
    /// + U2 动态修改 ui.set_text/set_progress/set_value/get_value。
    /// 面板定义在 packs/&lt;pack&gt;/ui/*.psui; id = 包id:面板名 (脚本内可省略 "包id:" 前缀, 自动补本包)。
    /// open 幂等 (重复 open 同 id 只置顶); close 0 参 = 本包最近打开的面板。
    /// 元素引用 elem_id 形如 "[包:]元素id", 窗口 = 该包当前面板。
    /// </summary>
    internal static class PsBuiltinsUi
    {
        internal static void Register(PsEnv env, PsUiService ui, string packId)
        {
            var ns = new PsNamespace("ui");
            ns.Members["open"] = PsBuiltins.BF("ui.open", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "ui.open(panel_id)", line);
                string id = AsPanelId(a[0], "ui.open", line);
                return ui.Open(id, packId, line);
            });
            ns.Members["close"] = PsBuiltins.BF("ui.close", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 1, "ui.close([panel_id])", line);
                string id = null;
                if (a.Count == 1) id = AsPanelId(a[0], "ui.close", line);
                try { return ui.Close(id, packId); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("ui.close 失败: " + e.Message, line); }
            });

            // ---- U2: 动态修改 (元素查找 = CustomUIManager.GetElement; elem_id 可带 "包:" 前缀, 缺省本包) ----
            ns.Members["set_text"] = PsBuiltins.BF("ui.set_text", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "ui.set_text(elem_id, text)", line);
                string id = AsElemId(a[0], "ui.set_text", line);
                ui.SetText(id, PsValues.Fmt(a[1]), packId, line);
                return null;
            });
            ns.Members["set_progress"] = PsBuiltins.BF("ui.set_progress", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 3, "ui.set_progress(elem_id, value 0..1[, label])", line);
                string id = AsElemId(a[0], "ui.set_progress", line);
                double v = AsNum(a[1], "ui.set_progress", line);
                string label = a.Count == 3 ? PsValues.Fmt(a[2]) : null;
                ui.SetProgress(id, v, label, packId, line);
                return null;
            });
            ns.Members["set_value"] = PsBuiltins.BF("ui.set_value", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "ui.set_value(elem_id, v)", line);
                string id = AsElemId(a[0], "ui.set_value", line);
                ui.SetValue(id, a[1], packId, line);
                return null;
            });
            ns.Members["get_value"] = PsBuiltins.BF("ui.get_value", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "ui.get_value(elem_id)", line);
                string id = AsElemId(a[0], "ui.get_value", line);
                return ui.GetValue(id, packId, line);
            });

            // ---- U4: 图元槽位 (slot/grid_slot 面板) ----
            ns.Members["get_slot_item"] = PsBuiltins.BF("ui.get_slot_item", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "ui.get_slot_item(elem_id) → 物品句柄|null (槽空)", line);
                string id = AsElemId(a[0], "ui.get_slot_item", line);
                return ui.GetSlotItem(id, packId, line);
            });
            ns.Members["get_slot_items"] = PsBuiltins.BF("ui.get_slot_items", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "ui.get_slot_items(elem_id) → [物品句柄...] (空槽=空表; grid_slot 多物品用这个)", line);
                string id = AsElemId(a[0], "ui.get_slot_items", line);
                return ui.GetSlotItems(id, packId, line);
            });

            // ---- M3: 动态面板 (on_build 构建期填充 + rebuild 整体重建 + 位置记忆) ----
            ns.Members["rebuild"] = PsBuiltins.BF("ui.rebuild", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 1, "ui.rebuild([panel_id])", line);
                string id = a.Count == 1 ? AsPanelId(a[0], "ui.rebuild", line) : null;
                return ui.Rebuild(id, packId, line);
            });
            ns.Members["is_open"] = PsBuiltins.BF("ui.is_open", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 1, "ui.is_open([panel_id])", line);
                string id = a.Count == 1 ? AsPanelId(a[0], "ui.is_open", line) : null;
                return ui.IsOpen(id, packId, line);
            });
            ns.Members["set_pref_size"] = PsBuiltins.BF("ui.set_pref_size", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 3, "ui.set_pref_size(elem_id, w, h)  (-1 = 不指定)", line);
                string id = AsElemId(a[0], "ui.set_pref_size", line);
                ui.SetPrefSize(id, AsNum(a[1], "ui.set_pref_size", line), AsNum(a[2], "ui.set_pref_size", line), packId, line);
                return null;
            });
            ns.Members["build_begin"] = PsBuiltins.BF("ui.build_begin", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "ui.build_begin(container_id)  — 推进静态树带 id 的容器, 须配 build_end", line);
                ui.BuildBegin(AsElemId(a[0], "ui.build_begin", line), line);
                return null;
            });
            ns.Members["build_row"] = PsBuiltins.BF("ui.build_row", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 1, "ui.build_row([spacing=4])", line);
                ui.BuildContainer("row", a.Count == 1 ? AsNum(a[0], "ui.build_row", line) : 4, line);
                return null;
            });
            ns.Members["build_column"] = PsBuiltins.BF("ui.build_column", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 1, "ui.build_column([spacing=4])", line);
                ui.BuildContainer("column", a.Count == 1 ? AsNum(a[0], "ui.build_column", line) : 4, line);
                return null;
            });
            ns.Members["build_scroll"] = PsBuiltins.BF("ui.build_scroll", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "ui.build_scroll(height)", line);
                ui.BuildContainer("scroll", AsNum(a[0], "ui.build_scroll", line), line);
                return null;
            });
            ns.Members["build_end"] = PsBuiltins.BF("ui.build_end", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "ui.build_end()", line);
                ui.BuildEnd(line);
                return null;
            });
            ns.Members["build_label"] = PsBuiltins.BF("ui.build_label", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "ui.build_label(text[, {id, tooltip}]) → 元素id", line);
                var opts = a.Count == 2 ? AsOpts(a[1], "ui.build_label", line) : null;
                return ui.BuildLabel(PsValues.Fmt(a[0]), opts, line);
            });
            ns.Members["build_button"] = PsBuiltins.BF("ui.build_button", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 3, "ui.build_button(text, fn名[, {id, arg, tooltip}]) → 元素id", line);
                string fn = AsPanelId(a[1], "ui.build_button", line);
                var opts = a.Count == 3 ? AsOpts(a[2], "ui.build_button", line) : null;
                return ui.BuildButton(PsValues.Fmt(a[0]), fn, opts, line, packId);
            });

            // ---- v1.4.0: 机器绑定面板 (machines/*.json ui="psui"; 首参 = 机器物品句柄, 回调参数带 machine) ----
            ns.Members["machine_set_text"] = PsBuiltins.BF("ui.machine_set_text", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 3, "ui.machine_set_text(machine, elem_id, text)", line);
                ui.MachineSetText(AsMachine(a[0], "ui.machine_set_text", line), AsElemId(a[1], "ui.machine_set_text", line), PsValues.Fmt(a[2]), line);
                return null;
            });
            ns.Members["machine_slot_item"] = PsBuiltins.BF("ui.machine_slot_item", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "ui.machine_slot_item(machine, slot_id) → 物品句柄|null (槽空)", line);
                return ui.MachineSlotItem(AsMachine(a[0], "ui.machine_slot_item", line), AsElemId(a[1], "ui.machine_slot_item", line), line);
            });
            ns.Members["machine_slot_items"] = PsBuiltins.BF("ui.machine_slot_items", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "ui.machine_slot_items(machine, slot_id) → 物品句柄数组 (空槽=空表; grid_slot 多物品用)", line);
                return ui.MachineSlotItems(AsMachine(a[0], "ui.machine_slot_items", line), AsElemId(a[1], "ui.machine_slot_items", line), line);
            });
            ns.Members["machine_set_whitelist"] = PsBuiltins.BF("ui.machine_set_whitelist", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 3, "ui.machine_set_whitelist(machine, slot_id, [id...])  — 覆盖式重设排他白名单", line);
                var ids = a[2] is List<object> l ? l
                    : throw new PsRuntimeError($"ui.machine_set_whitelist 的白名单须为数组, 实为 {PsValues.TypeName(a[2])}", line);
                ui.MachineSetWhitelist(AsMachine(a[0], "ui.machine_set_whitelist", line), AsElemId(a[1], "ui.machine_set_whitelist", line), ids, line);
                return null;
            });
            ns.Members["machine_spawn"] = PsBuiltins.BF("ui.machine_spawn", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 4, "ui.machine_spawn(machine, slot_id, item_id[, count=1]) → 物品句柄|null (输出槽预览用)", line);
                int count = a.Count == 4 ? AsCount(a[3], "ui.machine_spawn", line) : 1;
                return ui.MachineSpawn(AsMachine(a[0], "ui.machine_spawn", line), AsElemId(a[1], "ui.machine_spawn", line), AsPanelId(a[2], "ui.machine_spawn", line), count, line);
            });
            ns.Members["machine_lock"] = PsBuiltins.BF("ui.machine_lock", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 3, "ui.machine_lock(machine, slot_id, locked)  — 插入锁+取出锁双锁", line);
                ui.MachineLock(AsMachine(a[0], "ui.machine_lock", line), AsElemId(a[1], "ui.machine_lock", line), PsValues.Truthy(a[2]), line);
                return null;
            });
            ns.Members["machine_clear"] = PsBuiltins.BF("ui.machine_clear", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "ui.machine_clear(machine, slot_id)  — 槽内物品全部销毁 (清输出槽预览)", line);
                ui.MachineClear(AsMachine(a[0], "ui.machine_clear", line), AsElemId(a[1], "ui.machine_clear", line), line);
                return null;
            });
            ns.Members["machine_is_open"] = PsBuiltins.BF("ui.machine_is_open", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "ui.machine_is_open(machine) → bool", line);
                return ui.MachineIsOpen(AsMachine(a[0], "ui.machine_is_open", line), line);
            });
            ns.Members["machine_find_uid"] = PsBuiltins.BF("ui.machine_find_uid", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "ui.machine_find_uid(uid) → 物品句柄|null (按存档稳定 uniqueId 找 psui 机器)", line);
                long uid = a[0] is long l ? l : a[0] is double d ? (long)d
                    : throw new PsRuntimeError($"ui.machine_find_uid 的 uid 须为数字, 实为 {PsValues.TypeName(a[0])}", line);
                return ui.MachineFindUid(uid, line);
            });
            env.Define("ui", ns, true, 0);
        }

        private static Dictionary<string, object> AsOpts(object v, string fn, int line)
        {
            if (v is Dictionary<string, object> d) return d;
            throw new PsRuntimeError($"{fn} 的选项参数须为 dict, 实为 {PsValues.TypeName(v)}", line);
        }

        private static string AsElemId(object v, string fn, int line)
        {
            if (v is string s && !string.IsNullOrWhiteSpace(s)) return s;
            throw new PsRuntimeError($"{fn} 的元素 id 须为非空字符串, 实为 {PsValues.TypeName(v)}", line);
        }

        private static double AsNum(object v, string fn, int line)
        {
            if (v is long l) return l;
            if (v is double d) return d;
            throw new PsRuntimeError($"{fn} 的 value 须为数字, 实为 {PsValues.TypeName(v)}", line);
        }

        private static int AsCount(object v, string fn, int line)
        {
            long n = v is long l ? l : v is double d ? (long)d
                : throw new PsRuntimeError($"{fn} 的 count 须为数字, 实为 {PsValues.TypeName(v)}", line);
            if (n < 1 || n > 9999)
                throw new PsRuntimeError($"{fn} 的 count 须在 1..9999, 实为 {n}", line);
            return (int)n;
        }

        /// <summary>v1.4.0: 机器句柄参数 → 底层 GameItem (machine_find_uid/回调参数 machine 的句柄)。</summary>
        private static Il2Cpp.GameItem AsMachine(object v, string fn, int line)
        {
            if (v == null)
                throw new PsRuntimeError($"{fn} 的机器参数是 null (machine_find_uid 没找到? 请先判空)", line);
            if (v is PsItemHandle h) return h.NeedItem(line);
            if (v is PsHandle other)
                throw new PsRuntimeError($"{fn} 需要物品句柄, 实为 {other.Kind} 句柄", line);
            throw new PsRuntimeError($"{fn} 需要机器物品句柄 (回调的 machine / ui.machine_find_uid 的返回值), 实为 {PsValues.TypeName(v)}", line);
        }

        private static string AsPanelId(object v, string fn, int line)
        {
            if (v is string s && !string.IsNullOrWhiteSpace(s)) return s;
            throw new PsRuntimeError($"{fn} 的面板 id 须为非空字符串, 实为 {PsValues.TypeName(v)}", line);
        }
    }
}
