using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace PSApi.Events
{
    /// <summary>
    /// 官方 ModHook 静态事件 → EventBus 桥接(只订阅不 patch)。
    /// ModHook 事件是 Il2CppSystem.Action: 需 DelegateSupport.ConvertDelegate + 双 pin 防 GC(同 PSApi.Items 做法)。
    /// payload = Dictionary&lt;string,object&gt;(day/rel_day/weekday 等上下文字段)。
    /// </summary>
    internal sealed class GameHooks
    {
        /// <summary>
        /// 天数上下文: day = StoreStation.GetDayCounter(); rel_day = day - startingDay + 1
        /// (开业日=第1天, 与 NpcManager RevDeal 的 relDay 语义一致); weekday = (rel_day-1) % 7(周五=4)。
        /// </summary>
        internal static class GameDay
        {
            internal static bool TryRead(out long day, out long relDay, out long weekday)
            {
                day = relDay = weekday = 0;
                try
                {
                    int d = StoreStation.GetDayCounter();
                    int start = d;
                    var ps = PlayerStore.instance; // 静态字段(安全); Instance 属性会懒创建
                    if (ps != null) start = ps.startingDay;
                    long rel0 = d - start;
                    if (rel0 < 0) rel0 = 0;
                    day = d;
                    relDay = rel0 + 1;
                    weekday = rel0 % 7;
                    return true;
                }
                catch { return false; }
            }

            internal static Dictionary<string, object> Payload(bool withDayFields)
            {
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                if (!withDayFields) return dict;
                if (TryRead(out long day, out long relDay, out long weekday))
                {
                    dict["day"] = day;
                    dict["rel_day"] = relDay;
                    dict["weekday"] = weekday;
                }
                return dict;
            }
        }

        private readonly MelonLogger.Instance _logger;
        private readonly EventBus _bus;
        private readonly StoreEventService _storeEvents;
        private readonly NpcService _npcs;
        private readonly List<object> _pinned = new List<object>();

        internal GameHooks(MelonLogger.Instance logger, EventBus bus, StoreEventService storeEvents = null, NpcService npcs = null)
        {
            _logger = logger;
            _bus = bus;
            _storeEvents = storeEvents;
            _npcs = npcs;
        }

        internal void Subscribe()
        {
            Hook("OnGameLoadedNormal", d => ModHook.OnGameLoadedNormal += d, OnGameLoaded);
            Hook("OnWakingUpLate", d => ModHook.OnWakingUpLate += d, () => PublishDay("psapi.day.wake"));
            Hook("OnGoingSleepLate", d => ModHook.OnGoingSleepLate += d, () => PublishDay("psapi.day.sleep"));
            Hook("OnShutterOpenedLate", d => ModHook.OnShutterOpenedLate += d, () => PublishDay("psapi.shop.opened"));
            Hook("OnShutterClosedLate", d => ModHook.OnShutterClosedLate += d, () => PublishDay("psapi.shop.closed"));
            Hook("OnLeavingStoreLate", d => ModHook.OnLeavingStoreLate += d, () => PublishDayOnly("psapi.store.leaving"));
            Hook("OnReturningStoreLate", d => ModHook.OnReturningStoreLate += d, () => PublishDayOnly("psapi.store.returning"));
            Hook("OnHandlingNightlyServicesLate", d => ModHook.OnHandlingNightlyServicesLate += d, () => PublishDay("psapi.night.services"));
            if (_npcs != null)
            {
                // E4: 客户生成五相位钩子(官方 ModHook, 零补丁) — 早相位快照队列, 末相位差集发事件+调度入队
                Hook<StoreClientManager>("OnGenerateCustomerVeryEarly", d => ModHook.OnGenerateCustomerVeryEarly += d, m => _npcs.SnapshotQueue(m));
                Hook<StoreClientManager>("OnGenerateCustomerVeryLate", d => ModHook.OnGenerateCustomerVeryLate += d, m => _npcs.OnGenerationDone(m));
            }
        }

        private void Hook(string name, Action<Il2CppSystem.Action> add, Action handler)
        {
            try
            {
                var del = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(handler);
                add(del);
                _pinned.Add(handler);
                _pinned.Add(del);
            }
            catch (Exception e)
            {
                PsApi.Warn(_logger, $"ModHook {name} subscribe failed: {e.Message}");
            }
        }

        private void Hook<T>(string name, Action<Il2CppSystem.Action<T>> add, Action<T> handler)
        {
            try
            {
                var del = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<T>>(handler);
                add(del);
                _pinned.Add(handler);
                _pinned.Add(del);
            }
            catch (Exception e)
            {
                PsApi.Warn(_logger, $"ModHook {name} subscribe failed: {e.Message}");
            }
        }

        private void PublishDay(string eventId)
        {
            // day_wake 顺便驱动商店事件服务: 挂起注入重试 + 激活差集(started/ended)
            if (eventId == "psapi.day.wake")
                try { _storeEvents?.OnDayWake(); }
                catch (Exception e) { PsApi.Warn(_logger, "store_event day_wake hook failed: " + e.Message); }
            _bus.Publish(eventId, GameDay.Payload(withDayFields: true));
        }

        private void PublishDayOnly(string eventId)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            if (GameDay.TryRead(out long day, out _, out _)) dict["day"] = day;
            _bus.Publish(eventId, dict);
        }

        private void OnGameLoaded()
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                var ps = PlayerStore.instance;
                if (ps != null)
                {
                    dict["save_slot"] = (long)ps.saveSlotId;
                    dict["run_id"] = ps.runID;
                }
            }
            catch (Exception e) { PsApi.Warn(_logger, "game_loaded payload failed: " + e.Message); }
            _bus.Publish("psapi.game.loaded", dict);
        }
    }
}
