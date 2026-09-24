using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PackCompiler
{
    /// <summary>
    /// 内容包编译器 (编译 DLL 化 WP3): 文件夹包 (UserData/PSApi/packs/&lt;id&gt;/) → PSPack.&lt;id&gt;.dll。
    /// 包内全是数据/解释脚本, "编译" = 资源打包 (LogicalName=pspack/&lt;relpath&gt;) + MelonMod 引导 stub 生成。
    /// 用法:
    ///   pack_compiler &lt;packDir&gt; [outDir]   编译包目录 (outDir 缺省 = 工具目录 out/)
    ///   pack_compiler --verify &lt;dllPath&gt;   自检: 列出 pspack/ 资源数与总字节, 读 pack.json 打印 id/version
    /// 本工具是构建器不是 mod, 不引用 MelonLoader。
    /// </summary>
    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitUsage = 2;
        private const int ExitPackError = 3;
        private const int ExitPrereqMissing = 4;
        private const int ExitBuildFailed = 5;

        // 与 PackScanner 同款 JSON 选项 (容忍注释/尾逗号/大小写)
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true,
        };

        // 嵌入时排除的文件名 (隐藏文件 / 系统垃圾)
        private static readonly HashSet<string> ExcludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".DS_Store", "Thumbs.db", "desktop.ini" };

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }   // 中文输出防 GBK 控制台乱码
            try
            {
                if (args.Length >= 1 && args[0] == "--verify")
                {
                    if (args.Length != 2) return Usage();
                    return Verify(Path.GetFullPath(args[1]));
                }
                if (args.Length < 1 || args.Length > 2) return Usage();

                string packDir = Path.GetFullPath(args[0]);
                string outDir = args.Length == 2 ? Path.GetFullPath(args[1]) : Path.Combine(ToolDir(), "out");
                return Compile(packDir, outDir);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("[pack_compiler] 未处理异常: " + e);
                return ExitBuildFailed;
            }
        }

        private static int Usage()
        {
            Console.Error.WriteLine("用法: pack_compiler <packDir> [outDir] | pack_compiler --verify <dllPath>");
            return ExitUsage;
        }

        // ==================== 编译 ====================

        private static int Compile(string packDir, string outDir)
        {
            if (!Directory.Exists(packDir)) return PackError($"包目录不存在: {packDir}");
            string manifestPath = Path.Combine(packDir, "pack.json");
            if (!File.Exists(manifestPath)) return PackError($"包目录缺少 pack.json: {packDir}");

            Manifest manifest;
            try { manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOpts); }
            catch (Exception e) { return PackError($"pack.json 解析失败: {e.Message}"); }
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
                return PackError("pack.json 缺少必填字段 'id'");
            string id = manifest.Id.Trim();
            string version = string.IsNullOrWhiteSpace(manifest.Version) ? "0.0.0" : manifest.Version.Trim();
            string authors = manifest.Authors != null && manifest.Authors.Count > 0
                ? string.Join(", ", manifest.Authors) : "unknown";
            var prereqs = manifest.Prerequisites != null
                ? manifest.Prerequisites.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList()
                : new List<string>();

            // ---- 收集包文件 (全部嵌入, 含 pack.json; 排除隐藏/系统文件) ----
            var files = Directory.GetFiles(packDir, "*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string name = Path.GetFileName(f);
                    return !name.StartsWith(".", StringComparison.Ordinal) && !ExcludedNames.Contains(name);
                })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0) return PackError($"包目录无可嵌入文件: {packDir}");

            // ---- 前置: PSApi.Items.dll 必须已构建 (包 DLL 以 Private=false 引用它) ----
            string root = RepoRoot();
            string itemsDll = new[]
            {
                Path.Combine(root, "_psapi", "PSApi.Items", "bin", "Release", "PSApi.Items.dll"),
                Path.Combine(root, "_psapi", "PSApi.Items", "bin", "Release", "net6.0", "PSApi.Items.dll"),
            }.FirstOrDefault(File.Exists);
            if (itemsDll == null)
                return PackError("未找到 PSApi.Items.dll, 请先在 _psapi/PSApi.Items 执行 dotnet build -c Release");
            string melonDll = Path.Combine(root, "MelonLoader", "net6", "MelonLoader.dll");
            if (!File.Exists(melonDll))
                return PackError("未找到 MelonLoader.dll: " + melonDll);

            // ---- 临时构建目录 (工具 obj/ 下, 每次重建; 被工具自身 csproj 默认排除) ----
            string buildDir = Path.Combine(ToolDir(), "obj", "packbuild_" + Sanitize(id));
            if (Directory.Exists(buildDir)) Directory.Delete(buildDir, true);
            Directory.CreateDirectory(buildDir);

            File.WriteAllText(Path.Combine(buildDir, "PackPlugin.cs"), StubSource(id, version, authors, prereqs));
            File.WriteAllText(Path.Combine(buildDir, "PSPack." + id + ".csproj"),
                CsprojSource(id, files, packDir, melonDll, itemsDll), new UTF8Encoding(false));

            // ---- dotnet build ----
            Console.WriteLine($"[pack_compiler] 构建 PSPack.{id} ({files.Count} 文件) ...");
            var psi = new ProcessStartInfo("dotnet", "build -c Release")
            {
                WorkingDirectory = buildDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var proc = Process.Start(psi);
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Console.WriteLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine($"[pack_compiler] dotnet build 失败 (exit={proc.ExitCode}), 构建目录保留备查: {buildDir}");
                return ExitBuildFailed;
            }

            string produced = Path.Combine(buildDir, "bin", "Release", "PSPack." + id + ".dll");
            if (!File.Exists(produced))
            {
                Console.Error.WriteLine("[pack_compiler] 构建成功但未找到产物: " + produced);
                return ExitBuildFailed;
            }
            Directory.CreateDirectory(outDir);
            string target = Path.Combine(outDir, "PSPack." + id + ".dll");
            File.Copy(produced, target, true);

            long totalBytes = files.Sum(f => new FileInfo(f).Length);
            Console.WriteLine($"[pack_compiler] 完成: id={id} version={version}");
            Console.WriteLine($"[pack_compiler] 嵌入文件 {files.Count} 个, 共 {totalBytes:N0} 字节");
            Console.WriteLine($"[pack_compiler] 输出: {target} ({new FileInfo(target).Length:N0} 字节)");
            return ExitOk;
        }

        // ==================== 自检 ====================

        private static int Verify(string dllPath)
        {
            if (!File.Exists(dllPath)) return PackError("dll 不存在: " + dllPath);
            var asm = Assembly.LoadFrom(dllPath);
            // 只读元数据/资源, 不触碰类型 (避免触发 MelonLoader/PSApi.Items 引用解析)
            var resources = asm.GetManifestResourceNames()
                .Where(n => n.IndexOf("pspack/", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            long total = 0;
            foreach (var n in resources)
                using (var s = asm.GetManifestResourceStream(n))
                    total += s?.Length ?? 0;
            Console.WriteLine($"[verify] {Path.GetFileName(dllPath)}: pspack/ 资源 {resources.Count} 个, 共 {total:N0} 字节");

            string packJsonName = resources.FirstOrDefault(n =>
                n.EndsWith("pspack/pack.json", StringComparison.OrdinalIgnoreCase));
            if (packJsonName == null) return PackError("dll 内缺少 pspack/pack.json 资源");
            string text;
            using (var s = asm.GetManifestResourceStream(packJsonName))
            using (var r = new StreamReader(s))
                text = r.ReadToEnd();
            var manifest = JsonSerializer.Deserialize<Manifest>(text, JsonOpts);
            Console.WriteLine($"[verify] pack.json: id={manifest?.Id} version={manifest?.Version}"
                + $" prerequisites=[{string.Join(", ", manifest?.Prerequisites ?? new List<string>())}]");
            return ExitOk;
        }

        // ==================== 模板生成 ====================

        /// <summary>
        /// 引导 stub: MelonPriority(5) 必须先于 PSApi.Items(10) 注册, 否则合并扫描时内嵌包未就位、
        /// 冲突弹窗永远为空。RegisterCore 标 NoInlining: PSApi.Items 缺失时硬引用类型在方法 JIT 时
        /// 才抛 FileNotFound/TypeLoad, 调用方 try/catch 才能接住 (与 Events SnapshotEmbedded 防御同款)。
        /// </summary>
        private static string StubSource(string id, string version, string authors, List<string> prereqs)
        {
            string ns = "PSPack_" + Sanitize(id);
            string prereqList = string.Join(", ", prereqs.Select(p => CsString(p)));
            var sb = new StringBuilder();
            sb.AppendLine("using MelonLoader;");
            sb.AppendLine("using System.Runtime.CompilerServices;");
            sb.AppendLine();
            sb.AppendLine("// 由 pack_compiler 生成, 请勿手改 (源包: " + id + ")");
            sb.AppendLine($"[assembly: MelonInfo(typeof({ns}.PackPlugin), {CsString("PSPack." + id)}, {CsString(version)}, {CsString(authors)})]");
            sb.AppendLine("[assembly: MelonGame(\"Questing Goose Studio\", \"Probably Stolen\")]");
            sb.AppendLine("[assembly: MelonPriority(5)]");
            sb.AppendLine("[assembly: MelonOptionalDependencies(\"PSApi.Items\")]");
            sb.AppendLine();
            sb.AppendLine("namespace " + ns);
            sb.AppendLine("{");
            sb.AppendLine("    public class PackPlugin : MelonMod");
            sb.AppendLine("    {");
            sb.AppendLine("        public override void OnInitializeMelon()");
            sb.AppendLine("        {");
            sb.AppendLine("            try { RegisterCore(); }");
            sb.AppendLine("            catch (System.Exception e)");
            sb.AppendLine("            {");
            sb.AppendLine($"                LoggerInstance.Warning({CsString($"[PSPack.{id}] PSApi.Items 缺失或版本不兼容, 包未注册: ")} + e.GetType().Name);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        [MethodImpl(MethodImplOptions.NoInlining)]");
            sb.AppendLine("        private static void RegisterCore()");
            sb.AppendLine("        {");
            sb.AppendLine($"            PSApi.Items.EmbeddedPackRegistry.Register({CsString(id)}, {CsString(version)},");
            sb.AppendLine($"                new string[] {{ {prereqList} }}, typeof(PackPlugin).Assembly, \"pspack/\");");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>生成的包 csproj: 与 _psapi/PSApi.Items.csproj 同 TargetFramework/语言版本;
        /// 只引用 MelonLoader + PSApi.Items(Private=false), 不需要游戏 Il2Cpp 程序集。</summary>
        private static string CsprojSource(string id, List<string> files, string packDir, string melonDll, string itemsDll)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <TargetFramework>net6.0</TargetFramework>");
            sb.AppendLine("    <AssemblyName>PSPack." + Xml(id) + "</AssemblyName>");
            sb.AppendLine("    <RootNamespace>PSPack_" + Sanitize(id) + "</RootNamespace>");
            sb.AppendLine("    <Nullable>disable</Nullable>");
            sb.AppendLine("    <ImplicitUsings>disable</ImplicitUsings>");
            sb.AppendLine("    <LangVersion>latest</LangVersion>");
            sb.AppendLine("    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine("  <ItemGroup>");
            foreach (var f in files)
            {
                string rel = Path.GetRelativePath(packDir, f).Replace('\\', '/');
                sb.AppendLine($"    <EmbeddedResource Include=\"{Xml(f)}\">");
                sb.AppendLine($"      <LogicalName>pspack/{Xml(rel)}</LogicalName>");
                sb.AppendLine("    </EmbeddedResource>");
            }
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <Reference Include=\"MelonLoader\">");
            sb.AppendLine("      <HintPath>" + Xml(melonDll) + "</HintPath>");
            sb.AppendLine("    </Reference>");
            sb.AppendLine("    <Reference Include=\"PSApi.Items\">");
            sb.AppendLine("      <HintPath>" + Xml(itemsDll) + "</HintPath>");
            sb.AppendLine("      <Private>false</Private>");
            sb.AppendLine("    </Reference>");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("</Project>");
            return sb.ToString();
        }

        // ==================== 辅助 ====================

        private sealed class Manifest
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("version")] public string Version { get; set; }
            [JsonPropertyName("authors")] public List<string> Authors { get; set; }
            [JsonPropertyName("prerequisites")] public List<string> Prerequisites { get; set; }
        }

        /// <summary>包 id → 合法 C# 标识符 (非字母数字下划线 → _, 数字开头加 _ 前缀)。</summary>
        private static string Sanitize(string id)
        {
            var sb = new StringBuilder(id.Length);
            foreach (var c in id)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            if (sb.Length == 0) return "pack";
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        private static string CsString(string s)
            => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string Xml(string s)
            => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// <summary>仓库根: 从工具程序集位置向上找 MelonLoader/net6/MelonLoader.dll 所在层。</summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "MelonLoader", "net6", "MelonLoader.dll")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("无法定位仓库根 (MelonLoader/net6/MelonLoader.dll)");
        }

        private static string ToolDir() => Path.Combine(RepoRoot(), "_tools", "pack_compiler");

        private static int PackError(string msg)
        {
            Console.Error.WriteLine("[pack_compiler] 错误: " + msg);
            Console.Error.WriteLine("检查清单:");
            Console.Error.WriteLine("  1. packDir 是否指向内容包根目录 (含 pack.json 的那层)");
            Console.Error.WriteLine("  2. pack.json 是否为合法 JSON 且含必填字段 \"id\"");
            Console.Error.WriteLine("  3. PSApi.Items 是否已构建 (_psapi/PSApi.Items → dotnet build -c Release)");
            return ExitPackError;
        }
    }
}
