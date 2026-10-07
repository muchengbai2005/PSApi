using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppRNGNeeds;
using MelonLoader;
using PSApi.Events.PsScript;

namespace PSApi.Events
{
    /// <summary>
    /// v1.10.0 inject.loot_pool: 模组物品注入原版 LootTable 池 (NpcManager v3.1 NpcRules.cs:856-1009 移植)。
    ///   inject.loot_pool("表名", "物品id", 权重) — 表名短名自动补 "Table" 后缀(junk → junkTable);
    ///   权重 float 写 m_BaseProbability, 表 Roll 按总和归一化(原版表内总和≈1 → 0.03≈3%)。
    /// 活路径 = TableMaster.Instance.tableEntries (List&lt;TableEntry&gt;{tableId, lootTable});
    ///   条目 = ProbabilityItem&lt;string&gt;{m_Value, m_BaseProbability, m_Enabled}, 无公开构造器 → 克隆样本条目。
    /// 消费方: 拾荒客/小贼/Eleanor5 等 SELL 客户工厂 + 远征; Add 立即生效无总权重缓存; 表每局重建不进存档。
    /// 时机: 包注册在加载期, 入池延迟到 OnUpdate 轮询表就绪(tableEntries 非空); 按 TableMaster
    ///   实例指针判新局重注。幂等: 按 (表key, 条目指针) 记账, 重注前按指针 RemoveAt 撤旧;
    ///   id 先过 DirectoryMaster 存在性校验(未注册警告一次跳过)。
    /// GC 两条血泪红线(逐字复刻 NpcRules):
    ///   ① m_Value 字符串引用必须 il2cpp_gc_wbarrier_set_field 写屏障写入(裸 memcpy → 增量 GC 回收
    ///     字符串 → 下次 Roll 原生崩溃, 表现为隔天开张卡死闪退且无托管异常);
    ///   ② ManagedStringToIl2Cpp 先分配字符串再 object_new — object_new 之后不能再堆分配(分配可能
    ///     触发 GC, 此窗口内克隆体只有裸指针引用)。
    /// 无 Harmony patch。
    /// </summary>
    internal sealed class LootPoolService
    {
        internal sealed class PoolEntry
        {
            internal string PackId;
            internal string Table;      // 脚本写的表名(容许短名, 注入时解析)
            internal string Id;
            internal float Weight;
            internal bool Enabled = true;
        }

        private readonly MelonLogger.Instance _logger;
        private readonly List<PoolEntry> _entries = new List<PoolEntry>();
        private readonly List<(string key, long ptr)> _injected = new List<(string, long)>();
        private readonly HashSet<string> _warnedMissing = new HashSet<string>(StringComparer.Ordinal);
        private IntPtr _appliedFor = IntPtr.Zero;  // 已注入的 TableMaster 实例(换局/复位 → 指针变 → 撤旧重注)
        private float _poll;

        internal LootPoolService(MelonLogger.Instance logger) { _logger = logger; }

        internal int Count => _entries.Count;

        internal void Add(string packId, string table, string id, double weight, int line)
        {
            table = (table ?? "").Trim();
            if (table.Length == 0)
                throw new PsRuntimeError("inject.loot_pool 的表名须为非空字符串", line);
            id = InjectService.NormalizeId(id);
            if (id.Length == 0)
                throw new PsRuntimeError("inject.loot_pool 的物品 id 须为非空字符串", line);
            if (weight <= 0 || weight > 1)
                throw new PsRuntimeError($"inject.loot_pool 的权重须在 (0,1](按表内总和归一化, 原版总和≈1), 实为 {weight}", line);
            _entries.Add(new PoolEntry { PackId = packId, Table = table, Id = id, Weight = (float)weight });
        }

