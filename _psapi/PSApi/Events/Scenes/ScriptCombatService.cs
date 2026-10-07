using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using PSApi.Events.PsScript;
using PSApi.Items;

namespace PSApi.Events.Scenes
{
    /// <summary>
    /// P3 (v1.28.0) script 驱动场景的战斗能力服务 — combat.* builtin 的游戏侧后端。
    /// 数值公式仍由 pss 经 combat.* 公式 builtin 调 RaidCore 纯函数 (单一事实源), 本服务只提供:
    ///   实时战斗会话入口 (combat.rt_start → RtCombatService, v1.29.0 起唯一实战路径);
    ///   武器链 (箱槽 s_main 已鉴定枪械 w_atk/w_stab → s_melee 近战 → 空手;
    ///     FIREPOWER=w_atk, ACC=round(w_stab/4) 钳1-25, RELIABILITY=round(w_stab) 钳0-100;
    ///     rt 用 ResolveRtWeapon 逐级退化: 收武器→拳头 / 无弹→枪托 / w_mode=0 枪当近战抡);
    ///   弹药双源查找 (物品箱树递归 + 外出栏嵌套, UseCountHelper 次数物品) 与消耗;
    ///   投掷槽 (s_throw1-3) 第一个有物品的槽;
    ///   原生重伤 (combat.major_wound → HealthData.ReceiveMajorWound)。
    /// v1.35.0 (2026-09-30 拍板): 经典按钮面板战斗 (combat.panel_start/panel_update/panel_end,
    ///   pip 格血条 + 伤害阈值掉格) 整套删除 — rt 实时战斗是唯一实现, 不再回退降级。
    /// </summary>
    internal sealed class ScriptCombatService
    {
        private readonly MelonLogger.Instance _logger;

        internal SceneService Scenes;                 // Plugin 接线
        internal ScriptGridService Grid;              // Plugin 接线 (箱槽/地面/箱窗)
        internal RtCombatService Rt;                  // Plugin 接线 (v1.29.0 实时战斗会话)

        internal ScriptCombatService(MelonLogger.Instance logger)
        {
            _logger = logger;
        }

        internal bool InScene => Scenes != null && Scenes.Active != null;

        // ==================== 武器链 (ResolveWeapon 移植; 箱槽经 ScriptGridService) ====================

