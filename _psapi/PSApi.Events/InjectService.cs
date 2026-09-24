using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using PSApi.Events.PsScript;

namespace PSApi.Events
{
    /// <summary>
    /// M1 物品池注入服务 (NpcManager Injectors.cs 四通道移植为脚本 API, 命名空间 inject.*):
    ///   inject.buy_list(id|[ids]|fn(client))          — NPC 收购池: 入队(customer_generated) + 交易时(OpenUI Prefix)补单
    ///   inject.sell_shelf(id, count=1, chance=1, {uses=N} | fn) — SELL 客户货架: 客户 main 对话链末行播完
    ///                                                   (DialogUIManager.OnCurrentTextDisplayed Postfix, 与原版摆货 endAction 同帧)
    ///                                                   原生摆货 AddDirectSellingItemToTable, OpenUI Postfix 兜底,
    ///                                                   每客户实例一次(拖走不补, 防无限刷货);
    ///                                                   v1.10.0: count 支持 "min-max" 区间(上限 5, 每次注入独立掷)
    ///   inject.loot_pool(表名, id, 权重)              — v1.10.0 原版 LootTable 池注入(LootPoolService, 无 patch)
    ///   inject.doctor(id, count=1, {uses=N})          — 夜间博士商店: Visit 后等 frontInv 填充完成轮询注入, 每次访问一次
    ///   inject.barter(id, count=1)                    — 野外商人: ShowBarter 路径注 barterSellInventory, 每商人一次
    /// 与 NpcManager 的语义偏差: 无面板/开关/标记系统(脚本即配置); 脚本注册 NPC(npc.register)不收全局清单
    /// (其清单由脚本全权定义, 同 NpcManager 跳过规则客户); sell_shelf 对已有 sell_items 计划的客户不叠加。
    /// id 归一化同 ItemsFacade: "game:xxx" = 原版裸 id, 其他原样。
    /// </summary>
    internal sealed class InjectService
    {
        // ---------- 注册条目 ----------

        internal sealed class BuyEntry
        {
            internal string PackId;
            internal List<string> Ids;          // 静态清单
            internal PsCallable Fn;             // 动态: fn(client) → id/数组
            internal Interpreter Itp;
            internal bool Enabled = true;       // M3: 运行时开关(inject.tune, 不进存档)
        }

        internal sealed class ShelfEntry
        {
            internal string PackId;
            internal List<NpcService.SellSpec> Items; // 静态 (count/chance 注册时定)
            internal PsCallable Fn;                   // 动态: fn(client) → 同 npc.register sell_items 格式数组
            internal Interpreter Itp;
            internal bool Enabled = true;       // M3: 运行时开关
            internal int CountOverride;         // M3: >0 时覆盖静态条目的 count (函数形式不生效)
            internal int Uses;                  // v1.9.0: opts.uses — 动态函数形式也统一覆盖
        }

        internal sealed class StockEntry
        {
            internal string Id;
            internal int Count;
            internal bool Enabled = true;       // M3: 运行时开关
            internal int CountOverride;         // M3: >0 时覆盖 Count
            internal int Uses;                  // v1.9.0: opts.uses — 创建实例时启用次数机制
        }

        // 收购/出售意图集 (同 Injectors.cs)
        private static readonly HashSet<StoreClient.ClientIntent> BuyingIntents = new HashSet<StoreClient.ClientIntent>
        {
            StoreClient.ClientIntent.BUY, StoreClient.ClientIntent.SELLNBUY,
            StoreClient.ClientIntent.WHOLESALE, StoreClient.ClientIntent.PROCUREMENT_OFFER,
        };
        private static readonly HashSet<StoreClient.ClientIntent> SellingIntents = new HashSet<StoreClient.ClientIntent>
        {
            StoreClient.ClientIntent.SELL, StoreClient.ClientIntent.SELLNBUY,
        };

        private readonly MelonLogger.Instance _logger;
        private readonly NpcService _npcs;
        private readonly Random _rng = new Random();
        private readonly List<BuyEntry> _buy = new List<BuyEntry>();
        private readonly List<ShelfEntry> _shelf = new List<ShelfEntry>();
        private readonly List<StockEntry> _doctor = new List<StockEntry>();
        private readonly List<StockEntry> _barter = new List<StockEntry>();
        private readonly HashSet<string> _warnedMissing = new HashSet<string>(StringComparer.Ordinal);

