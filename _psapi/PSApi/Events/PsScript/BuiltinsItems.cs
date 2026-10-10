using System;
using System.Collections.Generic;
using PSApi.Items;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// E5 物品/品质/机器命名空间: items.give/has/value/find + quality.set/get/price_factor + machine.progress/producing/find。
    /// 全部转调 PSApi.Items.ItemsFacade (跨程序集引用, Private=false; MelonPriority Items=10 先于 Events=20 加载)。
    /// 参数校验先于游戏调用; 游戏调用包 try/catch → PsRuntimeError(行号); null/失效句柄友好错误。
    /// 注意: 函数体经 ItemsFacade 引用 Il2Cpp 类型, 仅在游戏进程内调用; 无头测试台只触发参数校验路径。
    /// </summary>
    internal static class PsBuiltinsItems
    {
        /// <summary>无条件注册三命名空间 (注册本身不碰游戏; Facade 调用发生在脚本执行时)。</summary>
        internal static void Register(PsEnv env)
        {
            RegisterItems(env);
            RegisterQuality(env);
            RegisterMachine(env);
        }

        // ---- items: 发放/持有/估价/背包检索 ----

        private static void RegisterItems(PsEnv env)
        {
            var ns = new PsNamespace("items");
            ns.Members["give"] = PsBuiltins.BF("items.give", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 3, "items.give(id[, count=1[, uses=0 | {name/desc/flavor/value/quality/uses/data/to}]]) → bool (uses>0 = 发放次数物品, 归零销毁; v1.16.0 to=inventory/counter_out/counter_in 选落点)", line);
                string id = AsId(a[0], "items.give", line);
                int count = a.Count >= 2 ? AsCount(a[1], "items.give", line) : 1;
                ItemsFacade.ItemOpts opts = null;
                if (a.Count == 3)
                {
                    // v1.15.0: 第三参接受 dict 定制(实例名/描述/单价/品质/次数/自定义数据); 数字 = 旧 uses 形式保留
                    if (a[2] is Dictionary<string, object> od)
                        opts = PsItemOpts.Parse(od, "items.give", line);
                    else
                    {
                        long uv = AsLongArg(a[2], "items.give", line);
                        if (uv < 1 || uv > 9999)
                            throw new PsRuntimeError($"items.give 的 uses 须在 1..9999, 实为 {uv}", line);
                        opts = new ItemsFacade.ItemOpts { Uses = (int)uv };
                    }
                }
                try { return RouteGive(id, count, opts); }
                catch (Exception e) { throw new PsRuntimeError("items.give 失败: " + e.Message, line); }
            });
            // v0.9.2(Items)/v1.9.1(Events): 发放到店门口柜台(贴原版送礼路径), 归玩家所有; 不在对局 = false
            // v1.16.0: 第三参升级为兼容 dict opts (同 items.give; to 缺省 = counter_out, 即维持别名语义)
            ns.Members["give_counter"] = PsBuiltins.BF("items.give_counter", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 3, "items.give_counter(id[, count=1[, uses=0 | {name/desc/.../to}]]) → bool (摆到店门口柜台=to:counter_out 别名, 归玩家; 不在对局=false)", line);
                string id = AsId(a[0], "items.give_counter", line);
                int count = a.Count >= 2 ? AsCount(a[1], "items.give_counter", line) : 1;
                ItemsFacade.ItemOpts opts = null;
                if (a.Count == 3)
                {
                    if (a[2] is Dictionary<string, object> od)
                        opts = PsItemOpts.Parse(od, "items.give_counter", line);
                    else
                    {
                        long uv = AsLongArg(a[2], "items.give_counter", line);
                        if (uv < 1 || uv > 9999)
                            throw new PsRuntimeError($"items.give_counter 的 uses 须在 1..9999, 实为 {uv}", line);
                        opts = new ItemsFacade.ItemOpts { Uses = (int)uv };
                    }
                }
                if (opts != null && opts.To == null) opts.To = "counter_out"; // 别名语义: 缺省落门口外部柜台
                try { return RouteGive(id, count, opts); }
                catch (Exception e) { throw new PsRuntimeError("items.give_counter 失败: " + e.Message, line); }
            });
            ns.Members["has"] = PsBuiltins.BF("items.has", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "items.has(id[, count=1])", line);
                string id = AsId(a[0], "items.has", line);
                int count = a.Count == 2 ? AsCount(a[1], "items.has", line) : 1;
                try { return ItemsFacade.PlayerHas(id, count); }
                catch (Exception e) { throw new PsRuntimeError("items.has 失败: " + e.Message, line); }
            });
            ns.Members["value"] = PsBuiltins.BF("items.value", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "items.value(id)", line);
                string id = AsId(a[0], "items.value", line);
                try { return (long)ItemsFacade.ValueOf(id); }
                catch (Exception e) { throw new PsRuntimeError("items.value 失败: " + e.Message, line); }
            });
            // v1.11.0: 物品显示名 (本地化中文名; 对话 {buy_list} 等点名用)。未知 id = null (请判空回退)。
            ns.Members["name"] = PsBuiltins.BF("items.name", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "items.name(id) → 显示名字符串 (未知 id = null)", line);
                string id = AsId(a[0], "items.name", line);
                try { return ItemsFacade.DisplayName(id); }
                catch (Exception e) { throw new PsRuntimeError("items.name 失败: " + e.Message, line); }
            });
            ns.Members["find"] = PsBuiltins.BF("items.find", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "items.find(id)", line);
                string id = AsId(a[0], "items.find", line);
                try
                {
                    var item = ItemsFacade.FindInPlayer(id);
                    return item == null ? null : (object)new PsItemHandle(item);
                }
                catch (Exception e) { throw new PsRuntimeError("items.find 失败: " + e.Message, line); }
            });
            // v1.7.0: 全量查找 (证书衰减等"全场 -1"用; 随身背包+后仓, 一个不放过)
            ns.Members["find_all"] = PsBuiltins.BF("items.find_all", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "items.find_all(id) → [物品句柄...] (玩家库存里该 id 的全部实例; 无=空表)", line);
                string id = AsId(a[0], "items.find_all", line);
                var result = new List<object>();
                try
                {
                    foreach (var item in ItemsFacade.FindAllInPlayer(id))
                        if (item != null) result.Add(new PsItemHandle(item));
                }
                catch (Exception e) { throw new PsRuntimeError("items.find_all 失败: " + e.Message, line); }
                return result;
            });
            // v2.0.2: 玩家库存全量枚举 (psconsole bag clear 用; 随身背包+后仓, 无=空表)
            ns.Members["inventory"] = PsBuiltins.BF("items.inventory", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "items.inventory() → [物品句柄...] (玩家随身背包+后仓全部物品; 无=空表)", line);
                var result = new List<object>();
                try
                {
                    foreach (var item in ItemsFacade.AllInPlayer())
                        if (item != null) result.Add(new PsItemHandle(item));
                }
                catch (Exception e) { throw new PsRuntimeError("items.inventory 失败: " + e.Message, line); }
                return result;
            });
            // U4: 消耗物品 (从所在库存取出并销毁; 与 RecipeService 探测物回收同路径)
            // v1.39.0: 可选第 2 参 count — 部分消耗: count < 堆数时 unitCount 直减 (整堆销毁不走
            // Expel/Destroy, 对拖拽中挂在鼠标指针上的物品更稳); count ≥ 堆数 = 旧行为整堆销毁
            ns.Members["consume"] = PsBuiltins.BF("items.consume", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "items.consume(item_handle[, count=全堆]) → bool — v1.39.0: count < 堆数 = 部分消耗 (unitCount 直减)", line);
                var h = AsItem(a[0], "items.consume", line);
                try
                {
                    var item = h.NeedItem(line);
                    if (a.Count == 2)
                    {
                        long n = AsLongArg(a[1], "items.consume", line);
                        if (n < 1) throw new PsRuntimeError($"items.consume 的 count 须 ≥ 1, 实为 {n}", line);
                        int cur = item.unitCount;
                        if (n < cur) { item.unitCount = cur - (int)n; return true; }
                    }
                    return ItemsFacade.ConsumeItem(item);
                }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("items.consume 失败: " + e.Message, line); }
            });
            // v1.39.0: 物品对物品 use 注册 (拖 source 到 target 上触发 fn(h_source, h_target);
            // 实现见 ItemTargetService — Harmony 接管 GameItem.MayTarget/CanTarget/Target)
            ns.Members["on_target"] = PsBuiltins.BF("items.on_target", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 3, 3, "items.on_target(source_id, target_id, fn) — 拖 source 到 target 上触发 fn(h_source, h_target); fn 返回 false=放行原生, 其他=跳过原生; 重复注册同键=覆盖+警告", line);
                string sid = AsId(a[0], "items.on_target", line);
                string tid = AsId(a[1], "items.on_target", line);
                if (a[2] is not PsCallable fn)
                    throw new PsRuntimeError($"items.on_target 的 fn 须为函数, 实为 {PsValues.TypeName(a[2])}", line);
                ItemTargetService.Register(sid, tid, fn, itp, line);
                return null;
            });
            // v1.47.0: 物品双击接管 (仿 crime.set_filter) — 双击该 id 物品调 fn(物品句柄) 并跳过原生双击行为;
            // 实现见 DoubleClickPatch (Harmony Prefix ItemMouseDoubleClickHandler.DoubleClickAction)
            ns.Members["on_double_click"] = PsBuiltins.BF("items.on_double_click", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "items.on_double_click(id, fn | null) — 双击该 id 物品时调 fn(物品句柄) 并跳过原生双击行为; null 清除 (普通物品用; 机器/容器勿用)", line);
                string did = AsId(a[0], "items.on_double_click", line);
                if (a[1] == null)
                {
                    DoubleClickRegistry.Handlers.Remove(did);
                    return null;
                }
                if (a[1] is not PsCallable dfn)
                    throw new PsRuntimeError($"items.on_double_click 的 fn 须为函数或 null, 实为 {PsValues.TypeName(a[1])}", line);
                DoubleClickRegistry.Handlers[did] = new DoubleClickRegistry.Entry { Fn = dfn, Itp = itp };
                return null;
            });
            // v1.37.0 (Items v0.9.15): 实例搬移到店内称重台 (保 NBT; 兜底后仓) — 搜打撤口袋撤离用
            ns.Members["move_to_counter"] = PsBuiltins.BF("items.move_to_counter", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "items.move_to_counter(item_handle) → bool — 实例搬移到店内称重台 (保 NBT; 摆柜失败兜底退回后仓; 不在对局=false)", line);
                var h = AsItem(a[0], "items.move_to_counter", line);
                try { return ItemsFacade.MoveToCounter(h.NeedItem(line)); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("items.move_to_counter 失败: " + e.Message, line); }
            });

            // ---- v1.6.0: 电力 / 使用次数 (ItemsFacade v0.8.1 薄封装; 机器隔夜结算用) ----
            ns.Members["power"] = PsBuiltins.BF("items.power", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "items.power(item_handle) → 电池当前电量 (非电源=0)", line);
                var h = AsItem(a[0], "items.power", line);
                try { return (long)ItemsFacade.PowerGet(h.NeedItem(line)); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("items.power 失败: " + e.Message, line); }
            });
            ns.Members["power_draw"] = PsBuiltins.BF("items.power_draw", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "items.power_draw(item_handle, amount) → bool (全有或全无: 不足不扣)", line);
                var h = AsItem(a[0], "items.power_draw", line);
                int amount = AsCount(a[1], "items.power_draw", line);
                try { return ItemsFacade.PowerDraw(h.NeedItem(line), amount); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("items.power_draw 失败: " + e.Message, line); }
            });
            ns.Members["uses"] = PsBuiltins.BF("items.uses", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "items.uses(item_handle) → 剩余使用次数 (无次数机制=0)", line);
                var h = AsItem(a[0], "items.uses", line);
                try { return (long)ItemsFacade.UseCountGet(h.NeedItem(line)); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("items.uses 失败: " + e.Message, line); }
            });
            ns.Members["uses_max"] = PsBuiltins.BF("items.uses_max", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "items.uses_max(item_handle) → 使用次数上限 (无次数机制=0)", line);
                var h = AsItem(a[0], "items.uses_max", line);
                try { return (long)ItemsFacade.UseCountMax(h.NeedItem(line)); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("items.uses_max 失败: " + e.Message, line); }
            });
            ns.Members["use"] = PsBuiltins.BF("items.use", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 2, "items.use(item_handle[, n=1]) → bool (归零按注册配置销毁)", line);
                var h = AsItem(a[0], "items.use", line);
                int n = a.Count == 2 ? AsCount(a[1], "items.use", line) : 1;
                try { return ItemsFacade.UseCountUse(h.NeedItem(line), n); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("items.use 失败: " + e.Message, line); }
            });
            ns.Members["use_init"] = PsBuiltins.BF("items.use_init", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 4, "items.use_init(item_handle, max[, base_value=0[, value_per_use=0]]) → bool (给已有物品启用次数机制)", line);
                var h = AsItem(a[0], "items.use_init", line);
                int max = AsCount(a[1], "items.use_init", line);
                // v1.13.4: base_value/value_per_use 允许显式传 0(文档默认即 0, 语义=关闭按次折价)
                int baseVal = a.Count >= 3 ? AsCount0(a[2], "items.use_init", line) : 0;
                int perUse = a.Count >= 4 ? AsCount0(a[3], "items.use_init", line) : 0;
                try { return ItemsFacade.UseCountInit(h.NeedItem(line), max, baseVal, perUse); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("items.use_init 失败: " + e.Message, line); }
            });

            // ---- v1.42.0 (Items v0.9.18): 原生实例 tag 读取 (模组 BONUS_/TEMP_ 百分比等) ----
            // 容错语义: 聚合循环内逐件调用, 缺键/句柄失效/读取异常一律 null 不抛 (参数个数错仍抛)
            ns.Members["tag_get_int"] = PsBuiltins.BF("items.tag_get_int", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "items.tag_get_int(item_handle, key) → int|null (读实例整数 tag, modifiedState 优先回退 state; 缺键/异常=null)", line);
                try
                {
                    var h = AsItem(a[0], "items.tag_get_int", line);
                    var v = ItemsFacade.TagGetInt(h.NeedItem(line), PsValues.Fmt(a[1]));
                    return v.HasValue ? (long)v.Value : null;
                }
                catch { return null; }
            });
            ns.Members["tag_get_string"] = PsBuiltins.BF("items.tag_get_string", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "items.tag_get_string(item_handle, key) → string|null (读实例字符串 tag; 缺键/异常=null)", line);
                try
                {
                    var h = AsItem(a[0], "items.tag_get_string", line);
                    return ItemsFacade.TagGetString(h.NeedItem(line), PsValues.Fmt(a[1]));
                }
                catch { return null; }
            });

            // ---- M3: 物品目录(管理面板物品浏览器) ----
            ns.Members["catalog"] = PsBuiltins.BF("items.catalog", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 0, 0, "items.catalog() → {cats=[30 中文标签], total}", line);
                List<ItemsFacade.CatalogEntry> all;
                try { all = ItemsFacade.Catalog(); }
                catch (Exception e) { throw new PsRuntimeError("items.catalog 失败: " + e.Message, line); }
                var cats = new List<object>();
                foreach (var l in ItemsFacade.CatalogLabels) cats.Add(l);
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["cats"] = cats,
                    ["total"] = (long)(all?.Count ?? 0),
                };
            });
            ns.Members["browse"] = PsBuiltins.BF("items.browse", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 4, "items.browse(cat_idx -1=全部|0..29, 搜索词[, page=0[, page_size=14]]) → {total, page, pages, items}", line);
                long catIdx = AsLongArg(a[0], "items.browse", line);
                string search = a[1] as string ?? PsValues.Fmt(a[1]);
                long page = a.Count >= 3 ? AsLongArg(a[2], "items.browse", line) : 0;
                long pageSize = a.Count >= 4 ? AsLongArg(a[3], "items.browse", line) : 14;
                // v1.4.0: 上界用 CatalogLabels (含第 29 类"测试", 不在 CatalogKeys 目录枚举内)
                if (catIdx < -1 || catIdx >= ItemsFacade.CatalogLabels.Length)
                    throw new PsRuntimeError($"items.browse 的 cat_idx 须在 -1..{ItemsFacade.CatalogLabels.Length - 1}, 实为 {catIdx}", line);
                List<ItemsFacade.CatalogEntry> all;
                try { all = ItemsFacade.Catalog(); }
                catch (Exception e) { throw new PsRuntimeError("items.browse 失败: " + e.Message, line); }
                return ShopService.BrowseItems(all, catIdx, search, page, pageSize);
            });
            env.Define("items", ns, true, 0);
        }

        /// <summary>v1.16.0: 按 opts.To 路由发放落点 (null/inventory=背包后仓 GrantWithOpts;
        /// counter_out=门口外部柜台 GrantToCounter; counter_in=店内内部柜台 GrantToCounterMiddle)。</summary>
        private static bool RouteGive(string id, int count, ItemsFacade.ItemOpts opts)
        {
            switch (opts?.To)
            {
                case "counter_out": return ItemsFacade.GrantToCounter(id, count, opts);
                case "counter_in": return ItemsFacade.GrantToCounterMiddle(id, count, 0, opts);
                default: return ItemsFacade.GrantWithOpts(id, count, opts) != null;
            }
        }

        private static long AsLongArg(object v, string fn, int line)
        {
            if (v is long l) return l;
            if (v is double d) return (long)d;
            throw new PsRuntimeError($"{fn} 的数值参数须为数字, 实为 {PsValues.TypeName(v)}", line);
        }

        // ---- quality: 品质层 (打层/读层/定价系数) ----

        private static void RegisterQuality(PsEnv env)
        {
            var ns = new PsNamespace("quality");
            ns.Members["set"] = PsBuiltins.BF("quality.set", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 2, 2, "quality.set(item_handle, quality_id)", line);
                var h = AsItem(a[0], "quality.set", line);
                string qid = AsId(a[1], "quality.set", line);
                try
                {
                    var item = h.NeedItem(line);
                    string itemId = null;
                    try { itemId = item.identifier; } catch { }
                    return ItemsFacade.SetQuality(item, itemId, qid);
                }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("quality.set 失败: " + e.Message, line); }
            });
            ns.Members["get"] = PsBuiltins.BF("quality.get", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "quality.get(item_handle)", line);
                var h = AsItem(a[0], "quality.get", line);
                try { return ItemsFacade.GetQuality(h.NeedItem(line)); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("quality.get 失败: " + e.Message, line); }
            });
            ns.Members["price_factor"] = PsBuiltins.BF("quality.price_factor", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "quality.price_factor(item_handle)", line);
                var h = AsItem(a[0], "quality.price_factor", line);
                try { return (double)ItemsFacade.PriceFactor(h.NeedItem(line)); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("quality.price_factor 失败: " + e.Message, line); }
            });
            // v1.17.0: 运行期品质注册 (Items v0.9.9 RegisterQuality; tag 模式, 重复注册=覆盖)
            ns.Members["register"] = PsBuiltins.BF("quality.register", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "quality.register({id, display, tag?, price?, tier?, prefix?, mode?, category?, category_display?}) → bool — 注册品质层 (tag 空=自动派生 PSQ_*; price=售价系数; mode=\"feature\" 走原版 ItemFeature 定价管线)", line);
                if (a[0] is not Dictionary<string, object> cfg)
                    throw new PsRuntimeError($"quality.register 的参数须为 dict, 实为 {PsValues.TypeName(a[0])}", line);
                string qid = null, disp = null, tag = null, prefix = null, mode = null, cat = null, catDisp = null;
                float price = 1f; int tier = 0;
                if (cfg.TryGetValue("id", out var v) && v is string s1 && !string.IsNullOrWhiteSpace(s1)) qid = s1;
                if (qid == null) throw new PsRuntimeError("quality.register 缺 id (非空字符串)", line);
                if (cfg.TryGetValue("display", out v) && v is string s2 && !string.IsNullOrWhiteSpace(s2)) disp = s2;
                if (disp == null) throw new PsRuntimeError("quality.register 缺 display (显示名)", line);
                if (cfg.TryGetValue("tag", out v) && v is string s3 && !string.IsNullOrWhiteSpace(s3)) tag = s3;
                if (cfg.TryGetValue("prefix", out v) && v is string s4 && !string.IsNullOrWhiteSpace(s4)) prefix = s4;
                // v1.17.1: feature 模式透传 (原版 ItemFeature 定价管线, 交易全路径加价生效)
                if (cfg.TryGetValue("mode", out v) && v is string s5 && !string.IsNullOrWhiteSpace(s5)) mode = s5;
                if (cfg.TryGetValue("category", out v) && v is string s6 && !string.IsNullOrWhiteSpace(s6)) cat = s6;
                if (cfg.TryGetValue("category_display", out v) && v is string s7 && !string.IsNullOrWhiteSpace(s7)) catDisp = s7;
                if (cfg.TryGetValue("price", out v) && v != null)
                {
                    if (v is long pl) price = pl;
                    else if (v is double pd) price = (float)pd;
                    else throw new PsRuntimeError($"quality.register 的 price 须为数字, 实为 {PsValues.TypeName(v)}", line);
                }
                if (cfg.TryGetValue("tier", out v) && v != null)
                {
                    if (v is long tl) tier = (int)tl;
                    else if (v is double td) tier = (int)td;
                    else throw new PsRuntimeError($"quality.register 的 tier 须为数字, 实为 {PsValues.TypeName(v)}", line);
                }
                try { return ItemsFacade.RegisterQuality(qid, tag, disp, price, tier, prefix, mode, cat, catDisp); }
                catch (Exception e) { throw new PsRuntimeError("quality.register 失败: " + e.Message, line); }
            });
            env.Define("quality", ns, true, 0);
        }

        // ---- machine: 多夜加工进度/产出/全店检索 ----

        private static void RegisterMachine(PsEnv env)
        {
            var ns = new PsNamespace("machine");
            ns.Members["progress"] = PsBuiltins.BF("machine.progress", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "machine.progress(item_handle)", line);
                var h = AsItem(a[0], "machine.progress", line);
                try { return (double)ItemsFacade.MachineProgress(h.NeedItem(line)); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("machine.progress 失败: " + e.Message, line); }
            });
            ns.Members["producing"] = PsBuiltins.BF("machine.producing", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "machine.producing(item_handle)", line);
                var h = AsItem(a[0], "machine.producing", line);
                try { return ItemsFacade.MachineProducing(h.NeedItem(line)); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("machine.producing 失败: " + e.Message, line); }
            });
            ns.Members["find"] = PsBuiltins.BF("machine.find", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "machine.find(item_id)", line);
                string id = AsId(a[0], "machine.find", line);
                try
                {
                    var item = ItemsFacade.FindMachine(id);
                    return item == null ? null : (object)new PsItemHandle(item);
                }
                catch (Exception e) { throw new PsRuntimeError("machine.find 失败: " + e.Message, line); }
            });
            env.Define("machine", ns, true, 0);
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

        /// <summary>v1.13.4: 允许 0 的 count 变体 (use_init 的 base_value/value_per_use, 0=关闭按次折价)。</summary>
        private static int AsCount0(object v, string fn, int line)
        {
            long n = v is long l ? l : v is double d ? (long)d
                : throw new PsRuntimeError($"{fn} 的 count 须为数字, 实为 {PsValues.TypeName(v)}", line);
            if (n < 0 || n > 9999)
                throw new PsRuntimeError($"{fn} 的 count 须在 0..9999, 实为 {n}", line);
            return (int)n;
        }

        private static PsItemHandle AsItem(object v, string fn, int line)
        {
            if (v == null)
                throw new PsRuntimeError($"{fn} 的第一个参数是 null (items.find/machine.find 没找到? 请先判空)", line);
            if (v is PsItemHandle h) return h;
            if (v is PsHandle other)
                throw new PsRuntimeError($"{fn} 需要物品句柄, 实为 {other.Kind} 句柄", line);
            throw new PsRuntimeError($"{fn} 需要物品句柄 (items.find/machine.find 的返回值), 实为 {PsValues.TypeName(v)}", line);
        }
    }
}
