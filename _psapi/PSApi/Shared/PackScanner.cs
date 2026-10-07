using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MelonLoader;

namespace PSApi
{
    /// <summary>
    /// 内容包扫描器: 扫 UserData/PSApi/packs/*, 解析 pack.json。
    /// 规则(与 NpcManager 一致语义): 目录按名排序; id 重复时先加载者胜, 后者记警告并跳过。
    /// 骨架阶段只统计子目录/文件数; 内容文件解析在 P1(items)/P3(events)落地。
    /// </summary>
    internal static class PackScanner
    {
        // PackMerger 解析内嵌包 pack.json 复用同一套选项
        internal static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true,
        };

        internal static List<PackInfo> Scan(MelonLogger.Instance logger)
        {
            var packs = new List<PackInfo>();
            PsApi.EnsureDirs();

            foreach (var dir in Directory.GetDirectories(PsApi.PacksDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var info = new PackInfo { Dir = dir, Source = new FolderPackSource(dir), FolderName = Path.GetFileName(dir) };
                var manifestPath = Path.Combine(dir, "pack.json");
                if (!File.Exists(manifestPath))
                {
                    info.Error = "missing pack.json";
                    packs.Add(info);
                    PsApi.Warn(logger, $"pack '{info.FolderName}': missing pack.json, skipped");
                    continue;
                }
                try
                {
                    info.Manifest = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(manifestPath), JsonOpts);
                    if (info.Manifest == null || string.IsNullOrWhiteSpace(info.Manifest.Id))
                        info.Error = "pack.json missing required 'id'";
                }
                catch (Exception e)
                {
                    info.Error = "pack.json parse error: " + e.Message;
                }

                // 子目录统计(存在性与文件数)
                info.HasItems = Directory.Exists(Path.Combine(dir, "items"));
                info.HasEvents = Directory.Exists(Path.Combine(dir, "events"));
                info.HasIcons = Directory.Exists(Path.Combine(dir, "icons"));
                if (info.HasItems) info.ItemFileCount = CountJson(dir, "items");
                if (info.HasEvents) info.EventFileCount = CountJson(dir, "events");

                packs.Add(info);
                if (info.Error != null)
                    PsApi.Warn(logger, $"pack '{info.FolderName}': {info.Error}");
            }

            // id 去重: 先加载(目录名序)者胜
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var accepted = new List<PackInfo>();
            foreach (var p in packs)
            {
                if (!p.Valid) continue;
                if (!seen.Add(p.Manifest.Id))
                {
                    PsApi.Warn(logger, $"pack '{p.FolderName}': duplicate id '{p.Manifest.Id}', skipped");
                    continue;
                }
                accepted.Add(p);
            }
            return accepted;
        }

        private static int CountJson(string packDir, string sub)
        {
            try { return Directory.GetFiles(Path.Combine(packDir, sub), "*.json", SearchOption.AllDirectories).Length; }
            catch { return 0; }
        }
    }
}
