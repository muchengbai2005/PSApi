using System;

namespace PSApi.Events.PsScript
{
    /// <summary>编译期错误(词法/语法): 携带 文件:行:列; 抛出后该文件整体不加载, 其余文件/包不受影响。</summary>
    internal sealed class PsCompileError : Exception
    {
        internal readonly string File;
        internal readonly int Line;
        internal readonly int Col;

        internal PsCompileError(string file, int line, int col, string msg)
            : base($"{file}:{line}:{col}: {msg}")
        {
            File = file;
            Line = line;
            Col = col;
        }
    }

    /// <summary>运行期错误: 携带源码行号(0=未知); handler 内抛出由引擎捕获记录, 不影响同事件其他 handler。</summary>
    internal class PsRuntimeError : Exception
    {
        internal readonly int Line;

        internal PsRuntimeError(string msg, int line = 0) : base(msg) { Line = line; }
    }

    // ---- 控制流信号(非错误, 不参与日志) ----

    internal sealed class PsBreak : Exception { }

    internal sealed class PsContinue : Exception { }

    internal sealed class PsReturn : Exception
    {
        internal readonly object Value;
        internal PsReturn(object value) { Value = value; }
    }
}
