using HarmonyLib;
using Il2Cpp;

namespace PSApi.Items
{
    /// <summary>
    /// v0.9.5 存档同步(与 PSApi.Events 同款): 模组状态(SaveStates)持久化严格绑定游戏真实存档点。
    /// Items 当前少用 SaveStates, 但 Shared/SaveStates.cs 语义已改(换槽/退出不再落盘),
    /// 补同款钩子保证本模块未来/现有状态与游戏世界存档严格一致。
    /// </summary>
    [HarmonyPatch(typeof(PlayerStore), "SaveGame")]
    internal static class Patch_SaveGameFlushStates
    {
        private static void Postfix() => SaveStates.FlushAll(null);
    }

    [HarmonyPatch(typeof(PlayerStore), "LoadGame")]
    internal static class Patch_LoadGameInvalidateStates
    {
        private static void Postfix() => SaveStates.InvalidateAll();
    }
}
