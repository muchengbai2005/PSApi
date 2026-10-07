using System;

namespace PSApi.Events.PsUI
{
    /// <summary>
    /// v1.41.0: grid_slot shape 串工具 — 引入第三状态 '2' = 锁定格 (暗色渲染可见, 表达
    /// "这里以后解锁"; 升级后 ui.machine_set_shape 变 0)。三态语义:
    ///   0 = 可放 (原生画底格) / 1 = 永久洞 (不渲染) / 2 = 锁定格 (框架画暗色 overlay)
    /// 原生 GridShapeBuilder.SetData 按 char-'0' 存字节, 放置判定只按 ==0 放行 — '2'
    /// 原生层面本就当 1 处理, 但为不依赖未文档化字节语义, 调原生 SetShape 前统一 2→1;
    /// psui 声明/builtin 参数里的串保留 2 (渲染层要知道哪些是锁定格)。
    /// </summary>
    internal static class ShapeUtil
    {
        /// <summary>三态串 → 原生 0/1 串 (2→1, 0/1 不变)。null 透传。</summary>
        internal static string ToNative(string shape) => shape?.Replace('2', '1');

        /// <summary>字符集校验: 非空且只含 0/1/2; 非法时 err 带 offending 字符。</summary>
        internal static bool IsValidCharset(string shape, out string err)
        {
            if (string.IsNullOrEmpty(shape)) { err = "空串"; return false; }
            for (int i = 0; i < shape.Length; i++)
            {
                char c = shape[i];
                if (c != '0' && c != '1' && c != '2') { err = $"含非法字符 '{c}' (只允许 0/1/2, 行优先)"; return false; }
            }
            err = null;
            return true;
        }

        /// <summary>统计某字符出现次数 (测试钉值用)。</summary>
        internal static int CountChar(string shape, char c)
        {
            if (shape == null) return 0;
            int n = 0;
            for (int i = 0; i < shape.Length; i++) if (shape[i] == c) n++;
            return n;
        }
    }
}