        private long _shelfSessionKey = -1;             // 货架注入会话守卫(每客户一次, 拖走不补)
        private readonly HashSet<long> _injectedTraders = new HashSet<long>(); // 野外商人按指针去重
        private bool _doctorActive;                     // VisitUpgradeMerchant ~ LeaveUpgradeMerchant
        private bool _doctorDone;
        private float _doctorPoll;

        internal InjectService(MelonLogger.Instance logger, NpcService npcs)
        {
            _logger = logger;
            _npcs = npcs;
        }

        /// <summary>测试/诊断用: 各通道已注册条目数。</summary>
        internal int BuyCount => _buy.Count;
        internal int ShelfCount => _shelf.Count;
        internal int DoctorCount => _doctor.Count;
        internal int BarterCount => _barter.Count;

        // ==================== M3: 注册查看 + 运行时调节 (管理面板) ====================

        /// <summary>四通道条目平铺描述 (顺序固定 buy→shelf→doctor→barter, idx 即注入序号, 当次会话内稳定)。
        /// 每行: {idx, channel, pack, id(多 id 逗号连/函数="(动态函数)"), count, chance, dynamic, enabled, uses}。</summary>
        internal List<object> Describe()
        {
            var r = new List<object>();
            int idx = 0;
            foreach (var e in _buy)
                r.Add(Row(idx++, "buy_list", e.PackId,
                    e.Fn != null ? "(动态函数)" : string.Join(",", e.Ids), 0, 0, e.Fn != null, e.Enabled));
            foreach (var e in _shelf)
            {
                if (e.Fn != null) { r.Add(Row(idx++, "sell_shelf", e.PackId, "(动态函数)", 0, 0, true, e.Enabled, e.Uses)); continue; }
                var s = e.Items[0];
                r.Add(Row(idx++, "sell_shelf", e.PackId, s.Id,
                    e.CountOverride > 0 ? e.CountOverride : s.Count, (long)(s.Chance * 100), false, e.Enabled, s.Uses, s.CountMax));
            }
            foreach (var e in _doctor)
                r.Add(Row(idx++, "doctor", null, e.Id, e.CountOverride > 0 ? e.CountOverride : e.Count, 100, false, e.Enabled, e.Uses));
            foreach (var e in _barter)
                r.Add(Row(idx++, "barter", null, e.Id, e.CountOverride > 0 ? e.CountOverride : e.Count, 100, false, e.Enabled, e.Uses));
            return r;
        }

        private static Dictionary<string, object> Row(int idx, string channel, string pack, string id,
            long count, long chancePct, bool dynamic, bool enabled, int uses = 0, long countMax = 0)
            => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["idx"] = (long)idx,
                ["channel"] = channel,
                ["pack"] = pack ?? "",
                ["id"] = id,
                ["count"] = count,
                ["count_max"] = countMax, // v1.10.0: >count = "min-max" 数量区间(0=固定)
                ["chance"] = chancePct,
                ["dynamic"] = dynamic,
                ["enabled"] = enabled,
                ["uses"] = (long)uses,
            };

        /// <summary>四通道条目总数(inject.list 的 loot_pool 行接在此后编号)。</summary>
        internal int EntryCount => _buy.Count + _shelf.Count + _doctor.Count + _barter.Count;

        /// <summary>运行时调节: enabled 开关 / count 覆盖 (1..99, 0=恢复注册值)。idx 越界 = false。</summary>
        internal bool Tune(long idx, bool? enabled, long? count, int line)
        {
            object entry = EntryAt(idx);
            if (entry == null) return false;
            if (count.HasValue && count.Value != 0 && (count.Value < 1 || count.Value > 99))
                throw new PsRuntimeError($"inject.tune 的 count 须在 1..99(0=恢复注册值), 实为 {count.Value}", line);
            switch (entry)
            {
                case BuyEntry b:
                    if (enabled.HasValue) b.Enabled = enabled.Value;
                    break;
                case ShelfEntry s:
                    if (enabled.HasValue) s.Enabled = enabled.Value;
                    if (count.HasValue) s.CountOverride = (int)count.Value;
                    break;
                case StockEntry st:
                    if (enabled.HasValue) st.Enabled = enabled.Value;
                    if (count.HasValue) st.CountOverride = (int)count.Value;
                    break;
            }
            return true;
        }

