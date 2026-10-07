using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;

namespace PSApi.Items
{
    /// <summary>
    /// 自建品质表的标签打印机支持(v0.2.4)。
    /// 原版 LabelerElement 硬编码已知 category —— 未知类别回退成 openBox 标题 + 空选项(用户实测)。
    /// 对 QualityService.CustomCategoryTitles 登记的自建类别补两板斧:
    /// ① GetCategory Postfix → 返回中文标题; ② CreateOptionList Postfix → 从 conditionLookup 装配选项。
    /// 原版类别(化学纯度/水质等)不受影响(不在 CustomCategoryTitles 里)。
    /// </summary>
    [HarmonyPatch(typeof(LabelerElement), "GetCategory")]
    internal static class LabelerCategoryPatch
    {
        private static void Postfix(LabelerElement __instance, ref string __result)
        {
            try
            {
                var f = __instance.thisFeature;
                string cat = f == null ? null : f.category;
                if (cat != null && QualityService.CustomCategoryTitles.TryGetValue(cat, out var title))
                {
                    __result = title;
                    MelonLogger.Msg($"[psapi] Labeler.GetCategory override: '{cat}' -> '{title}'");
                }
            }
            catch { }
        }
    }

    /// <summary>自建类别的选项装配: 选项文本按原版格式 "名称 (±N% Value)"。</summary>
    [HarmonyPatch(typeof(LabelerElement), "CreateOptionList")]
    internal static class LabelerOptionsPatch
    {
        private static void Postfix(string featureCategory, ref Il2CppSystem.Collections.Generic.List<TMP_Dropdown.OptionData> __result)
        {
            try
            {
                if (featureCategory == null || !QualityService.CustomCategoryTitles.ContainsKey(featureCategory)) return;
                var lookup = ItemConditionList.conditionLookup;
                if (lookup == null) return;
                Il2CppSystem.Collections.Generic.List<ItemCondition> conds;
                if (!lookup.TryGetValue(featureCategory, out conds) || conds == null || conds.Count == 0) return;
                var list = new Il2CppSystem.Collections.Generic.List<TMP_Dropdown.OptionData>();
                foreach (var c in conds)
                {
                    if (c == null) continue;
                    int m = c.modValueDisplay != 0 ? c.modValueDisplay : c.modValue;
                    string sign = m > 0 ? "+" : "";
                    list.Add(new TMP_Dropdown.OptionData($"{c.display} ({sign}{m}% Value)"));
                }
                if (list.Count > 0)
                {
                    __result = list;
                    MelonLogger.Msg($"[psapi] Labeler.CreateOptionList override: '{featureCategory}' -> {list.Count} option(s)");
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[psapi] LabelerOptionsPatch: {e.Message}"); }
        }
    }

    /// <summary>自建类别的可改标判定(v0.2.6)。
    /// 原版 CanEditLabel 硬编码已知类别(化学品/水可改, 装备成况不可); 未知类别返回 false
    /// → LabelerUIManager.OpenUI 不为该特性创建 LabelerElement → 面板空白(用户实测 v0.2.5)。
    /// 对 CustomCategoryTitles 登记的自建类别强制可编辑。
    /// 注: GetEditableLabelCount 内部调 CanEditLabel, 经 Harmony patch 后自动计入自建类别。</summary>
    [HarmonyPatch(typeof(LabelerHelper), "CanEditLabel")]
    internal static class LabelerCanEditPatch
    {
        private static void Postfix(ItemFeature itemFeature, ref bool __result)
        {
            try
            {
                if (__result || itemFeature == null) return;
                string cat = itemFeature.category;
                if (cat != null && QualityService.CustomCategoryTitles.ContainsKey(cat))
                {
                    __result = true;
                    MelonLogger.Msg($"[psapi] LabelerHelper.CanEditLabel override: '{cat}' -> true");
                }
            }
            catch { }
        }
    }

    /// <summary>兜底: 若原版 GetEditableLabelCount 有独立计数逻辑(不调 CanEditLabel)而漏算自建类别, 抬底到重计数值。
    /// 仅在物品带自建类别特性时介入, 原版物品完全不受影响。</summary>
    [HarmonyPatch(typeof(LabelerHelper), "GetEditableLabelCount")]
    internal static class LabelerCountPatch
    {
        private static void Postfix(GameItem item, ref int __result)
        {
            try
            {
                var feats = item == null ? null : item.itemFeatures;
                if (feats == null) return;
                bool hasCustom = false;
                foreach (var f in feats)
                    if (f != null && f.category != null && QualityService.CustomCategoryTitles.ContainsKey(f.category))
                    { hasCustom = true; break; }
                if (!hasCustom) return;
                int recount = 0;
                foreach (var f in feats)
                    if (f != null && LabelerHelper.CanEditLabel(f)) recount++;
                if (recount != __result)
                {
                    MelonLogger.Msg($"[psapi] GetEditableLabelCount override: {__result} -> {recount}");
                    __result = recount;
                }
            }
            catch { }
        }
    }

    /// <summary>自建类别的 InitElement 完全接管(v0.2.8)。
    /// v0.2.7 实测: conditionLookup 已在 OpenUI Prefix 重注册, InitElement 仍抛 KeyNotFoundException
    /// → 原生 InitElement 内部还访问了另一张以 category 为键的字典(疑 ItemFeatureList.CategoryNames)。
    /// IL2CPP 内联导致堆栈看不到真实调用点, 不再逐点猜: 自建类别直接手动初始化整个元素并跳过原生。
    /// 手动装配: thisFeature / featureName(中文标题) / currentFeatureDisplay(当前标签) / dropdown 选项与选中项。</summary>
    [HarmonyPatch(typeof(LabelerElement), "InitElement")]
    internal static class LabelerInitPatch
    {
        private static bool Prefix(LabelerElement __instance, ItemFeature itemFeature)
        {
            try
            {
                string cat = itemFeature == null ? null : itemFeature.category;
                if (cat == null || !QualityService.CustomCategoryTitles.TryGetValue(cat, out var title)) return true; // 原生处理
                QualityService.EnsureConditionOptions();
                __instance.thisFeature = itemFeature;
                if (__instance.featureName != null) __instance.featureName.text = title;
                var cond = itemFeature.fakeCondition != null ? itemFeature.fakeCondition : itemFeature.realCondition;
                if (__instance.currentFeatureDisplay != null)
                    __instance.currentFeatureDisplay.text = cond != null ? cond.display : (itemFeature.publicDisplay ?? "");
                Il2CppSystem.Collections.Generic.List<ItemCondition> conds = null;
                var lookup = ItemConditionList.conditionLookup;
                if (lookup != null) lookup.TryGetValue(cat, out conds);
                var dd = __instance.dropdown;
                if (dd != null && conds != null && conds.Count > 0)
                {
                    var options = new Il2CppSystem.Collections.Generic.List<TMP_Dropdown.OptionData>();
                    int sel = 0;
                    for (int i = 0; i < conds.Count; i++)
                    {
                        var c = conds[i];
                        if (c == null) continue;
                        int m = c.modValueDisplay != 0 ? c.modValueDisplay : c.modValue;
                        string sign = m > 0 ? "+" : "";
                        options.Add(new TMP_Dropdown.OptionData($"{c.display} ({sign}{m}% Value)"));
                        if (cond != null && c.identifier == cond.identifier) sel = options.Count - 1;
                    }
                    if (options.Count > 0)
                    {
                        dd.ClearOptions();
                        dd.AddOptions(options);
                        dd.SetValueWithoutNotify(sel);
                        if (dd.captionText != null) dd.captionText.text = options[sel].text;
                    }
                }
                MelonLogger.Msg($"[psapi] Labeler.InitElement manual: '{cat}' options={(conds == null ? 0 : conds.Count)}");
                return false; // 跳过原生(避免未知类别字典 KeyNotFound)
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[psapi] LabelerInitPatch: {e.Message}");
                return true; // 出错回退原生
            }
        }
    }

    /// <summary>自建类别的选中项 identifier 读取接管: 原生实现可能同样索引未知类别字典, 直接从 conditionLookup 按 dropdown.value 取。</summary>
    [HarmonyPatch(typeof(LabelerElement), "GetSelectedConditionIdentifier")]
    internal static class LabelerSelectedIdPatch
    {
        private static bool Prefix(LabelerElement __instance, ref string __result)
        {
            try
            {
                var f = __instance.thisFeature;
                string cat = f == null ? null : f.category;
                if (cat == null || !QualityService.CustomCategoryTitles.ContainsKey(cat)) return true;
                var lookup = ItemConditionList.conditionLookup;
                var dd = __instance.dropdown;
                Il2CppSystem.Collections.Generic.List<ItemCondition> conds;
                if (lookup != null && dd != null && lookup.TryGetValue(cat, out conds) && conds != null)
                {
                    int v = dd.value;
                    if (v >= 0 && v < conds.Count && conds[v] != null)
                    {
                        __result = conds[v].identifier;
                        return false;
                    }
                }
            }
            catch { }
            return true;
        }
    }

    /// <summary>诊断: 打开改标面板时dump物品的特性列表与可编辑判定, 供日志排障。</summary>
    [HarmonyPatch(typeof(LabelerUIManager), "OpenUI")]
    internal static class LabelerOpenDiagPatch
    {
        /// <summary>v0.2.7: 游戏 static ctor 会重建 conditionLookup 冲掉加载期注册的自建类别
        /// (InitElement KeyNotFoundException → 面板空白), 开面板前幂等重注册。</summary>
        private static void Prefix()
        {
            try { QualityService.EnsureConditionOptions(); }
            catch (Exception e) { MelonLogger.Warning($"[psapi] EnsureConditionOptions: {e.Message}"); }
        }

        private static void Postfix(GameItem gameItem)
        {
            try
            {
                var feats = gameItem == null ? null : gameItem.itemFeatures;
                var sb = new System.Text.StringBuilder();
                sb.Append($"[psapi] LabelerUI.OpenUI item={(gameItem == null ? "null" : gameItem.name)} features={(feats == null ? 0 : feats.Count)}");
                if (feats != null)
                    foreach (var f in feats)
                        if (f != null)
                            sb.Append($" [{f.category}/{f.identifier} editable={LabelerHelper.CanEditLabel(f)}]");
                MelonLogger.Msg(sb.ToString());
            }
            catch (Exception e) { MelonLogger.Warning($"[psapi] LabelerOpenDiag: {e.Message}"); }
        }
    }

    /// <summary>蓝色打印机(另一变体)同样需要在开面板前确保自建类别已注册。</summary>
    [HarmonyPatch(typeof(LabelerBlueUIManager), "OpenUI")]
    internal static class LabelerBlueOpenPatch
    {
        private static void Prefix()
        {
            try { QualityService.EnsureConditionOptions(); }
            catch (Exception e) { MelonLogger.Warning($"[psapi] EnsureConditionOptions(blue): {e.Message}"); }
        }
    }
}