        /// <summary>inject.list 平铺行(channel="loot_pool"; 权重落在 chance 字段=百分比; idx 接四通道之后)。</summary>
        internal List<object> Describe(int idxBase)
        {
            var r = new List<object>();
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                r.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["idx"] = (long)(idxBase + i),
                    ["channel"] = "loot_pool",
                    ["pack"] = e.PackId ?? "",
                    ["id"] = e.Table + " ← " + e.Id,
                    ["count"] = 0L,
                    ["chance"] = (long)Math.Round(e.Weight * 100),
                    ["dynamic"] = false,
                    ["enabled"] = e.Enabled,
                    ["uses"] = 0L,
                });
            }
            return r;
        }

        /// <summary>运行时开关(仅 enabled; 权重注册时定)。下帧 Poll 撤旧重注生效。idx 越界 = false。</summary>
        internal bool TuneEnabled(long localIdx, bool enabled)
        {
            if (localIdx < 0 || localIdx >= _entries.Count) return false;
            _entries[(int)localIdx].Enabled = enabled;
            _appliedFor = IntPtr.Zero;
            return true;
        }

        /// <summary>inject.reset_tracking 联动: 下帧撤旧重注。</summary>
        internal void ResetTracking() => _appliedFor = IntPtr.Zero;

        /// <summary>Plugin.OnUpdate 驱动(0.5s 节流): 表就绪(tableEntries 非空)且实例变更/未注入时撤旧重注。
        /// 表每局重建不进存档 → TableMaster 实例指针即局标识。</summary>
        internal void Poll()
        {
            if (_entries.Count == 0 && _injected.Count == 0) return;
            _poll += UnityEngine.Time.deltaTime;
            if (_poll < 0.5f) return;
            _poll = 0f;
            try
            {
                var tm = TableMaster.Instance; // MonoBehaviour 静态 Instance(游戏自建); is null 纯引用判空
                if (tm is null) return;
                var ptr = tm.Pointer;
                if (ptr == _appliedFor) return;
                var entries = tm.tableEntries;
                if (entries is null || entries.Count == 0) return; // 场景还没建表, 下秒再试
                EnsureInjected(tm, entries);
                _appliedFor = ptr;
            }
            catch { }
        }

        // ==================== 注入/撤旧 (NpcRules.EnsureLootTables 移植) ====================

        private void EnsureInjected(TableMaster tm, Il2CppSystem.Collections.Generic.List<TableEntry> entries)
        {
            var tables = BuildTableView(tm, entries);
            RemoveLootEntries(tables);
            if (_entries.Count == 0) return;

            IntPtr cls = Il2CppClassPointerStore<ProbabilityItem<string>>.NativeClassPtr;
            IntPtr fValue = IL2CPP.GetIl2CppField(cls, "m_Value");
            IntPtr fBaseP = IL2CPP.GetIl2CppField(cls, "m_BaseProbability");
            if (fValue == IntPtr.Zero || fBaseP == IntPtr.Zero)
            {
                PsApi.Warn(_logger, "[inject] loot_pool: ProbabilityItem 字段缺失(m_Value/m_BaseProbability), 跳过");
                return;
            }
            int dataSize = (int)IL2CPP.il2cpp_class_instance_size(cls) - 16; // 减去 IL2CPP 对象头(klass+monitor)
            int total = 0;
            foreach (var e in _entries)
            {
                if (!e.Enabled) continue;
                string key = ResolveTableKey(tables, e.Table);
                if (key == null) continue;
                Il2CppSystem.Collections.Generic.List<ProbabilityItem<string>> items = null;
                try { items = tables[key]?.table?.ProbabilityItems; } catch { }
                if (items is null || items.Count == 0)
                {
                    PsApi.Warn(_logger, $"[inject] loot_pool '{key}' 为空, 跳过");
                    continue;
                }
                var sample = items[0];
                try
                {
                    // v1.11.0: 存在性检查改 DirectoryMaster.Has(不构建实例) + 统一告警格式
                    if (!ItemExists(e.Id))
                    {
                        if (_warnedMissing.Add(key + "|" + e.Id))
                            PsApi.Warn(_logger, $"[psapi] 物品 id '{e.Id}' 在当前版本不存在, 已跳过 (来源: inject.loot_pool {key})");
                        continue;
                    }
                    long ptr = InjectOne(items, sample, cls, fValue, fBaseP, dataSize, e.Id, e.Weight);
                    _injected.Add((key, ptr));
                    total++;
                }
                catch (Exception ex) { PsApi.Warn(_logger, $"[inject] loot_pool '{key}' 注入 '{e.Id}': {ex.Message}"); }
            }
            if (total > 0)
                PsApi.Log(_logger, $"[inject] loot_pool 入池: {total} 个条目(SELL 客户带货/远征掷骰池, 权重按表内总和归一化)");
        }

        /// <summary>克隆样本条目: object_new + 整块拷贝数据区 + 写屏障改 m_Value + 直写 m_BaseProbability。
        /// 顺序严守 GC 红线②: 字符串先于 object_new 分配。obj 加入 items 前只有裸指针, 不做任何堆分配。</summary>
        private static unsafe long InjectOne(Il2CppSystem.Collections.Generic.List<ProbabilityItem<string>> items,
            ProbabilityItem<string> sample, IntPtr cls, IntPtr fValue, IntPtr fBaseP, int dataSize, string id, float weight)
        {
            IntPtr strPtr = IL2CPP.ManagedStringToIl2Cpp(id); // 红线②: 先分配字符串
            IntPtr obj = IL2CPP.il2cpp_object_new(cls);       // 之后不能再堆分配
            byte[] data = new byte[dataSize];
            Marshal.Copy(sample.Pointer + 16, data, 0, dataSize);
            Marshal.Copy(data, 0, obj + 16, dataSize);
            // 红线①: m_Value 是字符串引用, 必须 GC 写屏障(il2cpp_field_set_value 只 memcpy 不带屏障)
            IL2CPP.il2cpp_gc_wbarrier_set_field(obj,
                (IntPtr)((nint)obj + (int)IL2CPP.il2cpp_field_get_offset(fValue)),
                strPtr);
            IL2CPP.il2cpp_field_set_value(obj, fBaseP, &weight); // float 值类型, 无需屏障
            items.Add(new ProbabilityItem<string>(obj));
            return obj.ToInt64();
        }

        /// <summary>按指针撤掉本服务注入的条目(不动原版条目)。</summary>
        private void RemoveLootEntries(Dictionary<string, LootTable> tables)
        {
            if (_injected.Count == 0) return;
            foreach (var (key, ptr) in _injected)
            {
                try
                {
                    if (!tables.TryGetValue(key, out var lt)) continue;
                    var items = lt?.table?.ProbabilityItems;
                    if (items is null) continue;
                    for (int i = items.Count - 1; i >= 0; i--)
                        try { if (items[i].Pointer.ToInt64() == ptr) items.RemoveAt(i); } catch { }
                }
                catch { }
            }
            _injected.Clear();
        }

        /// <summary>tableEntries (List&lt;TableEntry&gt;{tableId, lootTable}) → id→LootTable 视图,
        /// GetTableId 实例方法兜底取 id (与 NpcRules.GetLootTables 同构)。</summary>
        private static Dictionary<string, LootTable> BuildTableView(TableMaster tm,
            Il2CppSystem.Collections.Generic.List<TableEntry> entries)
        {
            var dict = new Dictionary<string, LootTable>(StringComparer.Ordinal);
            foreach (var te in entries)
            {
                try
                {
                    var lt = te.lootTable;
                    if (lt is null) continue;
                    string id = te.tableId;
                    if (string.IsNullOrEmpty(id)) { try { id = tm.GetTableId(lt); } catch { } }
                    if (!string.IsNullOrEmpty(id)) dict[id] = lt;
                }
                catch { }
            }
            return dict;
        }

        /// <summary>表名解析: 原样命中 → 用之; 否则补 "Table" 后缀再试(junk → junkTable); 都没有 = 警告列出可用表名。</summary>
        private string ResolveTableKey(Dictionary<string, LootTable> tables, string name)
        {
            try
            {
                if (tables.ContainsKey(name)) return name;
                string t2 = name + "Table";
                if (tables.ContainsKey(t2)) return t2;
                PsApi.Warn(_logger, $"[inject] loot_pool: 未知表 '{name}'(可用: {string.Join(", ", tables.Keys)})");
            }
            catch { }
            return null;
        }

        private static bool ItemExists(string id)
        {
            try { return DirectoryMaster.Has<GameItem>(id); }
            catch { return false; }
        }
    }
}