        private object EntryAt(long idx)
        {
            long i = idx;
            if (i < 0) return null;
            if (i < _buy.Count) return _buy[(int)i];
            i -= _buy.Count;
            if (i < _shelf.Count) return _shelf[(int)i];
            i -= _shelf.Count;
            if (i < _doctor.Count) return _doctor[(int)i];
            i -= _doctor.Count;
            if (i < _barter.Count) return _barter[(int)i];
            return null;
        }

        /// <summary>重置注入记录 (NpcManager ResetTracking 平移): 货架会话键/野外商人去重/博士注入标记复位,
        /// 之后客户将重新注入。</summary>
        internal void ResetTracking()
        {
            _shelfSessionKey = -1;
            _injectedTraders.Clear();
            _doctorDone = false;
            PsApi.Log(_logger, "[inject] 注入记录已重置: 客户将重新注入");
        }

        /// <summary>给当前客户手动补货 (NpcManager RestockCurrentClient 平移): 买池补单 + 货架强制摆货
        /// (无视会话守卫)。返回注入件数(买池条目数+货架件数); 无当前客户 = 0。</summary>
        internal int RestockCurrent(int line)
        {
            var sc = ResolveTradeClient();
            if (sc is null) return 0;
            int n = InjectBuyingList(sc, "手动补货");
            n += StockShelf(sc, null, "手动补货", force: true);
            return n;
        }

        /// <summary>对今日队列中 ptr 指定的客户立即注入 (买池+货架, 无视会话守卫与注册意图过滤之外的一切);
        /// 客户不在队列 = false。M3 面板"补单"按钮用。</summary>
        internal bool ForceClient(long ptr)
        {
            var sc = ShopService.FindQueuedByPtr(ptr);
            if (sc is null) return false;
            InjectBuyingList(sc, "面板指定");
            StockShelf(sc, null, "面板指定", force: true);
            return true;
        }

        /// <summary>id 归一化 (同 ItemsFacade.NormalizeId, 本地副本避免跨模块耦合)。</summary>
        internal static string NormalizeId(string id)
        {
            id = (id ?? "").Trim();
            return id.StartsWith("game:", StringComparison.OrdinalIgnoreCase) ? id.Substring(5) : id;
        }

        // ==================== 脚本面注册 (加载期, 纯托管) ====================

        internal void AddBuyList(string packId, object raw, Interpreter itp, int line)
        {
            if (raw is PsCallable fn)
            {
                _buy.Add(new BuyEntry { PackId = packId, Fn = fn, Itp = itp });
                return;
            }
            var ids = ParseIdList(raw, "inject.buy_list", line);
            if (ids.Count > 0) _buy.Add(new BuyEntry { PackId = packId, Ids = ids });
        }

        internal void AddSellShelf(string packId, object raw, long countMin, long countMax, double chance, int uses, Interpreter itp, int line)
        {
            if (raw is PsCallable fn)
            {
                _shelf.Add(new ShelfEntry { PackId = packId, Fn = fn, Itp = itp, Uses = uses });
                return;
            }
            string id = raw as string;
            if (string.IsNullOrWhiteSpace(id))
                throw new PsRuntimeError($"inject.sell_shelf 的第一参须为物品 id 或函数, 实为 {PsValues.TypeName(raw)}", line);
            // v1.10.0: count 支持 "min-max" 区间(每次注入独立掷), 上限 5
            if (countMin < 1 || countMin > 5 || countMax > 5)
                throw new PsRuntimeError($"inject.sell_shelf 的 count 须在 1..5(或 \"min-max\" 区间), 实为 {countMin}{(countMax > 0 ? "-" + countMax : "")}", line);
            if (chance <= 0 || chance > 1)
                throw new PsRuntimeError($"inject.sell_shelf 的 chance 须在 (0,1], 实为 {chance}", line);
            _shelf.Add(new ShelfEntry
            {
                PackId = packId,
                Uses = uses,
                Items = new List<NpcService.SellSpec> { new NpcService.SellSpec { Id = NormalizeId(id), Count = countMin, CountMax = countMax, Chance = chance, Uses = uses } },
            });
        }

