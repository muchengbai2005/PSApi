using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PSApi.Events.PsScript;

namespace PSApi.Events.PsUI
{
    /// <summary>
    /// PSUI 解析器 (ui/11 §2): .psui 缩进树 → PsUiPanel。
    /// 行格式: 元素行 `type [id]: [行内属性 k: v, ...]` / 属性行 `key: value`。
    /// 子元素/属性缩进一级; 注释 # (引号外); 值 = 引号串/数字/布尔/数组[a, b]/尺寸WxH/裸串。
    /// 未知元素/属性 = 警告并跳过 (向前兼容), 不致命; 结构错误 (无 window 根/缩进乱跳) = PsCompileError。
    /// slot/grid_slot (U4) 解析即实例化: 面板含槽位 → 整体走图元树后端 (PsUiPixelBackend)。
    /// </summary>
    internal static class PsUiParser
    {
        /// <summary>可实例化元素 (U1: 布局/label/button/progress/image; U3: +toggle/slider/input/dropdown;
        /// U4: +slot/grid_slot — 仅在图元树后端实例化, CustomUIManager 后端不含槽位面板;
        /// v1.4.1: +spacer 网格空单元占位 — 同样仅图元树后端)。</summary>
        private static readonly HashSet<string> Instantiable = new HashSet<string>(StringComparer.Ordinal)
        {
            "row", "column", "grid", "scroll", "label", "button", "progress", "image",
            "toggle", "slider", "input", "dropdown",
            "slot", "grid_slot", "spacer",
        };

