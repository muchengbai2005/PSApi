using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using PSApi.Events.PsScript;

namespace PSApi.Events
{
    /// <summary>
    /// 原版商店事件蓝图池注入(store_event.* 的实现侧)。
    /// 蓝图 = StoreEventBlueprint(identifier/odd/storeEventFunc), 三静态池 normal/threat/cosmetic;
    /// storeEventFunc 指向我们的工厂(ConvertDelegate + pin, 同 GameHooks 做法)。
    /// 池未就绪(主菜单期静态表未建)时挂起登记, day_wake 时重试。
    /// 事件开始/结束回调: 不 patch, day_wake 时对 activeEvents 做差集 → psapi.store_event.started/.ended。
    /// </summary>
    internal sealed class StoreEventService
    {
        /// <summary>一条注册记录: 蓝图 + 托管工厂(queue 直接调托管侧, 免绕 Il2Cpp 委托)。</summary>
        private sealed class Entry
        {
            internal string Id;
            internal int Odd;
            internal string Pool;
            internal Dictionary<string, object> Config;
            internal Func<StoreEvent> Factory;
            internal StoreEventBlueprint Blueprint; // 入池成功非 null
        }

        private readonly MelonLogger.Instance _logger;
        private readonly EventBus _bus;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<object> _pinned = new List<object>(); // 防 GC: 托管委托 + il2cpp 委托
        private HashSet<string> _prevActive;                        // 上次 day_wake 的激活事件 id 快照

        internal StoreEventService(MelonLogger.Instance logger, EventBus bus)
        {
            _logger = logger;
            _bus = bus;
        }

        // ---- 脚本面(由 PsBuiltinsGame.store_event 调用) ----

        /// <summary>注册蓝图入池; 池未就绪则挂起(返回 false), 下个 day_wake 重试。id 重复/参数错误抛 PsRuntimeError。</summary>
        internal void Register(string id, Dictionary<string, object> config, int line)
        {
            if (_entries.ContainsKey(id))
                throw new PsRuntimeError($"store_event '{id}' 已注册(重复 id)", line);

            string pool = ConfigStr(config, "pool", "normal");
            if (pool != "normal" && pool != "threat" && pool != "cosmetic")
                throw new PsRuntimeError($"store_event.register 的 pool 须为 normal/threat/cosmetic, 实为 '{pool}'", line);
            int odd = (int)ConfigLong(config, "odd", 20);

            var entry = new Entry { Id = id, Odd = odd, Pool = pool, Config = config };
            entry.Factory = () => CreateEvent(entry);
            _entries[id] = entry;
            if (!TryInject(entry))
                PsApi.Warn(_logger, $"store_event '{id}': 事件池未就绪, 挂起至场景就绪后注入");
        }

        /// <summary>排队事件: delay=0 立即 ActivateEvent, 否则 QueueFuturEvent(绝对日)。</summary>
        internal void Queue(string id, long delayDays, int line)
        {
            if (!_entries.TryGetValue(id, out var entry))
                throw new PsRuntimeError($"store_event '{id}' 未注册(先 store_event.register)", line);
            if (entry.Blueprint == null && !TryInject(entry))
                throw new PsRuntimeError($"store_event '{id}' 尚未入池(事件池未就绪), 稍后再 queue", line);

            StoreEventManager mgr;
            try { mgr = StoreStation.instance?.storeEventManager; } catch { mgr = null; }
            if (mgr == null)
                throw new PsRuntimeError("store_event.queue 当前不可用(不在商店场景中)", line);

            try
            {
                var ev = entry.Factory();
                if (ev == null) throw new PsRuntimeError($"store_event '{id}' 工厂返回 null", line);
                if (delayDays <= 0)
                {
                    mgr.ActivateEvent(ev, debugLog: true);
                    PsApi.Log(_logger, $"store_event '{id}' activated immediately");
                }
                else
                {
                    if (!GameHooks.GameDay.TryRead(out long day, out _, out _))
                        throw new PsRuntimeError("store_event.queue 读不到天数, 无法延迟排队", line);
                    mgr.QueueFuturEvent(ev, (int)(day + delayDays), true);
                    PsApi.Log(_logger, $"store_event '{id}' queued for day {day + delayDays}");
                }
            }
            catch (PsRuntimeError) { throw; }
            catch (Exception e) { throw new PsRuntimeError($"store_event.queue('{id}') 失败: {e.Message}", line); }
        }

        // ---- day_wake 驱动: 挂起注入重试 + 开始/结束差集回调 ----

        internal void OnDayWake()
        {
            foreach (var e in _entries.Values)
                if (e.Blueprint == null) TryInject(e);
            PublishActiveDiff();
        }

        /// <summary>激活事件差集 → started/ended 总线事件(payload {event_id})。覆盖原版与注入的全部事件。</summary>
        private void PublishActiveDiff()
        {
            StoreEventManager mgr;
            try { mgr = StoreStation.instance?.storeEventManager; } catch { mgr = null; }
            if (mgr == null) return;

            var cur = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var actives = mgr.GetActiveEvents();
                if (actives != null)
                    foreach (var ev in actives)
                    {
                        try { if (ev?.identifier != null) cur.Add(ev.identifier); } catch { }
                    }
            }
            catch (Exception e) { PsApi.Warn(_logger, "store_event active diff failed: " + e.Message); return; }