        /// <summary>combat.weapon_info 后端 → dict {name, is_gun, unarmed, has_ammo, power, acc, reliability,
        /// mode, rate, pen, rng, rad (v1.34.0 六维全消费 §3.10: 枪 0/30/1, 近战·空手 0/1/1),
        /// stun (v1.36.0: w_stun 麻痹秒数, 钳 0-3, 缺省 0)}。
        /// 主武器槽已鉴定枪械 (appraised=1 + wtype=gun) → 近战槽 (wtype=melee) → 空手。</summary>
        internal Dictionary<string, object> WeaponInfoDict(string ammoId)
        {
            try
            {
                var main = Grid?.BoxSlot("s_main");
                if (main != null && IsAppraisedType(main, "gun"))
                {
                    float stab = DataFloat(main, "w_stab", 50f);
                    return new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["name"] = WeaponName(main, "枪械"),
                        ["is_gun"] = true,
                        ["unarmed"] = false,
                        ["has_ammo"] = FindAmmo(ammoId) != null,
                        ["power"] = (double)Math.Max(1f, DataFloat(main, "w_atk", 10f)),
                        ["acc"] = (long)Raid.RaidCore.Clamp((int)Math.Round(stab / 4f), 1, 25),
                        ["reliability"] = (long)Raid.RaidCore.Clamp((int)Math.Round(stab), 0, 100),
                        // v1.29.0: 实时战斗用 — NBT w_mode (0近战抡/1单发/2连发, 默认1)
                        // v1.35.1: RPM 键 w_rate → w_rpm (旧键与鉴定六维 rate 撞车被覆盖, 见第 76 条)
                        ["mode"] = (long)Raid.RaidCore.Clamp((int)DataFloat(main, "w_mode", 1f), 0, 2),
                        ["rate"] = (long)Raid.RaidCore.Clamp((int)DataFloat(main, "w_rpm", 300f), 1, 1200),
                        // v1.34.0: 六维全消费 (§3.10) — w_pen 破甲 0-100 / w_rng 距离带 (默认30) / w_rad 多目标 ≥1
                        ["pen"] = (long)Raid.RaidCore.Clamp((int)DataFloat(main, "w_pen", 0f), 0, 100),
                        ["rng"] = (long)Math.Max(0, (int)DataFloat(main, "w_rng", 30f)),
                        ["rad"] = (long)Raid.RaidCore.Clamp((int)DataFloat(main, "w_rad", 1f), 1, 8),
                        // v1.36.0: w_stun 麻痹秒数 (钳 0-3, 缺省 0; 命中后敌节奏器暂停+撤预警+打断冲锋)
                        ["stun"] = (double)Raid.RaidCore.StunSeconds(DataFloat(main, "w_stun", 0f)),
                    };
                }
                var melee = Grid?.BoxSlot("s_melee");
                if (melee != null && IsAppraisedType(melee, "melee"))
                {
                    return new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["name"] = WeaponName(melee, "近战武器"),
                        ["is_gun"] = false,
                        ["unarmed"] = false,
                        ["has_ammo"] = false,
                        ["power"] = (double)Math.Max(1f, DataFloat(melee, "w_atk", 8f)),
                        ["acc"] = 16L,          // 近战固定 64% 基础命中
                        ["reliability"] = 100L, // 近战不卡壳
                        ["mode"] = 0L,
                        ["rate"] = 0L,
                        // v1.34.0: 近战 rng 缺省 1 (近带特化, MeleeCanReach 强制近带)
                        ["pen"] = (long)Raid.RaidCore.Clamp((int)DataFloat(melee, "w_pen", 0f), 0, 100),
                        ["rng"] = (long)Math.Max(0, (int)DataFloat(melee, "w_rng", 1f)),
                        ["rad"] = (long)Raid.RaidCore.Clamp((int)DataFloat(melee, "w_rad", 1f), 1, 8),
                        ["stun"] = 0.0,
                    };
                }
            }
            catch (Exception e) { PsApi.Warn(_logger, "[combat] 武器读取异常, 按空手: " + e.Message); }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = "拳头",
                ["is_gun"] = false,
                ["unarmed"] = true,
                ["has_ammo"] = false,
                ["power"] = 0.0,
                ["acc"] = (long)Raid.RaidCore.UnarmedAcc,
                ["reliability"] = 100L,
                ["mode"] = 0L,
                ["rate"] = 0L,
                ["pen"] = 0L,
                ["rng"] = 1L,
                ["rad"] = 1L,
                ["stun"] = 0.0,
            };
        }

        // ==================== v1.29.0 实时战斗武器解析 (RtCombatService 用) ====================

        /// <summary>combat.rt_start 后端 (委托 RtCombatService; 未接线/不在场景 = false)。</summary>
        internal bool RtStart(Dictionary<string, object> cfg, PsCallable onEnd, Interpreter itp, int line)
        {
            if (Rt == null)
            {
                PsApi.Warn(_logger, "[combat] rt_start 实时战斗服务未接线, 已忽略");
                return false;
            }
            return Rt.RtStart(cfg, onEnd, itp, line);
        }

        /// <summary>实时战斗武器解析 (按偏好选枪械/近战, 逐级退化):
        /// 收武器状态 → 拳头; preferMelee 且有近战 → 近战; 有枪 → 有弹射击态 / 无弹枪托 /
        /// w_mode=0 枪当近战抡 (枪托基数); 有近战 → 近战; 都没有 → 拳头。
        /// ammoId null = 默认轻弹。</summary>
        internal RtWeapon ResolveRtWeapon(bool preferMelee, bool weaponDrawn, string ammoId)
        {
            try
            {
                var main = Grid?.BoxSlot("s_main");
                bool hasGun = main != null && IsAppraisedType(main, "gun");
                var meleeIt = Grid?.BoxSlot("s_melee");
                bool hasMelee = meleeIt != null && IsAppraisedType(meleeIt, "melee");
                if (weaponDrawn)
                {
                    if (preferMelee && hasMelee) return MeleeWeapon(meleeIt);
                    if (hasGun)
                    {
                        float atk = Math.Max(1f, DataFloat(main, "w_atk", 10f));
                        atk = Raid.RaidCore.DurabilityAtk(atk, DurRatio(main), IsBroken(main));   // v1.48.7: 破损 ×0.3 + 低耐久曲线 (枪抡/枪托同源)
                        int mode = Raid.RaidCore.Clamp((int)DataFloat(main, "w_mode", 1f), 0, 2);
                        int rate = Raid.RaidCore.Clamp((int)DataFloat(main, "w_rpm", 300f), 1, 1200);   // v1.35.1: w_rate→w_rpm (撞车修复)
                        int stab = Raid.RaidCore.Clamp((int)Math.Round(DataFloat(main, "w_stab", 50f)), 0, 100);
                        // v1.34.0: 六维全消费 (§3.10) — pen 破甲 / rng 距离带 / rad 多目标
                        int pen = Raid.RaidCore.Clamp((int)DataFloat(main, "w_pen", 0f), 0, 100);
                        int wrng = Math.Max(0, (int)DataFloat(main, "w_rng", 30f));
                        int rad = Raid.RaidCore.Clamp((int)DataFloat(main, "w_rad", 1f), 1, 8);
                        bool ammo = FindAmmo(ammoId) != null;
                        string wname = WeaponName(main, "枪械");
                        // v1.36.0: w_stun 麻痹秒数 (钳 0-3) — 仅枪械射击态透出 (枪托/抡/近战/空手恒 0)
                        float stun = Raid.RaidCore.StunSeconds(DataFloat(main, "w_stun", 0f));
                        if (mode == 0)
                            return new RtWeapon { Name = wname + " (抡)", IsGun = false, MeleeClass = true, Gunstock = true, Atk = Raid.RaidCore.GunstockAtk(atk), Stab = 100, Pen = pen, Rng = 1, Rad = rad, Wtype = "melee", Source = main };
                        if (!ammo)
                            return new RtWeapon { Name = wname + " (枪托)", IsGun = true, MeleeClass = true, Gunstock = true, Atk = Raid.RaidCore.GunstockAtk(atk), Stab = 100, Pen = pen, Rng = 1, Rad = rad, Wtype = "melee", Source = main };
                        return new RtWeapon { Name = wname, IsGun = true, MeleeClass = false, Atk = atk, Stab = stab, Mode = mode, Rate = rate, Pen = pen, Rng = wrng, Rad = rad, Stun = stun, Wtype = "gun", Source = main };
                    }
                    if (hasMelee) return MeleeWeapon(meleeIt);
                }
            }
            catch (Exception e) { PsApi.Warn(_logger, "[combat] rt 武器解析异常, 按空手: " + e.Message); }
            return new RtWeapon { Name = "拳头", Unarmed = true, MeleeClass = true, Stab = 100, Rng = 1, Wtype = "melee" };
        }

        private static RtWeapon MeleeWeapon(GameItem item)
            => new RtWeapon
            {
                Name = WeaponName(item, "近战武器"),
                MeleeClass = true,
                Atk = Raid.RaidCore.DurabilityAtk(Math.Max(1f, DataFloat(item, "w_atk", 8f)), DurRatio(item), IsBroken(item)),   // v1.48.7: 破损 ×0.3 + 低耐久曲线 (近战挥击同吃)
                Stab = 100,
                // v1.34.0: 近战 rng 缺省 1 (近带特化); pen/rad 走 NBT
                Pen = Raid.RaidCore.Clamp((int)DataFloat(item, "w_pen", 0f), 0, 100),
                Rng = Math.Max(0, (int)DataFloat(item, "w_rng", 1f)),
                Rad = Raid.RaidCore.Clamp((int)DataFloat(item, "w_rad", 1f), 1, 8),
                Wtype = "melee",
                Source = item,
            };

        /// <summary>v1.43.0: 破损判定 (ps_broken=1; 缺键/异常=false)。</summary>
        private static bool IsBroken(GameItem item)
        {
            try { return ItemsFacade.GetData(item, "ps_broken") is long l && l == 1; }
            catch { return false; }
        }

        /// <summary>v1.48.7: 耐久比例 ps_dur/ps_dur_max (无 ps_dur_max = 非耐久体系武器 → 1.0 不参与曲线;
        /// 缺 ps_dur 按满耐久; 异常 → 1.0 保底不影响战斗)。</summary>
        private static float DurRatio(GameItem item)
        {
            try
            {
                double durMax = DurNum(ItemsFacade.GetData(item, "ps_dur_max"), -1);
                if (durMax <= 0) return 1f;
                double dur = DurNum(ItemsFacade.GetData(item, "ps_dur"), durMax);
                if (dur <= 0) return 0f;
                return (float)(dur / durMax);
            }
            catch { return 1f; }
        }

        private static double DurNum(object v, double def) => v is long l ? l : v is double d ? d : def;

        private static bool IsAppraisedType(GameItem item, string wtype)
        {
            try
            {
                var ap = ItemsFacade.GetData(item, "appraised");
                if (!(ap is long l) || l != 1) return false;
                return string.Equals(ItemsFacade.GetData(item, "wtype") as string, wtype, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static string WeaponName(GameItem item, string fallback)
        {
            try { if (!string.IsNullOrEmpty(item.name)) return item.name; } catch { }
            return fallback;
        }

        /// <summary>NBT 数字读取 (GetData 整数回 long/小数回 double); 缺键/异常 = def。</summary>
        private static float DataFloat(GameItem item, string key, float def)
        {
            try
            {
                var v = ItemsFacade.GetData(item, key);
                if (v is long l) return l;
                if (v is double d) return (float)d;
            }
            catch { }
            return def;
        }

        /// <summary>投掷槽 (s_throw1-3) 第一个有物品的槽。</summary>
        internal GameItem FindThrowItem()
        {
            if (Grid == null) return null;
            try
            {
                foreach (var sid in new[] { "s_throw1", "s_throw2", "s_throw3" })
                {
                    var it = Grid.BoxSlot(sid);
                    if (it != null) return it;
                }
            }
            catch { }
            return null;
        }

        // ---- 弹药 (双源: 物品箱树递归 + 外出栏嵌套; UseCountHelper 次数物品) ----

        internal const string DefaultAmmoId = "gunworks:ammo_light";

        /// <summary>双源找弹药: ① 物品箱树 (机器槽/胸式槽背包递归) ② 外出栏 (含嵌套)。</summary>
        internal GameItem FindAmmo(string ammoId)
        {
            string id = string.IsNullOrWhiteSpace(ammoId) ? DefaultAmmoId : ammoId;
            var box = Grid?.BoxItem;
            if (box != null)
            {
                var hit = FindUsableInTree(box, id);
                if (hit != null) return hit;
            }
            try
            {
                var ah = EmporiumEntry.Instance?.afterhourInventory;
                Il2CppSystem.Collections.Generic.List<GameItem> children = null;
                try { children = ah == null ? null : ah.childItems; } catch { }
                if (children != null)
                    foreach (var c in children)
                    {
                        var hit = FindUsableInTree(c, id);
                        if (hit != null) return hit;
                    }
            }
            catch { }
            return null;
        }

        private static GameItem FindUsableInTree(GameItem root, string ammoId)
        {
            if (root == null) return null;
            try
            {
                Il2CppSystem.Collections.Generic.List<GameItem> all = null;
                try { all = root.FindAllChildItems(); } catch { }
                if (all != null)
                    foreach (var it in all)
                    {
                        if (it == null) continue;
                        try
                        {
                            if (string.Equals(it.identifier, ammoId, StringComparison.OrdinalIgnoreCase)
                                && ItemsFacade.UseCountCanUse(it, 1)) return it;
                        }
                        catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>combat.consume_ammo 后端: 消耗 1 发 (重查 — 上次实例可能已归零销毁)。</summary>
        internal bool ConsumeAmmo(string ammoId)
        {
            var ammo = FindAmmo(ammoId);
            if (ammo == null) return false;
            try { return ItemsFacade.UseCountUse(ammo, 1); }
            catch (Exception e) { PsApi.Warn(_logger, "[combat] 弹药消耗失败: " + e.Message); return false; }
        }

        /// <summary>combat.major_wound 后端: 原生重伤 (HealthData.ReceiveMajorWound; 晕倒半拉子链同款)。</summary>
        internal bool MajorWound()
        {
            try { HealthData.ReceiveMajorWound(); return true; }
            catch (Exception e) { PsApi.Warn(_logger, "[combat] ReceiveMajorWound 失败: " + e.Message); return false; }
        }
    }
}
