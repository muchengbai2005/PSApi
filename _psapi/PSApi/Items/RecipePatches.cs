using System;
using HarmonyLib;
using Il2Cpp;

namespace PSApi.Items
{
    /// <summary>
    /// The stock processing-machine UI calls CraftingHelper.InitCraftingTable
    /// instead of reading GameItem.recipeManager directly.  Return the isolated
    /// manager compiled by RecipeService when present; leave ordinary crafting
    /// tables on the game's original path.
    /// </summary>
    [HarmonyPatch(typeof(CraftingHelper), nameof(CraftingHelper.InitCraftingTable))]
    internal static class CraftingTableRecipePatch
    {
        private static bool Prefix(GameItem item, ref CraftingRecipeManager __result)
        {
            try
            {
                if (item == null || item.recipeManager == null) return true;
                var manager = item.recipeManager.TryCast<CraftingRecipeManager>();
                if (manager == null || manager.availableRecipes == null || manager.availableRecipes.Count == 0)
                    return true;
                __result = manager;
                return false;
            }
            catch (Exception e)
            {
                // A malformed/custom object must never break the native crafting UI.
                try { MelonLoader.MelonLogger.Warning("[psapi] crafting table bridge skipped: " + e.Message); } catch { }
                return true;
            }
        }
    }
}
