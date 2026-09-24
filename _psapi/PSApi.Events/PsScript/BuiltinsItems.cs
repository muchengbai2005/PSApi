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
                PsBuiltins.Need(a, 1, 3, "items.give(id[, count=1[, uses=0]]) → bool (uses>0 = 发放次数物品, 归零销毁)", line);
                string id = AsId(a[0], "items.give", line);
                int count = a.Count >= 2 ? AsCount(a[1], "items.give", line) : 1;
                int uses = 0;
                if (a.Count == 3)
                {
                    long uv = AsLongArg(a[2], "items.give", line);
                    if (uv < 1 || uv > 9999)
                        throw new PsRuntimeError($"items.give 的 uses 须在 1..9999, 实为 {uv}", line);
                    uses = (int)uv;
                }
                try { return ItemsFacade.Grant(id, count, uses) != null; }
                catch (Exception e) { throw new PsRuntimeError("items.give 失败: " + e.Message, line); }
            });
            // v0.9.2(Items)/v1.9.1(Events): 发放到店门口柜台(贴原版送礼路径), 归玩家所有; 不在对局 = false
            ns.Members["give_counter"] = PsBuiltins.BF("items.give_counter", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 3, "items.give_counter(id[, count=1[, uses=0]]) → bool (摆到店门口柜台, 归玩家; 不在对局=false)", line);
                string id = AsId(a[0], "items.give_counter", line);
                int count = a.Count >= 2 ? AsCount(a[1], "items.give_counter", line) : 1;
                int uses = 0;
                if (a.Count == 3)
                {
                    long uv = AsLongArg(a[2], "items.give_counter", line);
                    if (uv < 1 || uv > 9999)
                        throw new PsRuntimeError($"items.give_counter 的 uses 须在 1..9999, 实为 {uv}", line);
                    uses = (int)uv;
                }
                try { return ItemsFacade.GrantToCounter(id, count, uses); }
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
            // U4: 消耗物品 (从所在库存取出并销毁; 与 RecipeService 探测物回收同路径)
            ns.Members["consume"] = PsBuiltins.BF("items.consume", (itp, a, line) =>
            {
                PsBuiltins.Need(a, 1, 1, "items.consume(item_handle) → bool", line);
                var h = AsItem(a[0], "items.consume", line);
                try { return ItemsFacade.ConsumeItem(h.NeedItem(line)); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("items.consume 失败: " + e.Message, line); }
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
                int baseVal = a.Count >= 3 ? AsCount(a[2], "items.use_init", line) : 0;
                int perUse = a.Count >= 4 ? AsCount(a[3], "items.use_init", line) : 0;
                try { return ItemsFacade.UseCountInit(h.NeedItem(line), max, baseVal, perUse); }
                catch (PsRuntimeError) { throw; }
                catch (Exception e) { throw new PsRuntimeError("items.use_init 失败: " + e.Message, line); }
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
