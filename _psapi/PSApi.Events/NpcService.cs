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
    /// E4 NPC 服务: npc.register / npc.pool_add 的实现侧(NpcManager v3.3.1 机制收编)。
    /// 生成管线全走官方 ModHook.OnGenerateCustomer* 五相位钩子, 零 Harmony 补丁在生成路径上:
    ///   VeryEarly 快照今日队列指针 → VeryLate 先差集(原版/池客户 → customer_generated)
    ///   → 再评估脚本调度并入队(自家 spawn 单独发 customer_generated, source=schedule)。
    /// 补丁三个: IsClientSelling Postfix(出售资格兜底) + NegociationUIManager.OpenUI Postfix(原生摆货兜底)
    /// —— 均为 NpcManager 同点同法(生产验证); v1.13.1 起加 GameItem.AddClientItemBuyingFeature Prefix
    /// (收购特性方向闸门: 脚本 NPC 的收购溢价不漏到客户自有货品/玩家买单方向, 原版客户不动)。
    /// once/max_times 记账与评估日落 SaveStates(按存档槽隔离), owner=包 id / "psapi.events.npc"。
    /// </summary>
    internal sealed class NpcService
    {
        // ==================== 纯数据与解析(不碰 Il2Cpp, 无头可测) ====================

        internal sealed class SchedDef
        {
            internal string Mode = "manual"; // manual|daily|every_ndays|specific_day|random|once
            internal long Interval = 1;
            internal long Day;
            internal double Chance = 1.0;
            internal long MaxTimes = -1;
            internal bool PutFirst;
        }

        internal sealed class SellSpec
        {
            internal string Id;
            internal long Count = 1;
            internal long CountMax;  // v1.10.0: >Count = 数量区间 [Count, CountMax](每次注入/掷货独立掷); 0=固定 Count
            internal double Chance = 1.0;
            internal int Uses; // v1.9.0: >0 = 创建实例时启用次数机制(inject.sell_shelf opts.uses)
            internal string Src; // v1.11.0: 来源标签(sell_items/sell_pool/sell_items(fn)), 摆货告警定位用
        }

        /// <summary>sell_pool 候选条目: 权重=抽选概率(加权不放回), 与 SellSpec.Chance(每条目独立判定)语义不同,
        /// 二者可混用(先摆 sell_items, 再从 sell_pool 抽齐 sell_count 种)。</summary>
        internal sealed class PoolSpec
        {
            internal string Id;
            internal double Weight = 1.0;
        }

        /// <summary>对话选项 (E7; 仅 main 通道): label/desc/next(后续台词) + key(选项身份键物品 id, 可空=轮换默认)。
        /// v1.11.0: cond = 显示条件函数(每次构建对话时调用, 返回 falsy = 该选项不显示; 出错按不显示)。</summary>
        internal sealed class ChoiceDef
        {
            internal string Label;
            internal string Desc;
            internal string Next;
            internal string Key;
            internal PsCallable Cond;
        }

        /// <summary>一条对话候选: 1..n 段台词。段数>1 = NextDialogue 链顺序播放(原版分段对话机制),
        /// choices/endAction 只挂链尾。字符串候选 = 单段; dict 候选 texts = 多段链。</summary>
        internal sealed class DlgCandidate
        {
            internal readonly List<string> Segments = new List<string>();
        }

        internal sealed class PoolReq
        {
            internal string Name;      // client|buy|sell|upper|bm
            internal double WeightPct;
            internal int ResolvedWeight = -1; // 首次挂载时按池底数换算并缓存(跨天稳定)
            internal double Scale = 1.0;      // M2 npc.pool_scale: 挂载权重 = ResolvedWeight × Scale
        }

        internal sealed class NpcDef
        {
            internal string Id;
            internal string Name;
            internal string BaseTemplate;
            internal string Faction;
            internal string Intent;
            internal long? Cash;
            internal long[] Budget; // [amount, range]
            internal long? BuyPriceMod;   // v1.12.0 起废弃(9-18 语义=玩家买单加价%), 仅解析用于告警
            internal long? SellPriceMod;  // 同上
            // price: sell_single = 玩家单买价系数(→buyPriceModifier 负折扣, 9-18 实证有效);
            // sell_bulk = 批发(整桌买下)总价系数(原版 isOfferWholesale/wholesaleDiscount 补足差额);
            // buy = NPC 收购价系数(→clientItemFeatureBuying 溢价特性, 原版 premiumBuy15 同款机制)。
            // v1.13.0: 缺省改回原版中立 1.0/1.0/1.0(框架不内置折扣, 包侧显式声明);
            // gunworks 包统一 price={sell_single=0.9, sell_bulk=0.8, buy=1.05}。
            internal double PriceSellSingle = 1.0;
            internal double PriceSellBulk = 1.0;
            internal double PriceBuy = 1.0;
            internal bool? MultiBuyDisabled;   // M2: 每次只收一件(陈警官式登记销毁)
            internal bool? NoContraband;       // M2: 不收违禁品
            internal string Sprite;
            internal List<string> Sprites;
            internal List<string> BuyingIds, BuyingTags, BuyingFeatures, BlackIds, BlackTags;
            internal bool ClearBuying = true;
            internal List<PoolSpec> BuyingPool;                 // v1.11.0: 收购候选池(null=未写; 须搭配 buy_count)
            internal long BuyCountMin = -1, BuyCountMax = -1;   // v1.11.0: buy_count 当日收购种数(-1=未写)
            internal List<SellSpec> SellItems;          // 静态清单(null=未写)
            internal PsCallable SellItemsFn;            // 动态清单(每次生成调脚本函数)
            internal List<PoolSpec> SellPool;           // v1.10.0: 加权候选池(null=未写; 须搭配 sell_count)
            internal long SellCountMin = -1, SellCountMax = -1; // v1.10.0: sell_count 上架种数(-1=未写)
            internal Dictionary<string, List<DlgCandidate>> Dialogues; // 通道 → 候选(每候选 1..n 段)
            internal string DialogueId;                          // E7: main 对话配置 id (dialogue_choice 的 dialogue_id 用, 全名 = 包:id)
            internal List<ChoiceDef> Choices;                    // E7: main 对话的选项 (null = 无选项纯文本)
            internal bool AutoLeave = true;                      // v1.9.0: 说完就走(all_done/main 链尾挂离场 endAction)
            internal SchedDef Schedule;
            internal PsCallable CanSpawn;
            internal bool Register;                      // 进 storeClientDict
            internal List<PoolReq> Pools = new List<PoolReq>();
        }

        /// <summary>say/dialogues 通道 → 内部通道号(与 PsClientHandle.Channels 同序)。</summary>
        internal static readonly Dictionary<string, int> DialogueChannels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["main"] = 0, ["accept"] = 1, ["all_done"] = 2, ["repeat"] = 3,
            ["wrong_item"] = 4, ["right_item"] = 5, ["interogation"] = 6,
            ["glasse"] = 7, ["on_arrest"] = 8,
        };

        private static readonly HashSet<string> KnownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "name", "base_template", "faction", "intent", "cash", "budget",
            "buy_price_mod", "sell_price_mod", "price", "sprite", "sprites",
            "buying_ids", "buying_tags", "buying_features", "black_ids", "black_tags", "clear_buying",
            "buy_pool", "buy_count",
            "multi_buy_disabled", "no_contraband",
            "sell_items", "sell_pool", "sell_count", "dialogues", "schedule", "can_spawn", "register", "pools", "auto_leave",
        };

        /// <summary>解析 npc.register 的 dict → NpcDef; 配置错误抛 PsRuntimeError, 未知键经 warn 回调跳过。</summary>
        internal static NpcDef ParseConfig(Dictionary<string, object> cfg, int line, Action<string> warn)
        {
            var def = new NpcDef();
            foreach (var k in cfg.Keys)
                if (!KnownKeys.Contains(k))
                    warn?.Invoke($"npc.register: 未知配置键 '{k}' 已跳过");

            def.Id = CfgStr(cfg, "id", null, line, "id");
            if (string.IsNullOrEmpty(def.Id))
                throw new PsRuntimeError("npc.register 缺少必填键 id(非空字符串)", line);
            def.Name = CfgStr(cfg, "name", null, line, "name");
            def.BaseTemplate = CfgStr(cfg, "base_template", null, line, "base_template");
            def.Faction = CfgStr(cfg, "faction", null, line, "faction");
            def.Intent = CfgStr(cfg, "intent", null, line, "intent");
            def.Cash = CfgLongOpt(cfg, "cash", line);
            def.Budget = CfgLongArr(cfg, "budget", 2, line);
            def.BuyPriceMod = CfgLongOpt(cfg, "buy_price_mod", line);
            def.SellPriceMod = CfgLongOpt(cfg, "sell_price_mod", line);
            // v1.11.0 price: {sell_single=0.9, sell_bulk=0.8, buy=1.0} 全可选, 缺省 = 目标值
            if (cfg.TryGetValue("price", out var pc) && pc != null)
            {
                if (pc is not Dictionary<string, object> pcd)
                    throw new PsRuntimeError("npc.register 的 price 须为 dict({sell_single, sell_bulk, buy})", line);
                foreach (var pk in pcd.Keys)
                    if (pk != "sell_single" && pk != "sell_bulk" && pk != "buy")
                        warn?.Invoke($"npc.register: price 未知键 '{pk}' 已跳过(可用: sell_single/sell_bulk/buy)");
                def.PriceSellSingle = CfgPriceCoef(pcd, "sell_single", def.PriceSellSingle, line);
                def.PriceSellBulk = CfgPriceCoef(pcd, "sell_bulk", def.PriceSellBulk, line);
                def.PriceBuy = CfgPriceCoef(pcd, "buy", def.PriceBuy, line);
            }
            def.Sprite = CfgStr(cfg, "sprite", null, line, "sprite");
            def.Sprites = CfgStrList(cfg, "sprites", line);
            def.BuyingIds = CfgStrList(cfg, "buying_ids", line);
            def.BuyingTags = CfgStrList(cfg, "buying_tags", line);
            def.BuyingFeatures = CfgStrList(cfg, "buying_features", line);
            def.BlackIds = CfgStrList(cfg, "black_ids", line);
            def.BlackTags = CfgStrList(cfg, "black_tags", line);
            if (cfg.TryGetValue("clear_buying", out var cb) && cb != null)
                def.ClearBuying = PsValues.Truthy(cb);
            if (cfg.TryGetValue("multi_buy_disabled", out var mb) && mb != null)
                def.MultiBuyDisabled = PsValues.Truthy(mb);
            if (cfg.TryGetValue("no_contraband", out var nc) && nc != null)
                def.NoContraband = PsValues.Truthy(nc);
            if (cfg.TryGetValue("auto_leave", out var al) && al != null)
                def.AutoLeave = PsValues.Truthy(al);

            // sell_items: 数组(静态) 或 函数(动态, 生成时调用, 须返回同格式数组)
            if (cfg.TryGetValue("sell_items", out var si) && si != null)
            {
                if (si is PsCallable fn) def.SellItemsFn = fn;
                else def.SellItems = ParseSellItems(si, line);
            }

            // v1.10.0: sell_pool 加权候选池 + sell_count 上架种数("3-6" 区间或定数, 1..20);
            // sell_pool 必须搭配 sell_count(否则不知道每次抽几种), 反之 sell_count 无池 = 警告忽略
            if (cfg.TryGetValue("sell_count", out var scnt) && scnt != null)
                ParseSellCount(def, scnt, line);
            if (cfg.TryGetValue("sell_pool", out var sp) && sp != null)
                def.SellPool = ParseSellPool(sp, line);
            if (def.SellPool != null && def.SellCountMin < 0)
                throw new PsRuntimeError("npc.register: sell_pool 须搭配 sell_count(每次来访上架种数, \"3-6\" 或定数)", line);
            if (def.SellPool == null && def.SellCountMin >= 0)
                warn?.Invoke("npc.register: sell_count 无 sell_pool 搭配, 已忽略");

            // v1.11.0: buy_pool 收购候选池 + buy_count 当日收购种数(与 sell_pool/sell_count 同构,
            // 加权不放回抽 N 种); 当日收购 = buying_ids(必收) ∪ buy_pool 掷中种
            if (cfg.TryGetValue("buy_count", out var bcnt) && bcnt != null)
                ParseCountRange(bcnt, "buy_count", line, out def.BuyCountMin, out def.BuyCountMax);
            if (cfg.TryGetValue("buy_pool", out var bpp) && bpp != null)
                def.BuyingPool = ParseSellPool(bpp, line, "buy_pool");
            if (def.BuyingPool != null && def.BuyCountMin < 0)
                throw new PsRuntimeError("npc.register: buy_pool 须搭配 buy_count(当日收购种数, \"2-3\" 或定数)", line);
            if (def.BuyingPool == null && def.BuyCountMin >= 0)
                warn?.Invoke("npc.register: buy_count 无 buy_pool 搭配, 已忽略");

            // dialogues: { 通道 = 候选源 }。候选源 = "单段台词" 或 [候选, ...] 或 dict(=单候选)。
            // 候选 = "单段台词" 或 {text="单段" | texts=["段1","段2",...](顺序链式播放), id, choices=[...]};
            // id/choices 仅 main 通道; text 与 texts 同现 = 报错(歧义)。
            if (cfg.TryGetValue("dialogues", out var dg) && dg != null)
            {
                if (dg is not Dictionary<string, object> dd)
                    throw new PsRuntimeError("npc.register 的 dialogues 须为 dict(通道 → 台词/候选数组/dict)", line);
                def.Dialogues = new Dictionary<string, List<DlgCandidate>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in dd)
                {
                    if (!DialogueChannels.ContainsKey(kv.Key))
                    {
                        warn?.Invoke($"npc.register: 未知对话通道 '{kv.Key}' 已跳过(可用: {string.Join("/", DialogueChannels.Keys)})");
                        continue;
                    }
                    bool isMain = DialogueChannels[kv.Key] == 0;
                    var cands = new List<DlgCandidate>();
                    if (kv.Value is string one)
                    {
                        if (!string.IsNullOrWhiteSpace(one)) cands.Add(SingleSeg(one));
                    }
                    else if (kv.Value is List<object> many)
                    {
                        foreach (var v in many)
                        {
                            if (v is string s) { if (!string.IsNullOrWhiteSpace(s)) cands.Add(SingleSeg(s)); }
                            else if (v is Dictionary<string, object> dm) cands.Add(ParseDlgCandidate(dm, kv.Key, isMain, def, line, warn));
                            else throw new PsRuntimeError($"npc.register dialogues.{kv.Key} 候选须为字符串或 dict, 实为 {PsValues.TypeName(v)}", line);
                        }
                    }
                    else if (kv.Value is Dictionary<string, object> dm2)
                        cands.Add(ParseDlgCandidate(dm2, kv.Key, isMain, def, line, warn));
                    else throw new PsRuntimeError($"npc.register dialogues.{kv.Key} 须为字符串、候选数组或 dict", line);
                    if (cands.Count > 0) def.Dialogues[kv.Key] = cands;
                }
            }

            // schedule: {mode/interval/day/chance/max_times/put_first}
            if (cfg.TryGetValue("schedule", out var sc) && sc != null)
            {
                if (sc is not Dictionary<string, object> sd)
                    throw new PsRuntimeError("npc.register 的 schedule 须为 dict", line);
                var s = new SchedDef();
                s.Mode = (CfgStr(sd, "mode", "manual", line, "schedule.mode") ?? "manual").Trim().ToLowerInvariant();
                switch (s.Mode)
                {
                    case "manual": case "daily": case "every_ndays": case "everyndays":
                    case "specific_day": case "specificday": case "random": case "once":
                        break;
                    default:
                        throw new PsRuntimeError($"npc.register schedule.mode 未知 '{s.Mode}'(可用: manual/daily/every_ndays/specific_day/random/once)", line);
                }
                if (s.Mode == "everyndays") s.Mode = "every_ndays";
                if (s.Mode == "specificday") s.Mode = "specific_day";
                s.Interval = CfgLong(sd, "interval", 1, line, "schedule.interval");
                s.Day = CfgLong(sd, "day", 0, line, "schedule.day");
                s.Chance = CfgDouble(sd, "chance", 1.0, line, "schedule.chance");
                s.MaxTimes = CfgLong(sd, "max_times", -1, line, "schedule.max_times");
                if (sd.TryGetValue("put_first", out var pf) && pf != null) s.PutFirst = PsValues.Truthy(pf);
                def.Schedule = s;
            }

            if (cfg.TryGetValue("can_spawn", out var cs) && cs != null)
            {
                if (cs is not PsCallable fn2)
                    throw new PsRuntimeError("npc.register 的 can_spawn 须为函数(返回 truthy 才生成)", line);
                def.CanSpawn = fn2;
            }
            if (cfg.TryGetValue("register", out var rg) && rg != null)
                def.Register = PsValues.Truthy(rg);

            if (cfg.TryGetValue("pools", out var pl) && pl != null)
            {
                if (pl is not List<object> arr)
                    throw new PsRuntimeError("npc.register 的 pools 须为数组(元素 {name, weight_pct})", line);
                foreach (var v in arr)
                {
                    if (v is not Dictionary<string, object> pd)
                        throw new PsRuntimeError("npc.register pools 元素须为 dict({name, weight_pct})", line);
                    def.Pools.Add(new PoolReq
                    {
                        Name = (CfgStr(pd, "name", null, line, "pools[].name") ?? "").Trim().ToLowerInvariant(),
                        WeightPct = CfgDouble(pd, "weight_pct", -1, line, "pools[].weight_pct"),
                    });
                }
            }

            // 设计约束(05-NPC注册表 §2.1): 收购清单与出售货物不允许重叠
            if (def.BuyingIds != null && def.SellItems != null)
            {
                var buy = new HashSet<string>(def.BuyingIds, StringComparer.Ordinal);
                foreach (var s in def.SellItems)
                    if (buy.Contains(s.Id))
                        throw new PsRuntimeError($"npc.register: '{s.Id}' 同时出现在 buying_ids 与 sell_items(不允许重叠)", line);
            }
            if (def.BuyingIds != null && def.SellItemsFn != null)
                warn?.Invoke($"npc.register: sell_items 为动态函数, 与 buying_ids 的重叠只能在运行时留意");
            if (def.BuyingIds != null && def.SellPool != null)
            {
                var buy2 = new HashSet<string>(def.BuyingIds, StringComparer.Ordinal);
                foreach (var p in def.SellPool)
                    if (buy2.Contains(p.Id))
                        throw new PsRuntimeError($"npc.register: '{p.Id}' 同时出现在 buying_ids 与 sell_pool(不允许重叠)", line);
            }
            // v1.11.0: buy_pool 同样不得与出售侧重叠(池成员全量检查, 与掷骰结果无关)
            if (def.BuyingPool != null && def.SellItems != null)
            {
                var buy3 = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in def.BuyingPool) buy3.Add(p.Id);
                foreach (var s in def.SellItems)
                    if (buy3.Contains(s.Id))
                        throw new PsRuntimeError($"npc.register: '{s.Id}' 同时出现在 buy_pool 与 sell_items(不允许重叠)", line);
            }
            if (def.BuyingPool != null && def.SellPool != null)
            {
                var buy4 = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in def.BuyingPool) buy4.Add(p.Id);
                foreach (var p in def.SellPool)
                    if (buy4.Contains(p.Id))
                        throw new PsRuntimeError($"npc.register: '{p.Id}' 同时出现在 buy_pool 与 sell_pool(不允许重叠)", line);
            }
            if (def.BuyingPool != null && def.SellItemsFn != null)
                warn?.Invoke($"npc.register: sell_items 为动态函数, 与 buy_pool 的重叠只能在运行时留意");
            return def;
        }

        /// <summary>sell_count 解析: 定数 long 或 "min-max" 区间字符串, 1..20(种数, 不是件数)。</summary>
        private static void ParseSellCount(NpcDef def, object raw, int line)
        {
            ParseCountRange(raw, "sell_count", line, out long mn, out long mx);
            def.SellCountMin = mn; def.SellCountMax = mx;
        }

        /// <summary>v1.11.0: sell_count/buy_count 共用的种数区间解析(定数 long 或 "min-max" 字符串, 1..20)。</summary>
        private static void ParseCountRange(object raw, string what, int line, out long min, out long max)
        {
            if (raw is long n) { min = n; max = n; }
            else if (raw is string s)
            {
                if (!TryParseCountToken(s, out min, out max))
                    throw new PsRuntimeError($"{what} 须为 1..20 定数或 \"min-max\" 区间, 实为 '{s}'", line);
                if (max <= 0) max = min; // "5" 定数 = 5-5
            }
            else throw new PsRuntimeError($"{what} 须为数字或 \"min-max\" 字符串, 实为 {PsValues.TypeName(raw)}", line);
            if (min < 1 || min > 20 || max > 20)
                throw new PsRuntimeError($"{what} 须在 1..20, 实为 {min}-{Math.Max(min, max)}", line);
        }

        /// <summary>sell_pool/buy_pool 数组解析: 元素 = "id:权重" 字符串(权重缺省 1.0) 或 {id, weight} dict; 权重 > 0。</summary>
        internal static List<PoolSpec> ParseSellPool(object raw, int line, string what = "sell_pool")
        {
            if (raw is not List<object> arr)
                throw new PsRuntimeError($"{what} 须为数组(\"id:权重\" 或 {{id, weight}})", line);
            var list = new List<PoolSpec>();
            foreach (var v in arr)
            {
                if (v is string s)
                {
                    if (!TryParsePoolEntry(s, out var p))
                        throw new PsRuntimeError($"{what} 条目格式错误 '{s}'(应为 \"id:权重\", 权重>0)", line);
                    list.Add(p);
                }
                else if (v is Dictionary<string, object> d)
                {
                    string id = CfgStr(d, "id", null, line, what + "[].id");
                    if (string.IsNullOrEmpty(id))
                        throw new PsRuntimeError($"{what} dict 条目缺 id", line);
                    double w = CfgDouble(d, "weight", 1.0, line, what + "[].weight");
                    if (w <= 0)
                        throw new PsRuntimeError($"{what} 条目 '{id}' 的 weight 须 > 0, 实为 {w}", line);
                    list.Add(new PoolSpec { Id = id, Weight = w });
                }
                else throw new PsRuntimeError($"{what} 元素类型须为 string/dict, 实为 {PsValues.TypeName(v)}", line);
            }
            return list;
        }

        /// <summary>"id:权重" 词法: 从尾部识别 ":权重"(>0 的数字), 余下各段拼回 id(兼容带命名空间的 id)。</summary>
        internal static bool TryParsePoolEntry(string raw, out PoolSpec p)
        {
            p = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var parts = raw.Trim().Split(':');
            int idEnd = parts.Length;
            double w = 1.0;
            if (idEnd >= 2 && double.TryParse(parts[idEnd - 1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double pw))
            {
                if (pw <= 0) return false; // 数字尾缀但非正权重 = 非法条目(防 "rope:0" 静默吞进 id)
                w = pw; idEnd -= 1;
            }
            string id = string.Join(":", parts, 0, idEnd).Trim();
            if (id.Length == 0) return false;
            p = new PoolSpec { Id = id, Weight = w };
            return true;
        }

        /// <summary>sell_pool 加权不放回抽 need 种(权重=抽选概率, 与 sell_items 的 chance 独立判定语义不同)。
        /// need<=0 或池空 = 空表; 全零权池 = 提前停(防死循环)。</summary>
        internal static List<PoolSpec> RollPool(List<PoolSpec> pool, int need, Random rng)
        {
            var picked = new List<PoolSpec>();
            if (pool == null || need <= 0) return picked;
            var rest = new List<PoolSpec>(pool);
            while (rest.Count > 0 && picked.Count < need)
            {
                double sum = 0;
                foreach (var p in rest) sum += p.Weight;
                if (sum <= 0) break;
                double r = rng.NextDouble() * sum;
                int idx = rest.Count - 1;
                double acc = 0;
                for (int i = 0; i < rest.Count; i++) { acc += rest[i].Weight; if (r < acc) { idx = i; break; } }
                picked.Add(rest[idx]);
                rest.RemoveAt(idx);
            }
            return picked;
        }

        private static DlgCandidate SingleSeg(string text)
        {
            var c = new DlgCandidate();
            c.Segments.Add(text);
            return c;
        }

        /// <summary>dict 候选: {text|texts, (main)id/choices}。text 与 texts 同现 = 报错(二选一, 写明);
        /// id/choices 仅 main 通道; id 重复定义 = 后者覆盖+警告; choices 重复定义 = 报错。</summary>
        private static DlgCandidate ParseDlgCandidate(Dictionary<string, object> dm, string channel, bool isMain,
            NpcDef def, int line, Action<string> warn)
        {
            foreach (var mk in dm.Keys)
                if (mk != "text" && mk != "texts" && mk != "id" && mk != "choices")
                    warn?.Invoke($"npc.register: dialogues.{channel} 未知键 '{mk}' 已跳过");
            if (!isMain && (dm.ContainsKey("id") || dm.ContainsKey("choices")))
                throw new PsRuntimeError($"npc.register dialogues.{channel} 的 id/choices 仅支持 main 通道", line);

            string text = null;
            if (dm.TryGetValue("text", out var t1) && t1 is string s1 && !string.IsNullOrWhiteSpace(s1)) text = s1;
            List<object> texts = null;
            if (dm.TryGetValue("texts", out var t2) && t2 is List<object> l2 && l2.Count > 0) texts = l2;
            if (text != null && texts != null)
                throw new PsRuntimeError($"npc.register dialogues.{channel}: text 与 texts 只能二选一(text=单段, texts=多段顺序链)", line);

            var cand = new DlgCandidate();
            if (text != null) cand.Segments.Add(text);
            if (texts != null)
                foreach (var v in texts) { var s = PsValues.Fmt(v); if (!string.IsNullOrWhiteSpace(s)) cand.Segments.Add(s); }
            if (cand.Segments.Count == 0)
                throw new PsRuntimeError($"npc.register dialogues.{channel} dict 形式须含 text 或 texts(非空台词)", line);

            if (isMain)
            {
                string idStr = CfgStr(dm, "id", null, line, $"dialogues.{channel}.id");
                if (!string.IsNullOrEmpty(idStr))
                {
                    if (!string.IsNullOrEmpty(def.DialogueId) && def.DialogueId != idStr)
                        warn?.Invoke($"npc.register: dialogues.main.id 重复定义, '{idStr}' 覆盖 '{def.DialogueId}'");
                    def.DialogueId = idStr;
                }
                if (dm.TryGetValue("choices", out var ch) && ch != null)
                {
                    if (def.Choices != null)
                        throw new PsRuntimeError("npc.register dialogues.main: choices 只能在一个候选里定义", line);
                    if (ch is not List<object> carr)
                        throw new PsRuntimeError("npc.register dialogues.main.choices 须为数组(元素 {label, desc, next, key})", line);
                    def.Choices = new List<ChoiceDef>();
                    foreach (var cv in carr)
                    {
                        if (cv is not Dictionary<string, object> cd)
                            throw new PsRuntimeError("npc.register dialogues.main.choices 元素须为 dict({label, desc, next, key})", line);
                        foreach (var ck in cd.Keys)
                            if (ck != "label" && ck != "desc" && ck != "next" && ck != "key" && ck != "cond")
                                warn?.Invoke($"npc.register: choices[] 未知键 '{ck}' 已跳过");
                        PsCallable cond = null;
                        if (cd.TryGetValue("cond", out var cc) && cc != null)
                        {
                            if (cc is not PsCallable cf)
                                throw new PsRuntimeError("npc.register choices[] 的 cond 须为函数(返回 falsy = 不显示该选项)", line);
                            cond = cf;
                        }
                        var cdef = new ChoiceDef
                        {
                            Label = CfgStr(cd, "label", null, line, "choices[].label"),
                            Desc = CfgStr(cd, "desc", null, line, "choices[].desc") ?? "",
                            Next = CfgStr(cd, "next", null, line, "choices[].next"),
                            Key = CfgStr(cd, "key", null, line, "choices[].key"),
                            Cond = cond,
                        };
                        if (string.IsNullOrWhiteSpace(cdef.Label))
                            throw new PsRuntimeError("npc.register dialogues.main.choices[] 缺 label(选项文字)", line);
                        def.Choices.Add(cdef);
                    }
                }
            }
            return cand;
        }

        /// <summary>sell_items 数组解析: 元素 = "id:count:p" 字符串 或 {id, count, p} dict。</summary>
        internal static List<SellSpec> ParseSellItems(object raw, int line)
        {
            if (raw is not List<object> arr)
                throw new PsRuntimeError("sell_items 须为数组(\"id:数量:概率\" 或 {id, count, p})或函数", line);
            var list = new List<SellSpec>();
            foreach (var v in arr)
            {
                if (v is string s)
                {
                    if (!TryParseSellEntry(s, out var e))
                        throw new PsRuntimeError($"sell_items 条目格式错误 '{s}'(应为 \"id:数量:概率\")", line);
                    list.Add(e);
                }
                else if (v is Dictionary<string, object> d)
                {
                    string id = CfgStr(d, "id", null, line, "sell_items[].id");
                    if (string.IsNullOrEmpty(id))
                        throw new PsRuntimeError("sell_items dict 条目缺 id", line);
                    var e = new SellSpec { Id = id };
                    e.Count = CfgLong(d, "count", 1, line, "sell_items[].count");
                    e.Chance = CfgDouble(d, "p", 1.0, line, "sell_items[].p");
                    list.Add(e);
                }
                else throw new PsRuntimeError($"sell_items 元素类型须为 string/dict, 实为 {PsValues.TypeName(v)}", line);
            }
            return list;
        }

        /// <summary>数量词法: "5" → (5,0=固定); "1-5" → (1,5) 区间; 均限 1..99。非法 = false。</summary>
        internal static bool TryParseCountToken(string tok, out long min, out long max)
        {
            min = 0; max = 0;
            tok = (tok ?? "").Trim();
            int dash = tok.IndexOf('-');
            if (dash > 0)
            {
                if (!long.TryParse(tok.Substring(0, dash).Trim(), out min)) return false;
                if (!long.TryParse(tok.Substring(dash + 1).Trim(), out max)) return false;
                return min >= 1 && min <= 99 && max >= min && max <= 99;
            }
            if (!long.TryParse(tok, out min) || min < 1 || min > 99) return false;
            return true;
        }

        /// <summary>数量掷骰: CountMax > Count = 区间 [Count, CountMax] 闭区间随机, 否则固定 Count。</summary>
        internal static long RollCount(SellSpec e, Random rng)
            => e.CountMax > e.Count ? rng.Next((int)e.Count, (int)e.CountMax + 1) : e.Count;

        internal static bool TryParseSellEntry(string raw, out SellSpec e)
        {
            e = new SellSpec { Id = null, Count = 1, Chance = 1.0 };
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var parts = raw.Trim().Split(':');
            // 从尾部识别 ":数量:概率", 余下各段拼回 id —— 兼容带命名空间的 id("pack:id:数量:概率");
            // 原三格式 "id" / "id:数量" / "id:数量:概率" 解析结果与旧版逐字一致
            // v1.10.0: 数量支持 "min-max" 区间(如 "id:1-3:0.5", 每次掷货独立随机)
            int idEnd = parts.Length;
            if (idEnd >= 3 && TryParseCountToken(parts[idEnd - 2], out long c2, out long c2max)
                && double.TryParse(parts[idEnd - 1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double p2))
            { e.Count = c2; e.CountMax = c2max; e.Chance = p2; idEnd -= 2; }
            else if (idEnd >= 2 && TryParseCountToken(parts[idEnd - 1], out long c1, out long c1max))
            { e.Count = c1; e.CountMax = c1max; idEnd -= 1; }
            else if (idEnd >= 2 && double.TryParse(parts[idEnd - 1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double p1))
            { e.Chance = p1; idEnd -= 1; }
            e.Id = string.Join(":", parts, 0, idEnd).Trim();
            if (e.Id.Length == 0) return false;
            return true;
        }

        /// <summary>调度判定(与 NpcManager ShouldSpawn 语义一致; relDay0 = 0-based 开业日序号)。
        /// 带诊断输出: eligible=调度日是否命中; roll=chance 掷骰值(未掷为 null); skipReason=不生成原因。</summary>
        internal static bool EvalSchedule(SchedDef s, int relDay0, long count, bool onceFired, Random rng,
            out bool eligible, out double? roll, out string skipReason)
        {
            eligible = false; roll = null; skipReason = null;
            if (s == null) { skipReason = "no_schedule"; return false; }
            switch (s.Mode)
            {
                case "manual": skipReason = "manual"; return false;
                case "daily": eligible = true; break;
                case "every_ndays": eligible = relDay0 % Math.Max(1, (int)s.Interval) == 0; break;
                case "specific_day": eligible = relDay0 == s.Day; break;
                case "random": eligible = true; break; // 纯概率, 每天掷
                case "once": eligible = true; break;
                default: skipReason = "unknown_mode(" + s.Mode + ")"; return false;
            }
            if (!eligible) { skipReason = "day_not_matched"; return false; }
            if (s.Mode == "once" && onceFired) { skipReason = "once_fired"; return false; }
            if (s.MaxTimes >= 0 && count >= s.MaxTimes) { skipReason = $"max_times({count}/{s.MaxTimes})"; return false; }
            if (s.Chance < 1.0)
            {
                double r = (rng ?? new Random()).NextDouble();
                roll = r;
                if (r >= Math.Max(0.0, s.Chance)) { skipReason = "chance"; return false; }
            }
            return true;
        }

        /// <summary>调度判定(与 NpcManager ShouldSpawn 逐条对齐; relDay0 = 0-based 开业日序号)。</summary>
        internal static bool ShouldSpawn(SchedDef s, int relDay0, long count, bool onceFired, Random rng)
            => EvalSchedule(s, relDay0, count, onceFired, rng, out _, out _, out _);

        // ---- 配置读取辅助(纯) ----

        private static string CfgStr(Dictionary<string, object> cfg, string key, string def, int line, string what)
        {
            if (!cfg.TryGetValue(key, out var v) || v == null) return def;
            if (v is string s) return s;
            throw new PsRuntimeError($"npc.register 的 {what} 须为字符串, 实为 {PsValues.TypeName(v)}", line);
        }

        private static long CfgLong(Dictionary<string, object> cfg, string key, long def, int line, string what)
        {
            if (!cfg.TryGetValue(key, out var v) || v == null) return def;
            if (v is long l) return l;
            if (v is double d) return (long)d;
            throw new PsRuntimeError($"npc.register 的 {what} 须为数字, 实为 {PsValues.TypeName(v)}", line);
        }

        private static long? CfgLongOpt(Dictionary<string, object> cfg, string key, int line)
        {
            if (!cfg.TryGetValue(key, out var v) || v == null) return null;
            if (v is long l) return l;
            if (v is double d) return (long)d;
            throw new PsRuntimeError($"npc.register 的 {key} 须为数字, 实为 {PsValues.TypeName(v)}", line);
        }

        /// <summary>v1.11.0 price 子键: (0,10] 价格系数(1.0 = 原价)。</summary>
        private static double CfgPriceCoef(Dictionary<string, object> cfg, string key, double def, int line)
        {
            if (!cfg.TryGetValue(key, out var v) || v == null) return def;
            double d = v is long l ? l : v is double dd ? dd
                : throw new PsRuntimeError($"npc.register price.{key} 须为数字, 实为 {PsValues.TypeName(v)}", line);
            if (d <= 0 || d > 10)
                throw new PsRuntimeError($"npc.register price.{key} 须在 (0,10], 实为 {d}", line);
            return d;
        }

        private static double CfgDouble(Dictionary<string, object> cfg, string key, double def, int line, string what)
        {
            if (!cfg.TryGetValue(key, out var v) || v == null) return def;
            if (v is long l) return l;
            if (v is double d) return d;
            throw new PsRuntimeError($"npc.register 的 {what} 须为数字, 实为 {PsValues.TypeName(v)}", line);
        }

        private static long[] CfgLongArr(Dictionary<string, object> cfg, string key, int max, int line)
        {
            if (!cfg.TryGetValue(key, out var v) || v == null) return null;
            if (v is not List<object> arr || arr.Count == 0)
                throw new PsRuntimeError($"npc.register 的 {key} 须为非空数组", line);
            var r = new long[Math.Min(arr.Count, max)];
            for (int i = 0; i < r.Length; i++)
            {
                if (arr[i] is long l) r[i] = l;
                else if (arr[i] is double d) r[i] = (long)d;
                else throw new PsRuntimeError($"npc.register 的 {key}[{i}] 须为数字", line);
            }
            return r;
        }

        private static List<string> CfgStrList(Dictionary<string, object> cfg, string key, int line)
        {
            if (!cfg.TryGetValue(key, out var v) || v == null) return null;
            if (v is not List<object> arr)
                throw new PsRuntimeError($"npc.register 的 {key} 须为字符串数组", line);
            var r = new List<string>();
            foreach (var x in arr)
            {
                if (x is not string s)
                    throw new PsRuntimeError($"npc.register 的 {key} 元素须为字符串, 实为 {PsValues.TypeName(x)}", line);
                if (!string.IsNullOrWhiteSpace(s)) r.Add(s.Trim());
            }
            return r;
        }

        // ==================== 运行期(游戏侧) ====================

        /// <summary>一条注册: 定义 + 包上下文(脚本回调需要包解释器)。internal 供测试台直接驱动 EvalEntry。</summary>
        internal sealed class Entry
        {
            internal string PackId;
            internal NpcDef Def;
            internal Interpreter Itp;
            internal Il2CppSystem.Func<StoreClient> IlFactory; // 池/字典挂载用(惰性创建, 永久 pin)
        }

        private readonly MelonLogger.Instance _logger;
        private readonly EventBus _bus;
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<object> _pinned = new List<object>();
        private readonly Random _rng = new Random();

        private HashSet<long> _queueSnapshot;                 // VeryEarly 的 clientStack 指针快照
        private readonly Dictionary<long, RolledPlan> _rolledByPtr = new Dictionary<long, RolledPlan>();
        private readonly Dictionary<long, List<string>> _rolledBuyPoolByPtr = new Dictionary<long, List<string>>(); // v1.11.0: buy_pool 当日掷中 id({buy_list} 台词插值用)
        private readonly HashSet<string> _warnedMissing = new HashSet<string>(StringComparer.Ordinal); // v1.11.0: 物品 id 校验告警(来源|id 去重)
        private long _stockedSessionKey = -1;                 // 摆货会话守卫(每客户一次, 拖走不补)
        private readonly Dictionary<string, bool> _pendingSpawn = new Dictionary<string, bool>(StringComparer.Ordinal); // E7 npc.schedule: id → putFirst
        private readonly List<object> _choicePins = new List<object>(); // 选项委托 pin (每生成一 client 一组, 场景离开清)

        /// <summary>已掷摆货计划(每客户实例一份, 摆货后一次性消费)。</summary>
        private sealed class RolledPlan
        {
            internal List<SellSpec> Specs;
        }

        /// <summary>E7: main 对话(Dialogue 对象指针) → dialogue_id 全名(包:配置id), SelectChoice 补丁查。
        /// 场景销毁后指针失效, OnSceneLeft 清空。</summary>
        internal static readonly Dictionary<long, string> DialogueIds = new Dictionary<long, string>();

        /// <summary>三个默认选项身份键物品 (同 RevDeal: 不进背包, 仅作 Dialogue.choices 的键)。</summary>
        private static readonly string[] ChoiceKeyIds = { "newspaper", "paper_towel", "scrap_metal" };

        internal NpcService(MelonLogger.Instance logger, EventBus bus)
        {
            _logger = logger;
            _bus = bus;
        }

        /// <summary>补丁(static)侧的错误上报通道。</summary>
        internal void LogErr(string msg) => PsApi.Err(_logger, msg);

        // ---- 脚本面(npc.register / npc.pool_add, 加载期调用, 不碰游戏对象) ----

        internal void Register(string packId, Dictionary<string, object> cfg, Interpreter itp, int line)
        {
            var def = ParseConfig(cfg, line, w => PsApi.Warn(_logger, $"[{packId}] {w}"));
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (_entries[i].Def.Id == def.Id)
                {
                    PsApi.Warn(_logger, $"npc '{def.Id}': 重复注册, 后者({_entries[i].PackId} → {packId})覆盖前者");
                    _entries.RemoveAt(i);
                }
            _entries.Add(new Entry { PackId = packId, Def = def, Itp = itp });
            PsApi.Log(_logger, $"npc '{def.Id}' registered (pack={packId}, schedule={def.Schedule?.Mode ?? "manual"}, pools={def.Pools.Count})");
        }

        internal void PoolAdd(string pool, string id, double weightPct, int line)
        {
            var entry = Find(id);
            if (entry == null)
                throw new PsRuntimeError($"npc.pool_add: '{id}' 未注册(先 npc.register)", line);
            pool = (pool ?? "").Trim().ToLowerInvariant();
            if (pool != "client" && pool != "buy" && pool != "sell" && pool != "upper" && pool != "bm")
                throw new PsRuntimeError($"npc.pool_add 未知池 '{pool}'(可用: client/buy/sell/upper/bm)", line);
            entry.Def.Pools.Add(new PoolReq { Name = pool, WeightPct = weightPct });
        }

        /// <summary>M2 npc.pool_scale(pattern, factor): 按 id(精确或 "前缀*")缩放已注册 NPC 的全部池权重
        /// (NpcManager RevPoolMultiplier 平移: 老祝"接受"后 rev_cell_* 全部池权重 ×3 + RefreshPools)。
        /// 已挂载的池条目先撤再挂(权重 = 首次挂载缓存的基础权重 × factor); 不在对局只改 Scale, 下次挂载生效。
        /// 幂等(同 factor 重挂 = 同权重)。返回命中 NPC 数。</summary>
        internal int PoolScale(string pattern, double factor, int line)
        {
            pattern = (pattern ?? "").Trim();
            if (pattern.Length == 0)
                throw new PsRuntimeError("npc.pool_scale 的第一参须为 npc id 或 '前缀*'", line);
            if (factor <= 0)
                throw new PsRuntimeError($"npc.pool_scale 的 factor 须 > 0, 实为 {factor}", line);
            bool prefix = pattern.EndsWith("*");
            string key = prefix ? pattern.Substring(0, pattern.Length - 1) : pattern;
            int n = 0;
            foreach (var entry in _entries)
            {
                bool match = prefix
                    ? entry.Def.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(entry.Def.Id, key, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                n++;
                foreach (var p in entry.Def.Pools)
                {
                    p.Scale = factor;
                    ReMount(entry, p);
                }
            }
            if (n > 0) PsApi.Log(_logger, $"npc.pool_scale('{pattern}', x{factor}): {n} 个 NPC 池权重已重挂");
            return n;
        }

        /// <summary>撤旧挂新: 工厂委托按索引平行撤出 factories/weights, 再按 Scale 重挂(AddToPool 幂等)。</summary>
        private void ReMount(Entry entry, PoolReq p)
        {
            if (!GetPoolLists(p.Name, out var factories, out var weights) || factories == null) return; // 不在对局: 下次 EnsurePools 生效
            var del = FactoryOf(entry);
            int idx = factories.IndexOf(del);
            if (idx >= 0)
            {
                factories.RemoveAt(idx);
                if (weights != null)
                    foreach (var w in weights)
                        try { if (w != null && idx < w.Count) w.RemoveAt(idx); } catch { }
            }
            AddToPool(entry, p);
        }

        internal Entry FindEntry(string id) => Find(id);

        /// <summary>E7 npc.schedule(id, {put_first}): 挂起到下一次开店建队评估时强制生成(不看 schedule)。
        /// 接受 "包:id" 或裸 id。纯托管(只记标志), 无头/非对局调用安全。</summary>
        internal void ScheduleSpawn(string packId, string id, bool putFirst, int line)
        {
            string bare = (id ?? "").Trim();
            int ci = bare.IndexOf(':');
            if (ci > 0) bare = bare.Substring(ci + 1);
            var entry = Find(bare) ?? Find(id);
            if (entry == null)
                throw new PsRuntimeError($"npc.schedule: '{id}' 未注册(先 npc.register)", line);
            _pendingSpawn[entry.Def.Id] = putFirst;
        }

        /// <summary>测试/诊断用: 某 NPC 是否挂了待生成。</summary>
        internal bool IsPendingSpawn(string id) => _pendingSpawn.ContainsKey(id);

        /// <summary>场景离开(Plugin.OnSceneWasLoaded 调用): 清选项委托 pin 与 Dialogue 指针注册表
        /// (旧对话对象随场景销毁, 指针可能被复用 → 必须清, 否则误映射)。</summary>
        internal void OnSceneLeft()
        {
            _choicePins.Clear();
            DialogueIds.Clear();
        }

        private Entry Find(string id)
        {
            foreach (var e in _entries)
                if (e.Def.Id == id) return e;
            return null;
        }

        // ---- 生成钩子(GameHooks 转发) ----

        /// <summary>VeryEarly: 快照今日队列(差集基准)。</summary>
        internal void SnapshotQueue(StoreClientManager mgr)
        {
            try { _queueSnapshot = SnapshotPtrs(mgr); }
            catch (Exception e) { PsApi.Warn(_logger, "npc queue snapshot failed: " + e.Message); _queueSnapshot = null; }
        }

        /// <summary>VeryLate: 差集发 customer_generated(原版/池客户) → 评估脚本调度入队(自家 spawn 单独发)。</summary>
        internal void OnGenerationDone(StoreClientManager mgr)
        {
            try
            {
                var cur = SnapshotPtrs(mgr);
                if (_queueSnapshot != null && mgr?.clientStack != null)
                {
                    foreach (var sc in mgr.clientStack)
                    {
                        if (sc == null) continue;
                        long ptr;
                        try { ptr = sc.Pointer.ToInt64(); } catch { continue; }
                        if (_queueSnapshot.Contains(ptr)) continue;
                        string id = null, name = null;
                        try { id = sc.identifier; name = sc.displayName; } catch { }
                        bool ours = Find(id) != null;
                        if (ours) PsApi.Log(_logger, $"npc '{id}'({name}) 进入今日队列(source=pool)");
                        PublishCustomerGenerated(sc, id, name, ours ? "pool" : "vanilla");
                    }
                }
                _queueSnapshot = cur;
            }
            catch (Exception e) { PsApi.Warn(_logger, "npc customer_generated diff failed: " + e.Message); }

            try { EvaluateSchedules(mgr); }
            catch (Exception e) { PsApi.Warn(_logger, "npc schedule eval failed: " + e.Message); }
        }

        private static HashSet<long> SnapshotPtrs(StoreClientManager mgr)
        {
            var set = new HashSet<long>();
            var stack = mgr?.clientStack;
            if (stack == null) return set;
            foreach (var sc in stack)
            {
                if (sc == null) continue;
                try { set.Add(sc.Pointer.ToInt64()); } catch { }
            }
            return set;
        }

        private void PublishCustomerGenerated(StoreClient sc, string id, string name, string source)
        {
            _bus.Publish("psapi.customer.generated", new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["client"] = new PsClientHandle(sc),
                ["id"] = id,
                ["name"] = name,
                ["source"] = source,
            });
        }

        // ---- 调度评估(每天一次, 末相位入队) ----

        private void EvaluateSchedules(StoreClientManager mgr)
        {
            if (mgr == null) return;
            if (_entries.Count == 0) { PsApi.Log(_logger, "npc eval: skipped (无注册 NPC)"); return; }
            var ps = PlayerStore.instance; // 静态字段(安全); Instance 属性会懒创建
            if (ps == null) { PsApi.Log(_logger, "npc eval: skipped (PlayerStore 未就绪)"); return; }
            if (!GameHooks.GameDay.TryRead(out long day, out long relDay, out _))
            {
                PsApi.Log(_logger, "npc eval: skipped (读不到天数)");
                return;
            }
            int relDay0 = (int)Math.Max(0, relDay - 1); // 对齐 NpcManager/RevDeal 的 0-based

            var svcState = SaveStates.For("psapi.events.npc", _logger);
            if (svcState.Get("last_eval_day") == day.ToString())
            {
                PsApi.Log(_logger, $"npc eval: skipped (day {relDay0} 今日已评估)");
                return;
            }
            svcState.Set("last_eval_day", day.ToString());

            EnsureRegistered();
            EnsurePools();

            int spawned = 0;
            foreach (var entry in _entries)
            {
                try
                {
                    var st = SaveStates.For(entry.PackId, _logger);
                    long count = long.TryParse(st.Get($"npc.{entry.Def.Id}.count"), out long c) ? c : 0;
                    bool onceFired = st.Get($"npc.{entry.Def.Id}.once") == "1";
                    bool want = EvalEntry(entry, relDay0, count, onceFired, out string diag);
                    bool forced = _pendingSpawn.Remove(entry.Def.Id, out bool pf); // E7 npc.schedule 强制生成
                    if (forced) { want = true; diag += $" +schedule(put_first={pf})"; }
                    PsApi.Log(_logger, diag); // 常驻可见性: 每个评估日每个注册 NPC 一行
                    if (!want) continue;
                    if (Spawn(entry, forced ? "schedule+" : "schedule", forced ? pf : (bool?)null)) spawned++;
                    else PsApi.Warn(_logger, $"npc '{entry.Def.Id}' 调度生成失败(BuildClient/入队返回 false, 见上方警告)");
                }
                catch (Exception e) { PsApi.Warn(_logger, $"npc '{entry.Def.Id}' 调度失败: {e.Message}"); }
            }
            if (spawned > 0) PsApi.Log(_logger, $"npc: day {relDay0} spawned {spawned} script client(s)");
            SaveStates.FlushAll(_logger);
        }

        /// <summary>单个 NPC 的评估 + 诊断行(测试台直接驱动; diag 格式: npc eval: &lt;id&gt; day=N eligible=.. roll=../chance can_spawn=.. → SPAWN/skip(原因))。</summary>
        internal bool EvalEntry(Entry entry, int relDay0, long count, bool onceFired, out string diag)
        {
            var def = entry.Def;
            bool pass = EvalSchedule(def.Schedule, relDay0, count, onceFired, _rng,
                out bool eligible, out double? roll, out string why);
            bool cs = pass && PassCanSpawn(entry);
            string rollStr = roll.HasValue ? roll.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "-";
            string chanceStr = (def.Schedule?.Chance ?? 1.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            string verdict = pass && cs ? "SPAWN" : "skip(" + (pass ? "can_spawn=false" : why) + ")";
            diag = $"npc eval: {def.Id} day={relDay0} eligible={(eligible ? "true" : "false")} " +
                   $"roll={rollStr}/{chanceStr} can_spawn={(cs ? "true" : "false")} → {verdict}";
            return pass && cs;
        }

        /// <summary>can_spawn 脚本函数: 异常/非函数按不生成处理(警告一次)。</summary>
        private bool PassCanSpawn(Entry entry)
        {
            if (entry.Def.CanSpawn == null) return true;
            try
            {
                entry.Itp.BeginRun();
                return PsValues.Truthy(entry.Itp.CallCallable(entry.Def.CanSpawn, new List<object>(), 0));
            }
            catch (Exception e)
            {
                PsApi.Warn(_logger, $"npc '{entry.Def.Id}' can_spawn 出错(本次不生成): {e.Message}");
                return false;
            }
        }

        // ---- 生成与入队(ExtraPerks 验证模式, 与 NpcManager.BuildClient 同序) ----

        private bool Spawn(Entry entry, string source, bool? putFirstOverride = null)
        {
            var sc = BuildClient(entry);
            if (sc == null) return false; // BuildClient 内部已 Warn 具体原因
            var mgr = PlayerStore.instance?.storeClientManager;
            if (mgr == null)
            {
                PsApi.Warn(_logger, $"npc '{entry.Def.Id}' 生成失败: storeClientManager 不可用");
                return false;
            }
            bool putFirst = putFirstOverride ?? (entry.Def.Schedule != null && entry.Def.Schedule.PutFirst);
            if (putFirst) mgr.AddNextClient(sc);
            else mgr.AddClient(sc);

            var st = SaveStates.For(entry.PackId, _logger);
            long count = long.TryParse(st.Get($"npc.{entry.Def.Id}.count"), out long c) ? c : 0;
            st.Set($"npc.{entry.Def.Id}.count", (count + 1).ToString());
            if (entry.Def.Schedule != null && entry.Def.Schedule.Mode == "once")
                st.Set($"npc.{entry.Def.Id}.once", "1");
            try { PublishCustomerGenerated(sc, sc.identifier, sc.displayName, source); } catch { }
            return true;
        }

        private StoreClient BuildClient(Entry entry)
        {
            var def = entry.Def;
            StoreClient sc = null;
            string tpl = def.BaseTemplate;
            if (!string.IsNullOrEmpty(tpl))
            {
                try { sc = StoreClientListDict.CreateStoreClient(tpl); }
                catch (Exception e) { PsApi.Warn(_logger, $"npc '{def.Id}': 模板 '{tpl}' 克隆异常: {e.Message}"); sc = null; }
                if (sc == null) PsApi.Warn(_logger, $"npc '{def.Id}': 模板 '{tpl}' 无效, 回退奸商模板");
            }
            if (sc == null)
            {
                try { sc = StoreClientList.CreateShadyMerchant(); }
                catch (Exception e) { PsApi.Warn(_logger, $"npc '{def.Id}': 奸商模板兜底异常: {e.Message}"); }
            }
            if (sc == null) { PsApi.Warn(_logger, $"npc '{def.Id}': 无法创建任何模板客户"); return null; }

            ApplyFaction(sc, def.Faction, def.Id);
            try { sc.CompleteClientCreation(true); }
            catch (Exception e) { PsApi.Warn(_logger, $"npc '{def.Id}' CompleteClientCreation: {e.Message}"); }

            try { ApplyDef(sc, entry); }
            catch (Exception e)
            {
                PsApi.Err(_logger, $"npc '{def.Id}' 应用定义出错, 退回未修改模板客户: {e.Message}");
                return sc; // 同 NpcManager 回退语义: 绝不上半残客户
            }
            return sc;
        }

        private void ApplyFaction(StoreClient sc, string faction, string id)
        {
            if (string.IsNullOrEmpty(faction)) return;
            try
            {
                switch (faction.Trim().ToLowerInvariant())
                {
                    case "scav": StoreClientFactionSetup.InitScav(sc); break;
                    case "lower": case "lower_level": StoreClientFactionSetup.InitLowerLevelCitizen(sc); break;
                    case "security": StoreClientFactionSetup.InitSecurity(sc); break;
                    case "upper": case "upper_level": StoreClientFactionSetup.InitUpperLevelCitizen(sc); break;
                    case "tourist": StoreClientFactionSetup.InitTourist(sc); break;
                    case "rev": StoreClientFactionSetup.InitRev(sc); break;
                    case "black_market": case "blackmarket": StoreClientFactionSetup.InitBlackMarket(sc); break;
                    case "cartel": StoreClientFactionSetup.InitCartel(sc); break;
                    default: PsApi.Warn(_logger, $"npc '{id}': 未知阵营 '{faction}'(保留模板阵营)"); break;
                }
            }
            catch (Exception e) { PsApi.Warn(_logger, $"npc '{id}' 阵营初始化失败: {e.Message}"); }
        }

        private void ApplyDef(StoreClient sc, Entry entry)
        {
            var def = entry.Def;
            sc.identifier = def.Id;
            sc.displayName = !string.IsNullOrEmpty(def.Name) ? def.Name : def.Id;

            // 立绘按 id 哈希固定(同 NpcManager: 防每次换脸); FNV-1a 稳定哈希 —— GetHashCode 在
            // net6 按进程随机化(重启即变脸), 不可用
            try
            {
                if (def.Sprites != null && def.Sprites.Count > 0)
                {
                    var l = new Il2CppSystem.Collections.Generic.List<string>();
                    foreach (var s in def.Sprites) l.Add(s);
                    sc.possibleSprites = l;
                    sc.spriteName = !string.IsNullOrEmpty(def.Sprite) ? def.Sprite : l[(int)(Fnv1a(def.Id) % (uint)l.Count)];
                }
                else if (!string.IsNullOrEmpty(def.Sprite))
                {
                    sc.spriteName = def.Sprite;
                    var single = new Il2CppSystem.Collections.Generic.List<string>();
                    single.Add(def.Sprite);
                    sc.possibleSprites = single;
                }
                else
                {
                    // 未配皮肤: 原版模板 possibleSprites 池全空(旧代码对空池取模 = 死代码)。改为以工厂
                    // 已掷好的 spriteName(如 maleScav8)剥尾部数字得前缀, 从 SpriteDict 筛同前缀皮肤,
                    // FNV-1a 取模固定一张; 字典拿不到(场景未加载/无前缀匹配)就保留工厂掷的随机皮, 不崩
                    PinSpriteFromDict(sc, def.Id);
                }
            }
            catch (Exception e) { PsApi.Warn(_logger, $"npc '{def.Id}' 立绘: {e.Message}"); }

            if (!string.IsNullOrEmpty(def.Intent))
            {
                if (Enum.TryParse<StoreClient.ClientIntent>(def.Intent, true, out var ci))
                    sc.clientIntent = ci;
                else
                    PsApi.Warn(_logger, $"npc '{def.Id}': 未知意图 '{def.Intent}'");
            }

            // 经济(budget 优先; 只写 cash = 现金制)
            if (def.Budget != null && def.Budget.Length > 0)
            {
                int amt = (int)def.Budget[0];
                int range = def.Budget.Length > 1 ? (int)def.Budget[1] : 0;
                try { sc.SetClientBudget(amt, range); sc.useClientBudget = true; }
                catch (Exception e) { PsApi.Warn(_logger, $"npc '{def.Id}' SetClientBudget: {e.Message}"); }
            }
            if (def.Cash.HasValue)
            {
                sc.clientCash = (int)def.Cash.Value;
                if (def.Budget == null) sc.useClientBudget = false;
            }
            // auto_leave 盲区(调研 B): BUY 客户只配 cash 时 useClientBudget=false → OnDealAccepted 的
            // done 判定永不命中 → allDone 不播、永不离开。预算=现金顶格(花光即走), 上限语义不变
            if (def.AutoLeave && def.Budget == null && def.Cash.HasValue
                && string.Equals(def.Intent, "BUY", StringComparison.OrdinalIgnoreCase))
            {
                try { sc.SetClientBudget((int)def.Cash.Value, 0); sc.useClientBudget = true; }
                catch (Exception e) { PsApi.Warn(_logger, $"npc '{def.Id}' 现金制 BUY 预算兜底: {e.Message}"); }
            }
            // v1.13.0: 清模板继承的交易回调/特性清单 —— 自定义 NPC 一律白板起步。
            // 实证: 老猫·影克隆 shadyMerchant → 继承 <CreateShadyMerchant>b__33_1 摆货回调
            // (对每件上架货按 ContrabandHelper.GetContrabandLevel 挂 DiscountedLow/Mid/High/CriticalContraband
            // 压价特性, 组装狙击枪 critical 档 = 0元购); 其他模板同理带私有回调/收购特性清单。
            // 须在下方价格块(写 buyPriceModifier/挂溢价特性)之前清, 否则清掉自己刚挂的。
            try { sc.onItemAddedToClient = null; } catch { }
            try { sc.onItemAddedToWeighted = null; } catch { }
            try { sc.OnItemAddedToShowcase = null; } catch { }
            try { sc.clientItemFeatureBuying?.Clear(); } catch { }
            try { sc.sellPriceModifier = 0; } catch { } // 无新键映射, 恒置 0 中和模板残留
            // v1.12.0 价格体系重写(9-18 实证):
            // · buyPriceModifier 已被原版消费 —— 客户货物摆上柜台/展示区时每件挂「顾客折扣」特性,
            //   玩家买单价 = 基础价值 × (1 + buyMod/100)(probe dump 实证: buyMod=115 → 9→19)。
            //   旧键 buy_price_mod=110~125 因此变成玩家买单 +10~25% 加价(bug 根源), 废弃。
            // · unitValue 摆货折价(v1.11.0 TryScaleValue)在 9-18 不生效(摆上的实例 unitValue 仍是基础值),
            //   单买折扣改走 buyPriceModifier 负数 —— 原版公式天然支持, tooltip 正常显示「顾客折扣」。
            if (def.BuyPriceMod.HasValue || def.SellPriceMod.HasValue)
                PsApi.Warn(_logger, $"npc '{def.Id}': buy_price_mod/sell_price_mod 已废弃(9-18 起 buyPriceModifier=玩家买单加价%), 请改用 price.sell_single(买单系数)/price.buy(收购系数)");
            // price.sell_single(默认 1.0) → buyPriceModifier = (系数-1)×100, 恒写(覆盖模板残留)
            try { sc.buyPriceModifier = (int)Math.Round((def.PriceSellSingle - 1.0) * 100.0); } catch { }
            // price.buy(默认 1.0) → 收购溢价: clientItemFeatureBuying + ItemFeatureList.SpecialFeature
            // 注入自定义档位(原版退休农夫模板同款机制, premiumBuy15=+15%; 无 +5% 档故自建)
            if (def.PriceBuy != 1.0)
                TryAddBuyPremium(sc, def.Id, (int)Math.Round((def.PriceBuy - 1.0) * 100.0));
            // 卖给自定义 NPC 享受原版零售加价(和原版客户一致): 确保接受零售加价
            try { sc.acceptRetailMarkup = true; } catch { }
            if (def.MultiBuyDisabled.HasValue) try { sc.isMultiBuyDisabled = def.MultiBuyDisabled.Value; } catch { }
            if (def.NoContraband.HasValue) try { sc.noAcceptingContraband = def.NoContraband.Value; } catch { }

            // 收购清单(自定义默认清空模板清单, 再追加); v1.11.0: buy_pool 当日加权不放回掷 buy_count 种,
            // 与 buying_ids(必收)合并; 掷中子集缓存供 {buy_list} 台词插值。此处在对局内(客户生成时),
            // DirectoryMaster 已就绪 → RollBuyingList 内做 id 存在性校验(无效 id 跳过+告警)
            var buyIds = RollBuyingList(def, sc);
            FillStrList(sc, buyIds, def.ClearBuying, 0);
            FillStrList(sc, def.BuyingTags, def.ClearBuying, 1);
            FillStrList(sc, def.BuyingFeatures, def.ClearBuying, 2);
            FillStrList(sc, def.BlackIds, false, 3);
            FillStrList(sc, def.BlackTags, false, 4);

            // 出售货物: 动态函数优先, 静态清单(可叠 sell_pool 补齐种数)次之; 生成时一次性掷概率(与 NpcManager 一致)
            List<SellSpec> plan = null;
            if (def.SellItemsFn != null)
                plan = RollDynamicSellPlan(entry);
            else if (def.SellItems != null || def.SellPool != null)
                plan = RollStaticSellPlan(def);
            if (plan != null)
            {
                // v1.12.0: 单买折扣不再随计划折算 unitValue(9-18 不生效), 改在上方统一写 buyPriceModifier
                _rolledByPtr[sc.Pointer.ToInt64()] = new RolledPlan { Specs = plan };
                bool hasSellBiz = plan.Count > 0 || (def.Intent ?? "").IndexOf("sell", StringComparison.OrdinalIgnoreCase) >= 0;
                if (hasSellBiz)
                {
                    sc.isRegularSellDisable = false;
                    // price.sell_bulk: 开原版批发按钮(整桌买下), 折扣补足差额 —
                    // 批发总价 = Σ(已按 sell_single 折算的单价) × (1 + wholesaleDiscount/100) → 落到 sell_bulk 系数
                    int wd = WholesaleDiscountPct(def.PriceSellSingle, def.PriceSellBulk);
                    if (wd != 0)
                        try { sc.isOfferWholesale = true; sc.wholesaleDiscount = wd; }
                        catch (Exception e) { PsApi.Warn(_logger, $"npc '{def.Id}' 批发设置失败: {e.Message}"); }
                }
            }

            // 清模板残留的商洽门槛/到店动作(NpcManager 自定义客户同款)
            try { sc.clientNegociationCheck?.Clear(); } catch { }
            try { sc.OnArrival = null; } catch { }

            ApplyDialogues(sc, entry);
        }

        /// <summary>FNV-1a 32 位稳定哈希(UTF-8 字节流; offset 2166136261, prime 16777619)。
        /// 跨进程/重启一致 —— 立绘固定等"同 id 同结果"场景专用, 别用 GetHashCode(net6 按进程随机化)。</summary>
        internal static uint Fnv1a(string s)
        {
            uint h = 2166136261u;
            if (string.IsNullOrEmpty(s)) return h;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s))
                h = (h ^ b) * 16777619u;
            return h;
        }

        /// <summary>未配皮肤的 NPC: 以工厂掷好的 spriteName 前缀(剥尾部数字, maleScav8 → maleScav)在
        /// SpriteDict 里筛同前缀皮肤, FNV-1a(id) 取模固定一张写入 spriteName + possibleSprites 缩单元素。
        /// SpriteDict 不可用/无前缀匹配/无数字后缀 = 保留现状, 不崩。</summary>
        private void PinSpriteFromDict(StoreClient sc, string id)
        {
            string cur = null;
            try { cur = sc.spriteName; } catch { }
            if (string.IsNullOrEmpty(cur)) return;
            int cut = cur.Length;
            while (cut > 0 && cur[cut - 1] >= '0' && cur[cut - 1] <= '9') cut--;
            if (cut == 0 || cut == cur.Length) return; // 纯数字或无数字后缀: 已是确定皮肤
            string prefix = cur.Substring(0, cut);
            List<string> pool;
            try
            {
                var sd = SpriteDict.Instance;
                if (sd is null) return; // Unity 重载 == 会触发 Il2Cpp 静态初始化, 纯引用判空(§6 教训)
                var dict = sd.spriteDictionary;
                if (dict == null || dict.Count == 0) return;
                pool = new List<string>();
                foreach (var k in dict.Keys)
                    if (!string.IsNullOrEmpty(k) && k.StartsWith(prefix, StringComparison.Ordinal))
                        pool.Add(k);
            }
            catch { return; } // 场景未加载/字典未建: 保留工厂掷的随机皮
            if (pool.Count == 0) return;
            pool.Sort(StringComparer.Ordinal); // 字典枚举序不稳, 排序保证取模结果跨会话一致
            string pin = pool[(int)(Fnv1a(id) % (uint)pool.Count)];
            try
            {
                sc.spriteName = pin;
                var single = new Il2CppSystem.Collections.Generic.List<string>();
                single.Add(pin);
                sc.possibleSprites = single;
            }
            catch { }
        }

        private List<SellSpec> RollStaticSellPlan(NpcDef def)
        {
            var plan = new List<SellSpec>();
            if (def.SellItems != null)
                foreach (var e in def.SellItems)
                    if (e.Chance >= 1.0 || _rng.NextDouble() < e.Chance)
                        // 掷好的数量落成新实例(def.SellItems 是跨客户共享配置, 不能改)
                        plan.Add(new SellSpec { Id = e.Id, Count = RollCount(e, _rng), Uses = e.Uses, Src = "sell_items" });
            // v1.10.0: sell_pool 加权不放回补齐到 sell_count 种(池条目每种数量 1-2 随机);
            // "种数"按 id 计, sell_items 已掷中的 id 先从候选剔除
            if (def.SellPool != null && def.SellPool.Count > 0)
            {
                int target = def.SellCountMax > def.SellCountMin
                    ? _rng.Next((int)def.SellCountMin, (int)def.SellCountMax + 1) : (int)def.SellCountMin;
                var kinds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in plan) kinds.Add(p.Id);
                int need = target - kinds.Count;
                if (need > 0)
                {
                    var avail = def.SellPool.FindAll(p => !kinds.Contains(p.Id));
                    foreach (var pick in RollPool(avail, need, _rng))
                        plan.Add(new SellSpec { Id = pick.Id, Count = _rng.Next(1, 3), Src = "sell_pool" });
                }
            }
            return plan;
        }

        private List<SellSpec> RollDynamicSellPlan(Entry entry)
        {
            try
            {
                entry.Itp.BeginRun();
                var ret = entry.Itp.CallCallable(entry.Def.SellItemsFn, new List<object>(), 0);
                var specs = ParseSellItems(ret, 0);
                var plan = new List<SellSpec>();
                foreach (var e in specs)
                    if (e.Chance >= 1.0 || _rng.NextDouble() < e.Chance)
                        plan.Add(new SellSpec { Id = e.Id, Count = RollCount(e, _rng), Uses = e.Uses, Src = "sell_items(fn)" });
                return plan;
            }
            catch (Exception e)
            {
                PsApi.Warn(_logger, $"npc '{entry.Def.Id}' 动态 sell_items 出错(本次无货): {e.Message}");
                return new List<SellSpec>();
            }
        }

        /// <summary>v1.11.0 price.sell_bulk → 原版批发折扣百分比(负=便宜): 单件已按 sell_single 折算进
        /// unitValue, 批发总价系数 sell_bulk → 折扣补足差额。bulk >= single = 0(不开批发); 结果钳制 [-99,-1]。</summary>
        internal static int WholesaleDiscountPct(double single, double bulk)
        {
            if (bulk >= single) return 0;
            int pct = (int)Math.Round((1.0 - bulk / single) * 100.0);
            return -Math.Clamp(pct, 1, 99);
        }

        /// <summary>v1.11.0: 当日收购清单 = buying_ids(必收) ∪ buy_pool 加权不放回掷 buy_count 种
        /// (剔除必收已有的); buy_pool 掷中子集按客户指针缓存({buy_list} 台词插值只点当日随机加收,
        /// 必收项在包台词里写死, 避免复读)。无 buy_pool = 原样返回 buying_ids。
        /// 此处在对局内(客户生成时 DirectoryMaster 已就绪)逐 id 存在性校验: 无效 id 跳过+告警。</summary>
        private List<string> RollBuyingList(NpcDef def, StoreClient sc)
        {
            if (def.BuyingIds == null && def.BuyingPool == null)
            {
                // 无收购配置: 返回 null(FillStrList null+!clear = 模板清单原样不动); 顺带清指针缓存
                // (Il2Cpp 对象销毁后指针可能复用, 防上一个客户的 {buy_list} 掷中残留)
                try { _rolledBuyPoolByPtr.Remove(sc.Pointer.ToInt64()); } catch { }
                return null;
            }
            var rolled = new List<string>();
            if (def.BuyingIds != null)
                foreach (var id in def.BuyingIds)
                {
                    if (ItemExistsRuntime(id)) rolled.Add(id);
                    else WarnMissingOnce(id, $"npc {def.Id} buying_ids");
                }
            var poolPicks = new List<string>();
            if (def.BuyingPool != null && def.BuyingPool.Count > 0)
            {
                int target = def.BuyCountMax > def.BuyCountMin
                    ? _rng.Next((int)def.BuyCountMin, (int)def.BuyCountMax + 1) : (int)def.BuyCountMin;
                var kinds = new HashSet<string>(rolled, StringComparer.Ordinal);
                var avail = def.BuyingPool.FindAll(p => !kinds.Contains(p.Id));
                foreach (var pick in RollPool(avail, target, _rng))
                {
                    if (!ItemExistsRuntime(pick.Id)) { WarnMissingOnce(pick.Id, $"npc {def.Id} buy_pool"); continue; }
                    if (kinds.Add(pick.Id)) { rolled.Add(pick.Id); poolPicks.Add(pick.Id); }
                }
            }
            try { _rolledBuyPoolByPtr[sc.Pointer.ToInt64()] = poolPicks; } catch { }
            return rolled;
        }

        /// <summary>DirectoryMaster.Has 存在性检查(不构建实例, 模组 id 含包前缀原样可查 — 注册即全名入目录)。
        /// 仅执行点(对局内)调用; 加载期 DirectoryMaster 未就绪, 校验会全是假阳性。</summary>
        internal static bool ItemExistsRuntime(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            try { return DirectoryMaster.Has<GameItem>(id); } catch { return false; }
        }

        private void WarnMissingOnce(string id, string source)
        {
            if (_warnedMissing.Add(source + "|" + id))
                PsApi.Warn(_logger, $"[psapi] 物品 id '{id}' 在当前版本不存在, 已跳过 (来源: {source})");
        }

        // ---- v1.12.0 收购溢价(price.buy): clientItemFeatureBuying 特性 id → ItemFeatureList.SpecialFeature 工厂字典 ----
        private static readonly List<object> _premiumPins = new List<object>(); // 委托 pin(全局注册一次, 永不释放)
        private static readonly HashSet<int> _premiumRegistered = new HashSet<int>();

        /// <summary>给客户挂收购溢价特性(原版 retired_farmer 模板 premiumBuy15 同款机制:
        /// AddClientItemBuyingFeature 按 clientItemFeatureBuying 里的 id 查 SpecialFeature 字典,
        /// 调工厂取特性挂到玩家出售物上)。原版字典无任意百分比档 → 按 pct 注入自定义工厂
        /// (克隆 premiumBuy15 改 valueModifier/identifier, 显示名沿用原版「供应商加价」本地化);
        /// pct 为负 = 收购折价(克隆 discountBuy15)。</summary>
        private void TryAddBuyPremium(StoreClient sc, string npcId, int pct)
        {
            if (pct == 0) return;
            try
            {
                string fid = EnsurePremiumFeature(pct);
                var list = sc.clientItemFeatureBuying;
                if (list == null)
                {
                    list = new Il2CppSystem.Collections.Generic.List<string>();
                    sc.clientItemFeatureBuying = list;
                }
                if (!list.Contains(fid)) list.Add(fid);
            }
            catch (Exception e) { PsApi.Warn(_logger, $"npc '{npcId}' 收购溢价({pct}%)挂载失败: {e.Message}"); }
        }

        private static string EnsurePremiumFeature(int pct)
        {
            string fid = pct > 0 ? $"psapi_premium_buy_{pct}" : $"psapi_discount_buy_{-pct}";
            var dict = ItemFeatureList.SpecialFeature;
            if (dict == null) throw new InvalidOperationException("ItemFeatureList.SpecialFeature 未就绪");
            if (_premiumRegistered.Contains(pct)) return fid;
            bool has = false;
            try { has = dict.ContainsKey(fid); } catch { }
            if (!has)
            {
                Func<ItemFeature> managed = () =>
                {
                    var f = pct > 0 ? ItemFeatureList.PremiumBuy15() : ItemFeatureList.DiscountBuy15();
                    f.valueModifier = pct;
                    f.identifier = fid;
                    return f;
                };
                var del = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<ItemFeature>>(managed);
                _premiumPins.Add(managed);
                _premiumPins.Add(del);
                dict[fid] = del;
            }
            _premiumRegistered.Add(pct);
            return fid;
        }

        /// <summary>{buy_list} 台词插值: buy_pool 当日掷中 id 的显示名(「名」、分隔); 无掷中/无池 = null(不替换)。</summary>
        private string BuyPoolNames(StoreClient sc)
        {
            try
            {
                if (sc is null) return null;
                if (!_rolledBuyPoolByPtr.TryGetValue(sc.Pointer.ToInt64(), out var picks) || picks == null || picks.Count == 0)
                    return null;
                var names = new List<string>();
                foreach (var id in picks)
                {
                    string n = null;
                    try { n = Items.ItemsFacade.DisplayName(id); } catch { }
                    names.Add(string.IsNullOrEmpty(n) ? id : n);
                }
                return string.Join("、", names);
            }
            catch { return null; }
        }

        private void FillStrList(StoreClient sc, List<string> ids, bool clear, int kind)
        {
            if (ids == null && !clear) return;
            try
            {
                Il2CppSystem.Collections.Generic.List<string> l;
                switch (kind)
                {
                    case 0: l = sc.clientBuyingIdList; if (l == null) { l = new Il2CppSystem.Collections.Generic.List<string>(); sc.clientBuyingIdList = l; } break;
                    case 1: l = sc.clientBuyingTagList; if (l == null) { l = new Il2CppSystem.Collections.Generic.List<string>(); sc.clientBuyingTagList = l; } break;
                    case 2: l = sc.clientItemFeatureBuying; if (l == null) { l = new Il2CppSystem.Collections.Generic.List<string>(); sc.clientItemFeatureBuying = l; } break;
                    case 3: l = sc.clientBlackIdList; if (l == null) { l = new Il2CppSystem.Collections.Generic.List<string>(); sc.clientBlackIdList = l; } break;
                    default: l = sc.clientBlackTagList; if (l == null) { l = new Il2CppSystem.Collections.Generic.List<string>(); sc.clientBlackTagList = l; } break;
                }
                if (clear) l.Clear();
                if (ids != null)
                    foreach (var s in ids)
                        if (!l.Contains(s)) l.Add(s);
            }
            catch (Exception e) { PsApi.Warn(_logger, $"npc 清单填充(kind={kind}): {e.Message}"); }
        }

        private void ApplyDialogues(StoreClient sc, Entry entry)
        {
            var def = entry.Def;
            if (def.Dialogues == null) return;
            string nm = sc.displayName;
            foreach (var kv in def.Dialogues)
            {
                try
                {
                    var cand = kv.Value[_rng.Next(0, kv.Value.Count)];
                    // v1.11.0: {buy_list} 占位符 → buy_pool 当日掷中种的显示名(无池/无掷中 = "暂无特别加收")
                    string buyNames = null;
                    foreach (var seg in cand.Segments)
                        if (seg != null && seg.Contains("{buy_list}")) { buyNames = BuyPoolNames(sc) ?? "暂无特别加收"; break; }
                    string SegAt(int idx)
                        => buyNames == null ? cand.Segments[idx] : cand.Segments[idx].Replace("{buy_list}", buyNames);
                    // 多段候选 = NextDialogue 链(原版分段对话: OnCurrentTextDisplayed 播完自动推进),
                    // choices / endAction 只在链尾有效(原版机制), 一律挂链尾
                    var head = new Dialogue();
                    head.SetText(nm, SegAt(0));
                    Dialogue tail = head;
                    for (int i = 1; i < cand.Segments.Count; i++)
                        tail = tail.NextDialogue().SetText(nm, SegAt(i));
                    switch (DialogueChannels[kv.Key])
                    {
                        case 0:
                            if (def.Choices != null && def.Choices.Count > 0)
                                BuildChoices(tail, def, sc, entry, nm);
                            else if (def.AutoLeave && sc.clientIntent == StoreClient.ClientIntent.DIALOGUE)
                                // 仅纯聊天客户挂"说完就走"; 交易客户(BUY/SELL/SELLNBUY)离开走
                                // 成交/拒收 → allDone 链的原版流程, 这里挂了会"话没说完就走"
                                tail.SetEndAction(AutoLeaveAction());
                            sc.mainDialogue = head;
                            break;
                        case 1:
                            // acceptDealDialogue 原版不挂离开 endAction(成交后还有交割流程, 离开由
                            // OnDealAccepted → allDone 链负责), 勿挂
                            sc.acceptDealDialogue = head;
                            sc.acceptDealDialogueLastLine = tail;
                            sc.acceptDealDialoguePlayed = false;
                            break;
                        case 2:
                            // 覆盖模板克隆自带的 allDone(其 endAction 是原版的离开动作) → 必须补挂, 否则永不离开
                            if (def.AutoLeave) tail.SetEndAction(AutoLeaveAction());
                            sc.allDoneDialogue = head;
                            break;
                        case 3: sc.repeatDialogue = head; break;
                        case 4: sc.placedWrongItemWhenSellingToDialogue = head; break;
                        case 5: sc.placeRightItemWhenSellingToDialogue = head; break;
                        case 6: sc.interogationDialogue = head; break;
                        case 7: sc.glasseDialogue = head; break;
                        case 8: sc.customOnArrestDialogue = head; break;
                    }
                }
                catch (Exception e) { PsApi.Warn(_logger, $"npc '{def.Id}' 对话通道 {kv.Key}: {e.Message}"); }
            }
        }

        // ---- 自动离开(说完就走): 共享静态委托, 不捕获实例 → 全局只 ConvertDelegate+pin 一份 ----
        private static Il2CppSystem.Action _autoLeave;
        private static readonly List<object> _autoLeavePins = new List<object>();

        /// <summary>延迟 1s 离场(与原版 StoreClient.OnDealAccepted → CurrentClientLeave 同款);
        /// DialogUIManager 不可用(场景切换等)时静默, 不崩。</summary>
        private static Il2CppSystem.Action AutoLeaveAction()
        {
            if (_autoLeave != null) return _autoLeave;
            Action managed = () => { try { DialogUIManager.Instance?.CurrentClientLeave(1f); } catch { } };
            var del = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(managed);
            _autoLeavePins.Add(managed);
            _autoLeavePins.Add(del);
            _autoLeave = del;
            return del;
        }

        /// <summary>E7: main 对话挂选项 (RevDeal AddChoiceSafe 同款机制)。
        /// 选项 Action 只标 isBusinessComplete(对话完即离店); 剧情效果全部走 SelectChoice 补丁发的
        /// dialogue_choice 事件 → 包脚本 on dialogue_choice(): (dict {dialogue_id, choice, npc_id, ...})。
        /// dialogue_id = 包:配置id(未配 id = 包:npcId:main), 经 DialogueIds 指针注册表供补丁查。
        /// v1.11.0: choices[].cond 函数在构建时调用, 返回 falsy = 该选项不显示(出错按不显示+告警);
        /// 注意 event.choice 是运行时显示数组下标, 被隐藏的选项会使后续选项前移, 脚本判定须自担。</summary>
        private void BuildChoices(Dialogue d, NpcDef def, StoreClient sc, Entry entry, string nm)
        {
            try
            {
                d.isDummyChoiceDialogue = true; // 选项非物品交割(纯选择)
                string dlgId = entry.PackId + ":" + (!string.IsNullOrEmpty(def.DialogueId) ? def.DialogueId : def.Id + ":main");
                for (int i = 0; i < def.Choices.Count; i++)
                {
                    var c = def.Choices[i];
                    if (c.Cond != null)
                    {
                        bool show = false;
                        try
                        {
                            entry.Itp.BeginRun();
                            show = PsValues.Truthy(entry.Itp.CallCallable(c.Cond, new List<object>(), 0));
                        }
                        catch (Exception e) { PsApi.Warn(_logger, $"npc '{def.Id}' 选项({c.Label}) cond 出错(不显示): {e.Message}"); }
                        if (!show) continue;
                    }
                    GameItem key;
                    try { key = DirectoryMaster.Item(!string.IsNullOrEmpty(c.Key) ? c.Key : ChoiceKeyIds[i % ChoiceKeyIds.Length], true); }
                    catch (Exception e) { PsApi.Warn(_logger, $"npc '{def.Id}' 选项({c.Label})身份键物品失败: {e.Message}"); continue; }
                    Dialogue next = null;
                    if (!string.IsNullOrEmpty(c.Next))
                    {
                        next = new Dialogue();
                        next.SetText(nm, c.Next);
                    }
                    Action managed = () => { try { if (!(sc is null)) sc.isBusinessComplete = true; } catch { } };
                    var del = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(managed);
                    _choicePins.Add(managed);
                    _choicePins.Add(del);
                    d.AddChoice(key, c.Label, c.Desc ?? "", next, del);
                }
                DialogueIds[d.Pointer.ToInt64()] = dlgId;
            }
            catch (Exception e) { PsApi.Warn(_logger, $"npc '{def.Id}' 选项构建失败(退回纯文本对话): {e.Message}"); }
        }

        // ---- 模板注册(storeClientDict)与加权池(惰性, 进对局后每次生成评估时确保) ----

        private Il2CppSystem.Func<StoreClient> FactoryOf(Entry entry)
        {
            if (entry.IlFactory != null) return entry.IlFactory;
            Func<StoreClient> managed = () =>
            {
                // 工厂被调用 = 原版加权池抽中(或原版系统按 id 从 storeClientDict 创建)
                PsApi.Log(_logger, $"npc '{entry.Def.Id}' 工厂被调用(池抽中/按 id 创建)");
                return BuildClient(entry);
            };
            var del = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<StoreClient>>(managed);
            _pinned.Add(managed);
            _pinned.Add(del);
            entry.IlFactory = del;
            return del;
        }

        private void EnsureRegistered()
        {
            Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Func<StoreClient>> dict;
            try { dict = StoreClientListDict.storeClientDict; } catch { return; }
            if (dict == null) return;
            foreach (var entry in _entries)
            {
                try
                {
                    bool want = entry.Def.Register || entry.Def.Pools.Count > 0; // 写 pools 等效 register
                    if (!want) continue;
                    if (dict.ContainsKey(entry.Def.Id)) continue; // 已注册/原版键占用 → 不覆盖
                    dict.Add(entry.Def.Id, FactoryOf(entry));
                }
                catch (Exception e) { PsApi.Warn(_logger, $"npc '{entry.Def.Id}' 模板注册: {e.Message}"); }
            }
        }

        private void EnsurePools()
        {
            foreach (var entry in _entries)
                foreach (var p in entry.Def.Pools)
                {
                    try { AddToPool(entry, p); }
                    catch (Exception e) { PsApi.Warn(_logger, $"npc '{entry.Def.Id}' 池挂载({p.Name}): {e.Message}"); }
                }
        }

        private void AddToPool(Entry entry, PoolReq p)
        {
            if (!GetPoolLists(p.Name, out var factories, out var weights) || factories == null)
            {
                if (p.Name != null) PsApi.Warn(_logger, $"npc '{entry.Def.Id}': 未知池 '{p.Name}'(可用: client/buy/sell/upper/bm)");
                return;
            }
            var del = FactoryOf(entry);
            if (factories.Contains(del)) return; // 幂等

            // 首次挂载: 量池底数换算 weight_pct 并缓存(后续跨天重挂用同一权重, 防底数含自己而漂移)
            if (p.ResolvedWeight < 0)
            {
                if (p.WeightPct >= 0)
                {
                    int w = 1;
                    if (weights != null && weights.Length > 0 && weights[0] != null)
                    {
                        long sum = 0;
                        try { foreach (var x in weights[0]) sum += x; } catch { }
                        if (sum > 0) w = Math.Max(1, (int)Math.Round(sum * p.WeightPct / 100.0));
                    }
                    p.ResolvedWeight = w;
                }
                else p.ResolvedWeight = 10;
            }

            int mounted = Math.Max(1, (int)Math.Round(p.ResolvedWeight * p.Scale)); // M2 pool_scale
            factories.Add(del);
            if (weights != null)
                for (int i = 0; i < weights.Length; i++)
                    try { weights[i]?.Add(mounted); } catch { }
            PsApi.Log(_logger, $"npc '{entry.Def.Id}' mounted into pool '{p.Name}' (weight={mounted})");
        }

        private static bool GetPoolLists(string name,
            out Il2CppSystem.Collections.Generic.List<Il2CppSystem.Func<StoreClient>> factories,
            out Il2CppSystem.Collections.Generic.List<int>[] weights)
        {
            factories = null; weights = null;
            try
            {
                switch (name)
                {
                    case "client":
                        factories = StoreClientList.clientList;
                        weights = new[] { StoreClientList.clientListProba, StoreClientList.clientListProbaII, StoreClientList.clientListProbaIII };
                        return true;
                    case "buy":
                        factories = StoreClientList.buyClientList;
                        weights = new[] { StoreClientList.buyClientListProba };
                        return true;
                    case "sell":
                        factories = StoreClientList.sellClientList;
                        weights = new[] { StoreClientList.sellClientListProba };
                        return true;
                    case "upper":
                        factories = StoreClientList.clientListUpper;
                        weights = new[] { StoreClientList.clientListProbaUpper };
                        return true;
                    case "bm":
                        factories = StoreClientList.clientListBM;
                        weights = new[] { StoreClientList.clientListProbaBM, StoreClientList.clientListProbaBMII, StoreClientList.clientListProbaBMIII };
                        return true;
                    default:
                        return false;
                }
            }
            catch { return false; }
        }

        // ---- 摆货(main 对话链末行播完/OpenUI 补丁回调) ----

        internal bool HasSellPlan(StoreClient sc)
        {
            if (sc == null) return false;
            try
            {
                return _rolledByPtr.TryGetValue(sc.Pointer.ToInt64(), out var plan) && plan != null && plan.Specs != null && plan.Specs.Count > 0;
            }
            catch { return false; }
        }

        /// <summary>客户 main 对话链末行播完(InjectService.Patch_InjectDialogueEnded 转调):
        /// 脚本 NPC sell_items 主摆货时机, 对齐原版"链末行 endAction 执行时刻摆货"。</summary>
        internal void OnClientMainDialogueEnded(Dialogue d)
        {
            if (d is null) return;
            var ps = PlayerStore.instance;
            if (ps == null) return;
            var inst = ps.currentClientInstance;
            if (inst == null || inst.storeClient == null) inst = ps.lastVisitedClient;
            var sc = inst?.storeClient;
            if (!HasSellPlan(sc)) return;
            // 脚本 NPC 链尾=话说完即可(无需 endAction: v1.9.1 起 auto_leave 仅 DIALOGUE intent 挂链尾,
            // SELL 型自定义 NPC 链尾没有 endAction, 要求它会永不摆货); 归属指针走查已够
            if (!InjectService.IsMainChainTail(sc, d, needEndAction: false)) return;
            StockClient(sc, null, "对话结束");
        }

        private void OnTradeUIOpened(NegociationUIManager ui)
        {
            var ps = PlayerStore.instance;
            if (ps == null) return;
            var inst = ps.currentClientInstance;
            if (inst == null || inst.storeClient == null) inst = ps.lastVisitedClient;
            // 兜底: 极端路径(瞬移到店等)跳过对话末行钩子时在开交易 UI 时补摆; 会话键防双份
            StockClient(inst?.storeClient, ui, "OpenUI兜底");
        }

        /// <summary>共享摆货: 每客户实例一次(_stockedSessionKey), 原生 API 上架已掷货物。
        /// v1.11.0: 摆货执行点做 id 存在性校验(无效 id 跳过+统一格式告警, 版本更新删 id 不再静默);
        /// price.sell_single 系数在摆上每件时折算进 unitValue。</summary>
        private void StockClient(StoreClient sc, NegociationUIManager ui, string where)
        {
            if (!HasSellPlan(sc)) return;
            var intent = sc.clientIntent;
            if (intent != StoreClient.ClientIntent.SELL && intent != StoreClient.ClientIntent.SELLNBUY) return;

            long key = sc.Pointer.ToInt64();
            if (key == _stockedSessionKey) return; // 每客户一次, 拖走不补
            var ps = PlayerStore.instance;
            if (ps == null) return;
            string npcId;
            try { npcId = sc.identifier; } catch { npcId = "?"; }
            var rp = _rolledByPtr[key];
            int n = 0;
            foreach (var e in rp.Specs)
            {
                for (int i = 0; i < e.Count; i++)
                {
                    GameItem item = null;
                    try { item = DirectoryMaster.Item(e.Id, false); } catch { }
                    if (item == null) { WarnMissingOnce(e.Id, $"npc {npcId} {e.Src ?? "sell_items"}"); break; }
                    try { ps.AddDirectSellingItemToTable(item, false, false, false, 0); n++; }
                    catch (Exception ex) { PsApi.Warn(_logger, $"npc 摆货失败 {e.Id}: {ex.Message}"); break; }
                }
            }
            _stockedSessionKey = key;
            if (n > 0)
            {
                PsApi.Log(_logger, $"npc '{sc.identifier}' 原生摆货 +{n} 件({where}, 每客户一次)");
                if (ui != null)
                {
                    try { ui.DispatchUpdateForCurrentIntent(); } catch { }
                    try { ui.RefreshUI(); } catch { }
                }
            }
        }

        // ==================== 补丁(前两个为 NpcManager 生产验证点位; 第三个=v1.13.1 收购特性方向闸门) ====================

        /// <summary>出售资格兜底: 有已掷货物的 SELL/SELLNBUY 脚本客户强制可卖(否则交易入口死锁)。</summary>
        [HarmonyPatch(typeof(StoreClient), "IsClientSelling")]
        internal static class Patch_NpcIsSelling
        {
            private static void Postfix(StoreClient __instance, ref bool __result)
            {
                try
                {
                    if (__result) return;
                    var svc = EventsPlugin.Instance?.Npcs;
                    if (svc == null || !svc.HasSellPlan(__instance)) return;
                    var intent = __instance.clientIntent;
                    if (intent == StoreClient.ClientIntent.SELL || intent == StoreClient.ClientIntent.SELLNBUY)
                        __result = true;
                }
                catch { }
            }
        }

        /// <summary>交易 UI 打开后: 原生摆货 API 上架脚本客户的已掷货物(兜底; 主时机=main 对话链末行播完)。</summary>
        [HarmonyPatch(typeof(NegociationUIManager), "OpenUI")]
        internal static class Patch_NpcOpenTrade
        {
            private static void Postfix(NegociationUIManager __instance)
            {
                try { EventsPlugin.Instance?.Npcs?.OnTradeUIOpened(__instance); }
                catch (Exception e)
                {
                    try { EventsPlugin.Instance?.Npcs?.LogErr("npc OpenUI postfix: " + e.Message); } catch { }
                }
            }
        }

        /// <summary>收购特性方向闸门(v1.13.1): 9-18 实证 clientItemFeatureBuying 的特性会漏挂到
        /// 客户自己的在售货品上(probe: 诺曼的速食拉面带 psapi_premium_buy_5 → 玩家买单也被加价)。
        /// 该方法的语义应只是"玩家卖给客户的物品挂收购特性", 故对脚本注册 NPC:
        /// 物品非玩家所有(客户的货) → 整个跳过; 玩家所有 → 放行(清单已白板化, 只剩我方注入档)。
        /// 原版客户一律不动。</summary>
        [HarmonyPatch(typeof(GameItem), "AddClientItemBuyingFeature")]
        internal static class Patch_AddClientBuyingFeature
        {
            private static bool Prefix(GameItem __instance)
            {
                try
                {
                    var svc = EventsPlugin.Instance?.Npcs;
                    if (svc == null) return true;
                    var ps = PlayerStore.instance;
                    var sc = ps?.currentClientInstance?.storeClient;
                    if (sc == null) return true;
                    if (svc.FindEntry(sc.identifier) == null) return true; // 原版客户不动
                    if (!GeneralHelper.IsItemOwned(__instance)) return false; // 客户自己的货: 不挂收购特性
                    return true;
                }
                catch { return true; }
            }
        }
    }
}
