using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2Cpp;
using MelonLoader;

namespace PSApi
{
    /// <summary>
    /// 按存档槽隔离的字符串 KV 状态: UserData/PSApi/state/&lt;slot&gt;/&lt;owner&gt;.json
    /// 槽位来自 PlayerStore.saveSlotId(读不到时归 "global")。
    /// 骨架期采用 string→string(各模块自行序列化), 规避 STJ 多态/嵌套坑。
    /// 用法: st = SaveStates.For("my-module"); st.Get(key)/st.Set(key,val)/st.Flush()。
    /// </summary>
    internal sealed class SaveStates
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };
        private static readonly Dictionary<string, SaveStates> _byOwner = new Dictionary<string, SaveStates>(StringComparer.OrdinalIgnoreCase);
        private static string _loadedSlot;   // 当前已加载到内存的槽位

        private readonly string _owner;
        private Dictionary<string, string> _kv = new Dictionary<string, string>();

        private SaveStates(string owner) { _owner = owner; }

        internal static SaveStates For(string owner) => For(owner, null);

        /// <summary>按 owner 取状态访问器; 槽位变化时自动重载。</summary>
        internal static SaveStates For(string owner, MelonLogger.Instance logger)
        {
            EnsureSlot(logger);
            lock (_byOwner)
            {
                if (!_byOwner.TryGetValue(owner, out var st))
                {
                    st = new SaveStates(owner);
                    st.Load(logger);
                    _byOwner[owner] = st;
                }
                return st;
            }
        }

        internal static string CurrentSlot()
        {
            try
            {
                var ps = PlayerStore.instance;
                if (ps != null) return ps.saveSlotId.ToString();
            }
            catch { }
            return "global";
        }

        /// <summary>槽位切换检测: 内存态直接丢弃(不落盘)再按新槽位重载。</summary>
        internal static void EnsureSlot(MelonLogger.Instance logger = null)
        {
            var slot = CurrentSlot();
            if (slot == _loadedSlot) return;
            if (_loadedSlot != null)
            {
                // v1.14.2 存档同步: 持久化只随游戏真实存档(PlayerStore.SaveGame 钩子)发生。
                // 槽位切换(退菜单/换档)时内存里未落盘的变更必须随游戏世界一起回档丢弃,
                // 否则游戏回档而模组状态残留 → 引导员 gifted 类标志永久错位的病灶。
                lock (_byOwner) _byOwner.Clear();
            }
            _loadedSlot = slot;
        }

        /// <summary>读档回档: 丢弃内存态, 下次访问从磁盘重载(磁盘 = 上次游戏存档时点)。</summary>
        internal static void InvalidateAll()
        {
            lock (_byOwner) _byOwner.Clear();
        }

        internal string Get(string key, string def = null)
            => _kv.TryGetValue(key, out var v) ? v : def;

        internal void Set(string key, string value)
        {
            if (value == null) _kv.Remove(key);
            else _kv[key] = value;
        }

        internal IReadOnlyDictionary<string, string> Snapshot() => _kv;

        internal void Flush(MelonLogger.Instance logger = null)
        {
            try
            {
                var dir = Path.Combine(PsApi.StateDir, _loadedSlot ?? "global");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, _owner + ".json"), JsonSerializer.Serialize(_kv, JsonOpts));
            }
            catch (Exception e) { PsApi.Warn(logger, $"save-state flush failed ({_owner}): {e.Message}"); }
        }

        private void Load(MelonLogger.Instance logger)
        {
            try
            {
                var file = Path.Combine(PsApi.StateDir, _loadedSlot ?? "global", _owner + ".json");
                if (File.Exists(file))
                    _kv = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new Dictionary<string, string>();
            }
            catch (Exception e)
            {
                _kv = new Dictionary<string, string>();
                PsApi.Warn(logger, $"save-state load failed ({_owner}): {e.Message}");
            }
        }

        internal static void FlushAll(MelonLogger.Instance logger = null)
        {
            lock (_byOwner)
                foreach (var st in _byOwner.Values) st.Flush(logger);
        }
    }
}
