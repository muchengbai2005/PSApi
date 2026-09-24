using System.Collections.Generic;

namespace PSApi.Events.PsUI
{
    /// <summary>PSUI AST: 一个 .psui 文件 = 一个面板。属性值: string / long / double / bool / List&lt;object&gt;。
    /// 解析只做校验, 不建任何游戏对象(惰性实例化: ui.open 时才构建)。</summary>
    internal sealed class PsUiPanel
    {
        internal string File;                       // 诊断用相对路径 (pack/ui/xxx.psui)
        internal string ShortId;                    // 面板短名 (window 显式 id, 缺省 = 文件名)
        internal readonly Dictionary<string, object> Props = new Dictionary<string, object>(System.StringComparer.OrdinalIgnoreCase);
        internal readonly List<PsUiElem> Children = new List<PsUiElem>();
        /// <summary>on_click 绑定的本包函数名 (启动期校验用): (函数名, 来源行号)。</summary>
        internal readonly List<(string Fn, int Line)> OnClickRefs = new List<(string, int)>();
        /// <summary>on_change 绑定的本包函数名 (U2; 机制+校验, U3 控件才是主战场)。</summary>
        internal readonly List<(string Fn, int Line)> OnChangeRefs = new List<(string, int)>();
        /// <summary>v1.4.0: window on_open 绑定的本包函数名 (机器绑定面板每次开窗回调; 启动期校验用)。</summary>
        internal readonly List<(string Fn, int Line)> OnOpenRefs = new List<(string, int)>();

        internal string GetString(string key, string def = null)
            => Props.TryGetValue(key, out var v) && v is string s ? s : def;
        internal bool GetBool(string key, bool def = false)
            => Props.TryGetValue(key, out var v) && v is bool b ? b : def;
        /// <summary>v1.4.1: 数值属性 (window spacing)。</summary>
        internal double GetNumber(string key, double def = 0)
        {
            if (!Props.TryGetValue(key, out var v)) return def;
            return v is long l ? l : v is double d ? d : def;
        }

        /// <summary>U4 后端分派: 面板树含 slot/grid_slot = 图元树后端 (整面板), 否则 CustomUIManager。</summary>
        internal bool HasSlots()
        {
            var stack = new List<PsUiElem>(Children);
            while (stack.Count > 0)
            {
                var e = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                if (e.Type == "slot" || e.Type == "grid_slot") return true;
                stack.AddRange(e.Children);
            }
            return false;
        }
    }

    /// <summary>面板元素节点 (window 以下的全部元素; window 本身由 PsUiPanel 承载)。</summary>
    internal sealed class PsUiElem
    {
        internal string Type;                       // 小写元素名 (label/button/row/...)
        internal string Id;                         // 可空
        internal int Line;
        internal readonly Dictionary<string, object> Props = new Dictionary<string, object>(System.StringComparer.OrdinalIgnoreCase);
        internal readonly List<PsUiElem> Children = new List<PsUiElem>();

        internal string GetString(string key, string def = null)
            => Props.TryGetValue(key, out var v) && v is string s ? s : def;
        internal double GetNumber(string key, double def = 0)
        {
            if (!Props.TryGetValue(key, out var v)) return def;
            return v is long l ? l : v is double d ? d : def;
        }
        internal bool GetBool(string key, bool def = false)
            => Props.TryGetValue(key, out var v) && v is bool b ? b : def;

        /// <summary>数组属性 (如 dropdown options); 缺失/非数组 = null。</summary>
        internal List<object> GetList(string key)
            => Props.TryGetValue(key, out var v) && v is List<object> l ? l : null;

        /// <summary>"WxH" / "420x300" 尺寸属性 → (w, h); 解析失败 = null。</summary>
        internal (float W, float H)? GetSize(string key)
        {
            if (!Props.TryGetValue(key, out var v) || v is not string s) return null;
            var parts = s.ToLowerInvariant().Split('x');
            if (parts.Length != 2) return null;
            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float w)) return null;
            if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float h)) return null;
            if (w <= 0 || h <= 0) return null;
            return (w, h);
        }
    }
}