        internal void AddStock(List<StockEntry> target, object raw, long count, int uses, string api, int line)
        {
            string id = raw as string;
            if (string.IsNullOrWhiteSpace(id))
                throw new PsRuntimeError($"{api} 的第一参须为物品 id 字符串, 实为 {PsValues.TypeName(raw)}", line);
            if (count < 1 || count > 99)
                throw new PsRuntimeError($"{api} 的 count 须在 1..99, 实为 {count}", line);
            target.Add(new StockEntry { Id = NormalizeId(id), Count = (int)count, Uses = uses });
        }

        internal void AddDoctor(string packId, object raw, long count, int uses, int line) => AddStock(_doctor, raw, count, uses, "inject.doctor", line);
        internal void AddBarter(string packId, object raw, long count, int line) => AddStock(_barter, raw, count, 0, "inject.barter", line);

        private static List<string> ParseIdList(object raw, string api, int line)
        {
            var ids = new List<string>();
            if (raw is string one)
            {
                if (!string.IsNullOrWhiteSpace(one)) ids.Add(NormalizeId(one));
                return ids;
            }
            if (raw is List<object> many)
            {
                foreach (var v in many)
                {
                    var s = NormalizeId(PsValues.Fmt(v));
                    if (!string.IsNullOrWhiteSpace(s)) ids.Add(s);
                }
                return ids;
            }
            throw new PsRuntimeError($"{api} 的参数须为物品 id / id 数组 / 函数, 实为 {PsValues.TypeName(raw)}", line);
        }

        /// <summary>订阅 customer_generated (买池入队注入)。</summary>
        internal void Subscribe(EventBus bus)
        {
            bus.Subscribe("psapi.customer.generated", OnCustomerGenerated, owner: "psapi.events.inject");
        }

        // ==================== 1) 买池 ====================

        private void OnCustomerGenerated(object payload)
        {
            try
            {
                if (payload is Dictionary<string, object> d
                    && d.TryGetValue("client", out var c) && c is PsClientHandle h)
                    InjectBuyingList(h.Raw, "入队");
            }
            catch (Exception e) { PsApi.Warn(_logger, "inject buy_list(入队): " + e.Message); }
        }

        /// <summary>向客户的收购清单追加注册 id (去重)。脚本注册 NPC 跳过(其清单由脚本全权定义)。返回注入条数。</summary>
        internal int InjectBuyingList(StoreClient sc, string from)
        {
            if (_buy.Count == 0 || sc is null) return 0;
            try
            {
                if (!BuyingIntents.Contains(sc.clientIntent)) return 0;
                string cid = null;
                try { cid = sc.identifier; } catch { }
                if (cid != null && _npcs != null && _npcs.FindEntry(cid) != null) return 0;
                var list = sc.clientBuyingIdList;
                if (list is null) return 0;

                int added = 0;
                foreach (var e in _buy)
                {
                    if (!e.Enabled) continue; // M3: 运行时禁用
                    foreach (var id in ResolveBuyIds(e, sc))
                    {
                        // v1.11.0: 执行点存在性校验(对局内 DirectoryMaster 就绪; 无效 id 跳过+告警一次)
                        if (!NpcService.ItemExistsRuntime(id)) { WarnMissingOnce(id, "buy_list"); continue; }
                        if (!list.Contains(id)) { list.Add(id); added++; }
                    }
                }
                if (added > 0)
                    PsApi.Log(_logger, $"[inject] 买池 {sc.displayName}({sc.clientIntent}) +{added} via {from}");
                return added;
            }
            catch (Exception e) { PsApi.Warn(_logger, "inject buy_list: " + e.Message); return 0; }
        }

        private List<string> ResolveBuyIds(BuyEntry e, StoreClient sc)
        {
            if (e.Fn == null) return e.Ids;
            try
            {
                e.Itp.BeginRun();
                var ret = e.Itp.CallCallable(e.Fn, new List<object> { new PsClientHandle(sc) }, 0);
                return ParseIdList(ret, "inject.buy_list(fn 返回值)", 0);
            }
            catch (Exception ex)
            {
                PsApi.Warn(_logger, $"inject.buy_list 函数出错(包 {e.PackId}): {ex.Message}");
                return new List<string>();
            }
        }

        // ==================== 2) SELL 客户货架 (OpenUI 路径) ====================

        /// <summary>OpenUI Prefix: 交易时买池补单 (NpcManager 同款第二时机, 覆盖非生成路径入队的客户)。</summary>
        internal void OnTradeUIOpening()
        {
            var sc = ResolveTradeClient();
            if (!(sc is null)) InjectBuyingList(sc, "OpenUI");
        }

