using System;
using System.Collections.Generic;
using PSApi.Events.Scenes;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// P3 (v1.28.0) script 驱动场景能力命名空间: scene.log + grid.* + combat.*。
    /// scene.log = 场景信息栏 (SceneLogService); grid.* = 物品网格能力 (ScriptGridService);
    /// combat.* = 战斗公式 (RaidCore 纯函数薄封装, 无条件注册可无头测) + 武器/弹药/实时战斗会话
    /// (ScriptCombatService, 游戏进程内才可用)。
    /// 公式 builtin 是 RaidCore 同一套纯函数的直接包装 — 单一事实源, pss 不得另写数值。
    /// v1.35.0 (2026-09-30 拍板): 经典按钮面板 builtin (panel_start/panel_update/panel_end/panel_open)
    /// 与敌 HP 掉格公式 builtin (enemy_hp_loss) 随经典回退战斗系统整套删除 — rt 实时战斗是唯一实现。
    /// </summary>
    internal static class PsBuiltinsSceneApi
    {
        private static readonly System.Random _rng = new System.Random();

        internal static void Register(PsEnv env, SceneLogService logSvc, ScriptGridService grid, ScriptCombatService combat,
            Func<int> dangerProvider = null)
        {
            if (logSvc != null) RegisterScene(env, logSvc);
            if (grid != null) RegisterGrid(env, grid);
            RegisterCombat(env, combat, dangerProvider);   // 公式部分无条件注册 (combat=null 时游戏侧成员省略)
        }

        // ==================== scene: 场景通用 ====================

        private static void RegisterScene(PsEnv env, SceneLogService logSvc)
        {
            var ns = new PsNamespace("scene");
            ns.Members["log"] = PsBuiltins.BF("scene.log", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "scene.log(text) — 写场景信息栏 (左下滚动日志, 与 raid 信息栏同视觉; 不在场景 = no-op)", line);
                string sceneId = null;
                try { sceneId = logSvc.Scenes?.Active?.FullId; } catch { }
                if (sceneId != null) logSvc.Log(sceneId, PsValues.Fmt(a[0]));
                return null;
            });
            env.Define("scene", ns, true, 0);
        }

        // ==================== grid: 物品网格能力 ====================

        private static void RegisterGrid(PsEnv env, ScriptGridService grid)
        {
            var ns = new PsNamespace("grid");
            ns.Members["open_container"] = PsBuiltins.BF("grid.open_container", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 3, "grid.open_container(title, [{id, count}...] | [id...], [on_close]) → bool — 开真网格容器 (动态行数/逐件 stack=1/自动寻位不重叠/瀑布兜底; 关窗剩余销毁, on_close 收 {taken, left, reason})", line);
                string title = PsValues.Fmt(a[0]);
                var items = ParseLootItems(a[1], "grid.open_container", line);
                PsCallable onClose = null;
                if (a.Count == 3 && a[2] != null)
                {
                    if (a[2] is not PsCallable c)
                        throw new PsRuntimeError($"grid.open_container 的 on_close 须为 pss 函数, 实为 {PsValues.TypeName(a[2])}", line);
                    onClose = c;
                }
                try { return grid.OpenContainer(title, items, onClose, itp, line); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("grid.open_container 失败: " + e.Message, line); }
            });
            ns.Members["close_container"] = PsBuiltins.BF("grid.close_container", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 1, "grid.close_container([reason=\"script\"]) → {taken, left} | null — 程序化关容器 (剩余销毁, 与 X 关窗同路径)", line);
                try { return grid.CloseContainer(a.Count == 1 ? PsValues.Fmt(a[0]) : "script"); }
                catch (Exception e) { throw new PsRuntimeError("grid.close_container 失败: " + e.Message, line); }
            });
            ns.Members["container_open"] = PsBuiltins.BF("grid.container_open", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "grid.container_open() → bool", line);
                return grid.ContainerOpen;
            });
            ns.Members["ground_show"] = PsBuiltins.BF("grid.ground_show", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 1, "grid.ground_show([title=\"地面\"]) → bool — 地面网格窗幂等确保 (持久; 左缘贴物品箱右缘)", line);
                try { return grid.GroundShow(a.Count == 1 ? PsValues.Fmt(a[0]) : null); }
                catch (Exception e) { throw new PsRuntimeError("grid.ground_show 失败: " + e.Message, line); }
            });
            ns.Members["ground_count"] = PsBuiltins.BF("grid.ground_count", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "grid.ground_count() → 地面物品件数", line);
                try { return (long)grid.GroundCount(); }
                catch (Exception e) { throw new PsRuntimeError("grid.ground_count 失败: " + e.Message, line); }
            });
            ns.Members["ground_clear"] = PsBuiltins.BF("grid.ground_clear", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "grid.ground_clear() → 销毁件数 — 地面物品全部销毁 (前进/到底清空同款)", line);
                try { return (long)grid.GroundClear(); }
                catch (Exception e) { throw new PsRuntimeError("grid.ground_clear 失败: " + e.Message, line); }
            });
            ns.Members["ground_place"] = PsBuiltins.BF("grid.ground_place", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "grid.ground_place(id[, count=1]) → {ground, chest, lost} — 放物品到地面 (逐件 stack=1; 满→物品箱背包→销毁)", line);
                string id = AsId(a[0], "grid.ground_place", line);
                int count = a.Count == 2 ? AsCount(a[1], "grid.ground_place", line) : 1;
                try { return grid.GroundPlace(id, count); }
                catch (Exception e) { throw new PsRuntimeError("grid.ground_place 失败: " + e.Message, line); }
            });
            ns.Members["ground_steal"] = PsBuiltins.BF("grid.ground_steal", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "grid.ground_steal() → {id, count} | null — 随机拿走地面 1 件物品 (v1.45.0 掠窃者机制)", line);
                try { return grid.GroundSteal(); }
                catch (Exception e) { throw new PsRuntimeError("grid.ground_steal 失败: " + e.Message, line); }
            });
            ns.Members["box_follow"] = PsBuiltins.BF("grid.box_follow", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 1, "grid.box_follow([machine_id=\"gunworks:travel_box\"]) → bool — 物品箱面板跟随 (Show+底边钉位; 结构恒定只动显隐/位置)", line);
                string id = a.Count == 1 ? AsId(a[0], "grid.box_follow", line) : "gunworks:travel_box";
                try { return grid.BoxFollow(id); }
                catch (Exception e) { throw new PsRuntimeError("grid.box_follow 失败: " + e.Message, line); }
            });
            ns.Members["box_show"] = PsBuiltins.BF("grid.box_show", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "grid.box_show(visible) → bool — 物品箱窗显隐 (战斗锁用; 未 box_follow = false)", line);
                try { return grid.BoxShow(PsValues.Truthy(a[0])); }
                catch (Exception e) { throw new PsRuntimeError("grid.box_show 失败: " + e.Message, line); }
            });
            ns.Members["box_slot"] = PsBuiltins.BF("grid.box_slot", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "grid.box_slot(slot_id) → 物品句柄 | null — 读物品箱机器槽 (s_main/s_melee/s_throw1-3/s_chest)", line);
                string sid = AsId(a[0], "grid.box_slot", line);
                try
                {
                    var item = grid.BoxSlot(sid);
                    return item == null ? null : (object)new PsItemHandle(item);
                }
                catch (Exception e) { throw new PsRuntimeError("grid.box_slot 失败: " + e.Message, line); }
            });
            ns.Members["box_has"] = PsBuiltins.BF("grid.box_has", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "grid.box_has(id) → bool — 物品箱树递归检定 (含槽内/胸式背包嵌套)", line);
                string id = AsId(a[0], "grid.box_has", line);
                try { return grid.BoxHas(id); }
                catch (Exception e) { throw new PsRuntimeError("grid.box_has 失败: " + e.Message, line); }
            });
            ns.Members["afterhour_has"] = PsBuiltins.BF("grid.afterhour_has", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "grid.afterhour_has(id) → bool — 外出栏原版门控检定", line);
                string id = AsId(a[0], "grid.afterhour_has", line);
                try { return grid.AfterhourHas(id); }
                catch (Exception e) { throw new PsRuntimeError("grid.afterhour_has 失败: " + e.Message, line); }
            });
            ns.Members["has_tool"] = PsBuiltins.BF("grid.has_tool", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "grid.has_tool(id) → bool — 工具门控双源 (外出栏 + 物品箱树)", line);
                string id = AsId(a[0], "grid.has_tool", line);
                try { return grid.HasTool(id); }
                catch (Exception e) { throw new PsRuntimeError("grid.has_tool 失败: " + e.Message, line); }
            });
            ns.Members["hide_afterhour"] = PsBuiltins.BF("grid.hide_afterhour", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "grid.hide_afterhour() → bool — 场景内隐藏原版外出物品栏 (藏/还配对, 撤离自动恢复; 隐藏期间 ctrl+左键快捷转移自动重定向地面窗)", line);
                try { return grid.HideAfterhour(); }
                catch (Exception e) { throw new PsRuntimeError("grid.hide_afterhour 失败: " + e.Message, line); }
            });
            ns.Members["afterhour_place"] = PsBuiltins.BF("grid.afterhour_place", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "grid.afterhour_place(id[, count=1]) → bool — 放物品进外出背包", line);
                string id = AsId(a[0], "grid.afterhour_place", line);
                int count = a.Count == 2 ? AsCount(a[1], "grid.afterhour_place", line) : 1;
                try { return grid.AfterhourPlace(id, count); }
                catch (Exception e) { throw new PsRuntimeError("grid.afterhour_place 失败: " + e.Message, line); }
            });
            env.Define("grid", ns, true, 0);
        }

        /// <summary>open_container 的 items 参数: [{id, count}...] 或 [id...] (count 缺省 1, 钳 1-12)。</summary>
        private static List<KeyValuePair<string, int>> ParseLootItems(object v, string fn, int line)
        {
            if (v is not List<object> l || l.Count == 0)
                throw new PsRuntimeError($"{fn} 的 items 须为非空数组 ({{id, count}} 条目或 id 字符串), 实为 {PsValues.TypeName(v)}", line);
            var result = new List<KeyValuePair<string, int>>();
            for (int i = 0; i < l.Count; i++)
            {
                var e = l[i];
                if (e is string s)
                {
                    if (string.IsNullOrWhiteSpace(s))
                        throw new PsRuntimeError($"{fn} 的 items[{i}] 是空字符串", line);
                    result.Add(new KeyValuePair<string, int>(s, 1));
                    continue;
                }
                if (e is Dictionary<string, object> d)
                {
                    object idv;
                    if (!d.TryGetValue("id", out idv) || idv is not string id || string.IsNullOrWhiteSpace(id))
                        throw new PsRuntimeError($"{fn} 的 items[{i}] 缺 id (非空字符串)", line);
                    int count = 1;
                    object cv;
                    if (d.TryGetValue("count", out cv) && cv != null) count = AsCount(cv, fn, line);
                    result.Add(new KeyValuePair<string, int>(id, count));
                    continue;
                }
                throw new PsRuntimeError($"{fn} 的 items[{i}] 须为 id 字符串或 {{id, count}} dict, 实为 {PsValues.TypeName(e)}", line);
            }
            return result;
        }

        // ==================== combat: 公式 (RaidCore 薄封装) + 面板/武器/弹药 ====================

        private static void RegisterCombat(PsEnv env, ScriptCombatService combat, Func<int> dangerProvider = null)
        {
            var ns = new PsNamespace("combat");

            // ---- 常量表 ----
            ns.Members["consts"] = PsBuiltins.BF("combat.consts", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "combat.consts() → {max_stat, max_noise, rest_stamina, combat_stamina_cost, unarmed_acc, unarmed_atk_lo, unarmed_atk_hi, melee_acc, melee_reliability, loot_count_max, log_cap}", line);
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["max_stat"] = (long)Raid.RaidCore.MaxStat,
                    ["max_noise"] = (long)Raid.RaidCore.MaxNoise,
                    ["rest_stamina"] = (long)Raid.RaidCore.RestStamina,
                    ["combat_stamina_cost"] = (long)Raid.RaidCore.CombatStaminaCost,
                    ["unarmed_acc"] = (long)Raid.RaidCore.UnarmedAcc,
                    ["unarmed_atk_lo"] = (long)Raid.RaidCore.UnarmedAtkLo,
                    ["unarmed_atk_hi"] = (long)Raid.RaidCore.UnarmedAtkHi,
                    ["melee_acc"] = 16L,
                    ["melee_reliability"] = 100L,
                    ["loot_count_max"] = 12L,
                    ["log_cap"] = (long)Raid.RaidCore.LogCap,
                    // v1.29.0 实时动作战斗常量
                    ["dodge_perfect"] = (double)Raid.RaidCore.DodgePerfect,
                    ["dodge_graze"] = (double)Raid.RaidCore.DodgeGraze,
                    ["jam_lock"] = (double)Raid.RaidCore.JamLock,
                    ["melee_stun"] = (double)Raid.RaidCore.MeleeStun,
                    ["melee_cd"] = (double)Raid.RaidCore.MeleeCd,
                    ["sneak_mul"] = (double)Raid.RaidCore.SneakMul,
                    ["warn_duration"] = 1.2,
                    ["warn_melee_duration"] = (double)Raid.RaidCore.WarnDuration(true),
                    ["enemy_interval_lo"] = 2.2,
                    ["enemy_interval_hi"] = 3.5,
                    ["rt_hp_per_pip"] = (long)Raid.RaidCore.RtHpPerPip,
                    // v1.34.0 战斗升级 M3.6 (§3.10) 常量
                    ["weaken_mul"] = (double)Raid.RaidCore.WeakenMul(),
                    ["expose_mul"] = (double)Raid.RaidCore.ExposeMul(),
                    ["expose_hitbox_scale"] = (double)Raid.RaidCore.ExposeHitboxScale(),
                    ["shrink_scale"] = (double)Raid.RaidCore.ShrinkScale(),
                    ["weaken_duration"] = (double)Raid.RaidCore.WeakenDuration,
                    ["expose_duration"] = (double)Raid.RaidCore.ExposeDuration,
                    ["stagger_duration"] = (double)Raid.RaidCore.StaggerDuration,
                    ["charge_hitbox_scale"] = (double)Raid.RaidCore.ChargeHitboxScale,
                    ["rad_multi_radius"] = (double)Raid.RaidCore.RadMultiRadius,
                    ["rad_pad_px"] = (double)Raid.RaidCore.RadPadPx,
                    ["band_rng_near"] = (long)Raid.RaidCore.BandRngReqTable[0],
                    ["band_rng_mid"] = (long)Raid.RaidCore.BandRngReqTable[1],
                    ["band_rng_far"] = (long)Raid.RaidCore.BandRngReqTable[2],
                };
            });

            // ---- 状态/消耗公式 ----
            ns.Members["hp_tier"] = PsBuiltins.BF("combat.hp_tier", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.hp_tier(hp) → 0健康/1轻伤/2受伤/3重伤/4晕倒", line);
                return (long)Raid.RaidCore.HpTier(AsInt(a[0], "combat.hp_tier", line));
            });
            ns.Members["tier_name"] = PsBuiltins.BF("combat.tier_name", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.tier_name(hp) → 档位中文名", line);
                return Raid.RaidCore.TierNames[Raid.RaidCore.HpTier(AsInt(a[0], "combat.tier_name", line))];
            });
            ns.Members["posture_index"] = PsBuiltins.BF("combat.posture_index", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.posture_index(\"run|walk|crouch|prone\") → 0-3 (非法 = -1)", line);
                return (long)Raid.RaidCore.PostureIndex(PsValues.Fmt(a[0]));
            });
            ns.Members["advance_cost"] = PsBuiltins.BF("combat.advance_cost", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 3, "combat.advance_cost(posture 0-3, hp, hunger) → {stamina, hunger, noise, hp} (前进消耗结算)", line);
                Raid.RaidCore.AdvanceCost(AsInt(a[0], "combat.advance_cost", line), AsInt(a[1], "combat.advance_cost", line), AsInt(a[2], "combat.advance_cost", line),
                    out int st, out int hu, out int no, out int hp);
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["stamina"] = (long)st, ["hunger"] = (long)hu, ["noise"] = (long)no, ["hp"] = (long)hp,
                };
            });
            ns.Members["rest_hunger_cost"] = PsBuiltins.BF("combat.rest_hunger_cost", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.rest_hunger_cost(posture) → 休息饥饿消耗", line);
                return (long)Raid.RaidCore.RestHungerCost(AsInt(a[0], "combat.rest_hunger_cost", line));
            });
            ns.Members["search_value_mul"] = PsBuiltins.BF("combat.search_value_mul", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 3, "combat.search_value_mul(hp, hunger, posture) → 搜索价值倍率", line);
                return (double)Raid.RaidCore.SearchValueMul(AsInt(a[0], "combat.search_value_mul", line), AsInt(a[1], "combat.search_value_mul", line), AsInt(a[2], "combat.search_value_mul", line));
            });

            // ---- 战斗公式 ----
            ns.Members["hit_chance"] = PsBuiltins.BF("combat.hit_chance", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 4, 4, "combat.hit_chance(acc, posture, buff_mod, enemy_dodge) → 命中% (钳 5-95)", line);
                return (long)Raid.RaidCore.HitChance(AsInt(a[0], "combat.hit_chance", line), AsInt(a[1], "combat.hit_chance", line), AsInt(a[2], "combat.hit_chance", line), AsInt(a[3], "combat.hit_chance", line));
            });
            ns.Members["roll_damage"] = PsBuiltins.BF("combat.roll_damage", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 3, "combat.roll_damage(power, posture, hp) → 伤害 (×姿态×档位×0.85-1.15 随机, ≥1)", line);
                return (long)Raid.RaidCore.RollDamage(AsFloat(a[0], "combat.roll_damage", line), AsInt(a[1], "combat.roll_damage", line), AsInt(a[2], "combat.roll_damage", line), _rng);
            });
            ns.Members["jam_chance"] = PsBuiltins.BF("combat.jam_chance", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.jam_chance(reliability 0-100) → 卡壳率%", line);
                return (double)Raid.RaidCore.JamChance(AsInt(a[0], "combat.jam_chance", line));
            });
            ns.Members["flee_chance"] = PsBuiltins.BF("combat.flee_chance", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "combat.flee_chance(posture, stamina) → 逃跑成功率% (钳 0-95)", line);
                return (long)Raid.RaidCore.FleeChance(AsInt(a[0], "combat.flee_chance", line), AsInt(a[1], "combat.flee_chance", line));
            });
            ns.Members["unarmed_power"] = PsBuiltins.BF("combat.unarmed_power", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "combat.unarmed_power() → 空手 atk 随机 (2-5)", line);
                return (long)_rng.Next(Raid.RaidCore.UnarmedAtkLo, Raid.RaidCore.UnarmedAtkHi + 1);
            });

            // ---- v1.29.0 实时动作战斗公式 (RaidCore 薄封装, rt_start 会话同规) ----
            ns.Members["dodge_tier"] = PsBuiltins.BF("combat.dodge_tier", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.dodge_tier(|dt| 秒) → 0完美/1擦伤/2全伤 (边界 0.15/0.4)", line);
                return (long)Raid.RaidCore.DodgeTier(AsFloat(a[0], "combat.dodge_tier", line));
            });
            ns.Members["dodge_damage_mul"] = PsBuiltins.BF("combat.dodge_damage_mul", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.dodge_damage_mul(tier 0-2) → 伤害倍率 (0/0.4/1)", line);
                return (double)Raid.RaidCore.DodgeDamageMul(AsInt(a[0], "combat.dodge_damage_mul", line));
            });
            ns.Members["rt_damage"] = PsBuiltins.BF("combat.rt_damage", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.rt_damage(atk) → 实时命中伤害 (×0.9-1.1 随机, ≥1)", line);
                return (long)Raid.RaidCore.RollRtDamage(AsFloat(a[0], "combat.rt_damage", line), _rng);
            });
            ns.Members["gunstock_atk"] = PsBuiltins.BF("combat.gunstock_atk", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.gunstock_atk(w_atk) → 枪托伤害基数 (×0.4, ≥1)", line);
                return (double)Raid.RaidCore.GunstockAtk(AsFloat(a[0], "combat.gunstock_atk", line));
            });
            ns.Members["fire_cd"] = PsBuiltins.BF("combat.fire_cd", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.fire_cd(rpm) → 射击 CD 秒 (60/rpm)", line);
                return (double)Raid.RaidCore.FireCd(AsInt(a[0], "combat.fire_cd", line));
            });
            ns.Members["jitter_radius"] = PsBuiltins.BF("combat.jitter_radius", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.jitter_radius(w_stab 0-100) → 准星抖动半径 px ((100-stab)/10)", line);
                return (double)Raid.RaidCore.JitterRadius(AsInt(a[0], "combat.jitter_radius", line));
            });
            ns.Members["warn_duration"] = PsBuiltins.BF("combat.warn_duration", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.warn_duration(melee_class bool) → 敌预警秒 (1.2 / 近战类 0.96)", line);
                return (double)Raid.RaidCore.WarnDuration(PsValues.Truthy(a[0]));
            });
            ns.Members["enemy_interval"] = PsBuiltins.BF("combat.enemy_interval", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "combat.enemy_interval() → 敌攻击节奏秒 (2.2-3.5 随机)", line);
                return (double)Raid.RaidCore.EnemyInterval(_rng);
            });
            ns.Members["rt_enemy_hp"] = PsBuiltins.BF("combat.rt_enemy_hp", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.rt_enemy_hp(pip 格 1-3) → 实时战斗敌 HP (×15)", line);
                return (long)Raid.RaidCore.RtEnemyHp(AsInt(a[0], "combat.rt_enemy_hp", line));
            });

            // ---- v1.34.0 战斗升级 M3.6 (§3.10: 破甲/距离带/行为参数 — RaidCore 薄封装, 单一事实源) ----
            ns.Members["rt_enemy_hp_value"] = PsBuiltins.BF("combat.rt_enemy_hp_value", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.rt_enemy_hp_value(hp) → 敌 HP (直接值; ≤10 回退旧 pip×15 兼容)", line);
                return (long)Raid.RaidCore.RtEnemyHpValue(AsInt(a[0], "combat.rt_enemy_hp_value", line));
            });
            ns.Members["armor_eff"] = PsBuiltins.BF("combat.armor_eff", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "combat.armor_eff(armor% 0-100, pen 0-100) → 实际减伤率 (armor×(1−pen/100), 钳 0-0.95)", line);
                return (double)Raid.RaidCore.ArmorEff(AsFloat(a[0], "combat.armor_eff", line), AsFloat(a[1], "combat.armor_eff", line));
            });
            ns.Members["band_rng_req"] = PsBuiltins.BF("combat.band_rng_req", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.band_rng_req(band 0近1中2远) → rng 需求 (近0/中25/远55)", line);
                return (long)Raid.RaidCore.BandRngReq(AsInt(a[0], "combat.band_rng_req", line));
            });
            ns.Members["band_name"] = PsBuiltins.BF("combat.band_name", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.band_name(band 0近1中2远) → 「近|中|远」", line);
                return Raid.RaidCore.BandNames[Raid.RaidCore.ClampBand(AsInt(a[0], "combat.band_name", line))];
            });
            ns.Members["band_shortfall"] = PsBuiltins.BF("combat.band_shortfall", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "combat.band_shortfall(weapon_rng, band) → 射程短缺档数 0-2", line);
                return (long)Raid.RaidCore.BandShortfall(AsInt(a[0], "combat.band_shortfall", line), AsInt(a[1], "combat.band_shortfall", line));
            });
            ns.Members["band_damage_mul"] = PsBuiltins.BF("combat.band_damage_mul", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.band_damage_mul(shortfall 0-2) → 伤害倍率 (0.7^n)", line);
                return (double)Raid.RaidCore.BandDamageMul(AsInt(a[0], "combat.band_damage_mul", line));
            });
            ns.Members["band_scale_mul"] = PsBuiltins.BF("combat.band_scale_mul", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.band_scale_mul(shortfall 0-2) → 受击箱视觉倍率 (0.85^n)", line);
                return (double)Raid.RaidCore.BandScaleMul(AsInt(a[0], "combat.band_scale_mul", line));
            });
            ns.Members["melee_can_reach"] = PsBuiltins.BF("combat.melee_can_reach", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 3, "combat.melee_can_reach(wtype, rng, band) → bool — melee/rng≤2 只打近带; throw 只打近/中带", line);
                return Raid.RaidCore.MeleeCanReach(PsValues.Fmt(a[0]), AsInt(a[1], "combat.melee_can_reach", line), AsInt(a[2], "combat.melee_can_reach", line));
            });
            ns.Members["melee_effective_band"] = PsBuiltins.BF("combat.melee_effective_band", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "combat.melee_effective_band(in_enemy_attack_window bool, band) → 近战有效距离带 (敌 WARNING/RESOLVE 攻击窗口内=0近, 窗口外原带)", line);
                return (long)Raid.RaidCore.MeleeEffectiveBand(PsValues.Truthy(a[0]), AsInt(a[1], "combat.melee_effective_band", line));
            });
            ns.Members["charge_stagger_threshold"] = PsBuiltins.BF("combat.charge_stagger_threshold", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.charge_stagger_threshold(enemy_hp) → 冲锋击退累计伤害阈值 (max(10, hp×0.25))", line);
                return (long)Raid.RaidCore.ChargeStaggerThreshold(AsInt(a[0], "combat.charge_stagger_threshold", line));
            });
            ns.Members["shield_dot_count"] = PsBuiltins.BF("combat.shield_dot_count", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.shield_dot_count(dots) → 绿点数 (钳 1-6)", line);
                return (long)Raid.RaidCore.ShieldDotCount(AsInt(a[0], "combat.shield_dot_count", line));
            });
            ns.Members["shield_dot_size"] = PsBuiltins.BF("combat.shield_dot_size", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.shield_dot_size(px) → 绿点尺寸 (钳 14-40)", line);
                return (double)Raid.RaidCore.ShieldDotSize(AsFloat(a[0], "combat.shield_dot_size", line));
            });
            ns.Members["dot_hits_for_size"] = PsBuiltins.BF("combat.dot_hits_for_size", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.dot_hits_for_size(size_px) → 绿点击碎次数 (14-22px→1 / 23-31px→2 / 32-40px→3, 钳 1-3)", line);
                return (long)Raid.RaidCore.DotHitsForSize(AsFloat(a[0], "combat.dot_hits_for_size", line));
            });
            ns.Members["shield_reduction"] = PsBuiltins.BF("combat.shield_reduction", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.shield_reduction(r) → 盾减伤率 (钳 0.5-0.9)", line);
                return (double)Raid.RaidCore.ShieldReduction(AsFloat(a[0], "combat.shield_reduction", line));
            });
            ns.Members["shield_red_window"] = PsBuiltins.BF("combat.shield_red_window", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.shield_red_window(s) → 红点窗口秒 (钳 1.5-5)", line);
                return (double)Raid.RaidCore.ShieldRedWindow(AsFloat(a[0], "combat.shield_red_window", line));
            });
            ns.Members["weaken_mul"] = PsBuiltins.BF("combat.weaken_mul", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "combat.weaken_mul() → 虚弱承伤倍率 (1.5)", line);
                return (double)Raid.RaidCore.WeakenMul();
            });
            ns.Members["expose_mul"] = PsBuiltins.BF("combat.expose_mul", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "combat.expose_mul() → 破绽承伤倍率 (1.5)", line);
                return (double)Raid.RaidCore.ExposeMul();
            });
            ns.Members["expose_hitbox_scale"] = PsBuiltins.BF("combat.expose_hitbox_scale", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "combat.expose_hitbox_scale() → 破绽受击箱放大 (1.3)", line);
                return (double)Raid.RaidCore.ExposeHitboxScale();
            });
            ns.Members["shrink_scale"] = PsBuiltins.BF("combat.shrink_scale", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "combat.shrink_scale() → 伏身受击箱缩小 (0.5)", line);
                return (double)Raid.RaidCore.ShrinkScale();
            });

            // ---- 掷签/容器公式 ----
            ns.Members["container_rows"] = PsBuiltins.BF("combat.container_rows", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "combat.container_rows(total_cells, cols) → 容器动态行数", line);
                return (long)Raid.RaidCore.ContainerRows(AsInt(a[0], "combat.container_rows", line), AsInt(a[1], "combat.container_rows", line));
            });
            ns.Members["combat_rate"] = PsBuiltins.BF("combat.combat_rate", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.combat_rate({poi=0.3, combat=0.25, ...}) → combat 键归一化概率 (归途遇袭基数)", line);
                return (double)Raid.RaidCore.CombatRate(AsTable(a[0], "combat.combat_rate", line));
            });
            ns.Members["roll_encounter"] = PsBuiltins.BF("combat.roll_encounter", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.roll_encounter({poi=0.3, combat=0.25, event=0.15, empty=0.3}) → 抽中的键", line);
                return Raid.RaidCore.RollEncounter(AsTable(a[0], "combat.roll_encounter", line), _rng);
            });
            ns.Members["parse_range"] = PsBuiltins.BF("combat.parse_range", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.parse_range(\"3-4\" | \"4\") → {lo, hi}", line);
                try
                {
                    var r = Raid.RaidCore.ParseChain(PsValues.Fmt(a[0]));
                    return new Dictionary<string, object>(StringComparer.Ordinal) { ["lo"] = (long)r.Lo, ["hi"] = (long)r.Hi };
                }
                catch (FormatException e) { throw new PsRuntimeError("combat.parse_range: " + e.Message, line); }
            });
            ns.Members["roll_chain"] = PsBuiltins.BF("combat.roll_chain", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "combat.roll_chain(\"3-4\" | \"4\") → 链长掷签", line);
                try { return (long)Raid.RaidCore.RollChain(PsValues.Fmt(a[0]), _rng); }
                catch (FormatException e) { throw new PsRuntimeError("combat.roll_chain: " + e.Message, line); }
            });
            ns.Members["roll_loot"] = PsBuiltins.BF("combat.roll_loot", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 7, "combat.roll_loot(pool, depth[, ignore_min_depth=false[, value_mul=1[, min_items=3[, max_items=6[, rarity=省略]]]]]) → [{id, count}...] — pool = [{id, weight[, count=\"1-3\"][, min_depth=0][, rarity=\"common|fine|rare|epic|legend\"(或 0-4)]}...]; v1.37.0 稀有度两阶段掷签: rarity 省略=按当前场景 danger 默认分布两阶段, false=强制单层旧语义, {common=‰,...}=分布覆写", line);
                var pool = ParseLootPool(a[0], "combat.roll_loot", line);
                int depth = AsInt(a[1], "combat.roll_loot", line);
                bool ignoreMin = a.Count >= 3 && PsValues.Truthy(a[2]);
                float valueMul = a.Count >= 4 ? AsFloat(a[3], "combat.roll_loot", line) : 1f;
                int minItems = a.Count >= 5 ? AsInt(a[4], "combat.roll_loot", line) : 3;
                int maxItems = a.Count >= 6 ? AsInt(a[5], "combat.roll_loot", line) : 6;
                if (minItems < 1 || maxItems < minItems || maxItems > 50)
                    throw new PsRuntimeError($"combat.roll_loot 的件数区间须 1..50 且 min<=max, 实为 {minItems}-{maxItems}", line);
                // v1.37.0: 稀有度末参 — 省略=当前场景 danger 默认分布两阶段 (无场景/无头回退 1★);
                // false=强制单层旧语义; dict {common=70, fine=30, ...} (千分比) = 分布覆写
                int[] rarityTable = null;
                bool twoStage = true;
                if (a.Count >= 7 && a[6] != null)
                {
                    if (a[6] is bool rb)
                    {
                        if (rb) throw new PsRuntimeError("combat.roll_loot 的 rarity 参数: true 无意义 (省略即默认两阶段), false=强制单层, 或传分布 dict", line);
                        twoStage = false;
                    }
                    else rarityTable = ParseRarityOverride(a[6], line);
                }
                if (twoStage && rarityTable == null)
                {
                    int danger = 1;
                    try { if (dangerProvider != null) danger = dangerProvider(); } catch { danger = 1; }
                    rarityTable = Raid.RaidCore.RarityTableFor(danger);
                }
                var rolled = Raid.RaidCore.RollLoot(pool, depth, ignoreMin, valueMul, minItems, maxItems, rarityTable, _rng);
                var result = new List<object>();
                foreach (var kv in rolled)
                    result.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["id"] = kv.Key, ["count"] = (long)kv.Value,
                    });
                return result;
            });

            // ---- 游戏侧: 武器/弹药/实时战斗会话 (ScriptCombatService 未接线时省略 — 无头测试台只测公式) ----
            // v1.35.0: 经典按钮面板 builtin (panel_start/panel_update/panel_end/panel_open) 已删
            if (combat != null)
            {
                ns.Members["weapon_info"] = PsBuiltins.BF("combat.weapon_info", (itp, a, line) =>
                {
                    PsBuiltins.Need(a, 0, 1, "combat.weapon_info([ammo_id=\"gunworks:ammo_light\"]) → {name, is_gun, unarmed, has_ammo, power, acc, reliability} — 武器链 (箱槽主手已鉴定枪→近战→空手)", line);
                    string ammo = a.Count == 1 ? AsId(a[0], "combat.weapon_info", line) : null;
                    try { return combat.WeaponInfoDict(ammo); }
                    catch (Exception e) { throw new PsRuntimeError("combat.weapon_info 失败: " + e.Message, line); }
                });
                ns.Members["consume_ammo"] = PsBuiltins.BF("combat.consume_ammo", (itp, a, line) =>
                {
                    PsBuiltins.Need(a, 0, 1, "combat.consume_ammo([ammo_id]) → bool — 消耗 1 发 (双源查找)", line);
                    string ammo = a.Count == 1 ? AsId(a[0], "combat.consume_ammo", line) : null;
                    try { return combat.ConsumeAmmo(ammo); }
                    catch (Exception e) { throw new PsRuntimeError("combat.consume_ammo 失败: " + e.Message, line); }
                });
                ns.Members["throw_item"] = PsBuiltins.BF("combat.throw_item", (itp, a, line) =>
                {
                    PsBuiltins.Need(a, 0, 0, "combat.throw_item() → 物品句柄 | null — 投掷槽 (s_throw1-3) 第一个有物品的槽", line);
                    try
                    {
                        var item = combat.FindThrowItem();
                        return item == null ? null : (object)new PsItemHandle(item);
                    }
                    catch (Exception e) { throw new PsRuntimeError("combat.throw_item 失败: " + e.Message, line); }
                });
                ns.Members["major_wound"] = PsBuiltins.BF("combat.major_wound", (itp, a, line) =>
                {
                    PsBuiltins.Need(a, 0, 0, "combat.major_wound() → bool — 原生重伤 (HealthData.ReceiveMajorWound; HP 归零晕倒链用)", line);
                    try { return combat.MajorWound(); }
                    catch (Exception e) { throw new PsRuntimeError("combat.major_wound 失败: " + e.Message, line); }
                });
                // ---- v1.29.0 实时动作战斗会话 (RtCombatService; v1.35.0 起唯一实战路径) ----
                ns.Members["rt_start"] = PsBuiltins.BF("combat.rt_start", (itp, a, line) =>
                {
                    PsBuiltins.Need(a, 2, 2, "combat.rt_start({id?, name?, hp(必, 直接 HP 值; ≤10 回退旧 pip×15), atk?(\"3-6\"), strong?, posture_sneak?, player_hp?, flee?(成功率% 0-95), armor?(0-100 减伤%, 可被 pen 穿透), band?(0近1中2远 默认1), tempo?(0.5-1.5 行为间隙系数 默认1, v1.37.0), behaviors?([{type=strafe|shrink|hide|shield|charge, weight, ...参数}] 行为卡组), shield?({dots, reduction, moving_ratio, teleport_ratio, red_window}), charge?({duration, stagger_mul, heavy_mul}), on_hp_change?(pss 函数, HP 变化实时收 {hp, max})}, {on_end}) → bool — 实时动作战斗会话 (方向 QTE; on_end 收 {result=\"win|flee|lose\", player_hp, sp_cost(战斗时长×0.6 体力结算), enemy_id, enemy_name}; 自动锁物品箱/地面窗+隐藏光标)", line);
                    var cfg = AsDict(a[0], "combat.rt_start", line);
                    var opts = AsDict(a[1], "combat.rt_start", line);
                    object ov;
                    PsCallable onEnd = null;
                    if (opts.TryGetValue("on_end", out ov) && ov != null)
                    {
                        if (ov is not PsCallable c)
                            throw new PsRuntimeError($"combat.rt_start 的 on_end 须为 pss 函数, 实为 {PsValues.TypeName(ov)}", line);
                        onEnd = c;
                    }
                    try { return combat.RtStart(cfg, onEnd, itp, line); }
                    catch (PsRuntimeError) { throw; }
                    catch (Exception e) { throw new PsRuntimeError("combat.rt_start 失败: " + e.Message, line); }
                });
                ns.Members["rt_active"] = PsBuiltins.BF("combat.rt_active", (itp, a, line) =>
                {
                    PsBuiltins.Need(a, 0, 0, "combat.rt_active() → bool — 实时战斗会话进行中", line);
                    return combat.Rt != null && combat.Rt.Active;
                });
            }
            env.Define("combat", ns, true, 0);
        }

        /// <summary>roll_encounter/combat_rate 的 table 参数: dict → Dictionary&lt;string,float&gt;。</summary>
        private static Dictionary<string, float> AsTable(object v, string fn, int line)
        {
            if (v is not Dictionary<string, object> d || d.Count == 0)
                throw new PsRuntimeError($"{fn} 的权重表须为非空 dict (如 {{poi=0.3, combat=0.25}}), 实为 {PsValues.TypeName(v)}", line);
            var table = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var kv in d)
            {
                float w = AsFloat(kv.Value, fn, line);
                if (w < 0) throw new PsRuntimeError($"{fn} 的权重 '{kv.Key}' 不能为负", line);
                table[kv.Key] = w;
            }
            return table;
        }

        /// <summary>roll_loot 的 pool 参数: [{id, weight[, count][, min_depth][, rarity]}...] → SceneLootEntry 表。
        /// v1.37.0: rarity 写字符串 common/fine/rare/epic/legend (非法必抛带行号), 数字 0-4 容忍钳制。</summary>
        private static List<SceneLootEntry> ParseLootPool(object v, string fn, int line)
        {
            if (v is not List<object> l)
                throw new PsRuntimeError($"{fn} 的 pool 须为数组 ({{id, weight, count?, min_depth?, rarity?}} 条目), 实为 {PsValues.TypeName(v)}", line);
            var pool = new List<SceneLootEntry>();
            for (int i = 0; i < l.Count; i++)
            {
                if (l[i] is not Dictionary<string, object> d)
                    throw new PsRuntimeError($"{fn} 的 pool[{i}] 须为 dict, 实为 {PsValues.TypeName(l[i])}", line);
                object idv;
                if (!d.TryGetValue("id", out idv) || idv is not string id || string.IsNullOrWhiteSpace(id))
                    throw new PsRuntimeError($"{fn} 的 pool[{i}] 缺 id (非空字符串)", line);
                var e = new SceneLootEntry { Id = id, Weight = 1f };
                object wv;
                if (d.TryGetValue("weight", out wv) && wv != null) e.Weight = AsFloat(wv, fn, line);
                if (e.Weight < 0) throw new PsRuntimeError($"{fn} 的 pool[{i}] weight 不能为负", line);
                object cv;
                if (d.TryGetValue("count", out cv) && cv != null) e.Count = PsValues.Fmt(cv);
                object mv;
                if (d.TryGetValue("min_depth", out mv) && mv != null) e.MinDepth = AsInt(mv, fn, line);
                object rv;
                if (d.TryGetValue("rarity", out rv) && rv != null)
                {
                    if (rv is string rs)
                    {
                        int ri = Raid.RaidCore.RarityIndex(rs);
                        if (ri < 0)
                            throw new PsRuntimeError($"{fn} 的 pool[{i}] rarity 非法 '{rs}' (可用: common/fine/rare/epic/legend 或 0-4)", line);
                        e.Rarity = ri;
                    }
                    else if (rv is long || rv is double)
                        e.Rarity = Raid.RaidCore.Clamp(AsInt(rv, fn, line), 0, Raid.RaidCore.RarityTiers - 1);
                    else
                        throw new PsRuntimeError($"{fn} 的 pool[{i}] rarity 须为字符串或 0-4 数字, 实为 {PsValues.TypeName(rv)}", line);
                }
                pool.Add(e);
            }
            return pool;
        }

        /// <summary>roll_loot rarity 分布覆写 dict → 千分比表 (int[RarityTiers]; 键 common/fine/rare/epic/legend,
        /// 值数字 ≥0, 缺键=0; 未知键/负值必抛)。</summary>
        private static int[] ParseRarityOverride(object v, int line)
        {
            if (v is not Dictionary<string, object> d || d.Count == 0)
                throw new PsRuntimeError($"combat.roll_loot 的 rarity 参数须为 false (单层) 或非空分布 dict ({{common=‰,...}}), 实为 {PsValues.TypeName(v)}", line);
            var table = new int[Raid.RaidCore.RarityTiers];
            foreach (var kv in d)
            {
                int ri = Raid.RaidCore.RarityIndex(kv.Key);
                if (ri < 0)
                    throw new PsRuntimeError($"combat.roll_loot 的 rarity 分布键非法 '{kv.Key}' (可用: common/fine/rare/epic/legend)", line);
                int w = AsInt(kv.Value, "combat.roll_loot", line);
                if (w < 0) throw new PsRuntimeError($"combat.roll_loot 的 rarity 分布 '{kv.Key}' 不能为负", line);
                table[ri] = w;
            }
            return table;
        }

        // ---- 参数辅助 ----

        private static string AsId(object v, string fn, int line)
        {
            if (v is string s && !string.IsNullOrWhiteSpace(s)) return s;
            throw new PsRuntimeError($"{fn} 的 id 参数须为非空字符串, 实为 {PsValues.TypeName(v)}", line);
        }

        private static int AsCount(object v, string fn, int line)
        {
            long n = v is long l ? l : v is double d ? (long)d
                : throw new PsRuntimeError($"{fn} 的 count 须为数字, 实为 {PsValues.TypeName(v)}", line);
            if (n < 1 || n > 9999)
                throw new PsRuntimeError($"{fn} 的 count 须在 1..9999, 实为 {n}", line);
            return (int)n;
        }

        private static int AsInt(object v, string fn, int line)
        {
            if (v is long l) return (int)l;
            if (v is double d) return (int)d;
            throw new PsRuntimeError($"{fn} 的参数须为数字, 实为 {PsValues.TypeName(v)}", line);
        }

        private static float AsFloat(object v, string fn, int line)
        {
            if (v is long l) return l;
            if (v is double d) return (float)d;
            throw new PsRuntimeError($"{fn} 的参数须为数字, 实为 {PsValues.TypeName(v)}", line);
        }

        private static Dictionary<string, object> AsDict(object v, string fn, int line)
        {
            if (v is Dictionary<string, object> d) return d;
            throw new PsRuntimeError($"{fn} 的参数须为 dict, 实为 {PsValues.TypeName(v)}", line);
        }
    }
}
