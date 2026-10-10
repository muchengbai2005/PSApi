using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Il2Cpp;

namespace PSApi.Items
{
    /// <summary>
    /// E5 跨模块门面: 供 PSApi.Events (PSScript items/quality/machine 命名空间) 等外部程序集调用。
    /// 全部为薄封装: 转调 internal 实现, try/catch 友好返回 (不向外抛异常)。
    /// id 归一化: "game:xxx" (大小写不敏感) = 原版裸 id; 其他 (含 "pack:id" 全限定) 原样。
    /// 发放目标 = EmporiumEntry.backInvinvElement (后仓, F12 实证 UI 可见路径);
    /// 持有/检索范围 = PlayerStore.gridInv (随身背包) + 后仓。
    /// </summary>
    public static class ItemsFacade
    {
        /// <summary>v0.9.3: 无效物品 id 告警去重 (key = 来源|id, 同一来源同一 id 只报一次)。</summary>
        private static readonly HashSet<string> _warnedMissing = new HashSet<string>(StringComparer.Ordinal);

        private static void WarnMissingOnce(string id, string source)
        {
            if (_warnedMissing.Add(source + "|" + id))
                MelonLoader.MelonLogger.Warning($"[psapi] 物品 id '{id}' 在当前版本不存在, 已跳过 (来源: {source})");
        }

        /// <summary>"game:" 前缀 (大小写不敏感) = 原版物品, 剥前缀; 其他 id 原样 (trim)。</summary>
        public static string NormalizeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return id;
            id = id.Trim();
            return id.StartsWith("game:", StringComparison.OrdinalIgnoreCase) ? id.Substring(5) : id;
        }

        // ==================== 发放 / 持有 / 估价 ====================

        /// <summary>发放 count 个物品到玩家背包。返回最后一个成功发放的实例; 一个都没发成 = null。
        /// 走 F12 实证可见路径: 后仓 backInvinvElement + UncheckedAccept + 末尾 TransferOwnership×2;
        /// gridInv + 槽位放置返回成功但 UI 不可见 (归属未迁移), 勿用。</summary>
        public static GameItem Grant(string itemId, int count) => Grant(itemId, count, 0);

        /// <summary>v0.9.1: uses > 0 时每个发放实例启用次数机制(原生 UseCountHelper 归零销毁, 无次数折算价;
        /// 与 ItemStore 注册时 InitUseCountItem 同款)。旧双参签名保留(二进制兼容)。
        /// v0.9.6: 转调 GrantWithOpts (单一实现)。</summary>
        public static GameItem Grant(string itemId, int count, int uses)
            => GrantWithOpts(itemId, count, uses > 0 ? new ItemOpts { Uses = uses } : null);

