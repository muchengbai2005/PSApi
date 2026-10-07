using HarmonyLib;
using Il2Cpp;

namespace PSApi.Events.Scenes
{
    /// <summary>
    /// 地图注入: 原版地图面板 (MapUIManager.OpenUI) 打开时, 把 entry="map" 的自定义场景
    /// 注入为地图按钮 (克隆 dumpingGroundButton 改造, 排在模板按钮下方)。
    /// 玩家点门 → 原版选容器 → 原版地图 → 我们的按钮与博士商店/补给站/垃圾场并列。
    /// </summary>
    [HarmonyPatch(typeof(MapUIManager), nameof(MapUIManager.OpenUI))]
    internal static class SceneMapPatch
    {
        private static void Postfix(MapUIManager __instance)
        {
            try { EventsPlugin.Instance?.Scenes?.InjectMapButtons(__instance); }
            catch (System.Exception e) { EventsPlugin.LogWarn("[scenes] 地图注入异常: " + e.Message); }
        }
    }
}
