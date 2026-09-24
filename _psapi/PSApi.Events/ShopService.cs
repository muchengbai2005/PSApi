using System;
using System.Collections.Generic;
using Il2Cpp;
using PSApi.Items;

namespace PSApi.Events
{
    /// <summary>
    /// M3 商店查询服务 (shop.* 命名空间后端, NpcManager ManagerWindow 查询面移植):
    ///   Status()        — 状态总览 (距交租/队列长度/当前客户)
    ///   Attract()       — 商店吸引力与常客档位/生成率/预算加成阈值
    ///   CurrentClient() — 当前客户详情 (意图/现金/预算/收购清单/携带货物/展示区非拥有货物)
    ///   Queue()         — 今日待客队列 (名字/意图/现金/收购意愿/指针)
    /// 另有纯函数: IntentLabel(意图→中文标签, 与 ManagerWindow 逐字一致)、BrowseItems(目录过滤/搜索/分页)。
    /// 全部游戏访问包 try/catch: 不在对局 = Status 给 in_game=false, Attract/CurrentClient 给 null, Queue 给空表;
    /// 无头测试台只会走到这些兜底分支 (Il2Cpp 访问抛 TypeInitializationException 属 Exception, 可捕获)。
    /// </summary>
    internal static class ShopService
    {
        /// <summary>客户意图 → 中文标签 (与 NpcManager ManagerWindow.IntentLabel 逐字一致)。</summary>
        internal static string IntentLabel(StoreClient.ClientIntent i)
        {
            switch (i)
            {
                case StoreClient.ClientIntent.BUY: return "收购";
                case StoreClient.ClientIntent.SELL: return "出售";
                case StoreClient.ClientIntent.SELLNBUY: return "买卖兼有";
                case StoreClient.ClientIntent.INSPECTION: return "检查";
                case StoreClient.ClientIntent.DIALOGUE: return "对话";
                case StoreClient.ClientIntent.BARTER: return "以物易物";
                case StoreClient.ClientIntent.RENT: return "收租";
                case StoreClient.ClientIntent.LOAN_SHARK: return "放贷";
                case StoreClient.ClientIntent.SPECIAL: return "特殊";
                case StoreClient.ClientIntent.INFORMATION_DEALER: return "情报贩子";
                case StoreClient.ClientIntent.PROCUREMENT_OFFER: return "采购委托";
                case StoreClient.ClientIntent.PROCUREMENT_COLLECT: return "取货";
                case StoreClient.ClientIntent.WHOLESALE: return "批发";
                case StoreClient.ClientIntent.APPRAISAL_SERVICE: return "鉴定";
                case StoreClient.ClientIntent.GUNSMITH: return "枪匠";
                case StoreClient.ClientIntent.EXPEDITION: return "远征";
                default: return "未定义";
            }
        }

        // ==================== 状态总览 ====================

        internal static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["in_game"] = false;
            d["day_until_rent"] = 0L;
            d["queue"] = 0L;
            d["current_name"] = "";
            d["current_intent"] = "";
            try
            {
                var ps = PlayerStore.instance; // 静态字段(安全)
                if (ps is null) return d;
                d["in_game"] = true;
                try { d["day_until_rent"] = (long)ps.dayUntilRent; } catch { }
                try
                {
                    var mgr = ps.storeClientManager;
                    var stack = mgr is null ? null : mgr.clientStack;
                    if (stack != null) d["queue"] = (long)stack.Count;
                }
                catch { }
                try
                {
                    var sc = CurrentClient();
                    if (!(sc is null))
                    {
                        d["current_name"] = sc.displayName ?? "";
                        d["current_intent"] = IntentLabel(sc.clientIntent);
                    }
                }
                catch { }
            }
            catch { }
            return d;
        }

        // ==================== 商店吸引力 ====================