        /// <summary>v0.9.6: 带实例定制的发放 (opts 全可选, null=等价 Grant; 每个实例都应用 opts)。</summary>
        public static GameItem GrantWithOpts(string itemId, int count, ItemOpts opts)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId) || count <= 0) return null;
                string id = NormalizeId(itemId);
                EmporiumEntry entry = null;
                try { entry = EmporiumEntry.Instance; } catch { }
                if (entry == null || entry.backInvinvElement == null) return null;
                GameItem last = null;
                for (int i = 0; i < count; i++)
                {
                    GameItem item = null;
                    try { item = DirectoryMaster.Item(id, true); } catch { }
                    if (item == null)
                    {
                        WarnMissingOnce(id, "items.give");
                        break;
                    }
                    try { item.DisableTag("TAG_NOT_PURCHASED", true); } catch { }
                    try { item.DisableTag("not_purchased", true); } catch { }
                    ApplyOpts(item, opts);
                    try { entry.backInvinvElement.TryFindOneValidInventorySlot(item, false); } catch { }
                    bool ok = false;
                    try { ok = ((GameInventory)entry.backInvinvElement).UncheckedAccept(item); } catch { }
                    if (!ok) break;
                    last = item;
                    // v0.9.8: 落点诊断 (与 GrantToCounterCore 同款), 实测核对归属用
                    string win = "?";
                    try { win = item.contentWindow != null ? "非null" : "null"; } catch { }
                    MelonLoader.MelonLogger.Msg($"[psapi] give 落点诊断: id={id} to=inventory window={win}");
                }
                if (last != null)
                {
                    try { entry.TransferOwnershipBackInv(); entry.TransferOwnedItemBackToInv(); } catch { }
                }
                return last;
            }
            catch { return null; }
        }

        /// <summary>v0.9.2: 发放 count 个物品到店门口柜台(桌面直售区), 归玩家所有。
        /// 贴原版送礼路径 CheckSkinningKnifeReward: ItemSpawner.Spawn + AddDirectSellingItemToTable(item, isOwend: true)。
        /// 不在对局(EmporiumEntry/PlayerStore 不可用) = false + 警告; 全部摆上 = true, 一件都没摆上 = false。
        /// 柜台满位时剩余物品悬空(原生 API 不处理满位, 不做回退清理)。uses > 0 时每个实例启用次数机制(同 Grant)。
        /// v0.9.7: 转调 GrantToCounterCore (单一实现)。</summary>
        public static bool GrantToCounter(string itemId, int count, int uses = 0)
            => GrantToCounterCore(itemId, count, uses, null, false);

        /// <summary>v0.9.7: GrantToCounter 的实例定制重载 (opts 在 spawn 后 ApplyOpts; opts=null 等价旧三参)。</summary>
        public static bool GrantToCounter(string itemId, int count, ItemOpts opts)
            => GrantToCounterCore(itemId, count, 0, opts, false);

        /// <summary>v0.9.7: 发放到店内内部柜台; v0.9.8 改落点: AddDirectToWeightedTable (称重台 = 店内交易柜台,
        /// 与 NetworkUpgradeList 网购送货到店同款路径)。原落点 AddDirectSellingItemToTableMiddle 实测为
        /// 原版 NPC 自带货容器 (ISIL 调用方全是 StoreClientList 系列), 客户不在场时不可见/无意义。
        /// 其余语义与 GrantToCounter 完全一致 (同 Core)。uses/opts 双通道: uses>0 优先(旧式), 否则走 opts.Uses。</summary>
        public static bool GrantToCounterMiddle(string itemId, int count, int uses = 0, ItemOpts opts = null)
            => GrantToCounterCore(itemId, count, uses, opts, true);

        /// <summary>门口/店内柜台发放共用实现 (middle=false=门口外部柜台 AddDirectSellingItemToTable,
        /// true=店内称重台 AddDirectToWeightedTable, v0.9.8 起; 见 GrantToCounterMiddle 注释)。</summary>
        private static bool GrantToCounterCore(string itemId, int count, int uses, ItemOpts opts, bool middle)
        {
            string api = middle ? "items.give_counter_middle" : "items.give_counter";
            try
            {
                if (string.IsNullOrWhiteSpace(itemId) || count <= 0) return false;
                string id = NormalizeId(itemId);
                PlayerStore store = null;
                try
                {
                    if (EmporiumEntry.Instance == null) return false; // 不在对局
                    store = PlayerStore.instance; // 静态字段(安全); Instance 属性会懒创建
                }
                catch { return false; }
                if (store == null)
                {
                    MelonLoader.MelonLogger.Warning($"[psapi] {api}: PlayerStore 不可用(不在对局?), 发放取消");
                    return false;
                }
                int n = 0;
                for (int i = 0; i < count; i++)
                {
                    GameItem item = null;
                    try { item = ItemSpawner.Spawn(id); } catch { }
                    if (item == null)
                    {
                        WarnMissingOnce(id, api);
                        break;
                    }
                    // v0.9.7: opts 在 spawn 后应用; uses 参数(旧式)优先于 opts.Uses (与 ApplyOpts usesOverride 语义一致)
                    ApplyOpts(item, opts, uses > 0 ? uses : -1);
                    try
                    {
                        if (middle) store.AddDirectToWeightedTable(item, true);   // v0.9.8: 称重台 (原为 TableMiddle, NPC 自带货容器不可见)
                        else store.AddDirectSellingItemToTable(item, true);
                        n++;
                        // v0.9.8: 落点诊断 (save reattach check 同款风格), 实测核对归属用
                        string win = "?";
                        try { win = item.contentWindow != null ? "非null" : "null"; } catch { }
                        MelonLoader.MelonLogger.Msg($"[psapi] give 落点诊断: id={id} to={(middle ? "counter_in" : "counter_out")} window={win}");
                    }
                    catch (Exception e)
                    {
                        MelonLoader.MelonLogger.Warning($"[psapi] {api}: 摆柜失败 {id}: {e.Message} (已摆 {n}/{count}; 满位时剩余物品悬空, 原生不处理)");
                        break;
                    }
                }
                return n > 0;
            }
            catch { return false; }
        }

        /// <summary>玩家是否持有至少 count 个该物品 (堆叠 unitCount 计入; 搜随身背包 + 后仓, 含容器嵌套)。</summary>
        public static bool PlayerHas(string itemId, int count)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId) || count <= 0) return false;
                return CountInPlayer(NormalizeId(itemId)) >= count;
            }
            catch { return false; }
        }

        /// <summary>物品单价: 已注册 mod 物品查定义; 其他建探针实例读 unitValue。未知/不在对局 = -1。</summary>
        public static int ValueOf(string itemId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId)) return -1;
                if (ItemStore.TryGetDef(itemId.Trim(), out var def)) return def.Value;
                var probe = DirectoryMaster.Item(NormalizeId(itemId), true);
                if (probe == null) return -1;
                long v = probe.unitValue;
                return v < 0 ? 0 : v > int.MaxValue ? int.MaxValue : (int)v;
            }
            catch { return -1; }
        }

        private static readonly Dictionary<string, string> _displayNameCache = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>v0.9.3: 物品显示名 (本地化中文名)。已注册 mod 物品先取定义 name, 再查本地化表
        /// item_&lt;id&gt;_name (原版物品同表), 最后建探针实例读 name; 全部落空 = null。结果按 id 缓存。</summary>
        public static string DisplayName(string itemId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId)) return null;
                string id = NormalizeId(itemId);
                if (_displayNameCache.TryGetValue(id, out var cached)) return cached;
                string name = null;
                if (ItemStore.TryGetDef(itemId.Trim(), out var def) && !string.IsNullOrWhiteSpace(def.Name))
                    name = def.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    try
                    {
                        string key = $"item_{id}_name";
                        string loc = LocHelper.GetLocalizedItem(key);
                        // v2.0.7: 未注册键原版返回 "Translation Error" 错误字符串而非抛异常, 须剔除
                        if (!string.IsNullOrWhiteSpace(loc) && loc != key && !LocService.IsTranslationError(loc)) name = loc;
                    }
                    catch { }
                }
                if (string.IsNullOrWhiteSpace(name) && Exists(itemId))
                {
                    try
                    {
                        var probe = DirectoryMaster.Item(id, true);
                        if (probe != null && !string.IsNullOrWhiteSpace(probe.name)) name = probe.name;
                    }
                    catch { }
                }
                if (string.IsNullOrWhiteSpace(name)) return null;
                _displayNameCache[id] = name;
                return name;
            }
            catch { return null; }
        }

        /// <summary>v0.9.3: 物品 id 是否存在 (已注册 mod 物品 或 DirectoryMaster 可查)。
        /// 仅对局内调用 — 加载期 DirectoryMaster 未就绪, 结果不可靠。</summary>
        public static bool Exists(string itemId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId)) return false;
                if (ItemStore.TryGetDef(itemId.Trim(), out _)) return true;
                return DirectoryMaster.Has<GameItem>(NormalizeId(itemId));
            }
            catch { return false; }
        }

        /// <summary>v0.9.11: pack 图标查询 (键="packId:name", 对应 pack icons/name.png)。
        /// 供 Events 场景背景图等跨程序集需求; 未加载/不存在返回 false。</summary>
        public static bool TryGetIcon(string key, out UnityEngine.Sprite sprite)
        {
            sprite = null;
            try { return IconService.TryGet(key, out sprite); }
            catch { return false; }
        }

        // ==================== v0.9.6: 物品实例定制 (仿 NBT) ====================

        /// <summary>实例定制选项 (items.give opts / 售卖 dict 条目 / 句柄 setter 共用; 全字段可选, null/0=不改)。</summary>
        public sealed class ItemOpts
        {
            public string Name;      // 实例显示名 (SetName)
            public string Desc;      // shortDescription
            public string Flavor;    // flavorText
            public long? Value;      // 显式单价 (覆盖任何折算价 — 显式优先)
            public string Quality;   // 品质 id (走 QualityService)
            public int Uses;         // >0 = 启用次数机制 (原生 UseCountHelper)
            public Dictionary<string, object> Data; // 自定义数据 (PSD_ 标签, 值=null=删键)
            /// <summary>v0.9.7: 发放落点路由 (items.give opts 的 to 键: inventory/counter_out/counter_in)。
            /// 仅 Events 侧 items.give/give_counter 消费; ApplyOpts 忽略本字段, 售卖条目里写了也不生效。</summary>
            public string To;
        }

        /// <summary>自定义数据的 TagSystem 标签前缀 (防与原版/品质标签冲突)。
        /// tooltip 实证不显示: CreateItemTypeTooltip 只遍历 itemTypes (ISIL: 枚举 GameItem+2B0h 字段),
        /// state.dict 任意键不进 tooltip; QualityDisplayPatch 另对 PSD_ 剥 TYPE-STRING_ 噪音兜底。</summary>
        public const string DataTagPrefix = "PSD_";

        /// <summary>写自定义数据 (TagSystem 标签 PSD_&lt;key&gt;, 值编码进 valueString: s:文本 / n:数字 / b:0|1 /
        /// v0.9.8 起 j:JSON(dict/list, System.Text.Json 序列化); 随存档持久化, 与品质/使用次数同管线)。
        /// value=null = 删除; 不支持类型(句柄等) = false。</summary>
        public static bool SetData(GameItem item, string key, object value)
        {
            try
            {
                if (item == null || string.IsNullOrWhiteSpace(key)) return false;
                if (value == null) return DeleteData(item, key);
                string enc;
                switch (value)
                {
                    case string s: enc = "s:" + s; break;
                    case bool b: enc = b ? "b:1" : "b:0"; break;
                    case long l: enc = "n:" + l.ToString(CultureInfo.InvariantCulture); break;
                    case double d: enc = "n:" + d.ToString("R", CultureInfo.InvariantCulture); break;
                    case int i: enc = "n:" + i.ToString(CultureInfo.InvariantCulture); break;
                    // v0.9.8: 嵌套 dict/list → j: + STJ 序列化 (pss 值运行时类型 long/double/string/bool/Dictionary/List, 直接序列化无误)
                    case Dictionary<string, object>: case List<object>:
                        enc = "j:" + System.Text.Json.JsonSerializer.Serialize(value);
                        break;
                    default: return false;
                }
                var ts = item.state;
                if (ts == null) return false;
                string tag = DataTagPrefix + key.Trim();
                ts.InitTagString(tag);
                var st = ts.GetTag(tag);
                if (st == null) return false;
                st.valueString = enc;
                st.SetEnabled(true);
                return true;
            }
            catch { return false; }
        }

        /// <summary>读自定义数据; 无键/编码损坏/异常 = null。数字: 整数回 long, 其余回 double (pss 两侧通用)。</summary>
        public static object GetData(GameItem item, string key)
        {
            try
            {
                if (item == null || string.IsNullOrWhiteSpace(key)) return null;
                var ts = item.state;
                if (ts == null) return null;
                var st = ts.GetTag(DataTagPrefix + key.Trim());
                string raw = st == null ? null : st.valueString;
                if (string.IsNullOrEmpty(raw) || raw.Length < 2 || raw[1] != ':') return null;
                switch (raw[0])
                {
                    case 's': return raw.Substring(2);
                    case 'b': return raw == "b:1";
                    case 'n':
                        string num = raw.Substring(2);
                        if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
                        if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                        return null;
                    // v0.9.8: 嵌套 dict/list → JsonNode (Events 侧 ScriptJson.FromNode 转 pss 值)
                    case 'j':
                        try { return JsonNode.Parse(raw.Substring(2)); } catch { return null; }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>删自定义数据 (dict.Remove, 存档自然不含); 键本就不存在 = false。</summary>
        public static bool DeleteData(GameItem item, string key)
        {
            try
            {
                if (item == null || string.IsNullOrWhiteSpace(key)) return false;
                var dict = item.state?.dict;
                if (dict == null) return false;
                return dict.Remove(DataTagPrefix + key.Trim());
            }
            catch { return false; }
        }

        /// <summary>应用实例定制: 先 value 后其他 (品质/次数定价读 unitValue); 单项失败不中断后续。
        /// usesOverride >= 0 时覆盖 opts.Uses (inject opts.uses 统一覆盖条目内 uses 的语义);
        /// opts==null 仅按 usesOverride 启用次数。价格说明: npc price.sell_single 折算走 buyPriceModifier
        /// (客户级, v1.12.0 起不碰 unitValue), 故 opts.Value 直接生效无冲突。</summary>
        public static void ApplyOpts(GameItem item, ItemOpts opts, int usesOverride = -1)
        {
            if (item == null) return;
            int uses = usesOverride >= 0 ? usesOverride : (opts?.Uses ?? 0);
            if (opts != null)
            {
                if (opts.Value.HasValue) try { item.SetValue(opts.Value.Value); } catch { }
                if (opts.Name != null) try { item.SetName(opts.Name); } catch { }
                if (opts.Desc != null) try { item.shortDescription = opts.Desc; } catch { }
                if (opts.Flavor != null) try { item.flavorText = opts.Flavor; } catch { }
                if (!string.IsNullOrWhiteSpace(opts.Quality))
                {
                    string iid = null;
                    try { iid = item.identifier; } catch { }
                    SetQuality(item, iid, opts.Quality);
                }
            }
            if (uses > 0)
                try { UseCountHelper.InitUseCountItem(item, uses, true, true, false, 0, 0); } catch { }
            if (opts?.Data != null)
                foreach (var kv in opts.Data)
                    SetData(item, kv.Key, kv.Value);
        }

        /// <summary>改实例显示名 (原生 SetName); 异常 = false。</summary>
        public static bool SetName(GameItem item, string name)
        {
            try { if (item == null || name == null) return false; item.SetName(name); return true; }
            catch { return false; }
        }

        /// <summary>改实例短描述 (tooltip 正文); 异常 = false。</summary>
        public static bool SetDesc(GameItem item, string desc)
        {
            try { if (item == null || desc == null) return false; item.shortDescription = desc; return true; }
            catch { return false; }
        }

        /// <summary>改实例单价; 异常 = false。</summary>
        public static bool SetValue(GameItem item, long value)
        {
            try { if (item == null || value < 0) return false; item.SetValue(value); return true; }
            catch { return false; }
        }

        /// <summary>设置剩余使用次数: 已有次数机制改当前值 (SetCurrentUseCount), 无则启用 (上限=n)。异常 = false。</summary>
        public static bool UseCountSet(GameItem item, int n)
        {
            try
            {
                if (item == null || n <= 0) return false;
                if (UseCountMax(item) > 0) { UseCountHelper.SetCurrentUseCount(item, n); return true; }
                UseCountHelper.InitUseCountItem(item, n, true, true, false, 0, 0);
                return true;
            }
            catch { return false; }
        }

        // ==================== 品质 ====================

        /// <summary>v0.9.9: 运行期品质注册 (pss quality.register 底层)。tag 空 = 派生 "PSQ_"+id 末段大写;
        /// priceMul 售价系数 (<=0 按 1); 重复注册同 id = 覆盖 (QualityService 原地更新, Rescan 安全)。
        /// v0.9.10: + mode/category/categoryDisplay — mode="feature" 走原版 ItemFeature 定价管线
        /// (品质加价在全部交易路径生效; tag 模式的 GetFinalOfferValue Postfix 覆盖不到的批量/批发路径也含)。</summary>
        public static bool RegisterQuality(string id, string tag, string display, float priceMul, int tier, string namePrefix,
            string mode = null, string category = null, string categoryDisplay = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(display)) return false;
                id = id.Trim();
                bool feature = string.Equals(mode, "feature", StringComparison.OrdinalIgnoreCase);
                if (!feature && string.IsNullOrWhiteSpace(tag))
                {
                    string seg = id.Contains(":") ? id.Substring(id.IndexOf(':') + 1) : id;
                    tag = "PSQ_" + seg.ToUpperInvariant();
                }
                string owner = id.Contains(":") ? id.Substring(0, id.IndexOf(':')) : "runtime";
                return QualityService.Register(new QualityDefJson
                {
                    Id = id, Tag = tag?.Trim(), Display = display.Trim(),
                    PriceMul = priceMul, Tier = tier, NamePrefix = namePrefix,
                    Mode = mode, Category = category, CategoryDisplay = categoryDisplay,
                }, owner, null);
            }
            catch { return false; }
        }

        /// <summary>给物品打品质层 (tag/feature 两模式由 QualityService 分派)。品质 id 已注册且调用无异常 = true。</summary>
        public static bool SetQuality(GameItem item, string itemId, string qualityId)
        {
            try
            {
                if (item == null || string.IsNullOrWhiteSpace(qualityId)) return false;
                string qid = qualityId.Trim();
                if (!QualityService.TryGetById(qid, out _)) return false;
                if (string.IsNullOrWhiteSpace(itemId))
                    try { itemId = item.identifier; } catch { }
                QualityService.SetQuality(item, itemId, qid);
                return true;
            }
            catch { return false; }
        }

        /// <summary>物品当前品质 id (全限定, 如 "pack:q_xxx"); 无品质/异常 = null。</summary>
        public static string GetQuality(GameItem item)
        {
            try { return QualityService.GetQuality(item)?.Id; }
            catch { return null; }
        }

        /// <summary>品质定价系数 (无品质/异常 = 1)。</summary>
        public static float PriceFactor(GameItem item)
        {
            try { return QualityService.PriceFactor(item); }
            catch { return 1f; }
        }

        // ==================== 机器 ====================

        /// <summary>多夜加工进度 0..1; 非进度机器 (批次机/无 progress 配置)/异常 = -1。</summary>
        public static float MachineProgress(GameItem item)
        {
            try
            {
                if (item == null) return -1f;
                string id = item.identifier;
                if (!ProgressRecipeService.HasProgress(id)) return -1f;
                if (!RecipeService.TryGetMachineSpecPublic(id, out var spec) || spec.Progress == null) return -1f;
                int max = spec.Progress.Max;
                if (max <= 0) return -1f;
                float f = ProgressRecipeService.GetProgress(item) / (float)max;
                return f < 0f ? 0f : f > 1f ? 1f : f;
            }
            catch { return -1f; }
        }

        /// <summary>正在生产的产出物品 id (按当前输入槽材料匹配配方); 无匹配/非进度机器/异常 = null。</summary>
        public static string MachineProducing(GameItem item)
        {
            try
            {
                return ProgressRecipeService.TryGetProducingOutput(item, out var outputId) ? outputId : null;
            }
            catch { return null; }
        }

        // ==================== 检索 ====================

        /// <summary>在玩家背包 (随身 + 后仓, 含容器嵌套) 深搜第一个匹配物品; 未找到 = null。</summary>
        public static GameItem FindInPlayer(string itemId)
        {
            try
            {
                string id = NormalizeId(itemId);
                if (string.IsNullOrWhiteSpace(id)) return null;
                var seen = new HashSet<IntPtr>();
                foreach (var inv in PlayerInvs())
                {
                    var hit = FindInInventory(inv, id, seen);
                    if (hit != null) return hit;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>在全店活动库存 (ApiProbe LiveInventories 全表) 深搜第一个匹配物品/机器; 未找到 = null。</summary>
        public static GameItem FindMachine(string itemId)
        {
            try
            {
                string id = NormalizeId(itemId);
                if (string.IsNullOrWhiteSpace(id)) return null;
                EmporiumEntry entry = null;
                try { entry = EmporiumEntry.Instance; } catch { }
                if (entry == null) return null;
                var seen = new HashSet<IntPtr>();
                foreach (var inv in LiveInventories(entry))
                {
                    var hit = FindInInventory(inv, id, seen);
                    if (hit != null) return hit;
                }
                return null;
            }
            catch { return null; }
        }

        // ==================== 物品目录 (M3 管理面板物品浏览器) ====================

        /// <summary>物品目录条目: id + 显示名 + 单价 + 分类下标 (CatalogKeys 序号; v0.8.0 起 test 标记物品 = TestCategoryIndex)。</summary>
        public sealed class CatalogEntry
        {
            public string Id;
            public string Name;
            public int Cat;
            public long Value;
        }

        /// <summary>29 个物品目录键 (与 NEI/GAME_GUIDE 一致, 序即分类下标)。</summary>
        public static readonly string[] CatalogKeys =
        {
            "MiscItemDirectory", "ToolDirectory", "MedsItemDirectory", "FoodItemDirectory", "WineDirectory",
            "HydroponicDirectory", "HusbandryDirectory", "MaterialDirectory", "GunsItemDirectory", "GunModDirectory",
            "MeleeWeaponItemDirectory", "ExplosiveItemDirectory", "ArmorItemDirectory", "ContainerItemDirectory", "FurnitureItemDirectory",
            "ModuleDirectory", "ModItemDirectory", "StationMachinery", "RuinedMachineDirectory", "KeyItemDirectory",
            "OrganDirectory", "AmenitiesItemDirectory", "ConstructionItemDirectory", "EquipmentDirectory", "ShipItemDirectory",
            "ShipSystemDirectory", "TechnicianBackpackDirectory", "PlayerAbilityItemDirectory", "UnusedDirectory"
        };

        /// <summary>CatalogKeys 对应中文标签 (同序) + 第 29 类 "测试" (v0.8.0: 仅含 items/*.json 标 "test": true 的包物品,
        /// 该分类不混入任何其他物品 — 测试分类是干净的白名单, 不是目录)。</summary>
        public static readonly string[] CatalogLabels =
        {
            "杂项", "工具", "药品", "食物", "酒类",
            "水培", "畜牧", "材料", "枪械", "枪械配件",
            "近战武器", "爆炸物", "护甲", "容器", "家具",
            "模块", "Mod物品", "站台机械", "废弃机械", "钥匙",
            "器官", "便利设施", "建造", "装备", "飞船",
            "飞船系统", "技师背包", "玩家能力", "未使用",
            "测试"
        };

        /// <summary>v0.8.0: "测试" 分类下标 (CatalogLabels 末位; 不在 CatalogKeys 目录枚举内)。</summary>
        public const int TestCategoryIndex = 29;

        private static List<CatalogEntry> _catalog;
        private static string _catalogRunId = null;

        /// <summary>全量物品目录 (跨 29 目录枚举去重, 先注册目录优先; 第二遍取显示名/单价)。
        /// 按 runID 缓存, 换对局自动重建; 不在对局/枚举失败 = 已有缓存或空表。与 NpcManager EnsureCatalog 同路径。</summary>
        public static List<CatalogEntry> Catalog()
        {
            string runId = "";
            try { var ps = PlayerStore.instance; if (ps != null) runId = ps.runID; } catch { }
            if (_catalog != null && _catalogRunId == runId) return _catalog;
            var list = new List<CatalogEntry>();
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int ci = 0; ci < CatalogKeys.Length; ci++)
                {
                    Il2CppSystem.Collections.Generic.List<string> ids = null;
                    try { ids = DirectoryMaster.GetIdentifierList<GameItem>(CatalogKeys[ci]); } catch { }
                    if (ids == null) continue;
                    foreach (var id in ids)
                    {
                        if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                        string name = id;
                        long val = 0;
                        try
                        {
                            var item = DirectoryMaster.Item(id, true);
                            if (item != null)
                            {
                                try { if (!string.IsNullOrEmpty(item.name)) name = item.name; } catch { }
                                try { val = item.unitValue; if (val < 0) val = 0; } catch { }
                            }
                        }
                        catch { }
                        // v0.8.0: "test": true 的包物品归入"测试"分类 (第 29 类), 不再出现在原目录分类
                        int cat = ItemStore.IsTestId(id) ? TestCategoryIndex : ci;
                        list.Add(new CatalogEntry { Id = id, Name = name, Cat = cat, Value = val });
                    }
                }
            }
            catch { }
            _catalog = list;
            _catalogRunId = runId;
            return list;
        }

        // ==================== U4 图元槽位 (PSUI slot/grid_slot 混合窗, v0.7.0) ====================

        /// <summary>给自装配槽位注册白名单过滤 (SlotFilterRegistry; exclusive=true = 排他: 非白名单全拒,
        /// 连原生放行也压住 — 自装配槽位零委托, 判定全归注册表, 见 AI_HANDOFF §2.1)。
        /// id 归一化剥 "game:" 前缀 (patch 匹配用裸 identifier)。注册成功 = true。</summary>
        public static bool RegisterSlotWhitelist(GameInventory slot, string[] whitelist, bool exclusive)
        {
            try
            {
                if (slot == null || whitelist == null || whitelist.Length == 0) return false;
                var wl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in whitelist)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    wl.Add(NormalizeId(raw));
                }
                if (wl.Count == 0) return false;
                SlotFilterRegistry.Register(slot.Pointer, wl, exclusive);
                return true;
            }
            catch { return false; }
        }

        /// <summary>v0.9.20: psui slot strict_footprint: true 登记 (SlotFilterRegistry) — 固定槽默认
        /// 不验 footprint (一格任意大小), 登记的槽由 GridPlacementGuard 验外接矩形≤槽格数。</summary>
        public static void RegisterSlotStrictFootprint(GameInventory slot, bool strict)
        {
            try { if (slot != null) SlotFilterRegistry.SetStrictFootprint(slot.Pointer, strict); } catch { }
        }

        /// <summary>槽内物品快照 (childItems 的托管副本, 指针去重); 槽空/异常 = 空表 (非 null)。</summary>
        public static List<GameItem> SlotItems(GameInventory slot)
        {
            var list = new List<GameItem>();
            try
            {
                if (slot == null) return list;
                var items = slot.childItems;
                if (items == null) return list;
                var seen = new HashSet<IntPtr>();
                foreach (var item in items)
                {
                    if (item == null) continue;
                    try { if (!seen.Add(item.Pointer)) continue; } catch { continue; }
                    list.Add(item);
                }
            }
            catch { }
            return list;
        }

        /// <summary>消耗物品: 从全部所属库存 Expel 取出 + GeneralHelper.DestroyGameItems 销毁
        /// (与 RecipeService 探测物回收同路径)。成功销毁 = true。</summary>
        public static bool ConsumeItem(GameItem item)
        {
            try
            {
                if (item == null) return false;
                try
                {
                    var parents = item.parents;
                    if (parents != null)
                        foreach (var p in parents)
                        {
                            if (p == null) continue;
                            GameInventory inv = null;
                            try { inv = p.TryCast<GameInventory>(); } catch { }
                            if (inv != null) { try { inv.Expel(item); } catch { } }
                        }
                }
                catch { }
                var list = new Il2CppSystem.Collections.Generic.List<GameItem>();
                list.Add(item);
                GeneralHelper.DestroyGameItems(list);
                return true;
            }
            catch { return false; }
        }

        /// <summary>把物品退回玩家后仓 (EmporiumEntry.backInvinvElement + UncheckedAccept
        /// + TransferOwnership×2, 与 Grant 同一条 F12 实证 UI 可见路径); 失败 = false。</summary>
        public static bool ReturnToPlayer(GameItem item)
        {
            try
            {
                if (item == null) return false;
                EmporiumEntry entry = null;
                try { entry = EmporiumEntry.Instance; } catch { }
                if (entry == null || entry.backInvinvElement == null) return false;
                try { item.DisableTag("TAG_NOT_PURCHASED", true); } catch { }
                try { item.DisableTag("not_purchased", true); } catch { }
                try { ((GameInventory)entry.backInvinvElement).UncheckedAccept(item); } catch { }
                try { entry.TransferOwnershipBackInv(); entry.TransferOwnedItemBackToInv(); } catch { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>v0.9.15: 实例移动到店内称重台 (PlayerStore.AddDirectToWeightedTable, 与
        /// GrantToCounterMiddle 同落点) — 保 NBT 的搬移 (非销毁重建): 先从全部所属库存 Expel
        /// (ConsumeItem 前段同款双亲循环), 再摆称重台; 摆柜异常兜底 ReturnToPlayer (后仓路径)。
        /// 不在对局/实例无效 = false。</summary>
        public static bool MoveToCounter(GameItem item)
        {
            try
            {
                if (item == null) return false;
                PlayerStore store = null;
                try
                {
                    if (EmporiumEntry.Instance == null) return false; // 不在对局
                    store = PlayerStore.instance;                     // 静态字段(安全); Instance 属性会懒创建
                }
                catch { return false; }
                if (store == null) return false;
                try
                {
                    var parents = item.parents;
                    if (parents != null)
                        foreach (var p in parents)
                        {
                            if (p == null) continue;
                            GameInventory inv = null;
                            try { inv = p.TryCast<GameInventory>(); } catch { }
                            if (inv != null) { try { inv.Expel(item); } catch { } }
                        }
                }
                catch { }
                try
                {
                    store.AddDirectToWeightedTable(item, true);
                    return true;
                }
                catch (Exception e)
                {
                    MelonLoader.MelonLogger.Warning($"[psapi] items.move_to_counter: 摆称重台失败, 兜底退回后仓: {e.Message}");
                    return ReturnToPlayer(item);
                }
            }
            catch { return false; }
        }

        // ==================== v0.8.0: 机器绑定 PSUI 面板 + 槽位锁 + 槽内生成 ====================

        /// <summary>Events 注册的 PSUI 机器装配 hook: machines/*.json 声明 ui="psui" 时,
        /// RecipeService.TryCreateNativeMachine 建空物品后调本委托装配图元树窗口 + SetContentWindow。
        /// 参数 (机器物品, 物品 id, 面板引用短名或 "pack:panel"); 装配成功 = true。
        /// Events 未加载/面板缺失 = false → 机器回退 plain item。OnInitializeMelon 期注册。</summary>
        public static Func<GameItem, string, string, bool> PsuiMachineAttach;

        /// <summary>v0.8.0: 手动插入锁 (SlotFilterRegistry 三 patch 统一拦截; 组装中锁料/成品未取走拒新部件用)。
        /// 与进度机忙锁并列, 不互相覆盖。槽位失效/异常 = false。</summary>
        public static bool SetSlotInsertLock(GameInventory slot, bool locked)
        {
            try
            {
                if (slot == null) return false;
                SlotFilterRegistry.SetInsertLock(slot.Pointer, locked);
                return true;
            }
            catch { return false; }
        }

        /// <summary>v0.8.0: 取出锁 (原生 GameInventory.LockInv/UnlockInv — 原生只锁取出不锁放入;
        /// 输出槽预览武器"取不下来"用)。锁定后玩家无法从槽中取物, 解锁恢复。异常 = false。</summary>
        public static bool SetSlotRemovalLock(GameInventory slot, bool locked)
        {
            try
            {
                if (slot == null) return false;
                if (locked) slot.LockInv(); else slot.UnlockInv();
                return true;
            }
            catch { return false; }
        }

        /// <summary>v0.8.0: 建一个全新物品直接放进指定槽位 (输出槽武器预览用; id 走 NormalizeId 归一化)。
        /// 不经白名单/过滤器 (UncheckedAccept, 与机器隔夜产出同路径)。成功返回物品实例, 失败 = null。
        /// v0.9.4: UncheckedAccept 失败(槽满/形状放不进)时销毁已建实例 —— 旧逻辑直接丢弃,
        /// 反复 spawn 到满槽会累积游离真物品(这些实例有 uid 会进存档, 泄漏即脏档)。</summary>
        public static GameItem CreateIntoSlot(GameInventory slot, string itemId, int count)
        {
            try
            {
                if (slot == null || string.IsNullOrWhiteSpace(itemId) || count <= 0) return null;
                var item = DirectoryMaster.Item(NormalizeId(itemId), true);
                if (item == null) return null;
                if (count > 1) { try { item.unitCount = count; } catch { } }
                if (slot.UncheckedAccept(item)) return item;
                try
                {
                    var discard = new Il2CppSystem.Collections.Generic.List<GameItem>();
                    discard.Add(item);
                    GeneralHelper.DestroyGameItems(discard);
                }
                catch { }
                return null;
            }
            catch { return null; }
        }

        // ---- v0.8.1: 电力 / 使用次数 (原生 PowerHelper / UseCountHelper 薄封装, 机器隔夜结算用) ----

        /// <summary>v0.8.1: 读电池当前可用电量 (绕过带宽限制)。非电源/异常 = 0。</summary>
        public static int PowerGet(GameItem item)
        {
            try { return item == null ? 0 : PowerHelper.GetAvailableEnergyFromItem(item, false); }
            catch { return 0; }
        }

        /// <summary>v0.8.1: 电池能否一次抽出 amount 电 (全有或全无判定, 不扣电)。</summary>
        public static bool PowerCanDraw(GameItem item, int amount)
        {
            try { return item != null && amount > 0 && PowerHelper.CanDrawPowerSource(item, amount); }
            catch { return false; }
        }

        /// <summary>v0.8.1: 从电池抽电 (全有或全无: 电量不足不动返回 false, 够则扣 amount 返回 true)。</summary>
        public static bool PowerDraw(GameItem item, int amount)
        {
            try { return item != null && amount > 0 && PowerHelper.DrawPowerSource(item, amount); }
            catch { return false; }
        }

        /// <summary>v0.8.1: 读次数物品当前剩余次数 (原生 tag CURRENT_USE_COUNT_INT)。无次数机制 = 0。</summary>
        public static int UseCountGet(GameItem item) => ReadIntTag(item, "CURRENT_USE_COUNT_INT");

        /// <summary>v0.8.1: 读次数物品上限 (原生 tag MAX_USE_COUNT_INT)。无次数机制 = 0。</summary>
        public static int UseCountMax(GameItem item) => ReadIntTag(item, "MAX_USE_COUNT_INT");

        /// <summary>v0.9.18: 读物品整数 tag (modifiedState 优先回退 state, 同 ReadIntTag 模式但
        /// 缺键可辨 — 原版模组 BONUS_PERCENTAGE_*_INT/TEMP_PERCENTAGE_*_INT 等)。
        /// 缺键/异常 = null (注意与 ReadIntTag 的 0 缺省不同: 调用方要做 TEMP→BONUS 缺键回退)。</summary>
        public static int? TagGetInt(GameItem item, string key)
        {
            try
            {
                if (item == null || string.IsNullOrEmpty(key)) return null;
                var tag = item.modifiedState?.GetTag(key);
                if (tag == null) tag = item.state?.GetTag(key);
                return tag?.valueInt;
            }
            catch { return null; }
        }

        /// <summary>v0.9.18: 读物品字符串 tag (同 TagGetInt 路径; 如 MODULE_TYPE)。缺键/异常 = null。</summary>
        public static string TagGetString(GameItem item, string key)
        {
            try
            {
                if (item == null || string.IsNullOrEmpty(key)) return null;
                var tag = item.modifiedState?.GetTag(key);
                if (tag == null) tag = item.state?.GetTag(key);
                return tag?.valueString;
            }
            catch { return null; }
        }

        /// <summary>v0.8.1: 能否再消耗 n 次 (原生 UseCountHelper.CanUse)。</summary>
        public static bool UseCountCanUse(GameItem item, int n)
        {
            try { return item != null && UseCountHelper.CanUse(item, n <= 0 ? 1 : n); }
            catch { return false; }
        }

        /// <summary>v0.8.1: 消耗 n 次 (原生 UseCountHelper.Use; 归零按初始化时 destroyOnEmpty 销毁, 本框架注册的次数物品均为归零销毁)。</summary>
        public static bool UseCountUse(GameItem item, int n)
        {
            try
            {
                if (item == null || !UseCountHelper.CanUse(item, n <= 0 ? 1 : n)) return false;
                UseCountHelper.Use(item, n <= 0 ? 1 : n);
                return true;
            }
            catch { return false; }
        }

        /// <summary>v0.8.1: 给已有物品实例启用次数机制 (运行时补启用用; 语义同 JSON useCount/useBaseValue/useValuePerUse 字段)。</summary>
        public static bool UseCountInit(GameItem item, int maxUse, int baseValue, int valuePerUse)
        {
            try
            {
                if (item == null || maxUse <= 0) return false;
                UseCountHelper.InitUseCountItem(item, maxUse, true, true, valuePerUse > 0, baseValue, valuePerUse);
                return true;
            }
            catch { return false; }
        }

        // ---- v0.8.2: 展示柜枚举 / 玩家库存全量查找 (仿制证书等"扫场"脚本用) ----

        /// <summary>v0.8.2: 展示柜 (商品展示区) 内全部物品 (EmporiumEntry.showcaseElement.childItems,
        /// 与 ProbeTools 活库存同路径)。不在对局/异常 = 空表。</summary>
        public static List<GameItem> ShowcaseItems()
        {
            var result = new List<GameItem>();
            try
            {
                var ee = EmporiumEntry.Instance;
                var sc = ee == null ? null : ee.showcaseElement;
                var children = sc == null ? null : sc.childItems;
                if (children != null)
                    foreach (var item in children)
                        if (item != null) result.Add(item);
            }
            catch { }
            return result;
        }

        /// <summary>v0.8.2: 玩家库存 (随身背包+后仓) 里所有 id 匹配的物品 (id 走 NormalizeId; 一个不放过,
        /// 证书衰减等"全场 -1"用)。异常 = 空表。</summary>
        public static List<GameItem> FindAllInPlayer(string itemId)
        {
            var result = new List<GameItem>();
            try
            {
                string id = NormalizeId(itemId);
                if (string.IsNullOrWhiteSpace(id)) return result;
                foreach (var inv in PlayerInvs())
                {
                    Il2CppSystem.Collections.Generic.List<GameItem> children = null;
                    try { children = inv?.childItems; } catch { }
                    if (children == null) continue;
                    foreach (var item in children)
                    {
                        if (item == null) continue;
                        string iid = null;
                        try { iid = item.identifier; } catch { }
                        if (string.Equals(iid, id, StringComparison.Ordinal)) result.Add(item);
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>v2.0.2: 玩家库存 (随身背包+后仓) 全量物品句柄源 (不限 id; psconsole bag clear 用)。异常 = 空表。</summary>
        public static List<GameItem> AllInPlayer()
        {
            var result = new List<GameItem>();
            try
            {
                foreach (var inv in PlayerInvs())
                {
                    Il2CppSystem.Collections.Generic.List<GameItem> children = null;
                    try { children = inv?.childItems; } catch { }
                    if (children == null) continue;
                    foreach (var item in children)
                        if (item != null) result.Add(item);
                }
            }
            catch { }
            return result;
        }

        // ==================== 内部 ====================

        /// <summary>读物品整数 tag: modifiedState 优先, 回退 state (同 ModulePrinterService.ReadTag 模式)。</summary>
        private static int ReadIntTag(GameItem item, string key)
        {
            try
            {
                if (item == null) return 0;
                var tag = item.modifiedState?.GetTag(key);
                if (tag == null) tag = item.state?.GetTag(key);
                return tag?.valueInt ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>玩家侧库存: 随身背包 + 后仓 (指针去重)。</summary>
        private static IEnumerable<GameInventory> PlayerInvs()
        {
            GameGridInventory g = null, b = null;
            try { var ps = PlayerStore.instance; if (ps != null) g = ps.gridInv; } catch { }
            try { var e = EmporiumEntry.Instance; if (e != null) b = e.backInvinvElement; } catch { }
            if (g != null) yield return g;
            if (b != null)
            {
                bool dup = false;
                try { dup = g != null && b.Pointer == g.Pointer; } catch { }
                if (!dup) yield return b;
            }
        }

        /// <summary>全店活动库存表 (移植自 ProbeTools.ApiProbe.LiveInventories)。</summary>
        private static List<GameInventory> LiveInventories(EmporiumEntry entry)
        {
            var list = new List<GameInventory>();
            Action<GameInventory> add = inv => { if (inv != null) list.Add(inv); };
            try { add(entry.backInvinvElement); } catch { }
            try { add(entry.backInvinvElementCounter); } catch { }
            try { add(entry.frontInvinvElement); } catch { }
            try { add(entry.invElement); } catch { }
            try { add(entry.showcaseElement); } catch { }
            try { add(entry.hiddenElement); } catch { }
            try { add(entry.soldElement); } catch { }
            try { add(entry.trashcanInvElement); } catch { }
            try { add(entry.trashInvElement); } catch { }
            try { add(entry.docInvElement); } catch { }
            try { add(entry.bazarLeftinvElement); } catch { }
            try { add(entry.afterhourInventory); } catch { }
            try { add(entry.hirelingInv); } catch { }
            try { add(entry.swapBufferElement); } catch { }
            try { add(entry.drainInvElement); } catch { }
            return list;
        }

        private static GameItem FindInInventory(GameInventory inv, string id, HashSet<IntPtr> seen)
        {
            Il2CppSystem.Collections.Generic.List<GameItem> items = null;
            try { if (inv != null) items = inv.childItems; } catch { }
            if (items == null) return null;
            foreach (var item in items)
            {
                var hit = FindDeep(item, id, seen);
                if (hit != null) return hit;
            }
            return null;
        }

        private static GameItem FindDeep(GameItem item, string id, HashSet<IntPtr> seen)
        {
            if (item == null) return null;
            try { if (!seen.Add(item.Pointer)) return null; } catch { return null; }
            try
            {
                if (string.Equals(item.identifier, id, StringComparison.OrdinalIgnoreCase)) return item;
            }
            catch { }
            Il2CppSystem.Collections.Generic.List<GameItem> children = null;
            try { children = item.FindAllChildItems(); } catch { }
            if (children == null) return null;
            foreach (var child in children)
            {
                var hit = FindDeep(child, id, seen);
                if (hit != null) return hit;
            }
            return null;
        }

        private static int CountInPlayer(string id)
        {
            int total = 0;
            var seen = new HashSet<IntPtr>();
            foreach (var inv in PlayerInvs())
            {
                Il2CppSystem.Collections.Generic.List<GameItem> items = null;
                try { if (inv != null) items = inv.childItems; } catch { }
                if (items == null) continue;
                foreach (var item in items) CountDeep(item, id, seen, ref total);
            }
            return total;
        }

        private static void CountDeep(GameItem item, string id, HashSet<IntPtr> seen, ref int total)
        {
            if (item == null) return;
            try { if (!seen.Add(item.Pointer)) return; } catch { return; }
            try
            {
                if (string.Equals(item.identifier, id, StringComparison.OrdinalIgnoreCase))
                {
                    int n = 1;
                    try { n = item.unitCount; } catch { }
                    total += n > 0 ? n : 1;
                }
            }
            catch { }
            Il2CppSystem.Collections.Generic.List<GameItem> children = null;
            try { children = item.FindAllChildItems(); } catch { }
            if (children == null) return;
            foreach (var child in children) CountDeep(child, id, seen, ref total);
        }
    }
}