        /// <summary>客户 main 对话链末行播完(DialogUIManager.OnCurrentTextDisplayed Postfix, Patch_InjectDialogueEnded 转调):
        /// 全局货架主摆货时机, 对齐原版"链末行 endAction 执行时刻摆货"(49 处 SetEndAction 挂摆货 lambda)。
        /// 快退链 + 归属走查在 IsMainChainTail; 会话键防双份, OpenUI 兜底不变。</summary>
        internal void OnClientMainDialogueEnded(Dialogue d)
        {
            if (_shelf.Count == 0) return;
            if (d is null) return;
            var sc = ResolveTradeClient();
            if (sc is null) return;
            if (!SellingIntents.Contains(sc.clientIntent)) return;
            if (!IsMainChainTail(sc, d)) return;
            StockShelf(sc, null, "对话结束");
        }

        /// <summary>d 是否为 sc 的 main 对话链末行。needEndAction=true(全局货架)还要求带 endAction
        /// (镜像 OnCurrentTextDisplayed 原生调用守卫, 对齐原版摆货 lambda 执行帧); 脚本 NPC sell_items
        /// 传 false(SELL 型链尾无 endAction, 链尾=话说完即可)。指针走查(≤16 步)确认归属当前客户。</summary>
        internal static bool IsMainChainTail(StoreClient sc, Dialogue d, bool needEndAction = true)
        {
            try
            {
                if (needEndAction && d.endAction is null) return false;
                if (!(d.nextDialogue is null)) return false;
                var cur = sc.mainDialogue;
                for (int i = 0; i < 16 && !(cur is null); i++)
                {
                    if (cur.Pointer == d.Pointer) return true;
                    cur = cur.nextDialogue;
                }
            }
            catch { }
            return false;
        }

        /// <summary>OpenUI Postfix: 全局货架兜底(极端路径跳过对话末行钩子时补摆; 会话键防双份)。
        /// 有 sell_items 计划的脚本客户跳过(NpcService.OnTradeUIOpened 负责它们, 全局清单不叠加)。</summary>
        internal void OnTradeUIOpened(NegociationUIManager ui)
        {
            var sc = ResolveTradeClient();
            if (!(sc is null)) StockShelf(sc, ui, "OpenUI兜底");
        }

