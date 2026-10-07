using System;
using Il2Cpp;
using UnityEngine;

namespace PSApi.Items
{
    /// <summary>
    /// v0.9.10: 鉴定武器数值 tooltip 块 (gunworks 品质鉴定机)。
    /// 数据源 = 物品 NBT (ItemsFacade.GetData, 键由 gunworks events/appraiser.pss 写入):
    ///   appraised=1 / wtype(gun|melee|throw) / wq(品质id) /
    ///   w_&lt;k&gt; 最终值 / wb_&lt;k&gt; 基础值 / wp_&lt;k&gt; 加成百分比整数 (k ∈ atk/rate/stab/pen/rng/rad)。
    /// 长按 Ctrl 才显示 (原版 ItemTooltipHandler key-listener 会在按键时重建 tooltip,
    /// ModHook.OnCreateTooltipLate 随之重跑, 本块按 Ctrl 实时状态出现/消失);
    /// 未参与加成的属性 (零基/单体半径, 无 wb_/wp_ 键) 只显示最终值。
    /// </summary>
    internal static class AppraisalTooltip
    {
        private static readonly (string Key, string Cn)[] Stats =
        {
            ("atk", "攻击力"), ("rate", "射速"), ("stab", "稳定性"),
            ("pen", "穿透力"), ("rng", "攻击距离"), ("rad", "伤害半径"),
        };

        internal static void AddLines(RichTextBuilder builder, GameItem item)
        {
            if (builder == null || item == null) return;
            if (ItemsFacade.GetData(item, "appraised") is not long ap || ap != 1) return;
            if (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)) return;

            builder.AddLine("—— 鉴定数值 ——");

            string qcn = ItemsFacade.GetData(item, "wq") as string ?? "?";
            if (QualityService.TryGetById(qcn, out var qd) && !string.IsNullOrWhiteSpace(qd.Display))
                qcn = qd.Display;
            string typeCn = (ItemsFacade.GetData(item, "wtype") as string) switch
            {
                "gun" => "枪械", "melee" => "近战", "throw" => "投掷", _ => "?",
            };
            builder.AddLine($"品质: {qcn} ({typeCn})");

            foreach (var (key, cn) in Stats)
            {
                object fv = ItemsFacade.GetData(item, "w_" + key);
                if (fv == null) continue;
                object bv = ItemsFacade.GetData(item, "wb_" + key);
                object pv = ItemsFacade.GetData(item, "wp_" + key);
                if (bv == null || pv == null)
                {
                    builder.AddLine($"{cn}: {Fmt(fv)}");
                    continue;
                }
                long pct = pv is long pl ? pl : (long)Convert.ToDouble(pv);
                builder.AddLine($"{cn}: {Fmt(bv)} ({(pct >= 0 ? "+" : "")}{pct}%) → {Fmt(fv)}");
            }
        }

        /// <summary>long/double → 紧凑文本 (整数不带小数点, 否则 1 位小数)。</summary>
        private static string Fmt(object v) => v switch
        {
            long l => l.ToString(),
            double d => d == Math.Floor(d) ? ((long)d).ToString() : d.ToString("0.0"),
            _ => v?.ToString() ?? "?",
        };
    }
}