        /// <summary>吸引力可视化数据; 不在对局 = null。阈值静态字段读不到时给 -1。</summary>
        internal static Dictionary<string, object> Attract()
        {
            try
            {
                var ps = PlayerStore.instance;
                if (ps is null) return null;
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                int att = 0;
                try { att = ps.baseStoreAttractiveness; } catch { }
                d["value"] = (long)att;
                long chance = 0, normal = 0;
                try
                {
                    var mgr = ps.storeClientManager;
                    if (!(mgr is null))
                    {
                        try { chance = mgr.GetRandomClientSpawnChance(); } catch { }
                        try { normal = mgr.GetNormalCustomerCount(); } catch { }
                    }
                }
                catch { }
                d["spawn_chance"] = chance;
                d["normal_count"] = normal;
                d["t1"] = ReadThreshold(() => StoreClientManager.ONE_NORMAL_ATT_THRESHOLD);
                d["t2"] = ReadThreshold(() => StoreClientManager.TWO_NORMAL_ATT_THRESHOLD);
                d["t3"] = ReadThreshold(() => StoreClientManager.THREE_NORMAL_ATT_THRESHOLD);
                d["extra10"] = ReadThreshold(() => StoreClientManager.EXTRA_BUDGET_10_THRESHOLD);
                d["extra25"] = ReadThreshold(() => StoreClientManager.EXTRA_BUDGET_25_THRESHOLD);
                d["extra50"] = ReadThreshold(() => StoreClientManager.EXTRA_BUDGET_50_THRESHOLD);
                d["rich400"] = ReadThreshold(() => StoreClientManager.RICH_CUSTOMER_400_THRESHOLD);
                d["rich650"] = ReadThreshold(() => StoreClientManager.RICH_CUSTOMER_650_THRESHOLD);
                return d;
            }
            catch { return null; }
        }

        private static long ReadThreshold(Func<int> read)
        {
            try { return read(); } catch { return -1; }
        }

        // ==================== 当前客户详情 ====================

        /// <summary>当前客户 (店内 currentClientInstance, 兜底 lastVisitedClient); 无 = null。</summary>
        internal static StoreClient CurrentClient()
        {
            try
            {
                var ps = PlayerStore.instance;
                if (ps is null) return null;
                var inst = ps.currentClientInstance;
                if (!(inst is null) && !(inst.storeClient is null)) return inst.storeClient;
                inst = ps.lastVisitedClient;
                if (!(inst is null) && !(inst.storeClient is null)) return inst.storeClient;
            }
            catch { }
            return null;
        }

        /// <summary>当前客户详情 dict; 无当前客户/不在对局 = null。
        /// carried = 客户携带(gridInv 数据层); front = 展示区非玩家拥有货物。</summary>
        internal static Dictionary<string, object> CurrentClientDetail()
        {
            try
            {
                var sc = CurrentClient();
                if (sc is null) return null;
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                try { d["name"] = sc.displayName ?? "?"; } catch { d["name"] = "?"; }
                try { d["intent_key"] = sc.clientIntent.ToString(); d["intent"] = IntentLabel(sc.clientIntent); }
                catch { d["intent_key"] = "?"; d["intent"] = "?"; }
                try { d["cash"] = (long)sc.clientCash; } catch { d["cash"] = 0L; }
                try { d["has_budget"] = sc.useClientBudget; } catch { d["has_budget"] = false; }
                try { d["budget"] = (long)sc.clientBudget; } catch { d["budget"] = 0L; }
                try { d["faction"] = sc.clientFaction ?? ""; } catch { d["faction"] = ""; }
                d["buying"] = StrList(sc.clientBuyingIdList);
                d["tags"] = StrList(sc.clientBuyingTagList);
                d["carried"] = CarriedItems();
                d["front"] = FrontNonOwned(out long frontTotal);
                d["front_total"] = frontTotal;
                return d;
            }
            catch { return null; }
        }

        private static List<object> StrList(Il2CppSystem.Collections.Generic.List<string> list)
        {
            var r = new List<object>();
            try
            {
                if (list != null)
                    foreach (var s in list)
                        if (!string.IsNullOrEmpty(s)) r.Add(s);
            }
            catch { }
            return r;
        }

        /// <summary>当前客户实例携带货物 id 表 (gridInv 数据层)。</summary>
        private static List<object> CarriedItems()
        {
            var r = new List<object>();
            try
            {
                var ps = PlayerStore.instance;
                var inst = ps is null ? null : ps.currentClientInstance;
                var inv = inst is null ? null : inst.gridInv;
                var items = inv is null ? null : inv.items;
                if (items != null)
                    foreach (var it in items)
                    {
                        if (it is null) continue;
                        try { var id = it.identifier; if (!string.IsNullOrEmpty(id)) r.Add(id); } catch { }
                    }
            }
            catch { }
            return r;
        }

        /// <summary>展示区 (frontInv) 非玩家拥有货物 id 表; total = 非拥有总件数 (表最多列 20 条)。</summary>
        internal static List<object> FrontNonOwned(out long total)
        {
            var r = new List<object>();
            total = 0;
            try
            {
                var emp = EmporiumEntry.Instance;
                var inv = emp is null ? null : emp.frontInvinvElement;
                var items = inv is null ? null : inv.items;
                if (items != null)
                    foreach (var it in items)
                    {
                        if (it is null) continue;
                        try
                        {
                            if (InjectService.IsPlayerOwned(it)) continue;
                            total++;
                            if (r.Count < 20)
                            {
                                var id = it.identifier;
                                if (!string.IsNullOrEmpty(id)) r.Add(id);
                            }
                        }
                        catch { }
                    }
            }
            catch { }
            return r;
        }