            if (_prevActive != null)
            {
                foreach (var id in cur)
                    if (!_prevActive.Contains(id))
                        _bus.Publish("psapi.store_event.started", new Dictionary<string, object> { ["event_id"] = id });
                foreach (var id in _prevActive)
                    if (!cur.Contains(id))
                        _bus.Publish("psapi.store_event.ended", new Dictionary<string, object> { ["event_id"] = id });
            }
            _prevActive = cur;
        }

        // ---- 内部: 蓝图入池 / 事件工厂 ----

        private bool TryInject(Entry entry)
        {
            try
            {
                var pool = PoolOf(entry.Pool);
                if (pool == null) return false;
                // 幂等: 池里已有同 id 蓝图则不重复注入
                foreach (var bp in pool)
                    if (bp?.identifier == entry.Id)
                    {
                        entry.Blueprint = bp;
                        return true;
                    }
                var func = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<StoreEvent>>(entry.Factory);
                var blueprint = new StoreEventBlueprint(func, entry.Odd, entry.Id);
                pool.Add(blueprint);
                entry.Blueprint = blueprint;
                _pinned.Add(entry.Factory);
                _pinned.Add(func);
                PsApi.Log(_logger, $"store_event '{entry.Id}' injected into {entry.Pool} pool (odd={entry.Odd})");
                return true;
            }
            catch (Exception e)
            {
                PsApi.Warn(_logger, $"store_event '{entry.Id}' inject failed: {e.Message}");
                return false;
            }
        }

        private static Il2CppSystem.Collections.Generic.List<StoreEventBlueprint> PoolOf(string pool) => pool switch
        {
            "threat" => StoreEventManager.threatEventBlueprints,
            "cosmetic" => StoreEventManager.cosmeticEventBlueprints,
            _ => StoreEventManager.normalEventBlueprints,
        };

        /// <summary>事件工厂: new StoreEvent + 按 config 设字段; 未知/设不了的键 Warn 跳过。</summary>
        private StoreEvent CreateEvent(Entry entry)
        {
            var cfg = entry.Config ?? new Dictionary<string, object>();
            var ev = new StoreEvent();
            // 先走原版普通事件初始化(是否必需反编译看不出, 失败不致命)
            try { ev.InitNormalEvent(); }
            catch (Exception e) { PsApi.Warn(_logger, $"store_event '{entry.Id}': InitNormalEvent 失败(继续): {e.Message}"); }

            ev.identifier = entry.Id;
            ev.eventType = entry.Pool switch
            {
                "threat" => StoreEvent.EventType.THREAT,
                "cosmetic" => StoreEvent.EventType.COSMETIC,
                _ => StoreEvent.EventType.NORMALE,
            };
            string name = ConfigStr(cfg, "name", entry.Id);
            ev.displayName = name;
            ev.newsName = ConfigStr(cfg, "news", name);
            ev.newsDescription = ConfigStr(cfg, "description", "");
            int duration = (int)ConfigLong(cfg, "duration", 1);
            ev.duration = duration;
            ev.totalDuration = duration;
            ev.cooldown = (int)ConfigLong(cfg, "cooldown", 0);
            ev.isHidden = ConfigBool(cfg, "hidden", false);
            ev.importance = (int)ConfigLong(cfg, "importance", 0);
            WarnUnknownKeys(cfg, entry.Id);

            // 事件纸条(可选): 生成实体传单物品
            if (ConfigBool(cfg, "slip", false))
                try { ev.CreateAndAddSlip(Math.Max(1, duration)); }
                catch (Exception e) { PsApi.Warn(_logger, $"store_event '{entry.Id}': CreateAndAddSlip 失败(跳过): {e.Message}"); }
            return ev;
        }

        private void WarnUnknownKeys(Dictionary<string, object> cfg, string id)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "pool", "odd", "name", "news", "description", "duration", "cooldown", "hidden", "importance", "slip" };
            foreach (var k in cfg.Keys)
                if (!known.Contains(k))
                    PsApi.Warn(_logger, $"store_event '{id}': 未知配置键 '{k}' 已跳过(可用: {string.Join("/", known)})");
        }

        private static string ConfigStr(Dictionary<string, object> cfg, string key, string def)
            => cfg.TryGetValue(key, out var v) && v != null ? PsValues.Fmt(v) : def;

        private static long ConfigLong(Dictionary<string, object> cfg, string key, long def)
        {
            if (!cfg.TryGetValue(key, out var v) || v == null) return def;
            if (v is long l) return l;
            if (v is double d) return (long)d;
            return def;
        }

        private static bool ConfigBool(Dictionary<string, object> cfg, string key, bool def)
            => cfg.TryGetValue(key, out var v) ? PsValues.Truthy(v) : def;
    }
}
