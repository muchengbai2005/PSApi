using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace PSApi.Items
{
    /// <summary>
    /// 品质注册表(items/08 v2, 2026-09-08 对齐原版机制)。
    /// 两种模式:
    /// - tag(默认): TagSystem 标签 + 自建 priceMul 定价(GetFinalOfferValue Postfix) + tooltip 品质行。
    ///   适合无原版对标物的新品质体系。
    /// - feature: 原版 ItemFeature 管线(category/公开与实际显示/valueModifier 百分比), 自动获得
    ///   原版标签打印机改标(LabelerHelper)/安检识别(ExposeFeature)/预估与基础价值显示。
    ///   定价由原版特性管线负责, 本服务不再乘系数(防双重修正)。
    /// 显示名注入(StringToDisplayString Postfix)仅服务 tag 模式。
    /// </summary>
    internal static class QualityService
    {
        internal sealed class QualityDef
        {
            internal string Id;
            internal string Tag;
            internal string Display;
            internal string NamePrefix;
            internal float PriceMul = 1f;
            internal int Tier;
            internal string Owner;
            internal bool IsFeature;      // mode=feature
            internal string Category;     // feature 类别(如 "CATEGORY_CHEMICAL_PURITY" 或自建)
            internal string CategoryDisplay; // 自建类别中文标题(可选)
        }

        private static readonly Dictionary<string, QualityDef> _byId = new Dictionary<string, QualityDef>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, QualityDef> _byTag = new Dictionary<string, QualityDef>(StringComparer.Ordinal);
        // 物品 id → 该物品声明的品质组(互斥域)
        private static readonly Dictionary<string, List<string>> _itemGroups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        /// <summary>自建品质表(原版打印机硬编码表之外的 category) → 中文标题。LabelerPatch 用它接管标题与选项装配。</summary>
        internal static readonly Dictionary<string, string> CustomCategoryTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        private static MelonLogger.Instance _log;

        internal static void Init(MelonLogger.Instance log) { _log = log; }

        internal static void Clear()
        {
            _byId.Clear();
            _byTag.Clear();
            _itemGroups.Clear();
            CustomCategoryTitles.Clear();
        }

        internal static int Count => _byId.Count;

        /// <summary>全部已注册品质 id (打印机 randomEffects 随机抽取池)。</summary>
        internal static List<string> AllQualityIds
        {
            get
            {
                var ids = new List<string>(_byId.Count);
                ids.AddRange(_byId.Keys);
                return ids;
            }
        }

        internal static bool Register(QualityDefJson j, string packId, List<string> errors)
        {
            if (j == null || string.IsNullOrWhiteSpace(j.Id))
            {
                errors?.Add($"[{packId}] quality missing id, skipped");
                return false;
            }
            bool isFeature = string.Equals(j.Mode, "feature", StringComparison.OrdinalIgnoreCase);
            if (!isFeature && string.IsNullOrWhiteSpace(j.Tag))
            {
                errors?.Add($"[{packId}] tag-mode quality '{j.Id}' missing tag, skipped");
                return false;
            }
            if (string.IsNullOrWhiteSpace(j.Display))
            {
                errors?.Add($"[{packId}] quality '{j.Id}' missing display, skipped");
                return false;
            }
            var def = new QualityDef
            {
                Id = j.Id, Tag = j.Tag, Display = j.Display, NamePrefix = j.NamePrefix,
                PriceMul = j.PriceMul <= 0 ? 1f : j.PriceMul, Tier = j.Tier, Owner = packId,
                IsFeature = isFeature, Category = string.IsNullOrWhiteSpace(j.Category) ? j.Display : j.Category,
                CategoryDisplay = j.CategoryDisplay
            };
            if (_byId.TryGetValue(def.Id, out var old))
                PsApi.Warn(_log, $"quality '{def.Id}' overwritten: {old.Owner} -> {packId}");
            _byId[def.Id] = def;
            if (!isFeature)
            {
                if (_byTag.TryGetValue(def.Tag, out var oldT) && oldT.Id != def.Id)
                    PsApi.Warn(_log, $"quality tag '{def.Tag}' rebound: {oldT.Id} -> {def.Id}");
                _byTag[def.Tag] = def;
            }
            else
            {
                TryRegisterConditionOption(def);   // 并入原版标签打印机选项表
                // 自建类别的中文标题: 借 _byTag 注入 StringToDisplayString(仅显示, 不作品质匹配)
                if (!string.IsNullOrWhiteSpace(def.CategoryDisplay))
                    _byTag[def.Category] = new QualityDef { Id = def.Category, Tag = def.Category, Display = def.CategoryDisplay, Owner = packId };
            }
            return true;
        }

        /// <summary>登记物品的互斥品质组(item.qualities 列表; 仅 tag 模式入互斥表)。</summary>
        internal static void RegisterItemGroup(string itemId, List<string> qualityIds)
        {
            if (string.IsNullOrEmpty(itemId) || qualityIds == null || qualityIds.Count == 0) return;
            var tags = new List<string>();
            foreach (var qid in qualityIds)
                if (_byId.TryGetValue(qid, out var q))
                {
                    if (!q.IsFeature && q.Tag != null) tags.Add(q.Tag);
                }
                else PsApi.Warn(_log, $"item '{itemId}' references unknown quality '{qid}'");
            if (tags.Count > 0) _itemGroups[itemId] = tags;
        }

        internal static bool TryGetById(string id, out QualityDef def) => _byId.TryGetValue(id, out def);
        internal static bool TryGetByTag(string tag, out QualityDef def) => _byTag.TryGetValue(tag ?? "", out def);

        /// <summary>给物品打品质层。tag 模式: 清同组+EnableTag; feature 模式: 替换同类别 ItemFeature。</summary>
        internal static void SetQuality(GameItem item, string itemId, string qualityId)
        {
            if (item == null || !_byId.TryGetValue(qualityId, out var q)) return;
            try
            {
                if (q.IsFeature) { ApplyFeature(item, q); return; }
                if (_itemGroups.TryGetValue(itemId ?? "", out var group))
                    foreach (var t in group)
                        if (t != q.Tag) item.DisableTag(t, true);
                item.EnableTag(q.Tag, true);
            }
            catch (Exception e) { PsApi.Warn(_log, $"SetQuality({itemId},{qualityId}) failed: {e.Message}"); }
        }

        /// <summary>feature 模式: 移除同类别旧特性, 添加新 ItemFeature。
        /// 字段填法已按 P1.1 探针(api_probe_141706)对齐原版:
        /// 价值/显示由 ItemCondition 承载(modValue=百分比), feature 本体 useCondition=true + 双 condition;
        /// valueModifier 字段原版恒为 0(计算值来自 condition), publicDisplay 格式 "-名称 (±N%)"。
        /// 原版管线自动接管: tooltip [标签] 行/预估与基础价值; 标签打印机认 category。</summary>
        private static void ApplyFeature(GameItem item, QualityDef q)
        {
            if (item.itemFeatures == null) return;
            // 必须走原版 API: AddItemFeature 内部会写 parentItemUniqueId(+24),
            // 存档/读档(PlayerStore.savedItemFeatureList 按该字段匹配物品)依赖它,
            // 直接 feats.Add 会导致读档后特性被静默丢弃。
            item.RemoveFeatureByCategory(q.Category);

            var cond = BuildCondition(q);
            var nf = new ItemFeature(q.Category);
            nf.identifier = cond.identifier;
            nf.useCondition = true;
            nf.initiallyShown = true;
            nf.isExposable = false;   // 自建品质默认不参与"假标签被识破"链路
            nf.fakeCondition = cond;
            nf.realCondition = cond;
            string sign = cond.modValue > 0 ? "+" : "";
            nf.SetPublicDisplay($"-{q.Display} ({sign}{cond.modValue}%)");
            nf.SetActualDisplay($"-{q.Display}");
            item.AddItemFeature(nf);
        }

        /// <summary>按品质定义构建原版 ItemCondition(价值/显示载体)。</summary>
        private static ItemCondition BuildCondition(QualityDef q)
        {
            int mod = (int)Math.Round((q.PriceMul - 1f) * 100f);
            var cond = new ItemCondition();
            cond.display = q.Display;
            cond.identifier = string.IsNullOrEmpty(q.Tag) ? q.Id : q.Tag;
            cond.modValue = mod;
            cond.modValueDisplay = mod;
            cond.category = q.Category;
            return cond;
        }

        /// <summary>把 feature 模式品质并入原版选项表 ItemConditionList.conditionLookup:
        /// 标签打印机下拉框由此读取 —— 现有 category 追加层级, 新 category 注册全新品质体系。
        /// 重复注册按 identifier 原地更新(Rescan 安全)。</summary>
        private static void TryRegisterConditionOption(QualityDef q)
        {
            try
            {
                var lookup = ItemConditionList.conditionLookup;
                if (lookup == null) return;
                var cond = BuildCondition(q);
                Il2CppSystem.Collections.Generic.List<ItemCondition> list;
                if (!lookup.TryGetValue(q.Category, out list) || list == null)
                {
                    list = new Il2CppSystem.Collections.Generic.List<ItemCondition>();
                    lookup[q.Category] = list;
                    // 是我们新建的表 = 自建类别(原版类别 conditionLookup 里已存在)
                    CustomCategoryTitles[q.Category] = string.IsNullOrWhiteSpace(q.CategoryDisplay) ? q.Category : q.CategoryDisplay;
                    MelonLogger.Msg($"[psapi] condition category (re)registered: {q.Category}");
                }
                foreach (var c in list)
                {
                    if (c == null || c.identifier != cond.identifier) continue;
                    c.display = cond.display;
                    c.modValue = cond.modValue;
                    c.modValueDisplay = cond.modValueDisplay;
                    return;
                }
                list.Add(cond);
            }
            catch (Exception e) { PsApi.Warn(_log, $"condition option register failed for '{q.Id}': {e.Message}"); }
        }

        /// <summary>确保所有 feature 品质在 conditionLookup 中就位(幂等, 可反复调)。
        /// v0.2.6 实测: IL2CPP 静态类初始化晚于 mod 加载, 游戏 static ctor 会重建 conditionLookup 实例,
        /// 冲掉加载期注册的自建类别 → LabelerElement.InitElement KeyNotFoundException → 面板空白。
        /// 由 LabelerUIManager/LabelerBlueUIManager.OpenUI Prefix 在每次开面板前调用。</summary>
        internal static void EnsureConditionOptions()
        {
            foreach (var q in _byId.Values)
                if (q.IsFeature) TryRegisterConditionOption(q);
        }

        /// <summary>找物品当前品质(扫 state + modifiedState); 无则 null。</summary>
        internal static QualityDef GetQuality(GameItem item)
        {
            if (item == null || _byId.Count == 0) return null;
            try
            {
                var q = ScanTags(item.state);
                if (q != null) return q;
                q = ScanTags(item.modifiedState);
                if (q != null) return q;
                q = ScanFeatures(item);
                if (q != null) return q;
            }
            catch { }
            return null;
        }

        private static QualityDef ScanTags(TagSystem ts)
        {
            if (ts == null) return null;
            var dict = ts.dict;
            if (dict == null) return null;
            foreach (var kv in dict)
            {
                if (kv.Key == null) continue;
                if (_byTag.TryGetValue(kv.Key, out var def)) return def;
            }
            return null;
        }

        // Feature-mode qualities do not appear in TagSystem.dict.  They are
        // represented by ItemFeature.category/identifier and are used by the
        // machine qualityRule inheritance path as well as tooltip/labeler code.
        private static QualityDef ScanFeatures(GameItem item)
        {
            var features = item?.itemFeatures;
            if (features == null) return null;
            foreach (var feature in features)
            {
                if (feature == null) continue;
                foreach (var q in _byId.Values)
                {
                    if (!q.IsFeature || !string.Equals(q.Category, feature.category, StringComparison.Ordinal)) continue;
                    if (string.Equals(q.Category, feature.identifier, StringComparison.Ordinal)
                        || string.Equals(q.Id, feature.identifier, StringComparison.Ordinal)
                        || string.Equals(q.Tag, feature.identifier, StringComparison.Ordinal)
                        || (feature.realCondition != null && string.Equals(q.Category, feature.realCondition.category, StringComparison.Ordinal)
                            && string.Equals(q.Tag ?? q.Id, feature.realCondition.identifier, StringComparison.Ordinal)))
                        return q;
                }
            }
            return null;
        }

        /// <summary>tooltip 品质行文本(由 ModHook.OnCreateTooltipLate 调用)。
        /// feature 模式返回 null: 原版特性管线已自带显示, 不再加品质行(防重复/误导)。</summary>
        internal static string TooltipLine(GameItem item)
        {
            var q = GetQuality(item);
            if (q == null || q.IsFeature) return null;
            return q.PriceMul != 1f
                ? $"品质: {q.Display} ×{q.PriceMul:0.##}"
                : $"品质: {q.Display}";
        }

        /// <summary>定价管线系数(无品质=1)。
        /// v1.13.4: feature 模式恒 1 —— 同一 PriceMul 已被 BuildCondition 折成 ItemCondition.modValue
        /// 由原生 AccumulateFeatureStages 消费, 这里再乘 = 双重定价(priceMul=4 实得 ≈16 倍)。</summary>
        internal static float PriceFactor(GameItem item)
        {
            var q = GetQuality(item);
            if (q == null || q.IsFeature) return 1f;
            return q.PriceMul;
        }
    }

    /// <summary>品质显示名注入: tag → 中文显示名。
    /// v0.9.6: PSD_ 数据标签剥 TYPE-STRING_ 前缀噪音 (tooltip 实证不遍历 state.dict, 此防御其他 UI 路径)。</summary>
    [HarmonyPatch(typeof(TagSystem), "StringToDisplayString")]
    internal static class QualityDisplayPatch
    {
        private static void Postfix(string stringValue, ref string __result)
        {
            try
            {
                if (stringValue == null) return;
                if (stringValue.StartsWith(ItemsFacade.DataTagPrefix, StringComparison.Ordinal))
                {
                    __result = stringValue;
                    return;
                }
                if (QualityService.TryGetByTag(stringValue, out var def))
                    __result = def.Display;
            }
            catch { }
        }
    }

    /// <summary>品质定价管线: 最终报价 × 品质系数。</summary>
    [HarmonyPatch(typeof(NegociationUIManager), "GetFinalOfferValue")]
    internal static class QualityPricePatch
    {
        private static void Postfix(GameItem gameItem, ref int __result)
        {
            try
            {
                float f = QualityService.PriceFactor(gameItem);
                if (f != 1f && __result > 0)
                    __result = Math.Max(1, (int)Math.Round(__result * f));
            }
            catch { }
        }
    }
}
