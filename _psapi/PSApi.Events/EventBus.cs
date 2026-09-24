using System;
using System.Collections.Generic;
using MelonLoader;

namespace PSApi.Events
{
    /// <summary>
    /// 事件总线骨架(P3 声明式触发器的承载体)。
    /// 语义: 同事件多订阅按优先级(小→大)执行; once 触发一次即移除;
    /// Publish 同步执行(游戏主线程), 订阅者异常隔离不影响后续。
    /// </summary>
    internal sealed class EventBus
    {
        private sealed class Sub
        {
            internal int Priority;
            internal bool Once;
            internal Action<object> Handler;
            internal string Owner;
        }

        private readonly Dictionary<string, List<Sub>> _subs = new Dictionary<string, List<Sub>>(StringComparer.OrdinalIgnoreCase);
        private readonly MelonLogger.Instance _logger;

        internal EventBus(MelonLogger.Instance logger) { _logger = logger; }

        internal void Subscribe(string eventName, Action<object> handler, int priority = 0, bool once = false, string owner = null)
        {
            if (string.IsNullOrWhiteSpace(eventName) || handler == null) return;
            if (!_subs.TryGetValue(eventName, out var list))
                _subs[eventName] = list = new List<Sub>();
            list.Add(new Sub { Priority = priority, Once = once, Handler = handler, Owner = owner ?? "?" });
            list.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        /// <summary>发布事件; 返回成功执行的订阅数。</summary>
        internal int Publish(string eventName, object payload = null)
        {
            if (string.IsNullOrWhiteSpace(eventName)) return 0;
            if (!_subs.TryGetValue(eventName, out var list) || list.Count == 0) return 0;
            int ok = 0;
            // 倒序遍历以便安全移除 once
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var s = list[i];
                try { s.Handler(payload); ok++; }
                catch (Exception e) { _logger?.Error($"[psapi] event '{eventName}' sub({s.Owner}) failed: {e}"); }
                if (s.Once) list.RemoveAt(i);
            }
            return ok;
        }

        internal int SubscriberCount(string eventName)
            => _subs.TryGetValue(eventName, out var list) ? list.Count : 0;
    }
}