        /// <summary>共享摆货: 每客户实例一次(_shelfSessionKey), 原生 API 摆进 frontInv/backInv。
        /// force=true (M3 面板手动补货) 无视会话守卫。返回摆上的件数。</summary>
        private int StockShelf(StoreClient sc, NegociationUIManager ui, string where, bool force = false)
        {
            if (_shelf.Count == 0) return 0;
            if (sc is null) return 0;
            try
            {
                if (!SellingIntents.Contains(sc.clientIntent)) return 0;
                string cid = null;
                try { cid = sc.identifier; } catch { }
                if (cid != null && _npcs != null && _npcs.HasSellPlan(sc)) return 0; // 脚本客户货物全权
                long key = sc.Pointer.ToInt64();
                if (!force && key == _shelfSessionKey) return 0;

                var ps = PlayerStore.instance; // 静态字段(安全); Instance 属性会懒创建
                if (ps is null) return 0; // 商店不可用: 不标记会话, 留待下次

                var plan = new List<NpcService.SellSpec>();
                foreach (var e in _shelf)
                {
                    if (!e.Enabled) continue; // M3: 运行时禁用
                    if (e.Fn != null)
                    {
                        try
                        {
                            e.Itp.BeginRun();
                            var ret = e.Itp.CallCallable(e.Fn, new List<object> { new PsClientHandle(sc) }, 0);
                            foreach (var s in NpcService.ParseSellItems(ret, 0))
                            {
                                if (e.Uses > 0) s.Uses = e.Uses; // opts.uses 对动态函数形式统一覆盖
                                if (s.Chance >= 1.0 || _rng.NextDouble() < s.Chance)
                                    plan.Add(new NpcService.SellSpec { Id = s.Id, Count = NpcService.RollCount(s, _rng), Chance = s.Chance, Uses = s.Uses });
                            }
                        }
                        catch (Exception ex) { PsApi.Warn(_logger, $"inject.sell_shelf 函数出错(包 {e.PackId}): {ex.Message}"); }
                    }
                    else
                        foreach (var s in e.Items)
                            if (s.Chance >= 1.0 || _rng.NextDouble() < s.Chance)
                                // v1.10.0: 数量区间每次注入独立掷(上限 5); tune 覆盖优先
                                plan.Add(e.CountOverride > 0
                                    ? new NpcService.SellSpec { Id = s.Id, Count = e.CountOverride, Chance = s.Chance, Uses = s.Uses }
                                    : new NpcService.SellSpec { Id = s.Id, Count = NpcService.RollCount(s, _rng), Chance = s.Chance, Uses = s.Uses });
                }
                if (plan.Count == 0) { _shelfSessionKey = key; return 0; }

                // 并集计数(展示区 ∪ 后台, 排除玩家拥有) 只补差额 (v1.8.0 防双份)
                var have = CountNonOwnedUnion();
                int n = 0;
                foreach (var s in plan)
                {
                    have.TryGetValue(s.Id, out int h);
                    for (int i = h; i < s.Count; i++)
                    {
                        var item = CreateClientItem(s.Id, "sell_shelf", s.Uses);
                        if (item is null) break;
                        try { ps.AddDirectSellingItemToTable(item, false, false, false, 0); n++; }
                        catch (Exception ex) { PsApi.Warn(_logger, $"[inject] 摆货失败 {s.Id}: {ex.Message}"); break; }
                    }
                }
                _shelfSessionKey = key;
                if (n > 0)
                {
                    PsApi.Log(_logger, $"[inject] 货架 {sc.displayName}({sc.clientIntent}) 原生摆货 +{n} 件({where}, 每客户一次, 拖走不补)");
                    if (ui != null)
                    {
                        try { ui.DispatchUpdateForCurrentIntent(); } catch { }
                        try { ui.RefreshUI(); } catch { }
                    }
                }
                return n;
            }
            catch (Exception e) { PsApi.Warn(_logger, "inject sell_shelf: " + e.Message); return 0; }
        }

        /// <summary>当前交易客户: 店内当前客户, 兜底最近访问客户(夜间商人)。</summary>
        private static StoreClient ResolveTradeClient()
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

        // ==================== 3) 夜间博士商店 ====================

        internal void OnDoctorVisit()
        {
            _doctorActive = true;
            _doctorDone = false;
            PsApi.Log(_logger, "[inject] 玩家进入博士商店");
        }

        internal void OnDoctorLeave() => _doctorActive = false;

        /// <summary>Plugin.OnUpdate 驱动 (0.5s 节流): 等 frontInv 被场景填充(非空)后注入一次。</summary>
        internal void Poll()
        {
            if (!_doctorActive || _doctorDone || _doctor.Count == 0) return;
            _doctorPoll += UnityEngine.Time.deltaTime;
            if (_doctorPoll < 0.5f) return;
            _doctorPoll = 0f;
            try
            {
                var emp = EmporiumEntry.Instance;
                var inv = emp is null ? null : emp.frontInvinvElement;
                if (inv is null) return;
                var items = inv.items;
                if (items is null || items.Count == 0) return; // 场景尚未填充

                var have = CountNonOwnedUnion();
                int n = 0;
                foreach (var e in _doctor)
                {
                    if (!e.Enabled) continue; // M3: 运行时禁用
                    int want = e.CountOverride > 0 ? e.CountOverride : e.Count;
                    have.TryGetValue(e.Id, out int c);
                    for (int i = c; i < want; i++)
                    {
                        var item = CreateClientItem(e.Id, "doctor", e.Uses);
                        if (item is null) break;
                        if (PlaceItemInGrid(inv, item)) n++;
                        else break;
                    }
                }
                _doctorDone = true;
                if (n > 0) PsApi.Log(_logger, $"[inject] 博士商店展示区(frontInv) +{n} 件");
            }
            catch (Exception e)
            {
                _doctorDone = true; // 出错不再重试, 防刷屏
                PsApi.Err(_logger, "inject doctor: " + e.Message);
            }
        }

        // ==================== 4) 野外商人 (ShowBarter 路径) ====================