        // ==================== 今日待客队列 ====================

        /// <summary>今日队列 (clientStack 顺序): {idx, name, intent, intent_key, cash, ptr, buying, tags}。
        /// ptr = StoreClient 指针 (inject.force_client 的定位键, 仅当日有效)。不在对局 = 空表。</summary>
        internal static List<object> Queue()
        {
            var r = new List<object>();
            try
            {
                var ps = PlayerStore.instance;
                var mgr = ps is null ? null : ps.storeClientManager;
                var stack = mgr is null ? null : mgr.clientStack;
                if (stack is null) return r;
                for (int i = 0; i < stack.Count; i++)
                {
                    StoreClient sc;
                    try { sc = stack[i]; } catch { continue; }
                    if (sc is null) continue;
                    var d = new Dictionary<string, object>(StringComparer.Ordinal);
                    d["idx"] = (long)i;
                    try { d["name"] = sc.displayName ?? "?"; } catch { d["name"] = "?"; }
                    try { d["intent_key"] = sc.clientIntent.ToString(); d["intent"] = IntentLabel(sc.clientIntent); }
                    catch { d["intent_key"] = "?"; d["intent"] = "?"; }
                    try { d["cash"] = (long)sc.clientCash; } catch { d["cash"] = 0L; }
                    try { d["ptr"] = sc.Pointer.ToInt64(); } catch { d["ptr"] = 0L; }
                    d["buying"] = StrList(sc.clientBuyingIdList);
                    d["tags"] = StrList(sc.clientBuyingTagList);
                    r.Add(d);
                }
            }
            catch { }
            return r;
        }

        /// <summary>按指针在今日队列里找客户 (inject.force_client 用); 找不到 = null。</summary>
        internal static StoreClient FindQueuedByPtr(long ptr)
        {
            try
            {
                var ps = PlayerStore.instance;
                var mgr = ps is null ? null : ps.storeClientManager;
                var stack = mgr is null ? null : mgr.clientStack;
                if (stack is null) return null;
                for (int i = 0; i < stack.Count; i++)
                {
                    StoreClient sc;
                    try { sc = stack[i]; } catch { continue; }
                    if (sc is null) continue;
                    try { if (sc.Pointer.ToInt64() == ptr) return sc; } catch { }
                }
            }
            catch { }
            return null;
        }

        // ==================== 物品目录浏览 (纯函数, 无头可测) ====================

        /// <summary>目录过滤/搜索/分页: catIdx -1=全部, 0..28=分类; search 大小写不敏感匹配 id/显示名;
        /// page 越界自动收敛。返回 {total, page, pages, items:[{id,name,value,cat}]}。</summary>
        internal static Dictionary<string, object> BrowseItems(List<ItemsFacade.CatalogEntry> all,
            long catIdx, string search, long page, long pageSize)
        {
            if (pageSize < 1) pageSize = 14;
            if (pageSize > 100) pageSize = 100;
            string needle = (search ?? "").Trim().ToLowerInvariant();
            var view = new List<ItemsFacade.CatalogEntry>();
            if (all != null)
                foreach (var e in all)
                {
                    if (e == null) continue;
                    if (catIdx >= 0 && e.Cat != catIdx) continue;
                    if (needle.Length > 0
                        && !(e.Name ?? "").ToLowerInvariant().Contains(needle)
                        && !(e.Id ?? "").ToLowerInvariant().Contains(needle))
                        continue;
                    view.Add(e);
                }
            long pages = Math.Max(1, (view.Count + pageSize - 1) / pageSize);
            if (page < 0) page = 0;
            if (page >= pages) page = pages - 1;
            long start = page * pageSize;
            long end = Math.Min(start + pageSize, view.Count);
            var items = new List<object>();
            for (long i = start; i < end; i++)
            {
                var e = view[(int)i];
                items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["id"] = e.Id,
                    ["name"] = e.Name,
                    ["value"] = e.Value,
                    ["cat"] = (long)e.Cat,
                });
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["total"] = (long)view.Count,
                ["page"] = page,
                ["pages"] = pages,
                ["items"] = items,
            };
        }
    }
}
