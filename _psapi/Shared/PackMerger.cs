using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MelonLoader;

namespace PSApi
{
    /// <summary>
    /// 包合并器 (编译 DLL 化 WP2): 合并 PackScanner 的文件夹包与内嵌包描述符, 输出去重 +
    /// 前置检查后的最终包列表与冲突条目。internal: Shared 双拷贝, Items/Events 各自调用。
    /// 规则:
    ///   ① 同 id 时 DLL 胜, 被压制的文件夹包记 duplicate 冲突; DLL 之间同 id 先注册者胜 (仅警告不弹窗);
    ///   ② prerequisites (pack.json 声明, 此前从未消费; 语义=内容包 id) 按去重后的接受集合求值,
    ///     缺前置的包跳过加载并记 missing_prereq 冲突; 两遍扫描 (先收全部 id, 再逐包查前置)。
    /// </summary>
    internal static class PackMerger
    {
        /// <summary>合并冲突条目 (internal; public 发布走 Items 侧 EmbeddedPackRegistry 薄层)。</summary>
        internal sealed class Conflict
        {
            internal string Kind;      // "missing_prereq" | "duplicate"
            internal string PackId;    // 冲突涉及的包 id
            internal string Detail;    // 中文详情
        }

        /// <summary>
        /// 内嵌包描述符的 internal 镜像 (与 PSApi.Items.EmbeddedPackDescriptor 字段一一对应;
        /// 隔离 public 类型使 PackMerger 可独立编译测试)。Assembly+Prefix 足够各程序集
        /// 自造 EmbeddedPackSource —— IPackSource 实例不能跨程序集消费 (Shared 双拷贝)。
        /// </summary>
        internal sealed class EmbeddedDescriptor
        {
            internal string Id;
            internal string Version;
            internal IReadOnlyList<string> Prerequisites;
            internal System.Reflection.Assembly ResourceAssembly;
            internal string ResourcePrefix;
        }

        /// <summary>合并入口。folderPacks = PackScanner.Scan 结果; conflicts 输出冲突条目 (可空=null 不收集)。</summary>
        internal static List<PackInfo> Merge(List<PackInfo> folderPacks, IReadOnlyList<EmbeddedDescriptor> embedded,
            List<Conflict> conflicts, MelonLogger.Instance logger)
        {
            var result = new List<PackInfo>();
            var byId = new Dictionary<string, PackInfo>(StringComparer.OrdinalIgnoreCase);

            // ---- ① 内嵌包优先 (同 id DLL 胜) ----
            if (embedded != null)
                foreach (var d in embedded)
                {
                    var info = BuildEmbeddedPack(d, logger);
                    if (info == null) continue;
                    if (byId.ContainsKey(info.Id))
                    {
                        PsApi.Warn(logger, $"embedded pack '{d.Id}': duplicate id '{info.Id}', first registered wins, skipped");
                        continue;
                    }
                    byId[info.Id] = info;
                    result.Add(info);
                }

            // ---- ② 文件夹包, 同 id 被 DLL 压制记 duplicate 冲突 ----
            if (folderPacks != null)
                foreach (var p in folderPacks)
                {
                    if (p == null || !p.Valid) continue;   // 解析错误 PackScanner 已警告
                    if (byId.ContainsKey(p.Id))
                    {
                        conflicts?.Add(new Conflict
                        {
                            Kind = "duplicate",
                            PackId = p.Id,
                            Detail = $"{p.Id} 同时存在 dll 与 pack, 已默认加载 dll",
                        });
                        continue;
                    }
                    byId[p.Id] = p;
                    result.Add(p);
                }

            // ---- ③ 前置检查 (两遍: 先收去重后全部 id, 再逐包求值) ----
            var accepted = new HashSet<string>(byId.Keys, StringComparer.OrdinalIgnoreCase);
            var final = new List<PackInfo>();
            foreach (var p in result)
            {
                var prereqs = p.Manifest?.Prerequisites;
                if (prereqs != null)
                {
                    var missing = prereqs
                        .Where(pr => !string.IsNullOrWhiteSpace(pr) && !accepted.Contains(pr.Trim()))
                        .Select(pr => pr.Trim()).ToList();
                    if (missing.Count > 0)
                    {
                        conflicts?.Add(new Conflict
                        {
                            Kind = "missing_prereq",
                            PackId = p.Id,
                            Detail = $"{p.Id} 需要 {string.Join(", ", missing)}",
                        });
                        PsApi.Warn(logger, $"pack '{p.Id}': missing prerequisites: {string.Join(", ", missing)}, skipped");
                        continue;
                    }
                }
                final.Add(p);
            }
            return final;
        }

        /// <summary>内嵌包描述符 → PackInfo (Dir=null; pack.json 缺失/解析失败 → 警告 + null)。
        /// 描述符的 version/prerequisites 与 pack.json 不一致时以 pack.json 为准 (编译器同源生成, 理论相等)。</summary>
        private static PackInfo BuildEmbeddedPack(EmbeddedDescriptor d, MelonLogger.Instance logger)
        {
            if (d == null || d.ResourceAssembly == null || string.IsNullOrWhiteSpace(d.Id)) return null;
            var info = new PackInfo
            {
                Dir = null,
                FolderName = "dll:" + d.Id,
                Source = new EmbeddedPackSource(d.ResourceAssembly, d.ResourcePrefix),
            };
            if (!info.Source.HasFile("pack.json"))
            {
                PsApi.Warn(logger, $"embedded pack '{d.Id}': missing pack.json resource, skipped");
                return null;
            }
            try
            {
                info.Manifest = JsonSerializer.Deserialize<PackManifest>(info.Source.ReadText("pack.json"), PackScanner.JsonOpts);
                if (info.Manifest == null || string.IsNullOrWhiteSpace(info.Manifest.Id))
                {
                    PsApi.Warn(logger, $"embedded pack '{d.Id}': pack.json missing required 'id', skipped");
                    return null;
                }
            }
            catch (Exception e)
            {
                PsApi.Warn(logger, $"embedded pack '{d.Id}': pack.json parse error: {e.Message}, skipped");
                return null;
            }
            if (!string.Equals(info.Manifest.Id, d.Id, StringComparison.OrdinalIgnoreCase))
                PsApi.Warn(logger, $"embedded pack '{d.Id}': pack.json id is '{info.Manifest.Id}', using pack.json");

            // 子目录统计 (与 PackScanner 文件夹包口径一致)
            info.HasItems = info.Source.HasDir("items");
            info.HasEvents = info.Source.HasDir("events");
            info.HasIcons = info.Source.HasDir("icons");
            if (info.HasItems) info.ItemFileCount = info.Source.ListFiles("items", ".json").Length;
            if (info.HasEvents) info.EventFileCount = info.Source.ListFiles("events", ".json").Length;
            return info;
        }
    }
}
