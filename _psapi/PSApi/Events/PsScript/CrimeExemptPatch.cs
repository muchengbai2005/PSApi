using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// v1.7.0 治安记录豁免 + 禁售注册表 (仿制证书落点)。
    ///
    /// CheckSoldContraband 是治安档案唯一写入口 (柜台议价 PerformOnSoldCheck 与展示柜自动售货
    /// OnItemsSold 都走它): Prefix 在"今日枪械豁免"开启且物品是 WEAPON 类型时跳过整个记录
    /// (WEAPON_TRAFFICKING 与 FENCING 一并豁免, 其他物品照常)。豁免开关由脚本 crime.exempt_guns
    /// 控制 (惯例: shop_opened 扫到展示柜有证书 → true, day_wake → false, 只影响当天新记录,
    /// 历史档案不动)。
    ///
    /// v1.15.0: crime.set_filter(fn) 纯脚本谓词 — Prefix 已实证能拿到 GameItem (签名
    /// CheckSoldContraband(GameItem gameItem)), fn(物品句柄) 返回 truthy 同样跳过记录。
    /// 与 exempt_guns 并列 (任一命中即免)。脚本异常 = 不免除 + warn (不炸游戏); 防重入闸门。
    ///
    /// CanSellThisItem Prefix: 注册表内的 id 任何客户都不买 (展示柜自动售货 + 柜台都拦),
    /// 防证书放展示柜被路人买走。脚本 shop.block_sale/unblock_sale 管理。
    /// </summary>
    internal static class CrimeExemptState
    {
        /// <summary>今日卖枪是否免记录 (脚本 crime.exempt_guns 设置; 仅内存, 新的一天由脚本复位)。</summary>
        internal static bool GunsExemptToday;

        /// <summary>禁售 id 注册表 (裸 identifier 含包前缀; shop.block_sale 注册)。</summary>
        internal static readonly HashSet<string> BlockedSaleIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>v1.15.0: 犯罪豁免谓词 (crime.set_filter 注册; 可调对象不持久化, 包脚本顶层重新注册)。</summary>
        internal static PsCallable Filter;
        internal static Interpreter FilterItp;
        /// <summary>防重入: filter 执行中再触发 CheckSoldContraband (脚本里卖出?) 直接不免。</summary>
        internal static bool FilterRunning;
    }

    [HarmonyPatch(typeof(SecData), "CheckSoldContraband")]
    internal static class CrimeExemptPatch
    {
        private static bool Prefix(GameItem gameItem)
        {
            try
            {
                if (gameItem == null) return true;
                if (CrimeExemptState.GunsExemptToday)
                {
                    bool isGun = false;
                    try { isGun = gameItem.IsGameItemType("WEAPON"); } catch { }
                    if (isGun) return false; // 枪 → 跳过记录; 其他照常
                }
                // v1.15.0: 脚本谓词 (crime.set_filter); 异常 = 不免除 + warn
                var fn = CrimeExemptState.Filter;
                var itp = CrimeExemptState.FilterItp;
                if (fn != null && itp != null && !CrimeExemptState.FilterRunning)
                {
                    try
                    {
                        CrimeExemptState.FilterRunning = true;
                        itp.BeginRun();
                        var ret = itp.CallCallable(fn, new List<object> { new PsItemHandle(gameItem) }, 0);
                        if (PsValues.Truthy(ret)) return false;
                    }
                    catch (Exception e)
                    {
                        MelonLoader.MelonLogger.Warning($"[psapi] crime.set_filter 谓词出错(本笔不免记录): {e.Message}");
                    }
                    finally { CrimeExemptState.FilterRunning = false; }
                }
            }
            catch { }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerStore), "CanSellThisItem")]
    internal static class SaleBlockPatch
    {
        private static bool Prefix(GameItem gameItem, ref bool __result)
        {
            try
            {
                if (gameItem == null || CrimeExemptState.BlockedSaleIds.Count == 0) return true;
                string id = null;
                try { id = gameItem.identifier; } catch { }
                if (id != null && CrimeExemptState.BlockedSaleIds.Contains(id))
                {
                    __result = false;
                    return false;
                }
            }
            catch { }
            return true;
        }
    }
}
