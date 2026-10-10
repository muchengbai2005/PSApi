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
    /// P2 (v1.27.0): ui.panel/text/button/bar/set_fill/set_visible/destroy/clear — 场景 uGUI 自由布局
    /// (script 驱动场景第二条 UI 路线; 句柄式, 按场景归组自动清理; 后端 = UI.SceneUguiService)。
    /// ui.set_text 双形态: 字符串首参 = psui 元素 id (旧行为), 数字首参 = 场景 uGUI 句柄 (P2)。
    /// </summary>
    internal static class PsBuiltinsUi
    {
        internal static void Register(PsEnv env, PsUiService ui, string packId, UI.SceneUguiService sceneUgui = null)
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
                PsBuiltins.Need(a, 2, 2, "ui.set_text(elem_id | ugui句柄, text)", line);
                // P2 (v1.27.0): 数字首参 = 场景 uGUI 句柄 (ui.text/button 的返回值); 字符串 = psui 元素 id (旧行为一字不改)
                if (a[0] is long || a[0] is double)
                {
                    if (sceneUgui == null)
                        throw new PsRuntimeError("ui.set_text 的句柄形式需要场景 uGUI 服务 (PSApi.Events v1.27.0+)", line);
                    sceneUgui.SetText(AsHandle(a[0], "ui.set_text", line), PsValues.Fmt(a[1]), line);
                    return null;
                }
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

            // ---- v1.16.0: 按键绑定 (顶层调用; 按下 = 开/关切换面板, 进程级) ----
            ns.Members["bind_key"] = PsBuiltins.BF("ui.bind_key", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "ui.bind_key(key_name, panel_id)  — KeyCode 名 (如 \"Slash\"/\"F6\"), 顶层调用, 按下开/关切换面板", line);
                string key = AsPanelId(a[0], "ui.bind_key", line);
                string id = AsPanelId(a[1], "ui.bind_key", line);
                ui.BindKey(key, id, packId, line);
                return null;
            });

            // ---- v1.4.0: 机器绑定面板 (machines/*.json ui="psui"; 首参 = 机器物品句柄, 回调参数带 machine) ----
            ns.Members["machine_set_text"] = PsBuiltins.BF("ui.machine_set_text", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 4, "ui.machine_set_text(machine, elem_id, text [, color])  — v1.38.0: color = \"#RRGGBB\"/\"#RRGGBBAA\" hex, 省略=回白色", line);
                // v1.38.0: 先解析颜色再校验机器句柄 — 非法颜色在无头/句柄失效场景也报带行号的错
                (float r, float g, float b, float a)? col = null;
                if (a.Count > 3 && a[3] != null)
                {
                    string cs = PsValues.Fmt(a[3]);
                    float rr, gg, bb, aa;
                    if (!PsUiPixelBackend.TryParseHtmlColor(cs, out rr, out gg, out bb, out aa))
                        throw new PsRuntimeError($"ui.machine_set_text 的 color '{cs}' 非法 (需 \"#RRGGBB\" 或 \"#RRGGBBAA\" hex)", line);
                    col = (rr, gg, bb, aa);
                }
                ui.MachineSetText(AsMachine(a[0], "ui.machine_set_text", line), AsElemId(a[1], "ui.machine_set_text", line), PsValues.Fmt(a[2]), line, col);
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
            ns.Members["machine_set_shape"] = PsBuiltins.BF("ui.machine_set_shape", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 3, "ui.machine_set_shape(machine, slot_id, shape)  — grid_slot 逐格格态, 行优先 0/1/2 串 (0=可放 1=永久洞 2=锁定格暗色; v1.40.0)", line);
                var sh = PsValues.Fmt(a[2]);
                // 先校验字符集再校验机器句柄 — 非法串在无头/句柄失效场景也报带行号的错 (照 machine_set_text color 惯例)
                if (!PSApi.Events.PsUI.ShapeUtil.IsValidCharset(sh, out var sherr))
                    throw new PsRuntimeError($"ui.machine_set_shape 的 shape {sherr}", line);
                ui.MachineSetShape(AsMachine(a[0], "ui.machine_set_shape", line), AsElemId(a[1], "ui.machine_set_shape", line), sh, line);
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
            // v2.0.5 诊断: 句柄失效/无头不抛 (诊断专用, 脚本无需判空)
            ns.Members["machine_dump_layout"] = PsBuiltins.BF("ui.machine_dump_layout", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "ui.machine_dump_layout(machine [, tag=\"psui\"])  — v2.0.5 诊断: 面板子元素数+label 尺寸一行日志", line);
                Il2Cpp.GameItem dm = null;
                try { if (a[0] is PsItemHandle dh) dm = dh.NeedItem(line); } catch { }
                string dtag = a.Count >= 2 && a[1] != null ? PsValues.Fmt(a[1]) : "psui";
                return ui.MachineDumpLayout(dm, dtag);
            });
            ns.Members["machine_find_uid"] = PsBuiltins.BF("ui.machine_find_uid", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "ui.machine_find_uid(uid) → 物品句柄|null (按存档稳定 uniqueId 找 psui 机器)", line);
                long uid = a[0] is long l ? l : a[0] is double d ? (long)d
                    : throw new PsRuntimeError($"ui.machine_find_uid 的 uid 须为数字, 实为 {PsValues.TypeName(a[0])}", line);
                return ui.MachineFindUid(uid, line);
            });
            // ---- P2 (v1.27.0): 场景 uGUI 创建 API ----
            if (sceneUgui != null) AddSceneUguiMembers(ns, sceneUgui);
            env.Define("ui", ns, true, 0);
        }

        /// <summary>P2: 无 psui 服务时的退化注册 (仅场景 uGUI 成员 + 句柄版 set_text)。游戏内 PsUiService 恒在, 防御用。</summary>
        internal static void RegisterSceneUguiOnly(PsEnv env, UI.SceneUguiService sceneUgui)
        {
            var ns = new PsNamespace("ui");
            AddSceneUguiMembers(ns, sceneUgui);
            ns.Members["set_text"] = PsBuiltins.BF("ui.set_text", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "ui.set_text(ugui句柄, text)", line);
                sceneUgui.SetText(AsHandle(a[0], "ui.set_text", line), PsValues.Fmt(a[1]), line);
                return null;
            });
            env.Define("ui", ns, true, 0);
        }

        /// <summary>P2 (v1.27.0) 场景 uGUI 创建 API — 句柄式 (创建返回句柄, 后续操作带句柄)。
        /// 坐标: x/y 屏比 0-1 (顶层面板相对屏幕左下, 子元素相对父面板左下), w/h 像素。
        /// 句柄表按场景归组, 场景撤离自动销毁整组; 不在场景 = Warn + 返回 0 (创建) / no-op (操作)。</summary>
        private static void AddSceneUguiMembers(PsNamespace ns, UI.SceneUguiService su)
        {
            ns.Members["panel"] = PsBuiltins.BF("ui.panel", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 4, 5, "ui.panel(x, y, w, h[, {color=\"#rrggbb\", alpha 0..1, order, raycast, anchor=\"bl\"|\"center\"}]) → 句柄 (不在场景 = warn + 0; anchor=center 时 x/y=面板中心点屏比)", line);
                var opts = a.Count == 5 ? AsOpts(a[4], "ui.panel", line) : null;
                return su.CreatePanel(AsNum(a[0], "ui.panel", line), AsNum(a[1], "ui.panel", line),
                    AsNum(a[2], "ui.panel", line), AsNum(a[3], "ui.panel", line), opts, line);
            });
            ns.Members["text"] = PsBuiltins.BF("ui.text", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 5, 6, "ui.text(panel句柄 | 0=canvas根(中心锚), x, y, w, text[, font_size=16]) → 句柄", line);
                return su.CreateText(AsParent(a[0], "ui.text", line), AsNum(a[1], "ui.text", line), AsNum(a[2], "ui.text", line),
                    AsNum(a[3], "ui.text", line), PsValues.Fmt(a[4]), a.Count == 6 ? AsNum(a[5], "ui.text", line) : 16.0, line);
            });
            ns.Members["button"] = PsBuiltins.BF("ui.button", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 7, 8, "ui.button(panel句柄 | 0=canvas根(中心锚, raid poi 按钮同款), x, y, w, h, label, \"pss函数名\"[, font_size=16]) → 句柄 — 点击回调该函数 (0.3s 去抖), 函数参数收 {handle}", line);
                string fn = AsPanelId(a[6], "ui.button", line);
                return su.CreateButton(AsParent(a[0], "ui.button", line), AsNum(a[1], "ui.button", line), AsNum(a[2], "ui.button", line),
                    AsNum(a[3], "ui.button", line), AsNum(a[4], "ui.button", line), PsValues.Fmt(a[5]), fn,
                    a.Count == 8 ? AsNum(a[7], "ui.button", line) : 16.0, itp, line);
            });
            ns.Members["bar"] = PsBuiltins.BF("ui.bar", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 6, 7, "ui.bar(panel句柄 | 0=canvas根(中心锚), x, y, w, h, pct 0..1[, color=\"#rrggbb\"]) → 句柄", line);
                return su.CreateBar(AsParent(a[0], "ui.bar", line), AsNum(a[1], "ui.bar", line), AsNum(a[2], "ui.bar", line),
                    AsNum(a[3], "ui.bar", line), AsNum(a[4], "ui.bar", line), AsNum(a[5], "ui.bar", line),
                    a.Count == 7 ? PsValues.Fmt(a[6]) : null, line);
            });
            ns.Members["set_fill"] = PsBuiltins.BF("ui.set_fill", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "ui.set_fill(bar句柄, pct 0..1)", line);
                su.SetFill(AsHandle(a[0], "ui.set_fill", line), AsNum(a[1], "ui.set_fill", line), line);
                return null;
            });
            ns.Members["set_color"] = PsBuiltins.BF("ui.set_color", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "ui.set_color(bar/text/button句柄, \"#rrggbb\") — bar 改填充色, text/button 改文本色 (v1.28.0)", line);
                su.SetColor(AsHandle(a[0], "ui.set_color", line), PsValues.Fmt(a[1]), line);
                return null;
            });
            ns.Members["set_visible"] = PsBuiltins.BF("ui.set_visible", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "ui.set_visible(句柄, bool) — 未知句柄 = warn + no-op", line);
                su.SetVisible(AsHandle(a[0], "ui.set_visible", line), PsValues.Truthy(a[1]));
                return null;
            });
            ns.Members["destroy"] = PsBuiltins.BF("ui.destroy", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "ui.destroy(句柄) → bool (不存在 = warn + false)", line);
                return su.DestroyElem(AsHandle(a[0], "ui.destroy", line));
            });
            ns.Members["clear"] = PsBuiltins.BF("ui.clear", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "ui.clear() → 清理数 — 清当前场景全部自建 uGUI 元素 (不在场景 = warn + 0)", line);
                return su.ClearCurrent();
            });
        }

        /// <summary>P2 句柄参数: 数字且 > 0 (0 = 不在场景时创建 API 的无效返回值, 链传须先判)。</summary>
        private static long AsHandle(object v, string fn, int line)
        {
            long h = v is long l ? l : v is double d ? (long)d
                : throw new PsRuntimeError($"{fn} 的句柄须为数字 (ui.panel/text/button/bar 的返回值), 实为 {PsValues.TypeName(v)}", line);
            if (h <= 0) throw new PsRuntimeError($"{fn} 的句柄须 > 0 (0 = 不在场景时创建 API 的无效返回值), 实为 {h}", line);
            return h;
        }

        /// <summary>P3 (v1.28.0): 父句柄参数 — 允许 0 (场景 canvas 根, 元素中心锚); 负数拒绝。</summary>
        private static long AsParent(object v, string fn, int line)
        {
            long h = v is long l ? l : v is double d ? (long)d
                : throw new PsRuntimeError($"{fn} 的父句柄须为数字 (ui.panel 的返回值, 或 0=canvas 根), 实为 {PsValues.TypeName(v)}", line);
            if (h < 0) throw new PsRuntimeError($"{fn} 的父句柄须 >= 0 (0 = 场景 canvas 根, 中心锚)", line);
            return h;
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
