using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;

namespace PSApi.Items
{
    /// <summary>
    /// v0.5.9: 配方锁定状态存取。每台机器实例的 state (TagSystem) 存一个 tag,
    /// valueString = 逗号分隔的锁定 recipe ids。随存档存活 (TagSystem 是存档序列化载体,
    /// 见 ModulePrinterService 进度存取 + 文档 §3.3), 天然按实例隔离 (每台机器自己的 state)。
    /// recipe id 含 ':' 但不含 ',', 逗号分隔安全。
    /// </summary>
    internal static class RecipeLockService
    {
        private const string LockTag = "psapi_locked_recipes";
        private static readonly char[] Sep = { ',' };

        /// <summary>读该机器锁定的 recipe id 集合 (空表示无锁定)。</summary>
        internal static HashSet<string> GetLocked(GameItem machine)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (machine == null) return result;
            try
            {
                var tag = machine.state?.GetTag(LockTag);
                if (tag == null) return result;
                string s = tag.valueString;
                if (string.IsNullOrWhiteSpace(s)) return result;
                foreach (var part in s.Split(Sep, StringSplitOptions.RemoveEmptyEntries))
                    result.Add(part.Trim());
            }
            catch { }
            return result;
        }

        /// <summary>写该机器锁定的 recipe id 集合。</summary>
        internal static void SetLocked(GameItem machine, IEnumerable<string> ids)
        {
            if (machine == null) return;
            try
            {
                var ts = machine.state;
                if (ts == null) return;
                string joined = ids == null ? "" :
                    string.Join(",", ids.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
                ts.InitTagString(LockTag);
                var tag = ts.GetTag(LockTag);
                if (tag != null) tag.SetString(joined);
            }
            catch { }
        }

        internal static bool IsLocked(GameItem machine, string recipeId)
        {
            if (string.IsNullOrWhiteSpace(recipeId)) return false;
            return GetLocked(machine).Contains(recipeId);
        }

        /// <summary>切换某配方锁定状态, 返回新状态 (true=已锁定)。</summary>
        internal static bool Toggle(GameItem machine, string recipeId)
        {
            if (machine == null || string.IsNullOrWhiteSpace(recipeId)) return false;
            var set = GetLocked(machine);
            bool now;
            if (set.Contains(recipeId)) { set.Remove(recipeId); now = false; }
            else { set.Add(recipeId); now = true; }
            SetLocked(machine, set);
            return now;
        }
    }
}