        /// <summary>每元素已知属性表 (表外 = 警告跳过)。window 属性挂在 PsUiPanel 上。</summary>
        private static readonly Dictionary<string, HashSet<string>> KnownProps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["window"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "title", "size", "position", "draggable", "close_on_escape", "layer", "on_build", "persistent", "on_open", "spacing" },
            ["row"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "spacing" },
            ["column"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "spacing" },
            ["grid"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "spacing", "columns", "cell" },
            ["scroll"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "height" },
            ["label"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text", "size", "font_size" },
            ["button"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text", "on_click", "tooltip", "size", "icon", "font_size" },
            ["progress"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "value", "label" },
            ["image"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src", "size" },
            ["toggle"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "label", "checked", "on_change" },
            ["slider"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "label", "min", "max", "step", "value", "on_change" },
            ["input"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "label", "placeholder", "text", "on_change" },
            ["dropdown"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "label", "options", "index", "on_change" },
            ["slot"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "whitelist", "size", "on_change" },
            ["grid_slot"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "whitelist", "size", "on_change" },
            ["spacer"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "size" },
        };

        internal static bool IsInstantiable(string type) => Instantiable.Contains(type);
        internal static bool IsKnown(string type) => Instantiable.Contains(type);

        private sealed class Frame
        {
            internal int Indent;
            internal PsUiElem Elem;         // null = window 根 (属性落 panel.Props)
            internal PsUiPanel Panel;
            internal bool Skipped;          // 未知元素子树: 内容全部跳过
        }

        /// <summary>解析一个 .psui 文件。warnings 收集非致命问题; 结构错误抛 PsCompileError。</summary>
        internal static PsUiPanel Parse(string file, string text, string fallbackId, List<string> warnings)
        {
            var panel = new PsUiPanel { File = file };
            var stack = new List<Frame>();
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool sawWindow = false;

            for (int i = 0; i < lines.Length; i++)
            {
                int lineNo = i + 1;
                string raw = StripComment(lines[i]).TrimEnd();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                int indent = IndentOf(raw);
                string body = raw.TrimStart();

                // ---- 弹栈到本行缩进层级 ----
                while (stack.Count > 0 && indent <= stack[stack.Count - 1].Indent)
                {
                    // 同级: 弹出同缩进帧 (元素帧在其父级缩进上登记)
                    if (indent == stack[stack.Count - 1].Indent && stack.Count > 0) { stack.RemoveAt(stack.Count - 1); break; }
                    stack.RemoveAt(stack.Count - 1);
                }

                var parent = stack.Count > 0 ? stack[stack.Count - 1] : null;
                if (parent != null && parent.Skipped) continue;   // 未知元素子树整棵跳过

                // ---- 行分类: 元素行 (type [id]: [行内属性]) vs 属性行 (key: value) ----
                // 双标识符必为元素行; 单标识符: 冒号后为空 → 元素行 (已知或未知容器),
                // 冒号后有值 → 属性行 (元素行带行内属性必须写 id, 否则 "label: \"x\"" 与属性行不可区分)。
                // 未知元素入 Skipped 帧, 子树整棵跳过 (防误归属父级)。
                string type = null, id = null, inline = null, propKey = null, propVal = null;
                bool isElementLine = false, isPropLine = false;
                int colon = body.IndexOf(':');
                if (colon > 0)
                {
                    string head = body.Substring(0, colon).Trim();
                    var parts = head.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string after = body.Substring(colon + 1).Trim();
                    if (parts.Length == 2 && IsIdent(parts[0]))
                    {
                        type = parts[0]; id = parts[1]; inline = after.Length > 0 ? after : null;
                        isElementLine = true;
                    }
                    else if (parts.Length == 1 && IsIdent(parts[0]))
                    {
                        if (after.Length == 0)
                        {
                            type = parts[0]; inline = null;
                            isElementLine = true;
                        }
                        else { propKey = parts[0]; propVal = after; isPropLine = true; }
                    }
                }

                // ---- 元素行 ----
                if (isElementLine)
                {
                    if (type == "window")
                    {
                        if (sawWindow || stack.Count > 0)
                            throw new PsCompileError(file, lineNo, 1, "window 必须是唯一的根元素");
                        sawWindow = true;
                        if (id != null && !IsIdent(id))
                        {
                            warnings.Add($"{file}:{lineNo}: window id '{id}' 非法(须为标识符), 改用文件名 '{fallbackId}'");
                            id = null;
                        }
                        panel.ShortId = id ?? fallbackId;
                        if (inline != null) ParseInlineProps(panel.Props, "window", inline, file, lineNo, warnings, panel, null);
                        stack.Add(new Frame { Indent = indent, Panel = panel });
                        continue;
                    }
                    if (!sawWindow)
                        throw new PsCompileError(file, lineNo, 1, $"window 根元素之前不允许出现 '{type}'");
                    if (parent == null)
                        throw new PsCompileError(file, lineNo, 1, $"'{type}' 必须缩进在 window 之内");

                    if (!IsKnown(type))
                    {
                        warnings.Add($"{file}:{lineNo}: 未知元素 '{type}', 已跳过(及其子树)");
                        stack.Add(new Frame { Indent = indent, Panel = panel, Skipped = true });
                        continue;
                    }

                    var elem = new PsUiElem { Type = type, Id = id, Line = lineNo };
                    if (id != null && !IsIdent(id))
                    {
                        warnings.Add($"{file}:{lineNo}: 元素 id '{id}' 非法(须为标识符), 按匿名处理");
                        elem.Id = null;
                    }
                    if (parent.Elem == null) parent.Panel.Children.Add(elem);
                    else parent.Elem.Children.Add(elem);
                    if (inline != null) ParseInlineProps(elem.Props, type, inline, file, lineNo, warnings, panel, elem);
                    stack.Add(new Frame { Indent = indent, Elem = elem, Panel = parent.Panel });
                    continue;
                }

                // ---- 属性行 ----
                if (isPropLine)
                {
                    if (!sawWindow || parent == null)
                        throw new PsCompileError(file, lineNo, 1, $"属性 '{propKey}' 必须缩进在某个元素之内");
                    ApplyProp(parent.Elem != null ? parent.Elem.Props : parent.Panel.Props,
                        parent.Elem != null ? parent.Elem.Type : "window",
                        propKey, propVal, file, lineNo, warnings, panel, parent.Elem);
                    continue;
                }

                warnings.Add($"{file}:{lineNo}: 无法解析的行 '{body}', 已跳过");
            }

            if (!sawWindow)
                throw new PsCompileError(file, 1, 1, "缺少 window 根元素");
            return panel;
        }

        // ==================== 行级解析 ====================

        private static bool IsIdent(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
            for (int i = 1; i < s.Length; i++)
                if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_')) return false;
            return true;
        }

        // ==================== 属性应用 ====================

        /// <summary>行内属性: 逗号分隔的 k: v 对 (引号/数组内逗号跳过)。</summary>
        private static void ParseInlineProps(Dictionary<string, object> props, string elemType, string inline,
            string file, int line, List<string> warnings, PsUiPanel panel, PsUiElem elem)
        {
            foreach (var seg in SplitTopLevel(inline, ','))
            {
                int colon = seg.IndexOf(':');
                if (colon <= 0) { warnings.Add($"{file}:{line}: 行内属性段 '{seg.Trim()}' 无法解析, 已跳过"); continue; }
                string k = seg.Substring(0, colon).Trim();
                string v = seg.Substring(colon + 1).Trim();
                if (!IsIdent(k)) { warnings.Add($"{file}:{line}: 行内属性名 '{k}' 非法, 已跳过"); continue; }
                ApplyProp(props, elemType, k, v, file, line, warnings, panel, elem);
            }
        }

        private static void ApplyProp(Dictionary<string, object> props, string elemType, string key, string rawVal,
            string file, int line, List<string> warnings, PsUiPanel panel, PsUiElem elem)
        {
            if (!KnownProps.TryGetValue(elemType, out var known) || !known.Contains(key))
            {
                warnings.Add($"{file}:{line}: 元素 '{elemType}' 未知属性 '{key}', 已跳过");
                return;
            }
            var value = ParseValue(rawVal);
            props[key] = value;

            // on_click / on_change 收集 (启动期函数存在性校验用)
            if ((key == "on_click" || key == "on_change") && elem != null && value is string fn)
            {
                if (!IsIdent(fn))
                    warnings.Add($"{file}:{line}: {key} 值 '{fn}' 不是合法函数名, 触发时不生效");
                else if (key == "on_click")
                    panel.OnClickRefs.Add((fn, line));
                else
                    panel.OnChangeRefs.Add((fn, line));
            }
            // v1.4.0: window on_open 收集 (机器绑定面板每次开窗回调; window 属性 elem == null)
            if (key == "on_open" && elem == null && value is string openFn)
            {
                if (!IsIdent(openFn))
                    warnings.Add($"{file}:{line}: on_open 值 '{openFn}' 不是合法函数名, 触发时不生效");
                else
                    panel.OnOpenRefs.Add((openFn, line));
            }
        }

        /// <summary>值解析: 引号串 / [数组] / true|false / 整数 / 小数 / 裸串。</summary>
        internal static object ParseValue(string raw)
        {
            if (raw.Length == 0) return "";
            if (raw[0] == '"' && raw.Length >= 2 && raw[raw.Length - 1] == '"')
                return Unescape(raw.Substring(1, raw.Length - 2));
            if (raw[0] == '[' && raw[raw.Length - 1] == ']')
            {
                var list = new List<object>();
                string inner = raw.Substring(1, raw.Length - 2).Trim();
                if (inner.Length > 0)
                    foreach (var seg in SplitTopLevel(inner, ','))
                        list.Add(ParseValue(seg.Trim()));
                return list;
            }
            if (raw == "true") return true;
            if (raw == "false") return false;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            return raw;   // 裸串 (尺寸 420x300 / 函数名 / sprite 键等)
        }

        private static string Unescape(string s)
            => s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");

        /// <summary>顶层分隔: 引号/方括号内的分隔符不切。</summary>
        private static List<string> SplitTopLevel(string s, char sep)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            bool inStr = false;
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; sb.Append(c); continue; }
                if (c == '[') depth++;
                if (c == ']') depth--;
                if (c == sep && depth == 0) { parts.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            parts.Add(sb.ToString());
            return parts;
        }

        /// <summary>剥离行注释: # 在引号外为止。</summary>
        private static string StripComment(string line)
        {
            bool inStr = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '#') return line.Substring(0, i);
            }
            return line;
        }

        /// <summary>缩进宽度: 空格计 1, tab 计 4。</summary>
        private static int IndentOf(string raw)
        {
            int n = 0;
            foreach (char c in raw)
            {
                if (c == ' ') n++;
                else if (c == '\t') n += 4;
                else break;
            }
            return n;
        }
    }
}
