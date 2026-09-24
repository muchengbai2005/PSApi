using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PSApi
{
    /// <summary>
    /// pack.json 清单 DTO(托管 System.Text.Json 解析, 与游戏 il2cpp 无关)。
    /// 最小骨架: id 必填, 其余可选; 未知字段忽略。
    /// </summary>
    internal sealed class PackManifest
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("version")] public string Version { get; set; }
        [JsonPropertyName("authors")] public List<string> Authors { get; set; }
        [JsonPropertyName("gameVersions")] public List<string> GameVersions { get; set; }
        [JsonPropertyName("prerequisites")] public List<string> Prerequisites { get; set; }
        [JsonPropertyName("loadAfter")] public List<string> LoadAfter { get; set; }
    }

    /// <summary>扫描结果: 一个包目录 + 清单(或错误)。</summary>
    internal sealed class PackInfo
    {
        internal string Dir;                 // 包根目录绝对路径
        internal IPackSource Source;         // 内容读取入口 (WP1; 文件夹包 = FolderPackSource)
        internal string FolderName;          // 目录名(日志用)
        internal PackManifest Manifest;      // 解析成功非空
        internal string Error;               // 解析失败原因(成功为 null)
        internal bool Valid => Manifest != null && Error == null;
        internal string Id => Manifest?.Id ?? FolderName;

        // 内容子目录(存在才标记; 具体文件由 P1+ 消费)
        internal bool HasItems;
        internal bool HasEvents;
        internal bool HasIcons;

        internal int ItemFileCount;
        internal int EventFileCount;
    }
}
