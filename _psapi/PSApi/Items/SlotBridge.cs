using System;
using System.Collections.Generic;
using Il2Cpp;

namespace PSApi.Items
{
    /// <summary>
    /// Machine slot access bridge for the Demo (2026-09-15) game update.
    ///
    /// The update REMOVED MachineHelper.GetSlot(GameItem, int).  Its old-build
    /// implementation (ISIL dump) was:
    ///
    ///     GetSlot(machine, index)
    ///         => (machine.child as GridPixelElement).GetElement(index, 1)
    ///            as GameInventory
    ///
    /// Demo runtime reality (api_probe_084010 §9i.0, fresh + save-loaded machines):
    /// machine.child is now a GraphNodeWindow (same object as contentWindow), NOT
    /// the grid.  The GridPixelElement (5x2: row 0 headers, row 1 slots) lives one
    /// level below, under window.child.  GetElement(index, 1) semantics are
    /// unchanged: 0=battery 1=module 2..n=content slots (verified by pointer match
    /// against native GetBatterySlot/GetModuleInv).
    /// </summary>
    internal static class SlotBridge
    {
        internal static GameInventory GetSlot(GameItem machine, int index)
        {
            if (machine == null) return null;
            try
            {
                var window = machine.child;
                if (window == null) return null;
                // 旧版: window 即网格; Demo: window 是 GraphNodeWindow, 网格在其子树。
                var grid = window.TryCast<GridPixelElement>() ?? FindGrid(window.child);
                if (grid == null) return null;
                var element = grid.GetElement(index, 1);
                if (element == null) return null;
                return element.TryCast<GameInventory>();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>BFS the window subtree for the first GridPixelElement (GameItem subtrees skipped).</summary>
        private static GridPixelElement FindGrid(GraphNodeStorage root)
        {
            if (root == null) return null;
            var queue = new Queue<GraphNodeStorage>();
            queue.Enqueue(root);
            var seen = new HashSet<IntPtr>();
            int guard = 0;
            while (queue.Count > 0 && guard++ < 64)
            {
                var n = queue.Dequeue();
                if (n == null) continue;
                IntPtr p;
                try { p = n.Pointer; } catch { continue; }
                if (!seen.Add(p)) continue;
                GameItem asItem = null;
                try { asItem = n.TryCast<GameItem>(); } catch { }
                if (asItem != null) continue;
                GridPixelElement g = null;
                try { g = n.TryCast<GridPixelElement>(); } catch { }
                if (g != null) return g;
                try { foreach (var c in n.children) if (c != null) queue.Enqueue(c); } catch { }
            }
            return null;
        }
    }
}