        internal void InjectBarterShop(GameCharacterItem gci, OverlayHandler oh)
        {
            if (_barter.Count == 0 || gci is null) return;
            try
            {
                long key = gci.Pointer.ToInt64();
                if (_injectedTraders.Contains(key)) return;
                _injectedTraders.Add(key);

                var sellInv = gci.barterSellInventory;
                if (sellInv is null) return;
                var have = new HashSet<string>(StringComparer.Ordinal);
                var items = sellInv.items;
                if (!(items is null))
                    foreach (var it in items)
                    {
                        if (it is null) continue;
                        try { var iid = it.identifier; if (!string.IsNullOrEmpty(iid)) have.Add(iid); } catch { }
                    }

                int n = 0;
                foreach (var e in _barter)
                {
                    if (!e.Enabled) continue; // M3: 运行时禁用
                    if (have.Contains(e.Id)) continue;
                    int want = e.CountOverride > 0 ? e.CountOverride : e.Count;
                    for (int i = 0; i < want; i++)
                    {
                        GameItem item = null;
                        try { item = DirectoryMaster.Item(e.Id, true); } catch { }
                        if (item is null) { WarnMissingOnce(e.Id, "barter"); break; }
                        try { if (((GameInventory)sellInv).UncheckedAccept(item)) n++; } catch { break; }
                    }
                }
                if (n > 0)
                {
                    string tn;
                    try { tn = gci.name; } catch { tn = "?"; }
                    PsApi.Log(_logger, $"[inject] 野外商人 {tn} 货架 +{n} 件");
                    try { sellInv.Validate(true); } catch { }
                    try { if (!(oh is null)) oh.UpdateBarterImmediately(); } catch { }
                }
            }
            catch (Exception e) { PsApi.Warn(_logger, "inject barter: " + e.Message); }
        }

        // ==================== 通用工具 (Injectors.cs 移植) ====================

        /// <summary>客户货物实例: isOwned=false(交易UI的 GetNonOwnedBackInvItems 认这个状态)。
        /// 未注册 = 跳过+统一格式告警一次(v1.11.0: 返回 null 的静默路径也补上, 版本更新删 id 不再无感)。
        /// uses > 0 = 启用次数机制(原生 UseCountHelper 归零销毁, 无次数折算价; 与 ItemsFacade.Grant 同款)。</summary>
        private GameItem CreateClientItem(string id, string channel, int uses = 0)
        {
            GameItem item = null;
            try { item = DirectoryMaster.Item(id, false); }
            catch { WarnMissingOnce(id, channel); return null; }
            if (item is null) { WarnMissingOnce(id, channel); return null; }
            if (uses > 0)
            {
                try { Items.ItemsFacade.UseCountInit(item, uses, 0, 0); }
                catch (Exception e) { PsApi.Warn(_logger, $"[inject] {channel}: {id} 次数初始化失败: {e.Message}"); }
            }
            return item;
        }

        /// <summary>v1.11.0 统一告警格式(与 NpcService/LootPoolService/ItemsFacade 同款): 执行点调用,
        /// DirectoryMaster 对局内才就绪。channel 即来源("sell_shelf"/"doctor"/"barter"/"buy_list")。</summary>
        private void WarnMissingOnce(string id, string channel)
        {
            if (_warnedMissing.Add(channel + "|" + id))
                PsApi.Warn(_logger, $"[psapi] 物品 id '{id}' 在当前版本不存在, 已跳过 (来源: inject.{channel})");
        }

        /// <summary>槽位放置(v1.7.0 路径): 优先原生接受(TryAcceptOnce 带UI通知) → AcceptUnchecked 落位 → UncheckedAccept 保底。</summary>
        private static bool PlaceItemInGrid(GameGridInventory inv, GameItem item)
        {
            if (inv is null || item is null) return false;
            SlotMarker slot = null;
            try { slot = inv.TryFindOneValidInventorySlot(item, false); } catch { }
            if (!(slot is null))
            {
                try { if (slot.TryAcceptOnce(-1) > 0) return true; } catch { }
                try { slot.AcceptUnchecked(); return true; } catch { }
            }
            try { return ((GameInventory)inv).UncheckedAccept(item); }
            catch { return false; }
        }

