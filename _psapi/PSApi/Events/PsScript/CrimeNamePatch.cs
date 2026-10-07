using HarmonyLib;
using Il2Cpp;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// 自定义犯罪显示名补丁: crime.commit(id, amount, display) 注册的显示名覆盖原版 GetCrimeString 输出。
    /// 原版只认自己的 crime_*(bribery/contraband/...), 未知 id 原样返回(方案同 NpcManager RevDeal)。
    /// </summary>
    [HarmonyPatch(typeof(SecData), "GetCrimeString")]
    internal static class CrimeNamePatch
    {
        private static void Postfix(string crimeID, ref string __result)
        {
            if (crimeID != null && PsBuiltinsGame.CrimeNames.TryGetValue(crimeID, out var name))
                __result = name;
        }
    }
}
