using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using PSApi.Items;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// E2 游戏内置命名空间: state(SaveStates, 按存档槽隔离) / time / player / rep / crime / power。
    /// 所有游戏 API 调用包 try/catch → PsRuntimeError(行号); 单例为 null(主菜单等)给友好错误。
    /// 注意: 本类方法体引用 Il2Cpp 类型, 仅在游戏进程内调用; 无头测试台不得触发。
    /// </summary>
    internal static class PsBuiltinsGame
    {
        /// <summary>脚本简写 → 游戏 factionId(探针实测: PlayerStore.storeReputations 实例 id,
        /// 见 UserData/probe/api_probe_195744.txt)。表外名字走 IsFactionValid 透传, 无效报运行错误。</summary>
        private static readonly Dictionary<string, string> FactionIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sec"] = "FACTION_SECURITY",
            ["rev"] = "FACTION_REVOLUTION",
            ["bm"] = "FACTION_BLACK_MARKET",
            ["cartel"] = "FACTION_CARTEL",
            ["ul"] = "FACTION_UPPER_LEVEL",
            ["ll"] = "FACTION_LOWER_LEVEL",
        };

        /// <summary>势力数值 which → StoreOperationManager 字段映射。</summary>
        private static readonly Dictionary<string, string> PowerFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rev"] = "revPower",
            ["sec"] = "secPower",
            ["bm"] = "blackMarketPower",
            ["lower_unrest"] = "lowerLevelUnrest",
            ["upper_friend"] = "upperLevelFriendliness",
        };

        /// <summary>自定义犯罪显示名注册表(crime.commit 的 display 参数 → GetCrimeString 补丁读)。</summary>
        internal static readonly Dictionary<string, string> CrimeNames = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>把 E2/E3/E4/E6/M1 命名空间注册进包全局环境; storeEvents/npcs/ui/inject 为 null(无头测试)时跳过对应命名空间。</summary>
        internal static void Register(PsEnv env, MelonLogger.Instance logger, string packId,
            StoreEventService storeEvents = null, NpcService npcs = null, PsUI.PsUiService ui = null,
            InjectService inject = null, LootPoolService loot = null)
        {
            RegisterState(env, logger, packId);
            RegisterTime(env);
            RegisterPlayer(env);
            RegisterRep(env);
            RegisterCrime(env);
            RegisterPower(env);
            RegisterShop(env);             // M3: 商店查询(状态/吸引力/当前客户/队列)
            if (storeEvents != null) RegisterStoreEvent(env, storeEvents);
            if (npcs != null) RegisterNpc(env, npcs, packId);
            PsBuiltinsItems.Register(env);   // E5: items/quality/machine (无条件注册; 调用经 ItemsFacade 才碰游戏)
            if (ui != null) PsBuiltinsUi.Register(env, ui, packId);   // E6: ui.open/close
            if (inject != null) RegisterInject(env, inject, loot, packId);  // M1: inject.* 物品池注入 + v1.10.0 loot_pool
        }

        // ---- inject: 物品池注入(M1, NpcManager Injectors.cs 四通道移植 + v1.10.0 loot_pool 第五通道) ----

        private static void RegisterInject(PsEnv env, InjectService svc, LootPoolService loot, string packId)
        {
            var ns = new PsNamespace("inject");
            ns.Members["buy_list"] = PsBuiltins.BF("inject.buy_list", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "inject.buy_list(id | [ids] | fn(client) -> id/[ids])", line);
                svc.AddBuyList(packId, a[0], itp, line);
                return null;
            });
            ns.Members["sell_shelf"] = PsBuiltins.BF("inject.sell_shelf", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 4, "inject.sell_shelf(id | fn(client) -> sell_items数组[, count=1 或 \"min-max\"[, chance=1[, {uses=N}]]]) — count 上限 5, 区间每次注入独立掷; uses>0 = 摆上的是次数物品", line);
                long cmin = 1, cmax = 0;
                if (a.Count >= 2)
                {
                    // v1.10.0: count 支持 "1-3" 区间字符串
                    if (a[1] is string cs)
                    {
                        if (!NpcService.TryParseCountToken(cs, out cmin, out cmax) || cmax <= 0)
                            throw new PsRuntimeError($"inject.sell_shelf 的 count 字符串须为 \"min-max\" 区间(如 \"1-3\"), 实为 '{cs}'", line);
                    }
                    else cmin = AsLong(a[1], "inject.sell_shelf", line);
                }
                double chance = 1.0;
                if (a.Count >= 3)
                    chance = a[2] is long l ? l : a[2] is double d ? d
                        : throw new PsRuntimeError($"inject.sell_shelf 的 chance 须为数字, 实为 {PsValues.TypeName(a[2])}", line);
                int uses = a.Count >= 4 ? ParseUsesOpt(a[3], "inject.sell_shelf", line) : 0;
                svc.AddSellShelf(packId, a[0], cmin, cmax, chance, uses, itp, line);
                return null;
            });
            ns.Members["doctor"] = PsBuiltins.BF("inject.doctor", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 3, "inject.doctor(id[, count=1[, {uses=N}]])", line);
                long count = a.Count >= 2 ? AsLong(a[1], "inject.doctor", line) : 1;
                int uses = a.Count >= 3 ? ParseUsesOpt(a[2], "inject.doctor", line) : 0;
                svc.AddDoctor(packId, a[0], count, uses, line);
                return null;
            });
            ns.Members["barter"] = PsBuiltins.BF("inject.barter", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "inject.barter(id[, count=1])", line);
                long count = a.Count == 2 ? AsLong(a[1], "inject.barter", line) : 1;
                svc.AddBarter(packId, a[0], count, line);
                return null;
            });
            // v1.10.0: 原版 LootTable 池注入( LootPoolService; 注册在加载期, 入池等表就绪轮询)
            ns.Members["loot_pool"] = PsBuiltins.BF("inject.loot_pool", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 3, "inject.loot_pool(表名, 物品id, 权重) — 表名短名自动补 Table 后缀; 权重 (0,1] 按表内总和归一化", line);
                if (loot == null)
                    throw new PsRuntimeError("inject.loot_pool 不可用( LootPoolService 未接线)", line);
                string table = a[0] as string;
                if (string.IsNullOrWhiteSpace(table))
                    throw new PsRuntimeError($"inject.loot_pool 的表名须为非空字符串, 实为 {PsValues.TypeName(a[0])}", line);
                string id = a[1] as string;
                if (string.IsNullOrWhiteSpace(id))
                    throw new PsRuntimeError($"inject.loot_pool 的物品 id 须为非空字符串, 实为 {PsValues.TypeName(a[1])}", line);
                double w = a[2] is long lw ? lw : a[2] is double dw ? dw
                    : throw new PsRuntimeError($"inject.loot_pool 的权重须为数字, 实为 {PsValues.TypeName(a[2])}", line);
                loot.Add(packId, table, id, w, line);
                return null;
            });

            // ---- M3: 注册查看 + 运行时调节(管理面板; 覆盖层不进存档, 面板脚本自行持久化) ----
            ns.Members["list"] = PsBuiltins.BF("inject.list", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "inject.list() → [{idx, channel, pack, id, count, count_max, chance, dynamic, enabled, uses}]", line);
                var r = svc.Describe();
                if (loot != null) r.AddRange(loot.Describe(svc.EntryCount)); // v1.10.0: loot_pool 行接在四通道之后
                return r;
            });
            ns.Members["tune"] = PsBuiltins.BF("inject.tune", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "inject.tune(idx, {enabled=bool, count=1..99|0恢复}) — loot_pool 条目仅支持 enabled", line);
                long idx = AsLong(a[0], "inject.tune", line);
                if (a[1] is not Dictionary<string, object> d)
                    throw new PsRuntimeError($"inject.tune 的第二参须为 dict({{enabled/count}}), 实为 {PsValues.TypeName(a[1])}", line);
                bool? enabled = null;
                long? count = null;
                if (d.TryGetValue("enabled", out var ev) && ev != null) enabled = PsValues.Truthy(ev);
                if (d.TryGetValue("count", out var cv) && cv != null) count = AsLong(cv, "inject.tune", line);
                if (svc.Tune(idx, enabled, count, line)) return null;
                // v1.10.0: loot_pool 条目( idx 接在四通道之后)仅 enabled 可调
                long li = idx - svc.EntryCount;
                if (loot != null && li >= 0 && li < loot.Count)
                {
                    if (count.HasValue)
                        throw new PsRuntimeError("inject.tune: loot_pool 条目的权重注册时定, 仅支持 enabled 开关", line);
                    if (enabled.HasValue && loot.TuneEnabled(li, enabled.Value)) return null;
                }
                throw new PsRuntimeError($"inject.tune: 序号 {idx} 越界(inject.list() 取有效序号)", line);
            });
            ns.Members["reset_tracking"] = PsBuiltins.BF("inject.reset_tracking", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "inject.reset_tracking()", line);
                svc.ResetTracking();
                loot?.ResetTracking(); // v1.10.0: loot_pool 下帧撤旧重注
                return null;
            });
            ns.Members["restock_current"] = PsBuiltins.BF("inject.restock_current", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "inject.restock_current() → 注入件数", line);
                return (long)svc.RestockCurrent(line);
            });
            ns.Members["force_client"] = PsBuiltins.BF("inject.force_client", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "inject.force_client(ptr)  — ptr 取自 shop.queue() 条目", line);
                long ptr = AsLong(a[0], "inject.force_client", line);
                return svc.ForceClient(ptr);
            });
            env.Define("inject", ns, true, 0);
        }

        // ---- npc: 自定义客户注册/加权池(E4, NpcManager 收编) ----

        private static void RegisterNpc(PsEnv env, NpcService npcs, string packId)
        {
            var ns = new PsNamespace("npc");
            ns.Members["register"] = PsBuiltins.BF("npc.register", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "npc.register({id/name/base_template/faction/intent/cash/budget/buying_ids/sell_items/dialogues/schedule/can_spawn/pools/auto_leave/...}) — dialogues 候选支持 texts=[段1,段2,...] 多段顺序链(text 与 texts 二选一); auto_leave=false 关闭说完就走", line);
                if (a[0] is not Dictionary<string, object> cfg)
                    throw new PsRuntimeError($"npc.register 的参数须为 dict, 实为 {PsValues.TypeName(a[0])}", line);
                npcs.Register(packId, cfg, itp, line);
                return null;
            });
            ns.Members["pool_add"] = PsBuiltins.BF("npc.pool_add", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 3, "npc.pool_add(pool, id, weight_pct)", line);
                string pool = AsKey(a[0], "npc.pool_add", line);
                string id = AsKey(a[1], "npc.pool_add", line);
                double pct = a[2] is long l ? l : a[2] is double d ? d
                    : throw new PsRuntimeError($"npc.pool_add 的 weight_pct 须为数字, 实为 {PsValues.TypeName(a[2])}", line);
                npcs.PoolAdd(pool, id, pct, line);
                return null;
            });
            ns.Members["pool_scale"] = PsBuiltins.BF("npc.pool_scale", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "npc.pool_scale(id或\"前缀*\", factor)", line);
                string pattern = AsKey(a[0], "npc.pool_scale", line);
                double factor = a[1] is long l ? l : a[1] is double d ? d
                    : throw new PsRuntimeError($"npc.pool_scale 的 factor 须为数字, 实为 {PsValues.TypeName(a[1])}", line);
                return (long)npcs.PoolScale(pattern, factor, line);
            });
            ns.Members["schedule"] = PsBuiltins.BF("npc.schedule", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "npc.schedule(id[, {put_first = true/false}])", line);
                string id = AsKey(a[0], "npc.schedule", line);
                bool putFirst = false;
                if (a.Count == 2)
                {
                    if (a[1] is not Dictionary<string, object> opts)
                        throw new PsRuntimeError($"npc.schedule 的第二参须为 dict({{put_first = bool}}), 实为 {PsValues.TypeName(a[1])}", line);
                    if (opts.TryGetValue("put_first", out var pf) && pf != null) putFirst = PsValues.Truthy(pf);
                }
                npcs.ScheduleSpawn(packId, id, putFirst, line);
                return null;
            });
            env.Define("npc", ns, true, 0);
        }

        // ---- shop: 商店查询(M3, NpcManager ManagerWindow 查询面移植; 全部只读) ----

        private static void RegisterShop(PsEnv env)
        {
            var ns = new PsNamespace("shop");
            ns.Members["status"] = PsBuiltins.BF("shop.status", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "shop.status() → {in_game, day_until_rent, queue, current_name, current_intent}", line);
                return ShopService.Status();
            });
            ns.Members["attract"] = PsBuiltins.BF("shop.attract", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "shop.attract() → {value, spawn_chance, normal_count, t1..t3, extra10/25/50, rich400/650} | null", line);
                return ShopService.Attract();
            });
            ns.Members["current_client"] = PsBuiltins.BF("shop.current_client", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "shop.current_client() → 客户详情 dict | null", line);
                return ShopService.CurrentClientDetail();
            });
            ns.Members["queue"] = PsBuiltins.BF("shop.queue", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "shop.queue() → [{idx, name, intent, intent_key, cash, ptr, buying, tags}]", line);
                return ShopService.Queue();
            });
            // v1.7.0: 展示柜(商品展示区)物品枚举 + 禁售注册表 (仿制证书)
            ns.Members["showcase_items"] = PsBuiltins.BF("shop.showcase_items", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "shop.showcase_items() → [物品句柄...] (展示柜内全部物品; 空柜=空表)", line);
                var result = new List<object>();
                try
                {
                    foreach (var item in ItemsFacade.ShowcaseItems())
                        if (item != null) result.Add(new PsItemHandle(item));
                }
                catch (Exception e) { throw new PsRuntimeError("shop.showcase_items 失败: " + e.Message, line); }
                return result;
            });
            ns.Members["block_sale"] = PsBuiltins.BF("shop.block_sale", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "shop.block_sale(id) → 注册后任何客户都不买此 id (展示柜自动售货+柜台都拦; 仅内存, 包脚本顶层调用即可)", line);
                string id = AsKey(a[0], "shop.block_sale", line);
                try { id = ItemsFacade.NormalizeId(id); } catch { }
                CrimeExemptState.BlockedSaleIds.Add(id);
                return null;
            });
            ns.Members["unblock_sale"] = PsBuiltins.BF("shop.unblock_sale", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "shop.unblock_sale(id) → 移出禁售注册表", line);
                string id = AsKey(a[0], "shop.unblock_sale", line);
                try { id = ItemsFacade.NormalizeId(id); } catch { }
                CrimeExemptState.BlockedSaleIds.Remove(id);
                return null;
            });
            env.Define("shop", ns, true, 0);
        }

        // ---- store_event: 原版商店事件蓝图池注入(E3) ----
        private static void RegisterStoreEvent(PsEnv env, StoreEventService svc)
        {
            var ns = new PsNamespace("store_event");
            ns.Members["register"] = PsBuiltins.BF("store_event.register", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "store_event.register(id[, {pool/odd/name/news/description/duration/cooldown/hidden/importance/slip}])", line);
                string id = AsKey(a[0], "store_event.register", line);
                Dictionary<string, object> cfg = null;
                if (a.Count == 2)
                {
                    if (a[1] is not Dictionary<string, object> d)
                        throw new PsRuntimeError("store_event.register 的配置参数须为 dict", line);
                    cfg = d;
                }
                svc.Register(id, cfg, line);
                return null;
            });
            ns.Members["queue"] = PsBuiltins.BF("store_event.queue", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "store_event.queue(id[, delay_days=0])", line);
                string id = AsKey(a[0], "store_event.queue", line);
                long delay = a.Count == 2 ? AsLong(a[1], "store_event.queue", line) : 0;
                svc.Queue(id, delay, line);
                return null;
            });
            env.Define("store_event", ns, true, 0);
        }

        // ---- state: 存档槽隔离 KV, 脚本值 JSON 序列化 ----

        private static void RegisterState(PsEnv env, MelonLogger.Instance logger, string packId)
        {
            // 每次调用现取: 槽位切换后 SaveStates.For 内部自动重载, 不能缓存实例
            SaveStates Store() => SaveStates.For(packId, logger);

            var ns = new PsNamespace("state");
            ns.Members["get"] = PsBuiltins.BF("state.get", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "state.get(key[, 默认值])", line);
                string key = AsKey(a[0], "state.get", line);
                string raw = Store().Get(key);
                if (raw == null) return a.Count == 2 ? a[1] : null;
                return ScriptJson.TryDecode(raw, out var v) ? v : raw;
            });
            ns.Members["set"] = PsBuiltins.BF("state.set", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "state.set(key, value)", line);
                string key = AsKey(a[0], "state.set", line);
                Store().Set(key, ScriptJson.Encode(a[1]));
                return null;
            });
            ns.Members["has"] = PsBuiltins.BF("state.has", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "state.has(key)", line);
                return Store().Get(AsKey(a[0], "state.has", line)) != null;
            });
            ns.Members["del"] = PsBuiltins.BF("state.del", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "state.del(key)", line);
                Store().Set(AsKey(a[0], "state.del", line), null);
                return null;
            });
            env.Define("state", ns, true, 0);
        }

        // ---- time: 天数(与事件上下文字段同源, 见 GameHooks.GameDay) ----

        private static void RegisterTime(PsEnv env)
        {
            var ns = new PsNamespace("time");
            ns.Members["day"] = PsBuiltins.BF("time.day", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "time.day()", line);
                return ReadDay(line).day;
            });
            ns.Members["rel_day"] = PsBuiltins.BF("time.rel_day", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "time.rel_day()", line);
                return ReadDay(line).relDay;
            });
            ns.Members["weekday"] = PsBuiltins.BF("time.weekday", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "time.weekday()", line);
                return ReadDay(line).weekday;
            });
            env.Define("time", ns, true, 0);
        }

        private static (long day, long relDay, long weekday) ReadDay(int line)
        {
            if (!GameHooks.GameDay.TryRead(out long day, out long relDay, out long weekday))
                throw new PsRuntimeError("time.* 当前不可用(不在存档场景中)", line);
            return (day, relDay, weekday);
        }

        // ---- player: 现金 ----

        private static void RegisterPlayer(PsEnv env)
        {
            var ns = new PsNamespace("player");
            ns.Members["cash"] = PsBuiltins.BF("player.cash", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "player.cash()", line);
                var ps = NeedStore("player.cash", line);
                try { return (long)ps.GetCash(); }
                catch (Exception e) { throw new PsRuntimeError("player.cash() 失败: " + e.Message, line); }
            });
            ns.Members["add_cash"] = PsBuiltins.BF("player.add_cash", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "player.add_cash(amount[, sound=false])", line);
                var ps = NeedStore("player.add_cash", line);
                long amount = AsLong(a[0], "player.add_cash", line);
                bool sound = a.Count == 2 && PsValues.Truthy(a[1]);
                try { ps.ModCash((int)amount, sound); }
                catch (Exception e) { throw new PsRuntimeError("player.add_cash() 失败: " + e.Message, line); }
                return null;
            });
            env.Define("player", ns, true, 0);
        }

        // ---- rep: 派系声望 ----

        private static void RegisterRep(PsEnv env)
        {
            var ns = new PsNamespace("rep");
            ns.Members["get"] = PsBuiltins.BF("rep.get", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "rep.get(faction)", line);
                string id = ResolveFaction(AsKey(a[0], "rep.get", line), line);
                try
                {
                    var rep = StoreReputation.GetStoreReputation(id);
                    if (rep == null) throw new PsRuntimeError($"派系 '{id}' 当前无声望记录(场景未就绪?)", line);
                    return (long)rep.GetReputation();
                }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("rep.get() 失败: " + e.Message, line); }
            });
            ns.Members["add"] = PsBuiltins.BF("rep.add", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "rep.add(faction, amount)", line);
                string id = ResolveFaction(AsKey(a[0], "rep.add", line), line);
                long amount = AsLong(a[1], "rep.add", line);
                try { StoreReputation.ModReputation(id, (int)amount, true); }
                catch (Exception e) { throw new PsRuntimeError("rep.add() 失败: " + e.Message, line); }
                return null;
            });
            env.Define("rep", ns, true, 0);
        }

        private static string ResolveFaction(string name, int line)
        {
            if (FactionIds.TryGetValue(name, out var id)) return id;
            // 表外名字: 允许直接传游戏 factionId(如 FACTION_CHURCH), 无效则报错
            try
            {
                if (StoreReputation.IsFactionValid(name)) return name;
            }
            catch { }
            throw new PsRuntimeError($"未知派系 '{name}'(可用: sec/rev/bm/cartel/ul/ll 或有效 factionId)", line);
        }

        // ---- crime: 治安档案 ----

        private static void RegisterCrime(PsEnv env)
        {
            var ns = new PsNamespace("crime");
            ns.Members["commit"] = PsBuiltins.BF("crime.commit", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 3, "crime.commit(crime_id, amount[, 显示名])", line);
                string id = AsKey(a[0], "crime.commit", line);
                long amount = AsLong(a[1], "crime.commit", line);
                string display = a.Count == 3 ? PsValues.Fmt(a[2]) : null;
                var ps = NeedStore("crime.commit", line);
                try
                {
                    if (ps.secData == null) throw new PsRuntimeError("crime.commit() 当前不可用(secData 为空)", line);
                    ps.secData.CommitCrime(id, (int)amount);
                    if (!string.IsNullOrEmpty(display))
                        CrimeNames[id] = display; // 显示名补丁(CrimeNamePatch)统一服务
                }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("crime.commit() 失败: " + e.Message, line); }
                return null;
            });
            // v1.7.0: 今日卖枪免记录开关 (仿制证书; 惯例 shop_opened 扫展示柜设置, day_wake 复位, 只管当天新记录)
            ns.Members["exempt_guns"] = PsBuiltins.BF("crime.exempt_guns", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "crime.exempt_guns(bool) → 设置后返回当前状态 (今日卖 WEAPON 类型物品不写治安档案, WEAPON_TRAFFICKING/FENCING 同免)", line);
                CrimeExemptState.GunsExemptToday = PsValues.Truthy(a[0]);
                return CrimeExemptState.GunsExemptToday;
            });
            env.Define("crime", ns, true, 0);
        }

        // ---- power: 五势力数值 ----

        private static void RegisterPower(PsEnv env)
        {
            var ns = new PsNamespace("power");
            ns.Members["get"] = PsBuiltins.BF("power.get", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "power.get(which)", line);
                string which = ResolvePower(AsKey(a[0], "power.get", line), line);
                var op = NeedOps("power.get", line);
                try { return (long)GetPowerField(op, which); }
                catch (Exception e) { throw new PsRuntimeError("power.get() 失败: " + e.Message, line); }
            });
            ns.Members["add"] = PsBuiltins.BF("power.add", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "power.add(which, delta)", line);
                string which = ResolvePower(AsKey(a[0], "power.add", line), line);
                long delta = AsLong(a[1], "power.add", line);
                var op = NeedOps("power.add", line);
                try { SetPowerField(op, which, GetPowerField(op, which) + (int)delta); }
                catch (Exception e) { throw new PsRuntimeError("power.add() 失败: " + e.Message, line); }
                return null;
            });
            env.Define("power", ns, true, 0);
        }

        private static string ResolvePower(string which, int line)
        {
            if (PowerFields.TryGetValue(which, out var field)) return field;
            throw new PsRuntimeError($"未知势力 '{which}'(可用: rev/sec/bm/lower_unrest/upper_friend)", line);
        }

        private static int GetPowerField(StoreOperationManager op, string field) => field switch
        {
            "revPower" => op.revPower,
            "secPower" => op.secPower,
            "blackMarketPower" => op.blackMarketPower,
            "lowerLevelUnrest" => op.lowerLevelUnrest,
            "upperLevelFriendliness" => op.upperLevelFriendliness,
            _ => 0,
        };

        private static void SetPowerField(StoreOperationManager op, string field, int value)
        {
            switch (field)
            {
                case "revPower": op.revPower = value; break;
                case "secPower": op.secPower = value; break;
                case "blackMarketPower": op.blackMarketPower = value; break;
                case "lowerLevelUnrest": op.lowerLevelUnrest = value; break;
                case "upperLevelFriendliness": op.upperLevelFriendliness = value; break;
            }
        }

        // ---- 公共辅助 ----

        private static PlayerStore NeedStore(string api, int line)
        {
            PlayerStore ps = null;
            try { ps = PlayerStore.instance; } catch { }
            if (ps == null)
                throw new PsRuntimeError($"{api} 当前不可用(不在存档场景中)", line);
            return ps;
        }

        private static StoreOperationManager NeedOps(string api, int line)
        {
            StoreOperationManager op = null;
            try { op = StoreStation.instance?.storeOperationManager; } catch { }
            if (op == null)
                throw new PsRuntimeError($"{api} 当前不可用(不在商店场景中)", line);
            return op;
        }

        private static string AsKey(object v, string api, int line)
        {
            if (v is not string s || s.Length == 0)
                throw new PsRuntimeError($"{api} 的键/名参数须为非空字符串", line);
            return s;
        }

        private static long AsLong(object v, string api, int line)
        {
            if (v is long l) return l;
            if (v is double d) return (long)d;
            throw new PsRuntimeError($"{api} 的数值参数须为数字, 实为 {PsValues.TypeName(v)}", line);
        }

        /// <summary>v1.9.0: inject.sell_shelf/doctor 的末参 opts dict({uses=N}, uses 1..99; 未知键忽略同 inject.tune)。</summary>
        private static int ParseUsesOpt(object raw, string api, int line)
        {
            if (raw is not Dictionary<string, object> d)
                throw new PsRuntimeError($"{api} 的 opts 须为 dict({{uses=N}}), 实为 {PsValues.TypeName(raw)}", line);
            if (!d.TryGetValue("uses", out var u) || u == null) return 0;
            long v = AsLong(u, api + ".uses", line);
            if (v < 1 || v > 99)
                throw new PsRuntimeError($"{api} 的 uses 须在 1..99, 实为 {v}", line);
            return (int)v;
        }
    }
}