        /// <summary>玩家所有权: IS_OWNED_TAG 且 valueEnabled (交易UI官方过滤同款)。M3 ShopService 复用。</summary>
        internal static bool IsPlayerOwned(GameItem it)
        {
            try
            {
                var dict = it.state is null ? null : it.state.dict;
                if (dict is null) return false;
                foreach (var kv in dict)
                {
                    try
                    {
                        if (kv.Key == "IS_OWNED_TAG" && !(kv.Value is null) && kv.Value.valueEnabled) return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>展示区 ∪ 后台 非玩家拥有物品 id→数量 并集计数(数据层扫描, 不用 UI 过滤结果)。</summary>
        private static Dictionary<string, int> CountNonOwnedUnion()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var emp = EmporiumEntry.Instance;
                if (emp is null) return map;
                AccumulateNonOwned(emp.frontInvinvElement, map);
                AccumulateNonOwned(emp.backInvinvElement, map);
            }
            catch { }
            return map;
        }

        private static void AccumulateNonOwned(GameGridInventory inv, Dictionary<string, int> map)
        {
            if (inv is null) return;
            Il2CppSystem.Collections.Generic.List<GameItem> items = null;
            try { items = inv.items; } catch { }
            if (items is null) return;
            foreach (var it in items)
            {
                if (it is null) continue;
                try
                {
                    if (IsPlayerOwned(it)) continue;
                    var iid = it.identifier;
                    if (string.IsNullOrEmpty(iid)) continue;
                    map.TryGetValue(iid, out int c);
                    map[iid] = c + 1;
                }
                catch { }
            }
        }

        // ==================== 补丁 (点位均经 NpcManager 生产验证 + ISIL 核字节) ====================

        /// <summary>交易 UI 打开: Prefix 买池补单; Postfix 全局货架摆货 (OpenUI 为大方法, NpcManager v1.8+ 生产验证点位)。</summary>
        [HarmonyPatch(typeof(NegociationUIManager), "OpenUI")]
        internal static class Patch_InjectOpenTrade
        {
            private static void Prefix()
            {
                try { EventsPlugin.Instance?.Inject?.OnTradeUIOpening(); } catch { }
            }

            private static void Postfix(NegociationUIManager __instance)
            {
                try { EventsPlugin.Instance?.Inject?.OnTradeUIOpened(__instance); } catch { }
            }
        }

        /// <summary>main 对话链每行播完回调(~350 行 ISIL, 安全; ExecuteEndAction 本体仅 ~30 字节尾调用, 触 40 字节红线不可 patch,
        /// 故挂其主调用点)。末行路径上 native 先执行 ExecuteEndAction(原版摆货 lambda)再返回 → Postfix 与原版摆货同帧。
        /// 转调两路: 全局货架(Inject) + 脚本 NPC sell_items(Npcs), 各自会话键防双份, OpenUI 兜底不变。</summary>
        [HarmonyPatch(typeof(DialogUIManager), "OnCurrentTextDisplayed")]
        internal static class Patch_InjectDialogueEnded
        {
            private static void Postfix(DialogUIManager __instance)
            {
                Dialogue d = null;
                try { d = __instance is null ? null : __instance.currentDialog; } catch { }
                if (d is null) return;
                try { EventsPlugin.Instance?.Inject?.OnClientMainDialogueEnded(d); } catch { }
                try { EventsPlugin.Instance?.Npcs?.OnClientMainDialogueEnded(d); } catch { }
            }
        }

        /// <summary>博士商店会话标记 (VisitUpgradeMerchant ~50+ 指令 / LeaveUpgradeMerchant 163 行 ISIL, 均远超 40 字节红线)。</summary>
        [HarmonyPatch(typeof(MapUIManager), "VisitUpgradeMerchant")]
        internal static class Patch_InjectDoctorVisit
        {
            private static void Postfix()
            {
                try { EventsPlugin.Instance?.Inject?.OnDoctorVisit(); } catch { }
            }
        }

        [HarmonyPatch(typeof(MapUIManager), "LeaveUpgradeMerchant")]
        internal static class Patch_InjectDoctorLeave
        {
            private static void Postfix()
            {
                try { EventsPlugin.Instance?.Inject?.OnDoctorLeave(); } catch { }
            }
        }

        /// <summary>野外商人货架注入 (ShowBarter 142 行 ISIL, 安全)。</summary>
        [HarmonyPatch(typeof(OverlayHandler), "ShowBarter")]
        internal static class Patch_InjectBarter
        {
            private static void Postfix(OverlayHandler __instance, GameCharacterItem gci)
            {
                try { EventsPlugin.Instance?.Inject?.InjectBarterShop(gci, __instance); } catch { }
            }
        }
    }
}
