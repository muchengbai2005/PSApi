using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;

namespace PSApi.Items
{
    /// <summary>
    /// 内嵌包描述符 (编译 DLL 化 WP2): 包 DLL (PSPack.&lt;id&gt;.dll, MelonMod) 在 OnInitializeMelon
    /// 时经 EmbeddedPackRegistry.Register 登记自身。
    /// 只携带 Assembly + 资源前缀而非 IPackSource 实例 —— IPackSource 是 Shared 双拷贝的
    /// internal 类型, Items/Events 程序集身份不同, Events 必须拿 Assembly+Prefix 自造
    /// 自己程序集内的 EmbeddedPackSource, 不能消费 Items 构造的实例。
    /// </summary>
    public sealed class EmbeddedPackDescriptor
    {
        public string Id;
        public string Version;
        public IReadOnlyList<string> Prerequisites;
        public Assembly ResourceAssembly;
        public string ResourcePrefix;   // "pspack/"
    }

    /// <summary>合并冲突条目 (WP4 弹窗消费): 缺前置 / dll 与文件夹重复加载。</summary>
    public sealed class PackConflictInfo
    {
        public string Kind;     // "missing_prereq" | "duplicate"
        public string PackId;   // 冲突涉及的包 id
        public string Detail;   // 中文详情, 如 "gunworks 需要 psapi_core" / "gunworks 同时存在 dll 与 pack, 已默认加载 dll"
    }

    /// <summary>
    /// 内嵌包注册表: 跨程序集唯一通道 (Shared 静态状态双拷贝, 注册表只能放 PSApi.Items public API)。
    /// 包 DLL MelonPriority=5 &lt; Items=10, 保证 Register 先于 Items 扫包合并。
    /// Conflicts 由 Items 每次合并 (Rescan) 后重填, Events 只读消费。
    /// </summary>
    public static class EmbeddedPackRegistry
    {
        private static readonly List<EmbeddedPackDescriptor> _packs = new List<EmbeddedPackDescriptor>();
        private static readonly List<PackConflictInfo> _conflicts = new List<PackConflictInfo>();
        private static readonly HashSet<string> _ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>包 DLL 登记入口。防御: 空 id / 空 assembly 拒绝; 同 id 重复注册先者胜 + 警告。</summary>
        public static void Register(string id, string version, IList<string> prerequisites,
            Assembly resourceAssembly, string resourcePrefix)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                Warn("embedded pack register rejected: empty id");
                return;
            }
            if (resourceAssembly == null)
            {
                Warn($"embedded pack '{id}' register rejected: null resourceAssembly");
                return;
            }
            if (!_ids.Add(id))
            {
                Warn($"embedded pack '{id}': duplicate registration, first registered wins, skipped");
                return;
            }
            _packs.Add(new EmbeddedPackDescriptor
            {
                Id = id.Trim(),
                Version = version,
                Prerequisites = prerequisites != null
                    ? prerequisites.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList()
                    : null,
                ResourceAssembly = resourceAssembly,
                ResourcePrefix = string.IsNullOrWhiteSpace(resourcePrefix) ? "pspack/" : resourcePrefix,
            });
        }

        /// <summary>已登记的内嵌包描述符 (登记序)。</summary>
        public static IReadOnlyList<EmbeddedPackDescriptor> Packs => _packs;

        /// <summary>最近一次合并产出的冲突条目 (Items Rescan 后填充; 未合并过为空)。</summary>
        public static IReadOnlyList<PackConflictInfo> Conflicts => _conflicts;

        // ==================== internal: Items 侧薄层 (PackMerger 是 internal 结构) ====================

        /// <summary>描述符快照 → PackMerger internal 镜像 (合并输入)。</summary>
        internal static List<PackMerger.EmbeddedDescriptor> SnapshotForMerge()
        {
            return _packs.Select(p => new PackMerger.EmbeddedDescriptor
            {
                Id = p.Id,
                Version = p.Version,
                Prerequisites = p.Prerequisites,
                ResourceAssembly = p.ResourceAssembly,
                ResourcePrefix = p.ResourcePrefix,
            }).ToList();
        }

        /// <summary>合并后发布冲突条目 (internal 结构 → public PackConflictInfo)。</summary>
        internal static void PublishConflicts(List<PackMerger.Conflict> conflicts)
        {
            _conflicts.Clear();
            if (conflicts == null) return;
            foreach (var c in conflicts)
                _conflicts.Add(new PackConflictInfo { Kind = c.Kind, PackId = c.PackId, Detail = c.Detail });
        }

        // Register 可能先于 ItemsPlugin.OnInitializeMelon (包 DLL priority=5), logger 未就绪时自建
        private static void Warn(string msg)
        {
            var log = ItemsPlugin.Instance?.LoggerInstance ?? new MelonLogger.Instance("PSApi.Items");
            PsApi.Warn(log, msg);
        }
    }
}
