using System;
using System.Text.Json.Nodes;
using Il2Cpp;

namespace PSApi.Items
{
    /// <summary>
    /// v0.9.16: 通用 NBT tooltip 注入器 — 任何物品 NBT 带 ps_tooltip 键 (list) 就逐行
    /// 追加到 tooltip (行序 = 包侧写入序)。
    /// v0.9.17: 行元素除纯字符串外支持 dict {text=..., color="#RRGGBB"/"#RRGGBBAA"} —
    ///   color 转 RGB int 直传 RenderHandler.ColorPalette (反汇编实证 GetColorFromPalette
    ///   无枚举 switch, 任意 int 右移 16/8 取 RGB 字节, alpha 恒 0xFF 故 8 位色忽略后两
    ///   位); color 缺失/非法回退纯色行 (不丢文本)。
    /// 读取走 ItemsFacade.GetData ('j:' JSON → JsonNode), 与 AppraisalTooltip 同路径。
    /// 用途: 机器/容器被拿起 (物品形态) 时展示面板运行期聚合的状态缓存 (gunworks 熔炉
    /// 模组效果行 / 流体储罐内容物行) — NBT 随物品存档, 不依赖面板存活。包侧删除缓存
    /// = set_data null 删键, GetData 返 null 自然不显示。异常隔离: 外层 onTip 统一
    /// try/catch, 本类逐行防御。
    /// </summary>
    internal static class PsTooltip
    {
        internal const string Key = "ps_tooltip";

        internal static void AddLines(RichTextBuilder builder, GameItem item)
        {
            if (builder == null || item == null) return;
            if (ItemsFacade.GetData(item, Key) is not JsonArray arr) return;
            foreach (var ln in arr)
            {
                try
                {
                    if (ln is JsonObject obj)
                    {
                        var text = obj["text"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(text)) continue;
                        string hex = null;
                        try { hex = obj["color"]?.GetValue<string>(); } catch { }
                        if (hex != null && TryParseHex(hex, out int rgb))
                            builder.AddLine(text, true, (RenderHandler.ColorPalette)rgb);
                        else
                            builder.AddLine(text);
                    }
                    else
                    {
                        var s = ln?.GetValue<string>();
                        if (!string.IsNullOrEmpty(s)) builder.AddLine(s);
                    }
                }
                catch { }
            }
        }

        /// <summary>"#RRGGBB"/"#RRGGBBAA" → RGB int (8 位色 alpha 忽略, 调色板恒不透明)。非法 → false。</summary>
        internal static bool TryParseHex(string hex, out int rgb)
        {
            rgb = 0;
            if (string.IsNullOrEmpty(hex) || hex[0] != '#') return false;
            int n = hex.Length - 1;
            if (n != 6 && n != 8) return false;
            for (int i = 1; i <= 6; i++)
            {
                int c = hex[i];
                int d = c >= '0' && c <= '9' ? c - '0'
                      : c >= 'a' && c <= 'f' ? c - 'a' + 10
                      : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (d < 0) return false;
                rgb = (rgb << 4) | d;
            }
            return true;
        }
    }
}
