using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using PSApi.Events.PsScript;
using PSApi.Items;

namespace PSApi.Events.Scenes
{
    /// <summary>
    /// v1.43.0 组装枪耐久实战消耗 (gunworks 阶段三):
    ///   rt 战斗实际开火 (每发, 连发每发; 卡壳未射出不扣) + 近战抡/枪托抡 (每次挥击) 扣 ps_wear;
    ///   ps_dur ≤ 0 钳 0 + 写 ps_broken=1 → 伤害 ×0.3 (RtWeapon 解析处单点, 见 ScriptCombatService /
    ///   RaidCore.BrokenAtk); ps_tooltip 耐久行同步重写 (x 取整显示), 破损追加「已破损」行。
    ///   投掷物不参与; 修理在包侧 (items.on_target 拖修理物, gunworks bench/03_repair.pss)。
    ///   v1.48.7 (拍板): 低耐久伤害曲线 (RaidCore.DurabilityAtk, RtWeapon 解析单点) + 破损开火
    ///   25% 卡壳 (RtCombatService.TryFire, 与 stab 卡壳取大) + 耐久行 <30% 黄 <15% 红变色 (DurTipEntry)。
    ///   **只有带 ps_dur_max 的武器参与** (组装枪; 原版枪/战利品枪零开销直返); NBT 读写异常只 Warn 不抛。
    ///   耐久 NBT 由 gunworks bench_apply_mat 写入: ps_dur (当前, 浮点两位小数) / ps_dur_max (上限 long) /
    ///   ps_wear (单次损耗浮点, 下限 0.1) / ps_broken=1 (破损标记, 修理删键)。
    /// </summary>
    internal static class WeaponDurability
    {
        internal const string KeyDur = "ps_dur";
        internal const string KeyDurMax = "ps_dur_max";
        internal const string KeyWear = "ps_wear";
        internal const string KeyBroken = "ps_broken";
        internal const string KeyTip = "ps_tooltip";
        internal const string TipDurPrefix = "耐久: ";
        internal const string TipBroken = "已破损";

        /// ps_wear 缺键缺省 (与 gunworks bench_wear(0)=1.0 一致)。
        internal const double WearDefault = 1.0;

        /// v1.48.7 (拍板): 耐久行低耐久变色 — <30% 黄 / <15% 红 (与包侧正负上色同色系);
        ///   ≥30% 维持纯字符串行 (旧行为)。变色行 = dict {text,color}, 重写两侧 (本类与
        ///   gunworks refinery/03_gun_repair.pss gun_repair_tip) 都按 text 前缀识别「耐久: 」。
        internal const string TipDurYellow = "#E0C04A";
        internal const string TipDurRed = "#E07A7A";
        internal const double TipRatioYellow = 0.30;
        internal const double TipRatioRed = 0.15;

        /// v1.48.7: 耐久行条目 — 比例 <15% dict 红 / <30% dict 黄 / 否则纯字符串 (durMax≤0 防御=字符串)。
        internal static object DurTipEntry(long durShow, long durMax)
        {
            string durLine = TipDurPrefix + durShow + "/" + durMax;
            if (durMax > 0)
            {
                double ratio = durShow / (double)durMax;
                if (ratio < TipRatioRed)
                    return new Dictionary<string, object> { ["text"] = durLine, ["color"] = TipDurRed };
                if (ratio < TipRatioYellow)
                    return new Dictionary<string, object> { ["text"] = durLine, ["color"] = TipDurYellow };
            }
            return durLine;
        }

        /// 行文本提取 (纯字符串行 / dict {text,color} 行 → text; 其他 → null)。
        private static string LineText(object line)
        {
            if (line is string s) return s;
            if (line is Dictionary<string, object> dd && dd.TryGetValue("text", out var tv)) return tv as string;
            return null;
        }

        /// v1.48.7: 破损判定外露 (rt 破损卡壳用; ps_broken=1, 缺键/异常=false)。
        internal static bool IsBroken(GameItem item)
        {
            try { return ItemsFacade.GetData(item, KeyBroken) is long l && l == 1; }
            catch { return false; }
        }

        /// 纯函数 (无头可测): 单次扣减 — 两位小数 (AwayFromZero, 与 pss floor(x*100+0.5)/100 同规), 下限钳 0。
        internal static double WearStep(double dur, double wear)
            => Math.Max(0.0, Math.Round(dur - wear, 2, MidpointRounding.AwayFromZero));

        /// 纯函数 (无头可测): ps_tooltip 重写 — 「耐久: 」前缀行替换 (无则插行首, 重复行只留一行;
        ///   v1.48.7: 纯字符串行与 dict {text,color} 行都按 text 前缀识别, 低耐久条目变色见 DurTipEntry);
        /// broken 追加「已破损」(先全清再追加 = 去重), 非 broken 移除; 其他行 (含 dict 上色行) 原样保留。
        internal static List<object> RewriteTip(List<object> tip, long durShow, long durMax, bool broken)
        {
            var r = new List<object>();
            object durEntry = DurTipEntry(durShow, durMax);
            bool durWritten = false;
            if (tip != null)
                foreach (var line in tip)
                {
                    var txt = LineText(line);
                    if (txt != null)
                    {
                        if (txt.StartsWith(TipDurPrefix, StringComparison.Ordinal))
                        {
                            if (!durWritten) { r.Add(durEntry); durWritten = true; }
                            continue;
                        }
                        if (txt == TipBroken) continue;
                    }
                    r.Add(line);
                }
            if (!durWritten) r.Insert(0, durEntry);
            if (broken) r.Add(TipBroken);
            return r;
        }

        /// 扣耐久主入口 (rt 开火/挥击一次调)。返回 0=非耐久体系武器(零开销) / 1=已扣 / 2=本次扣到破损。
        internal static int OnUse(GameItem item, MelonLogger.Instance logger)
        {
            try
            {
                if (item == null) return 0;
                double durMax = ToNum(ItemsFacade.GetData(item, KeyDurMax), -1);
                if (durMax <= 0) return 0;                          // 无 ps_dur_max = 不参与, 零开销
                double dur = ToNum(ItemsFacade.GetData(item, KeyDur), durMax);
                double wear = ToNum(ItemsFacade.GetData(item, KeyWear), WearDefault);
                bool wasBroken = ItemsFacade.GetData(item, KeyBroken) is long b && b == 1;
                double nd = WearStep(dur, wear);
                bool broken = nd <= 0.0;
                ItemsFacade.SetData(item, KeyDur, nd);
                if (broken && !wasBroken) ItemsFacade.SetData(item, KeyBroken, 1L);
                var tip = ScriptJson.FromNode(ItemsFacade.GetData(item, KeyTip) as System.Text.Json.Nodes.JsonNode) as List<object>;
                ItemsFacade.SetData(item, KeyTip,
                    RewriteTip(tip, (long)Math.Round(nd, MidpointRounding.AwayFromZero), (long)Math.Round(durMax), broken));
                if (broken && !wasBroken)
                    PsApi.Log(logger, "[durability] 武器耐久耗尽已破损 (伤害 ×0.3, 拖同材料修理物可修)");
                return broken && !wasBroken ? 2 : 1;
            }
            catch (Exception e)
            {
                PsApi.Warn(logger, "[durability] 扣耐久异常 (已忽略, 战斗不受影响): " + e.Message);
                return 1;
            }
        }

        private static double ToNum(object v, double def)
        {
            if (v is long l) return l;
            if (v is double d) return d;
            return def;
        }
    }
}
