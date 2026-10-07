using HarmonyLib;
using Il2Cpp;

namespace PSApi.Events
{
    /// <summary>
    /// v1.14.2 存档同步: 模组状态(SaveStates)持久化严格绑定游戏真实存档点。
    /// 病根: 旧版 SaveStates 在日落评估/退菜单换槽/退出游戏时独立落盘,
    /// 而游戏只在晚上睡觉(EndOfDay → SaveGame)和新开局(InitialSave → SaveGame)时存档——
    /// 白天退主菜单游戏回档到开店前, 模组状态却已落盘 → gifted 类标志永久错位(剧情/物品丢失)。
    /// 修复: SaveGame Postfix 落盘(游戏写盘成功后才写模组态); LoadGame Postfix 丢弃内存态
    /// (下次访问从磁盘重载, 磁盘=上次存档时点, 与游戏世界严格一致)。
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
