using System;
using System.Collections.Generic;
using MelonLoader;

namespace PSApi.Events
{
    /// <summary>
    /// 事件总线骨架(P3 声明式触发器的承载体)。
    /// 语义: 同事件多订阅按优先级(小→大)执行, 同优先级按注册先后;
    /// once 触发一次即移除; Publish 同步执行(游戏主线程), 订阅者异常隔离不影响后续。
    /// v1.13.4 修正: 旧实现倒序遍历升序列表(实际大→小, 与文档相反)且 List.Sort 不稳定;
    /// 现稳定排序(优先级, 注册序号) + 快照迭代(handler 内可安全订阅/退订, 不再炸枚举)。
    /// </summary>
    internal sealed class EventBus
    {
        private sealed class Sub
        {
            internal int Priority;
            internal long Seq;
            internal bool Once;
            internal Action<object> Handler;
            internal string Owner;
        }

        private readonly Dictionary<string, List<Sub>> _subs = new Dictionary<string, List<Sub>>(StringComparer.OrdinalIgnoreCase);
        private readonly MelonLogger.Instance _logger;
        private long _seq;

        internal EventBus(MelonLogger.Instance logger) { _logger = logger; }

        internal void Subscribe(string eventName, Action<object> handler, int priority = 0, bool once = false, string owner = null)
        {
            if (string.IsNullOrWhiteSpace(eventName) || handler == null) return;
            if (!_subs.TryGetValue(eventName, out var list))
                _subs[eventName] = list = new List<Sub>();
            list.Add(new Sub { Priority = priority, Seq = _seq++, Once = once, Handler = handler, Owner = owner ?? "?" });
            list.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Seq.CompareTo(b.Seq));
        }

        /// <summary>发布事件; 返回成功执行的订阅数。</summary>
        internal int Publish(string eventName, object payload = null)
        {
            if (string.IsNullOrWhiteSpace(eventName)) return 0;
            if (!_subs.TryGetValue(eventName, out var list) || list.Count == 0) return 0;
            int ok = 0;
            // 快照迭代: handler 内订阅(改 list+重排)不炸枚举; once 触发后从活表移除
            foreach (var s in list.ToArray())
            {
                if (!list.Contains(s)) continue; // 已被前序 handler 移除的 once 订阅不重复触发
                try { s.Handler(payload); ok++; }
                catch (Exception e) { _logger?.Error($"[psapi] event '{eventName}' sub({s.Owner}) failed: {e}"); }
                if (s.Once) list.Remove(s);
            }
            return ok;
        }

        internal int SubscriberCount(string eventName)
            => _subs.TryGetValue(eventName, out var list) ? list.Count : 0;
    }
}
