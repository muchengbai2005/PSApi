using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;

namespace PSApi
{
    /// <summary>
    /// PS-API 共享常量与日志。两个宿主 DLL(PSApi.Items/PSApi.Events)各自编译本文件,
    /// 类型均为 internal, 无跨程序集身份问题。
    /// 内容包根目录: UserData/PSApi/packs/&lt;pack&gt;/(pack.json + items/ + events/ + icons/ ...)
    /// </summary>
    internal static class PsApi
    {
        internal const string Version = "0.2.9";

        internal static readonly string RootDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "PSApi");
        internal static readonly string PacksDir = Path.Combine(RootDir, "packs");
        internal static readonly string StateDir = Path.Combine(RootDir, "state");
        internal static readonly string LogsDir = Path.Combine(RootDir, "logs");

        internal static void EnsureDirs()
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(PacksDir);
            Directory.CreateDirectory(StateDir);
            Directory.CreateDirectory(LogsDir);
        }

        // 每宿主传入自己的 logger, 保持 MelonLoader 控制台来源可分辨
        internal static void Log(MelonLogger.Instance logger, string msg) => logger?.Msg("[psapi] " + msg);
        internal static void Warn(MelonLogger.Instance logger, string msg) => logger?.Warning("[psapi] " + msg);
        internal static void Err(MelonLogger.Instance logger, string msg) => logger?.Error("[psapi] " + msg);

        /// <summary>内容包错误汇总落盘: UserData/PSApi/logs/pack_errors_yyyyMMdd_HHmmss.log(有错误才写)。</summary>
        internal static void WriteErrorLog(MelonLogger.Instance logger, List<string> errors)
        {
            if (errors == null || errors.Count == 0) return;
            try
            {
                EnsureDirs();
                string file = Path.Combine(LogsDir, $"pack_errors_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllLines(file, errors);
                Warn(logger, $"{errors.Count} pack error(s), details: {file}");
            }
            catch (Exception e) { Err(logger, "write error log failed: " + e.Message); }
        }
    }
}
