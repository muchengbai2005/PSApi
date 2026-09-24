using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PSApi
{
    /// <summary>
    /// 内容包来源抽象 (编译 DLL 化 WP1): 文件夹包与后续 DLL 内嵌包共用同一读取入口。
    /// 逻辑路径统一正斜杠、相对包根 (如 "items/gun1.json")。
    /// 防御语义与原先各调用点一致: 目录扫描/存在性探测内部吞异常 (返回空数组/false);
    /// 单文件读取 (ReadText/ReadBytes) 异常原样抛出, 由调用方按文件粒度记错误。
    /// internal: Shared 同时编进 Items/Events 两个程序集, 不能加 public 跨程序集类型。
    /// </summary>
    internal interface IPackSource
    {
        /// <summary>日志用描述, 如 "folder: UserData/PSApi/packs/gunworks"。</summary>
        string Describe();

        /// <summary>子目录是否存在内容 (异常视为不存在)。</summary>
        bool HasDir(string subdir);

        /// <summary>递归列出子目录下指定扩展名 (含点, 如 ".json") 的文件;
        /// 返回逻辑相对路径 (正斜杠, 含 subdir 前缀), 按 OrdinalIgnoreCase 排序; 异常返回空数组。</summary>
        string[] ListFiles(string subdir, string extension);

        /// <summary>逻辑相对路径 (正斜杠) 指向的文件是否存在。</summary>
        bool HasFile(string relPath);

        /// <summary>按逻辑相对路径读文本; 失败抛异常 (调用方按文件粒度捕获)。</summary>
        string ReadText(string relPath);

        /// <summary>按逻辑相对路径读字节; 失败抛异常。</summary>
        byte[] ReadBytes(string relPath);
    }

    /// <summary>文件夹内容包 (UserData/PSApi/packs/&lt;id&gt;/): 现状直读行为原样搬入。</summary>
    internal sealed class FolderPackSource : IPackSource
    {
        private readonly string _dir;

        internal FolderPackSource(string dir) { _dir = dir; }

        public string Describe() => "folder: " + _dir;

        public bool HasDir(string subdir)
        {
            try { return Directory.Exists(ToFull(subdir)); }
            catch { return false; }
        }

        public string[] ListFiles(string subdir, string extension)
        {
            try
            {
                string full = ToFull(subdir);
                if (!Directory.Exists(full)) return Array.Empty<string>();
                return Directory.GetFiles(full, "*" + extension, SearchOption.AllDirectories)
                    .Select(ToLogical)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        public bool HasFile(string relPath)
        {
            try { return File.Exists(ToFull(relPath)); }
            catch { return false; }
        }

        public string ReadText(string relPath) => File.ReadAllText(ToFull(relPath));

        public byte[] ReadBytes(string relPath) => File.ReadAllBytes(ToFull(relPath));

        // 逻辑路径 (正斜杠) ↔ 物理绝对路径
        private string ToFull(string relPath) => Path.Combine(_dir, relPath.Replace('/', Path.DirectorySeparatorChar));

        private string ToLogical(string fullPath) => Path.GetRelativePath(_dir, fullPath).Replace('\\', '/');
    }

    /// <summary>
    /// DLL 内嵌内容包 (编译 DLL 化 WP2): 包文件作为程序集资源, 逻辑名前缀 "pspack/"。
    /// 索引时定位资源名中前缀首次出现处截断 (兼容编译器未用 LogicalName 固定、
    /// 资源名带默认命名空间前缀的情况), 截得的部分即逻辑相对路径。
    /// 懒建索引: 首次访问时枚举 GetManifestResourceNames()。
    /// </summary>
    internal sealed class EmbeddedPackSource : IPackSource
    {
        internal const string DefaultPrefix = "pspack/";

        private readonly System.Reflection.Assembly _assembly;
        private readonly string _prefix;                       // 归一化为 "xxx/" 形式
        private Dictionary<string, string> _index;             // 逻辑路径 → 资源全名 (OrdinalIgnoreCase), null=未建

        internal EmbeddedPackSource(System.Reflection.Assembly assembly, string resourcePrefix = DefaultPrefix)
        {
            _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
            string p = string.IsNullOrWhiteSpace(resourcePrefix) ? DefaultPrefix : resourcePrefix.Replace('\\', '/');
            _prefix = p.EndsWith("/", StringComparison.Ordinal) ? p : p + "/";
        }

        public string Describe() => "dll: " + _assembly.GetName().Name;

        public bool HasDir(string subdir)
        {
            string head = subdir.TrimEnd('/') + "/";
            EnsureIndex();
            foreach (var key in _index.Keys)
                if (key.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public string[] ListFiles(string subdir, string extension)
        {
            string head = subdir.TrimEnd('/') + "/";
            EnsureIndex();
            return _index.Keys
                .Where(k => k.StartsWith(head, StringComparison.OrdinalIgnoreCase)
                         && k.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public bool HasFile(string relPath)
        {
            EnsureIndex();
            return _index.ContainsKey(relPath);
        }

        public string ReadText(string relPath)
        {
            using (var stream = OpenStream(relPath))
            using (var reader = new StreamReader(stream))   // 默认 UTF-8 + BOM 探测
                return reader.ReadToEnd();
        }

        public byte[] ReadBytes(string relPath)
        {
            using (var stream = OpenStream(relPath))
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private Stream OpenStream(string relPath)
        {
            EnsureIndex();
            if (!_index.TryGetValue(relPath, out var resourceName))
                throw new FileNotFoundException("embedded resource not found: " + relPath);
            var stream = _assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                throw new FileNotFoundException("embedded resource stream missing: " + resourceName);
            return stream;
        }

        // 资源名 → 逻辑路径: 找前缀首次出现处, 其后部分为逻辑路径 (反斜杠归一)
        private void EnsureIndex()
        {
            if (_index != null) return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in _assembly.GetManifestResourceNames())
            {
                string normalized = name.Replace('\\', '/');
                int idx = normalized.IndexOf(_prefix, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                string logical = normalized.Substring(idx + _prefix.Length);
                if (logical.Length == 0) continue;
                if (!map.ContainsKey(logical)) map[logical] = name;
            }
            _index = map;
        }
    }
}
