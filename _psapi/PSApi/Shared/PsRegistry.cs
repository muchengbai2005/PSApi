using System;
using System.Collections.Generic;
using MelonLoader;

namespace PSApi
{
    /// <summary>
    /// 通用内容注册表(骨架): 类别→(id→条目)。同 id 后者覆盖 + 警告(与 NpcManager 语义一致)。
    /// P1+ 的物品/配方/NPC/事件注册都经此入口, 便于冲突诊断与 dump。
    /// </summary>
    internal sealed class PsRegistry
    {
        internal sealed class Entry
        {
            internal string Category;
            internal string Id;
            internal string Owner;      // 来源(pack id / module 名)
            internal object Payload;    // 各阶段自定义
            internal DateTime At;
        }

        private readonly Dictionary<string, Dictionary<string, Entry>> _cats =
            new Dictionary<string, Dictionary<string, Entry>>(StringComparer.OrdinalIgnoreCase);

        private readonly MelonLogger.Instance _logger;
        internal PsRegistry(MelonLogger.Instance logger) { _logger = logger; }

        /// <summary>注册; 返回 false 表示参数无效被拒绝。</summary>
        internal bool Register(string category, string id, object payload, string owner = null)
        {
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(id)) return false;
            if (!_cats.TryGetValue(category, out var cat))
                _cats[category] = cat = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            if (cat.TryGetValue(id, out var old))
                PsApi.Warn(_logger, $"registry[{category}] '{id}' overwritten: {old.Owner} -> {owner ?? "?"}");
            cat[id] = new Entry { Category = category, Id = id, Owner = owner ?? "?", Payload = payload, At = DateTime.Now };
            return true;
        }

        internal bool TryGet(string category, string id, out Entry entry)
        {
            entry = null;
            return _cats.TryGetValue(category, out var cat) && cat.TryGetValue(id, out entry);
        }

        internal int Count(string category)
            => _cats.TryGetValue(category, out var cat) ? cat.Count : 0;

        internal IEnumerable<Entry> All(string category)
        {
            if (_cats.TryGetValue(category, out var cat))
                foreach (var e in cat.Values) yield return e;
        }
    }
}
