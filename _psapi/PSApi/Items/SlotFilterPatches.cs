using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace PSApi.Items
{
    /// <summary>
    /// v0.5.12: Harmony patch 方式放宽槽位过滤器。
    /// 红线: 不修改 mayInventoryAddItemFunc 委托字段 (含包装) — 任何赋值都破坏原生闭包委托协议致拖拽失灵。
    ///
    /// 替代方案: patch 三个 virtual/instance 方法，绕过 mayAdd 检查:
    /// 1. GameSlotInventory.MayHaveValidInventorySlot Postfix → 白名单物品 true (StartDrag 存在性预检)
    /// 2. GameSlotInventory.TryInventorySlot Postfix → 手动创建 SlotMarker (Drag 阶段槽位查找)
    /// 3. SlotMarker.TryAcceptOnce Prefix → 调 AcceptUnchecked 绕过 mayAdd (EndDrag 实际放置)
    ///
    /// 拖拽管线 (native 侧):
    ///   StartDrag → MayHaveValidInventorySlot (预筛高亮)
    ///   Drag      → TryInventorySlot (找具体槽位 → lastSlot)
    ///   EndDrag   → lastSlot.TryAcceptOnce (执行放置)
    /// v0.9.19: internal → public (测试台段 106c 谓词钉值需跨程序集访问; 行为不变)。
    /// </summary>
    public static class SlotFilterRegistry
    {
        private static readonly Dictionary<IntPtr, HashSet<string>> _slotWhitelists =
            new Dictionary<IntPtr, HashSet<string>>();
        // v0.6.3: 排他槽位 (ui=custom 自装配机器) — 非白名单物品一律拒, 连原生放行也压住。
        // 自装配槽位零 interop 委托, 原生 mayAdd 链为空, 原生 MayHave 判定不可靠, 必须排他。
        private static readonly HashSet<IntPtr> _exclusiveSlots = new HashSet<IntPtr>();
        // v0.5.22: 输入类槽位 → 所属机器 (加工中插入锁定用; 输出槽不登记)
        private static readonly Dictionary<IntPtr, GameItem> _slotMachines =
            new Dictionary<IntPtr, GameItem>();
        // v0.8.0: 脚本/机器面板手动插入锁 (ui.machine_lock; psui 机器组装中锁料/成品未取走拒新部件)
        private static readonly HashSet<IntPtr> _insertLocks = new HashSet<IntPtr>();
        // v0.9.20: strict_footprint 槽 (psui slot strict_footprint: true) — 固定槽默认不验 footprint
        // (slot 语义=一格任意大小), 仅本集合内的槽由 GridPlacementGuard 验外接矩形≤槽格数。
        private static readonly HashSet<IntPtr> _strictFootprint = new HashSet<IntPtr>();
        private static MelonLogger.Instance _log;

        internal static void Init(MelonLogger.Instance log) => _log = log;

        /// <summary>v0.9.20: strict_footprint 注册/查询 (psui 构建期按属性登记)。</summary>
        internal static void SetStrictFootprint(IntPtr slotPtr, bool strict)
        {
            if (slotPtr == IntPtr.Zero) return;
            if (strict) _strictFootprint.Add(slotPtr);
            else _strictFootprint.Remove(slotPtr);
        }

        internal static bool IsStrictFootprint(IntPtr slotPtr) => _strictFootprint.Contains(slotPtr);

        /// <summary>v0.8.0: 手动插入锁 (三个 patch 的 IsInsertLocked 统一拦截; 与进度机忙锁并列)。</summary>
        internal static void SetInsertLock(IntPtr slotPtr, bool locked)
        {
            if (slotPtr == IntPtr.Zero) return;
            if (locked) _insertLocks.Add(slotPtr);
            else _insertLocks.Remove(slotPtr);
        }

        internal static void Register(IntPtr slotPtr, HashSet<string> whitelist, bool exclusive = false)
        {
            if (slotPtr != IntPtr.Zero && whitelist != null && whitelist.Count > 0)
            {
                _slotWhitelists[slotPtr] = whitelist;
                if (exclusive) _exclusiveSlots.Add(slotPtr);
            }
        }

        internal static bool IsExclusive(IntPtr slotPtr) => _exclusiveSlots.Contains(slotPtr);

        /// <summary>v0.9.9: 白名单统一匹配 — 普通条目按物品 id; "#TAG" 条目按物品 itemTypes 标签命中
        /// (大小写不敏感; 鉴定机"只收武器"用 whitelist: ["#WEAPON"])。标签条目只放行不报错。
        /// v2.0.3: "#TAG" 追加运行时标签命中 (state/modifiedState 的 IsTag) — 原版背包/挎包/腰包的
        /// BACKPACK_TAG 是 ContainerHelper.InitBackpackItem 的 EnableTag 运行时标签, itemTypes 够不着
        /// (ContainerItemDirectory 七工厂实证: BackpackSmall/Medium/MediumMilitary/Large/LargeMilitary/
        /// FannyPack/Satchel 全部调 InitBackpackItem; SaveBag 系不调)。旅行物品箱胸式槽 ["#BACKPACK_TAG"]。
        /// v0.9.13: "#DATA:key=value" 条目按物品 NBT 数据匹配 — 多个 DATA 条目之间是 AND 语义
        /// (全部命中才算), 与 id/#TAG 的 OR 组并存时两组都必须过 (旅行物品箱"appraised=1 且 wtype=gun")。
        /// 比较: 两侧都能解析为数值时按数值比 (double), 否则按字符串 OrdinalIgnoreCase 比。
        /// v0.9.14: "!" 前缀排除条目 (黑名单) — 任一命中即拒, 先于所有放行组判定;
        /// "!id" 按物品 id 排除; "!#TAG" 按 itemTypes+运行时标签排除 (v2.0.3 同步扩展);
        /// 特例 "!#CONTAINER" 额外按运行时容器判定
        /// (item.contentWindow 非空 = 容器/机器 — 原版背包/腰包/储物柜是 EnableTag 运行时标签, itemTypes 够不着)。
        /// 纯排除名单 (无放行条目) = 默认全放行只挡排除项 (旅行物品箱口袋/大件 ["!#CONTAINER","!#MACHINE"])。
        /// v0.9.19: internal → public (测试台段 106c 谓词钉值; 行为不变)。
        /// v0.9.21: "*" 全放行条目 (OR 组一员, 任何非空 id 命中) — 显式"全物品可放"写法
        /// (g_big v0.45.2 放开容器/机器过滤; 比纯排除名单的隐式全放更可读)。</summary>
        public static bool MatchesWhitelist(HashSet<string> wl, GameItem item, string itemId)
        {
            if (wl == null || string.IsNullOrEmpty(itemId)) return false;
            // 第 0 组: "!" 排除条目 — 任一命中即拒
            foreach (var e in wl)
            {
                if (string.IsNullOrEmpty(e) || e[0] != '!' || e.Length < 2) continue;
                if (MatchesExclusion(e.Substring(1), item, itemId)) return false;
            }
            // 第一组: id / #TAG / "*", OR 语义 (无该组条目视为已通过)
            bool hasOrGroup = false, orHit = false;
            if (wl.Contains(itemId)) { orHit = true; hasOrGroup = true; }
            foreach (var e in wl)
            {
                if (string.IsNullOrEmpty(e)) continue;
                if (e[0] == '!') continue;
                if (e.StartsWith("#DATA:", StringComparison.OrdinalIgnoreCase)) continue;
                hasOrGroup = true;   // 普通 id 条目与 #TAG 条目都算 OR 组成员
                if (orHit) break;
                if (e == "*") { orHit = true; break; }   // v0.9.21: 全放行条目
                if (e[0] != '#' || e.Length < 2) continue;   // 普通 id 条目: 直接 Contains 已判过
                if (item != null)
                {
                    string tag = e.Substring(1);
                    try
                    {
                        var types = item.itemTypes;
                        if (types != null)
                        {
                            foreach (var t in types)
                                if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) { orHit = true; break; }
                        }
                    }
                    catch { }
                    // v2.0.3: itemTypes 不中再查运行时标签 (EnableTag 系, 原版背包 BACKPACK_TAG 等)
                    if (!orHit && RuntimeHasTag(item, tag)) orHit = true;
                }
                if (orHit) break;
            }
            if (hasOrGroup && !orHit) return false;
            // 第二组: #DATA:key=value, AND 语义
            foreach (var e in wl)
            {
                if (string.IsNullOrEmpty(e) || !e.StartsWith("#DATA:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!MatchesDataEntry(e.Substring(6), item)) return false;
            }
            return true;
        }

        /// <summary>v0.9.14: 单条排除条目 ("!" 之后的主体) 匹配。"#CONTAINER" 特例: itemTypes 标签 +
        /// 运行时容器 (contentWindow 非空 — 机器面板也是 contentWindow, 故机器同样被挡)。
        /// v2.0.3: 其余 "!#TAG" 同步查运行时标签 (与放行组同语义, 先 CONTAINER 特例再标签)。</summary>
        private static bool MatchesExclusion(string body, GameItem item, string itemId)
        {
            if (string.IsNullOrEmpty(body)) return false;
            if (body[0] != '#') return string.Equals(body, itemId, StringComparison.Ordinal);
            string tag = body.Substring(1);
            if (tag.Length == 0) return false;
            if (item == null) return false;
            if (string.Equals(tag, "CONTAINER", StringComparison.OrdinalIgnoreCase))
            {
                try { if (item.contentWindow != null) return true; } catch { }
            }
            try
            {
                var types = item.itemTypes;
                if (types != null)
                    foreach (var t in types)
                        if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return RuntimeHasTag(item, tag);   // v2.0.3
        }

        /// <summary>v2.0.3: 运行时标签命中 (EnableTag 写入的 state 标签; modifiedState 兜底 —
        /// TagGetInt 同款 modifiedState??state 模式的双向版, 槽位变化重建 modifiedState 后仍命中)。</summary>
        internal static bool RuntimeHasTag(GameItem item, string tag)
        {
            if (item == null || string.IsNullOrEmpty(tag)) return false;
            try { var st = item.state; if (st != null && st.IsTag(tag)) return true; } catch { }
            try { var mt = item.modifiedState; if (mt != null && mt.IsTag(tag)) return true; } catch { }
            return false;
        }

        /// <summary>v0.9.13: 单条 DATA 谓词 "key=value" 匹配。物品为 null 或键不存在 = 不命中。</summary>
        private static bool MatchesDataEntry(string entry, GameItem item)
        {
            if (item == null || string.IsNullOrEmpty(entry)) return false;
            int eq = entry.IndexOf('=');
            if (eq <= 0) return false;
            string key = entry.Substring(0, eq).Trim();
            string want = entry.Substring(eq + 1).Trim();
            if (key.Length == 0) return false;
            object got;
            try { got = ItemsFacade.GetData(item, key); } catch { return false; }
            if (got == null) return false;
            string gotStr;
            try { gotStr = System.Convert.ToString(got, System.Globalization.CultureInfo.InvariantCulture); } catch { return false; }
            if (gotStr == null) return false;
            if (double.TryParse(gotStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gNum)
                && double.TryParse(want, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double wNum))
                return gNum == wNum;
            return string.Equals(gotStr, want, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>BuildItem / 存档重挂两处调用。注册机器的输入/输出槽位白名单。</summary>
        internal static void RegisterMachine(GameItem machine, string itemId)
        {
            if (machine == null || string.IsNullOrEmpty(itemId)) return;
            if (!RecipeService.TryGetMachineSpecPublic(itemId, out var spec)) return;

            // v0.6.3: ui=custom 自装配机器的输入/输出槽排他注册 (原生委托链为空, 判定全归本注册表)
            bool custom = RecipeService.IsCustomUi(spec.Ui);

            if (spec.InputWhitelist != null && spec.InputWhitelist.Count > 0)
            {
                int inIdx = spec.InputSlot ?? 2;
                RegisterSlot(machine, itemId, inIdx, spec.InputWhitelist, "input", lockable: true, exclusive: custom);
            }

            if (spec.OutputWhitelist != null && spec.OutputWhitelist.Count > 0)
            {
                int outIdx = spec.OutputSlot ?? (spec.InputSlot ?? 2) + 1;
                RegisterSlot(machine, itemId, outIdx, spec.OutputWhitelist, "output", lockable: false, exclusive: custom);
            }

            if (spec.SlotWhitelists != null)
            {
                foreach (var sw in spec.SlotWhitelists)
                {
                    if (sw?.Slot == null || sw.Whitelist == null || sw.Whitelist.Count == 0) continue;
                    RegisterSlot(machine, itemId, sw.Slot.Value, sw.Whitelist, $"extra[{sw.Slot.Value}]", lockable: true);
                }
            }
        }

        private static void RegisterSlot(GameItem machine, string itemId, int slotIdx, List<string> ids, string label, bool lockable, bool exclusive = false)
        {
            try
            {
                var slot = SlotBridge.GetSlot(machine, slotIdx);
                if (slot == null) return;
                // v0.6.2: 注册时归一化 — 剥 game: 前缀 (patch 匹配用的是裸 identifier;
                // ui=custom 机器示例包写了 "game:scrap_metal" 导致 Postfix 永不命中的事故)
                var wl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in ids)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string s = raw.Trim();
                    if (s.StartsWith("game:", StringComparison.OrdinalIgnoreCase))
                        s = s.Substring("game:".Length);
                    wl.Add(s);
                }
                if (wl.Count == 0) return;
                Register(slot.Pointer, wl, exclusive);
                if (lockable) _slotMachines[slot.Pointer] = machine;
                PsApi.Log(_log, $"slot-filter register: {itemId} {label} slot[{slotIdx}] wl={wl.Count}{(exclusive ? " exclusive" : "")}");
            }
            catch (Exception e) { PsApi.Warn(_log, $"slot-filter register {label} failed ({itemId}, {slotIdx}): {e.Message}"); }
        }

        internal static bool TryGetWhitelist(IntPtr slotPtr, out HashSet<string> whitelist)
        {
            return _slotWhitelists.TryGetValue(slotPtr, out whitelist);
        }

        /// <summary>v0.5.22: 该槽位是否因机器加工中而禁止放入 (进度机 progress>0 时输入槽连放入也锁, 仿原版机器忙)。
        /// v0.8.0: 手动插入锁 (_insertLocks) 优先判定 — 脚本锁与进度锁并列。</summary>
        internal static bool IsInsertLocked(IntPtr slotPtr)
        {
            if (_insertLocks.Contains(slotPtr)) return true;
            try
            {
                if (!_slotMachines.TryGetValue(slotPtr, out var machine) || machine == null) return false;
                string id = machine.identifier;
                if (string.IsNullOrEmpty(id)) return false;
                if (!RecipeService.TryGetMachineSpecPublic(id, out var spec) || spec.Progress == null) return false;
                return ProgressRecipeService.GetProgress(machine) > 0;
            }
            catch { return false; }   // 场景卸载后机器引用失效 → 不锁
        }

        /// <summary>v2.0.6: 所有权闸门 (NPC 待售货盗窃漏洞修复) — 受管槽位 (whitelist 注册) 拒收
        /// 非玩家所有物品。原版语义实证 (ISIL): 所有权 = GameItem 的 IS_OWNED_TAG 标签
        /// (GeneralHelper.IsItemOwned = IsTag("IS_OWNED_TAG")); DirectoryMaster.Item 工厂默认
        /// isOwned=true (玩家获取的一切物品自带标签), NPC 货品由客户清单工厂显式
        /// SetItemOwned(false) 摘除 (例: StoreClientListCartel); 原版容器/背包由
        /// ContainerHelper.AllowOnlyOwnedItems 挂 mayAdd 谓词拒收无标签物品 — 但 psui 槽位是裸
        /// GameGridInventory/GameSlotInventory (零原生谓词), 白名单强放路径 (AcceptUnchecked/
        /// UncheckedAccept) 又绕过一切原生判定 → 交易未完成即可把 NPC 柜台货拖进机器槽, 送走
        /// NPC 即白拿, 必须自闸。读取失败保守拒 (反盗窃闸门宁可误拦); item=null 不拦。</summary>
        internal static bool IsItemPlayerOwned(GameItem item)
        {
            if (item == null) return true;
            try { return GeneralHelper.IsItemOwned(item); }
            catch { return false; }
        }

        /// <summary>纯函数 (无头可测, 测试台段 134): 受管槽位的所有权放行判定 — 不受管槽位
        /// 永不拦 (原生谓词链自己管, 如柜台谈判桌上整理 NPC 货是合法原生行为);
        /// 受管槽位仅玩家所有物品可放。</summary>
        public static bool OwnershipAllows(bool slotManaged, bool itemOwned) => !slotManaged || itemOwned;

        internal static void Clear()
        {
            _slotWhitelists.Clear();
            _exclusiveSlots.Clear();
            _slotMachines.Clear();
            _insertLocks.Clear();
            _strictFootprint.Clear();
        }

        /// <summary>patch 诊断日志 (v0.5.14 修复: 之前 PsApi.Log(null,..) 被静默丢弃)。</summary>
        internal static void Dbg(string msg) => PsApi.Log(_log, msg);
        internal static void DbgWarn(string msg) => PsApi.Warn(_log, msg);
    }

    /// <summary>Patch 1: MayHaveValidInventorySlot — StartDrag 存在性预检。</summary>
    [HarmonyPatch(typeof(GameSlotInventory), nameof(GameSlotInventory.MayHaveValidInventorySlot))]
    internal static class MayHaveValidSlotPatch
    {
        private static readonly HashSet<string> _probeIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scrap_metal", "common_ore", "newspaper", "bottled_water" };

        private static void Postfix(GameSlotInventory __instance, GameItem item, ref bool __result)
        {
            string probeId = null;
            try { if (item != null && _probeIds.Contains(item.identifier)) probeId = item.identifier; } catch { }

            IntPtr slotPtr;
            try { slotPtr = __instance.Pointer; } catch { return; }

            if (SlotFilterRegistry.IsInsertLocked(slotPtr))
            {
                if (__result) __result = false;
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot item={probeId} slotPtr={slotPtr.ToInt64()} insert-locked (machine busy)");
                return;
            }

            // v2.0.6: 所有权闸门 — 受管槽位 (whitelist 注册) 拒收非玩家所有物品 (NPC 待售货),
            // 高亮预检阶段即拒 (红=禁放); 不受管槽位放行原生 (谈判桌整理 NPC 货合法)
            if (SlotFilterRegistry.TryGetWhitelist(slotPtr, out _) && !SlotFilterRegistry.IsItemPlayerOwned(item))
            {
                if (__result) __result = false;
                string oid = probeId;
                try { if (oid == null && item != null) oid = item.identifier; } catch { }
                SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot item={(oid ?? "?")} slotPtr={slotPtr.ToInt64()} not-owned (NPC ware?) -> reject");
                return;
            }

            // v0.6.3: 排他槽位先判 — 非白名单一律拒, 原生 true 也压住 (自装配槽位零委托, 原生判定不可靠)
            bool exclusive = SlotFilterRegistry.IsExclusive(slotPtr);
            if (exclusive)
            {
                string exId = null;
                try { if (item != null) exId = item.identifier; } catch { }
                bool wlHit = !string.IsNullOrEmpty(exId)
                    && SlotFilterRegistry.TryGetWhitelist(slotPtr, out var exWl)
                    && SlotFilterRegistry.MatchesWhitelist(exWl, item, exId);
                if (!wlHit)
                {
                    if (__result) __result = false;
                    if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot item={exId} slotPtr={slotPtr.ToInt64()} exclusive-reject");
                    return;
                }
            }

            if (__result)
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot item={probeId} slotPtr={slotPtr.ToInt64()} native-already-true");
                return;
            }
            if (item == null) return;

            if (!SlotFilterRegistry.TryGetWhitelist(slotPtr, out var whitelist))
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot item={probeId} slotPtr={slotPtr.ToInt64()} NOT-registered");
                return;
            }

            string itemId;
            try { itemId = item.identifier; } catch { return; }
            if (string.IsNullOrEmpty(itemId) || !SlotFilterRegistry.MatchesWhitelist(whitelist, item, itemId))
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot item={itemId} slotPtr={slotPtr.ToInt64()} registered-but-not-whitelisted");
                return;
            }

            try
            {
                var existing = __instance.childItem;
                if (existing == null) { __result = true; SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot true item={itemId} slotPtr={slotPtr.ToInt64()}"); return; }
                string existingId;
                try { existingId = existing.identifier; } catch { return; }
                if (string.Equals(existingId, itemId, StringComparison.OrdinalIgnoreCase))
                {
                    __result = true;
                    SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot true (same-id) item={itemId} slotPtr={slotPtr.ToInt64()}");
                }
            }
            catch { }
        }
    }

    /// <summary>Patch 2: TryInventorySlot — Drag 阶段槽位查找，手动创建 SlotMarker。</summary>
    [HarmonyPatch(typeof(GameSlotInventory), nameof(GameSlotInventory.TryInventorySlot),
        new[] { typeof(GameItem), typeof(int), typeof(Vector2), typeof(GridShape), typeof(TagSystem) })]
    internal static class TryInventorySlotPatch
    {
        private static readonly HashSet<string> _probeIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scrap_metal", "common_ore", "newspaper", "bottled_water" };

        private static void Postfix(GameSlotInventory __instance, GameItem item, int requestedNum,
            Vector2 itemGraphicsCenterPosition, GridShape tempShape, TagSystem tempState,
            ref SlotMarker __result)
        {
            string probeId = null;
            try { if (item != null && _probeIds.Contains(item.identifier)) probeId = item.identifier; } catch { }

            IntPtr slotPtr;
            try { slotPtr = __instance.Pointer; } catch { return; }

            if (SlotFilterRegistry.IsInsertLocked(slotPtr))
            {
                if (__result != null) __result = null;
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot item={probeId} slotPtr={slotPtr.ToInt64()} insert-locked (machine busy)");
                return;
            }

            // v2.0.6: 所有权闸门 — 受管槽位拒收非玩家所有物品 (NPC 待售货), Drag 阶段不产 marker
            if (SlotFilterRegistry.TryGetWhitelist(slotPtr, out _) && !SlotFilterRegistry.IsItemPlayerOwned(item))
            {
                if (__result != null) __result = null;
                string oid = probeId;
                try { if (oid == null && item != null) oid = item.identifier; } catch { }
                SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot item={(oid ?? "?")} slotPtr={slotPtr.ToInt64()} not-owned (NPC ware?) -> reject");
                return;
            }

            // v0.6.3: 排他槽位先判 — 非白名单物品即使有原生 marker 也作废
            bool exclusive = SlotFilterRegistry.IsExclusive(slotPtr);
            if (exclusive)
            {
                string exId = null;
                try { if (item != null) exId = item.identifier; } catch { }
                bool wlHit = !string.IsNullOrEmpty(exId)
                    && SlotFilterRegistry.TryGetWhitelist(slotPtr, out var exWl)
                    && SlotFilterRegistry.MatchesWhitelist(exWl, item, exId);
                if (!wlHit)
                {
                    if (__result != null) __result = null;
                    if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot item={exId} slotPtr={slotPtr.ToInt64()} exclusive-reject");
                    return;
                }
            }

            if (__result != null)
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot item={probeId} slotPtr={slotPtr.ToInt64()} native-marker (no override needed)");
                return;
            }
            if (item == null) return;

            if (!SlotFilterRegistry.TryGetWhitelist(slotPtr, out var whitelist))
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot item={probeId} slotPtr={slotPtr.ToInt64()} NOT-registered");
                return;
            }

            string itemId;
            try { itemId = item.identifier; } catch { return; }
            if (string.IsNullOrEmpty(itemId) || !SlotFilterRegistry.MatchesWhitelist(whitelist, item, itemId))
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot item={itemId} slotPtr={slotPtr.ToInt64()} registered-but-not-whitelisted");
                return;
            }

            try
            {
                var existing = __instance.childItem;
                GameItem target = null;
                if (existing != null)
                {
                    string existingId;
                    try { existingId = existing.identifier; } catch { return; }
                    if (!string.Equals(existingId, itemId, StringComparison.OrdinalIgnoreCase))
                        return;
                    target = existing;
                }

                var marker = new SlotMarker();
                marker.inventory = __instance;
                marker.item = item;
                marker.index = 0;
                marker.itemGridShape = tempShape;
                marker.targetItem = target;
                marker.numTransfer = requestedNum;
                marker.priority = 0;
                __result = marker;
                // v0.5.14 诊断: 白名单命中即记录 (含 marker 有效性)
                bool valid = false;
                try { valid = marker.IsValid(); } catch { }
                SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot marker created item={itemId} slotPtr={slotPtr.ToInt64()} valid={valid} tempShape={(tempShape == null ? "null" : "set")} target={(target == null ? "-" : "same")}");
            }
            catch (Exception e) { SlotFilterRegistry.DbgWarn($"slot-filter TryInventorySlot postfix: {e.Message}"); }
        }
    }

    /// <summary>v0.6.4: 网格槽排他版 Patch 1 — GameGridInventory.MayHaveValidInventorySlot。
    /// 与 GameSlotInventory 版不同: 白名单命中不需要强制 true/造 marker, 原生网格实现自己找空位,
    /// 这里只负责"排他槽位拒非白名单"(零委托网格原生会乱放任何东西进来)。</summary>
    [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.MayHaveValidInventorySlot))]
    internal static class GridMayHaveValidSlotPatch
    {
        private static void Postfix(GameGridInventory __instance, GameItem item, ref bool __result)
        {
            IntPtr slotPtr;
            try { slotPtr = __instance.Pointer; } catch { return; }

            if (SlotFilterRegistry.IsInsertLocked(slotPtr))
            {
                if (__result) __result = false;
                return;
            }
            // v2.0.6: 所有权闸门 — 受管网格槽拒收非玩家所有物品 (NPC 待售货)
            if (SlotFilterRegistry.TryGetWhitelist(slotPtr, out _) && !SlotFilterRegistry.IsItemPlayerOwned(item))
            {
                if (__result) __result = false;
                SlotFilterRegistry.Dbg($"slot-filter dbg: grid MayHaveValidSlot slotPtr={slotPtr.ToInt64()} not-owned (NPC ware?) -> reject");
                return;
            }
            if (!SlotFilterRegistry.IsExclusive(slotPtr)) return;   // 非排他网格槽: 原生判定

            string itemId = null;
            try { if (item != null) itemId = item.identifier; } catch { }
            bool wlHit = !string.IsNullOrEmpty(itemId)
                && SlotFilterRegistry.TryGetWhitelist(slotPtr, out var wl)
                && SlotFilterRegistry.MatchesWhitelist(wl, item, itemId);
            if (!wlHit)
            {
                if (__result) __result = false;
                SlotFilterRegistry.Dbg($"slot-filter dbg: grid MayHaveValidSlot item={(itemId ?? "?")} slotPtr={slotPtr.ToInt64()} exclusive-reject");
            }
        }
    }

    /// <summary>v0.6.4: 网格槽排他版 Patch 2 — GameGridInventory.TryInventorySlot。排他槽位非白名单物品作废原生 marker。</summary>
    [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.TryInventorySlot),
        new[] { typeof(GameItem), typeof(int), typeof(Vector2), typeof(GridShape), typeof(TagSystem) })]
    internal static class GridTryInventorySlotPatch
    {
        private static void Postfix(GameGridInventory __instance, GameItem item, ref SlotMarker __result)
        {
            IntPtr slotPtr;
            try { slotPtr = __instance.Pointer; } catch { return; }

            if (SlotFilterRegistry.IsInsertLocked(slotPtr))
            {
                if (__result != null) __result = null;
                return;
            }
            // v2.0.6: 所有权闸门 — 受管网格槽拒收非玩家所有物品 (NPC 待售货), 作废原生 marker
            if (SlotFilterRegistry.TryGetWhitelist(slotPtr, out _) && !SlotFilterRegistry.IsItemPlayerOwned(item))
            {
                if (__result != null) __result = null;
                SlotFilterRegistry.Dbg($"slot-filter dbg: grid TryInventorySlot slotPtr={slotPtr.ToInt64()} not-owned (NPC ware?) -> reject");
                return;
            }
            if (!SlotFilterRegistry.IsExclusive(slotPtr)) return;
            if (__result == null) return;   // 原生没找到位置, 无需干预 (白名单物品的放置由原生网格逻辑完成)

            string itemId = null;
            try { if (item != null) itemId = item.identifier; } catch { }
            bool wlHit = !string.IsNullOrEmpty(itemId)
                && SlotFilterRegistry.TryGetWhitelist(slotPtr, out var wl)
                && SlotFilterRegistry.MatchesWhitelist(wl, item, itemId);
            if (!wlHit)
            {
                __result = null;
                SlotFilterRegistry.Dbg($"slot-filter dbg: grid TryInventorySlot item={(itemId ?? "?")} slotPtr={slotPtr.ToInt64()} exclusive-reject");
            }
        }
    }

    /// <summary>Patch 3: TryAcceptOnce — EndDrag 实际放置，绕过 mayAdd 调 AcceptUnchecked。</summary>
    [HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce))]
    internal static class TryAcceptOncePatch
    {
        private static readonly HashSet<string> _probeIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scrap_metal", "common_ore", "newspaper", "bottled_water" };

        private static bool Prefix(SlotMarker __instance, int amount, ref int __result)
        {
            GameItem item0 = null;
            string probeId = null;
            try { item0 = __instance.item; } catch { }
            try { if (item0 != null && _probeIds.Contains(item0.identifier)) probeId = item0.identifier; } catch { }

            GameInventory inventory;
            try { inventory = __instance.inventory; } catch { return true; }
            if (inventory == null) return true;

            IntPtr slotPtr;
            try { slotPtr = inventory.Pointer; } catch { return true; }

            // v0.5.22: 加工中输入槽连放入也锁 — 无论白名单/原生放行都拒收 (原生路径也会被此前缀拦截)
            if (SlotFilterRegistry.IsInsertLocked(slotPtr))
            {
                __result = 0;
                SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce item={(probeId ?? "?")} slotPtr={slotPtr.ToInt64()} insert-locked (machine busy) -> reject");
                return false;
            }

            // v2.0.6: 所有权闸门 (NPC 待售货盗窃漏洞根修) — 受管槽位 (whitelist 注册) 拒收
            // 非玩家所有物品 (__result=0 回弹)。原版容器/背包由 AllowOnlyOwnedItems 谓词拒收
            // 无 IS_OWNED_TAG 物品; psui 裸槽无原生谓词且下方强放路径 (AcceptUnchecked/
            // UncheckedAccept) 绕过一切原生判定, 不拦则交易未完成拖 NPC 柜台货进机器=白拿。
            // 不受管槽位 (谈判桌等) 放行原生 — 谈判中整理 NPC 货是合法原生行为, 不可误伤。
            if (SlotFilterRegistry.TryGetWhitelist(slotPtr, out _) && !SlotFilterRegistry.IsItemPlayerOwned(item0))
            {
                __result = 0;
                string nid = probeId;
                try { if (nid == null && item0 != null) nid = item0.identifier; } catch { }
                SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce item={(nid ?? "?")} slotPtr={slotPtr.ToInt64()} not-owned (NPC ware?) -> reject");
                return false;
            }

            // v0.6.3: 排他槽位先判 — 非白名单物品直接拒, 不放给原生 (自装配槽位零委托, 原生会乱放)
            if (SlotFilterRegistry.IsExclusive(slotPtr))
            {
                string exId = null;
                try { if (item0 != null) exId = item0.identifier; } catch { }
                bool wlHit = !string.IsNullOrEmpty(exId)
                    && SlotFilterRegistry.TryGetWhitelist(slotPtr, out var exWl)
                    && SlotFilterRegistry.MatchesWhitelist(exWl, item0, exId);
                if (!wlHit)
                {
                    __result = 0;
                    SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce item={(exId ?? "?")} slotPtr={slotPtr.ToInt64()} exclusive-reject");
                    return false;
                }
            }

            if (!SlotFilterRegistry.TryGetWhitelist(slotPtr, out var whitelist))
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce item={probeId} slotPtr={slotPtr.ToInt64()} NOT-registered -> native");
                return true;
            }

            if (item0 == null) return true;

            string itemId;
            try { itemId = item0.identifier; } catch { return true; }
            if (string.IsNullOrEmpty(itemId) || !SlotFilterRegistry.MatchesWhitelist(whitelist, item0, itemId))
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce item={itemId} slotPtr={slotPtr.ToInt64()} registered-but-not-whitelisted -> native");
                return true;
            }

            // v0.9.19: 越界强放修复 (实测: 口袋 2x3 红标仍可强放 1x3) — 白名单强制放行前先过几何守卫:
            // grid_slot: footprint 越界/shape 洞/压已占格 → 硬拒 (__result=0 回弹), "红=禁放" 真正成立。
            // 旧路径 AcceptUnchecked/UncheckedAccept 绕过原生网格判定且不重设物品位置, 是放行口。
            // v0.9.20: 固定 slot 默认不验 footprint (slot 语义=一格任意大小; v0.9.19 强制校验是回归),
            //   仅 strict_footprint: true 的槽验 (SlotFilterRegistry._strictFootprint)。
            GridShape markerShape = null;
            try { markerShape = __instance.itemGridShape; } catch { }
            GameItem stackTarget = null;
            try { stackTarget = __instance.targetItem; } catch { }
            if (!GridPlacementGuard.MarkerFits(inventory, item0, markerShape, stackTarget,
                    SlotFilterRegistry.IsStrictFootprint(slotPtr)))
            {
                __result = 0;
                SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce item={itemId} slotPtr={slotPtr.ToInt64()} placement-invalid (bounds/hole/overlap) -> reject");
                return false;
            }

            try
            {
                // 防 double-prefix 二次执行: 物品已在本槽则直接报成功
                var slotInv = inventory.TryCast<GameSlotInventory>();
                if (slotInv != null)
                {
                    GameItem cur = null;
                    try { cur = slotInv.childItem; } catch { }
                    if (cur != null && cur.Pointer == item0.Pointer)
                    {
                        __result = 1;
                        SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce prefix item={itemId} slotPtr={slotPtr.ToInt64()} already-in-slot -> success");
                        return false;
                    }
                }

                int r = -1;
                try { r = __instance.AcceptUnchecked(); }
                catch (Exception e) { SlotFilterRegistry.DbgWarn($"slot-filter dbg: AcceptUnchecked err item={itemId}: {e.Message}"); }
                SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce prefix item={itemId} slotPtr={slotPtr.ToInt64()} acceptResult={r}");
                if (r > 0) { __result = r; return false; }

                // 回退: §9i 实证路径 — 槽级 UncheckedAccept 原生放行多格物品, SlotMarker.AcceptUnchecked 有额外检查
                var parent = FindParentInventory(item0);
                if (parent != null)
                {
                    try { if (parent.Pointer != inventory.Pointer) parent.Expel(item0); } catch { }
                }
                bool ok = false;
                try { ok = inventory.UncheckedAccept(item0); }
                catch (Exception e) { SlotFilterRegistry.DbgWarn($"slot-filter dbg: UncheckedAccept err item={itemId}: {e.Message}"); }
                SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce fallback item={itemId} slotPtr={slotPtr.ToInt64()} UncheckedAccept={ok}");
                if (ok) { __result = 1; return false; }
                return true; // 全失败 → 走原生 (回弹)
            }
            catch (Exception e)
            {
                SlotFilterRegistry.DbgWarn($"slot-filter dbg: TryAcceptOnce prefix err item={itemId}: {e.Message}");
                return true;
            }
        }

        private static GameInventory FindParentInventory(GameItem item)
        {
            try
            {
                var parents = item.parents;
                if (parents == null) return null;
                foreach (var p in parents)
                {
                    if (p == null) continue;
                    var inv = p.TryCast<GameInventory>();
                    if (inv != null) return inv;
                }
            }
            catch { }
            return null;
        }
    }
}