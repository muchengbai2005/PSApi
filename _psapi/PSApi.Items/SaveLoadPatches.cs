using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace PSApi.Items
{
    /// <summary>
    /// Save-load re-attachment (v0.5.0, decode-capture sync-registration).
    ///
    /// Background & Root Causes (v0.4.x bugs):
    ///   - Bug 1 (position loss): v0.4.1 saved machine position from modifiedXOrigin/YOrigin
    ///     (runtime cache fields, always 0 on decoded shells). Real position lives in itemModifiedShape.minX/minY
    ///     (serialized in save, confirmed at 14/18,2 for test furnaces). UncheckedAccept does NOT assign position;
    ///     rendering uses modifiedShape.minX/minY.
    ///   - Bug 2 (slot scramble): childItemInventoryNode was misinterpreted as MachineHelper.GetSlot index (0-4).
    ///     ISIL confirms it's a BFS node index over the machine's GraphNodeStorage subtree:
    ///       root unindexed → non-GameItem nodes indexed in dequeue order (0,1,2,...)
    ///       GameItem nodes skipped (no index, no children traversal).
    ///     Furnace BFS: contentWindow(0), header(1), battery(2), module(3), input(4), output(5), manual(6).
    ///     Empirical: [2,4]=battery+input matches perfectly; but v0.4.1 used GetSlot(i), mapping incorrectly.
    ///   - Bug 0 (root cause of everything): frame-sliced registration completes AFTER DecodeNodes runs
    ///     (log: decode 18:20:23.377, register 18:20:23.694). DecodeNodes DOES call DirectoryMaster.Item() natively
    ///     but mod items decode as inert shells (no window, empty graph). The "UI ready" diagnosis from handoff
    ///     was likely a misread—DecodeItems runs before any mod registration because we were slicing!
    ///
    /// Design (v0.5.0):
    ///   - Fix A (sync registration): ItemStore.RegisterAllNow(string from) called synchronously from
    ///     DecodeNodes Prefix AND OnDirectoryInit. Cheap: just dict.Add(lazyFactory); actual BuildItem runs
    ///     during native DecodeNodes.DirectoryMaster.Item() calls. Once registered, native decode builds full
    ///     machines (windows/slots/recipes/wiring), relinks children via BFS-indexed slots, restores positions
    ///     from itemModifiedShape automatically. Shellification never happens → deferred pass is pure safety net.
    ///   - Fix B (fallback position): In RebuildMachineInstance, clone old.modifiedShape onto fresh BEFORE accept
    ///     (native decode assigns itemModifiedShape to modifiedShape before relink; ValidateShapeState preserves position).
    ///     Belt-and-braces: re-assert position after accept too via GridShapeBuilder().SetPosition(x,y).Build().
    ///     Position source priority: capture.SavedX/Y > old.modifiedShape.minX/minY.
    ///   - Fix C (fallback slots): ReplacePlaceChild's GetSlot indexing with BFS node resolution replicating
    ///     EncodeNode/DecodeNodes exact semantics (root seed skipped, GameItem skip, others indexed dequeue-order).
    ///     ResolveNodeIndex(machine, recorded_idx) → tryCast<GameInventory> → UncheckedAccept (no filter pre-check).
    ///     Fallback to FindAcceptingSlot if resolution fails.
    ///   - Capture extends to ALL registered native-machine-ui items (children optional), reading position from
    ///     n.itemModifiedShape (not children count only). Furnace #1 (uuid=13, no children) also captured for pos restore.
    ///   - Note: Patch duplicates ×2 per call (HarmonyPatch applied twice artifact). Prefix captures duplicate entries
    ///     harmless; Postfix idempotent checks skip resolved LiveItems. All best-effort with logs.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.DecodeNodes))]
    internal static class SaveLoadPatches
    {
        private static readonly List<object> _pinned = new List<object>();
        private static MelonLogger.Instance _log;
        private static int _pendingFrames = -1;
        private static int _passAttempt = 0;  // 0=none, 1=first, 2=final retry

        // ==================== decode-time capture ====================

        private class CapturedChild
        {
            internal long ChildUuid;
            internal int NodeIdx = -1;              // BFS node index (NOT Slot index!)
            internal string Identifier;
            internal int UniqueId;
            internal GameItem LiveItem;   // resolved in Postfix; may die later (checked at use)
        }

        private class CapturedMachine
        {
            internal long ParentUuid;
            internal string Identifier;
            internal int UniqueId;
            internal GameItem LiveItem;   // resolved in Postfix
            internal int SavedX, SavedY;
            internal bool HasSavedPos;
            internal readonly List<CapturedChild> Children = new List<CapturedChild>();
        }

        private static readonly List<CapturedMachine> _captured = new List<CapturedMachine>();

        internal static void Init(MelonLogger.Instance log) => _log = log;

        /// <summary>Prefix: register items BEFORE native decode (Fix A), then capture parent->child relations.</summary>
        private static void Prefix(Il2CppSystem.Collections.Generic.List<SaveItemNode> savedItems)
        {
            try
            {
                // First bag-decode of a fresh load: drop stale capture from a previous life.
                if (_pendingFrames < 0)
                {
                    _captured.Clear();
                    _passAttempt = 0;
                }

                // ★ Fix A: Synchronous registration BEFORE native decode.
                // 注册只是 dir.Add(惰性工厂)，代价极小;真正的 BuildItem 在 DirectoryMaster.Item(id) 被调用时才执行。
                try { ItemStore.RegisterAllNow("decode-prefix"); }
                catch (Exception e) { PsApi.Warn(_log, $"save capture prefix failed to register: {e.Message}"); }

                if (savedItems == null) return;

                var byUuid = new Dictionary<long, SaveItemNode>();
                foreach (var n in savedItems)
                {
                    if (n == null) continue;
                    try { byUuid[n.uuid] = n; } catch { }
                }

                foreach (var n in savedItems)
                {
                    if (n == null) continue;
                    string id; long uuid; int uniq;
                    Il2CppSystem.Collections.Generic.List<long> kids;
                    Il2CppSystem.Collections.Generic.List<int> kidSlots;
                    try { id = n.identifier; uuid = n.uuid; uniq = n.uniqueId; kids = n.childItems; kidSlots = n.childItemInventoryNode; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(id)) continue;   // Allow machines without children
                    if (!ItemStore.TryGetDef(id, out _) || !RecipeService.HasNativeMachineUi(id)) continue;

                    var m = new CapturedMachine { ParentUuid = uuid, Identifier = id, UniqueId = uniq };

                    // ★ Fix B: Capture serialized position from itemModifiedShape.
                    try
                    {
                        var ms = n.itemModifiedShape;
                        if (ms != null) { m.SavedX = ms.minX; m.SavedY = ms.minY; m.HasSavedPos = true; }
                    }
                    catch { /* Ignore position read failures */ }

                    // Fill children only if available (non-null list).
                    if (kids != null && kids.Count > 0)
                    {
                        for (int i = 0; i < kids.Count; i++)
                        {
                            long cu;
                            try { cu = kids[i]; } catch { continue; }
                            int ni = -1;
                            try { if (kidSlots != null && i < kidSlots.Count) ni = kidSlots[i]; } catch { }
                            var c = new CapturedChild { ChildUuid = cu, NodeIdx = ni };
                            if (byUuid.TryGetValue(cu, out var cn) && cn != null)
                            {
                                try { c.Identifier = cn.identifier; c.UniqueId = cn.uniqueId; } catch { }
                            }
                            m.Children.Add(c);
                        }
                    }

                    // Only add if we have useful data (children OR position).
                    if (m.Children.Count > 0 || m.HasSavedPos)
                    {
                        _captured.Add(m);
                        PsApi.Log(_log, $"save capture: {id} uuid={uuid} uniq={uniq} children={m.Children.Count} pos={m.SavedX},{m.SavedY}");
                    }
                }
            }
            catch (Exception e) { PsApi.Warn(_log, "save capture prefix failed: " + e.Message); }
        }

        /// <summary>Postfix: resolve captured uuids to live items, then arm the deferred pass.</summary>
        private static void Postfix(GameItem __result)
        {
            try
            {
                var dict = SaveManager.itemDict;
                if (dict != null && _captured.Count > 0)
                {
                    int resolved = 0;
                    foreach (var m in _captured)
                    {
                        if (m.LiveItem == null)
                        {
                            GameItem gi = null;
                            try { dict.TryGetValue(m.ParentUuid, out gi); } catch { }
                            m.LiveItem = gi;
                        }
                        foreach (var c in m.Children)
                        {
                            if (c.LiveItem != null) continue;
                            GameItem gi = null;
                            try { dict.TryGetValue(c.ChildUuid, out gi); } catch { }
                            c.LiveItem = gi;
                            if (gi != null) resolved++;
                        }
                    }
                    PsApi.Log(_log, $"save capture resolve: {_captured.Count} machine(s), {resolved} child item(s) resolved live");
                }
            }
            catch (Exception e) { PsApi.Warn(_log, "save capture resolve failed: " + e.Message); }
            // ~45 frames after the store scene is up: load fully settled, UI ready.
            _pendingFrames = 45;
        }

        // ==================== deferred live pass ====================

        /// <summary>Called every frame from Plugin.OnUpdate.</summary>
        internal static void Drive()
        {
            if (_pendingFrames < 0) return;
            EmporiumEntry entry = null;
            PlayerStore store = null;
            try { entry = EmporiumEntry.Instance; } catch { }
            try { store = PlayerStore.Instance; } catch { }
            if (entry == null || store == null) return;   // not in game yet; hold
            if (--_pendingFrames > 0) return;
            _pendingFrames = -1;

            _passAttempt++;
            PsApi.Log(_log, $"save reattach: pass {_passAttempt} starting");
            try
            {
                int fixes = ProcessLiveInventories(entry);
                // If we did nothing AND it's first pass, schedule one final retry shortly after.
                if (fixes == 0 && _passAttempt == 1)
                {
                    PsApi.Log(_log, "save reattach: first pass empty, scheduling final retry at frame 60");
                    _pendingFrames = 60;  // ~1 second
                }
                else if (fixes > 0)
                {
                    PsApi.Log(_log, $"save reattach: pass {_passAttempt} applied {fixes} fix(es)");
                }
            }
            catch (Exception e) { PsApi.Warn(_log, $"save reattach pass {_passAttempt} failed: {e.Message}"); }
        }

        private static int ProcessLiveInventories(EmporiumEntry entry)
        {
            var seen = new HashSet<IntPtr>();
            int fixes = 0;
            foreach (var inv in EnumerateInventories(entry))
            {
                Il2CppSystem.Collections.Generic.List<GameItem> items = null;
                try { items = inv.childItems; } catch { }
                if (items == null) continue;
                foreach (var item in items)
                {
                    try { fixes += ReattachDeep(item, seen); }
                    catch (Exception e) { PsApi.Warn(_log, "save reattach item failed: " + e.Message); }
                }
            }
            return fixes;
        }

        private static int ReattachDeep(GameItem item, HashSet<IntPtr> seen)
        {
            if (item == null) return 0;
            try { if (!seen.Add(item.Pointer)) return 0; } catch { }
            int fixes = ReattachOne(item);
            Il2CppSystem.Collections.Generic.List<GameItem> children = null;
            try { children = item.FindAllChildItems(); } catch { }
            if (children != null)
                foreach (var child in children)
                {
                    try { fixes += ReattachDeep(child, seen); }
                    catch (Exception e) { PsApi.Warn(_log, "save reattach child failed: " + e.Message); }
                }
            return fixes;
        }

        private static List<GameInventory> EnumerateInventories(EmporiumEntry entry)
        {
            var list = new List<GameInventory>();
            Action<GameInventory> add = inv => { if (inv != null) list.Add(inv); };
            try { add(entry.backInvinvElement); } catch { }
            try { add(entry.backInvinvElementCounter); } catch { }
            try { add(entry.frontInvinvElement); } catch { }
            try { add(entry.invElement); } catch { }
            try { add(entry.showcaseElement); } catch { }
            try { add(entry.hiddenElement); } catch { }
            try { add(entry.soldElement); } catch { }
            try { add(entry.trashcanInvElement); } catch { }
            try { add(entry.trashInvElement); } catch { }
            try { add(entry.docInvElement); } catch { }
            try { add(entry.bazarLeftinvElement); } catch { }
            try { add(entry.afterhourInventory); } catch { }
            try { add(entry.hirelingInv); } catch { }
            try { add(entry.swapBufferElement); } catch { }
            try { add(entry.drainInvElement); } catch { }
            return list;
        }

        private static int ReattachOne(GameItem item)
        {
            string id;
            try { id = item.identifier; } catch { return 0; }
            if (string.IsNullOrEmpty(id) || !ItemStore.TryGetDef(id, out var def)) return 0;
            int fixes = 0;

            // Diagnostic: log current state of the shell.
            bool hasWindow;
            try { hasWindow = item.contentWindow != null; } catch { hasWindow = false; }
            int graphChildCount = 0;
            try { if (item.children != null) graphChildCount = item.children.Count; } catch { }
            int findAllCount = 0;
            try { var fa = item.FindAllChildItems(); if (fa != null) findAllCount = fa.Count; } catch { }
            int slotChildTotal = 0;
            bool isMachine = RecipeService.HasNativeMachineUi(id);
            if (isMachine)
                for (int i = 0; i <= 3; i++)
                {
                    try
                    {
                        var slot = SlotBridge.GetSlot(item, i);
                        if (slot?.childItems != null) slotChildTotal += slot.childItems.Count;
                    }
                    catch { }
                }
            PsApi.Log(_log, $"save reattach check: {id} window={hasWindow} graphChildren={graphChildCount} findAll={findAllCount} slotChildren={slotChildTotal}");

            // Match decode-time capture by uniqueId (serialized, survives), pointer as fallback.
            CapturedMachine cap = FindCaptureFor(item, id);

            if (isMachine)
            {
                if (!hasWindow)
                {
                    // Inert shell: swap for a factory-built machine, migrate captured children.
                    bool rebuilt = false;
                    try { rebuilt = RebuildMachineInstance(item, id, cap); }
                    catch (Exception e) { PsApi.Warn(_log, $"save reattach machine rebuild failed ({id}): {e.Message}"); }
                    if (rebuilt) return 1;
                }
                else if (slotChildTotal == 0 && cap != null && cap.Children.Count > 0)
                {
                    // Vanilla restored the window but not the children: inject them directly.
                    int moved = InjectCapturedChildren(item, cap, id);
                    if (moved > 0) fixes += moved;
                }
                // hasWindow && slotChildTotal > 0: vanilla restored everything; leave the shell alone.
            }

            // Feature-mode qualities are not serialized; restore the factory default when none shows.
            try
            {
                if (def.Qualities != null && def.Qualities.Count > 0 && QualityService.GetQuality(item) == null)
                {
                    string qid = def.DefaultQuality;
                    if (string.IsNullOrEmpty(qid) || !QualityService.TryGetById(qid, out _)) qid = def.Qualities[0];
                    QualityService.SetQuality(item, id, qid);
                    PsApi.Log(_log, $"save reattach: quality {qid} -> {id}");
                    fixes++;
                }
            }
            catch (Exception e) { PsApi.Warn(_log, $"save reattach quality failed ({id}): {e.Message}"); }

            // JSON recipes + nightly cycle callback.
            try
            {
                bool hasManager;
                try { hasManager = item.recipeManager != null; } catch { hasManager = true; }
                if (!hasManager && RecipeService.HasRecipesFor(id) && RecipeService.Attach(item, id, null, _pinned))
                {
                    PsApi.Log(_log, $"save reattach: recipes -> {id}");
                    fixes++;
                }
            }
            catch (Exception e) { PsApi.Warn(_log, $"save reattach recipes failed ({id}): {e.Message}"); }

            // 模组打印机: 隔夜回调委托不序列化, 存档重载后必须重挂 (v0.5.4)
            try
            {
                if (ModulePrinterService.IsPrinter(id) && ModulePrinterService.TryAttach(item, id, _pinned))
                {
                    PsApi.Log(_log, $"save reattach: printer -> {id}");
                    fixes++;
                }
            }
            catch (Exception e) { PsApi.Warn(_log, $"save reattach printer failed ({id}): {e.Message}"); }

            // 进度+配方结合: 隔夜回调委托不序列化, 存档重载后必须重挂 (v0.5.11)
            try
            {
                if (ProgressRecipeService.HasProgress(id) && ProgressRecipeService.TryAttach(item, id, _pinned))
                {
                    PsApi.Log(_log, $"save reattach: progress-recipe -> {id}");
                    fixes++;
                }
            }
            catch (Exception e) { PsApi.Warn(_log, $"save reattach progress-recipe failed ({id}): {e.Message}"); }

            // v0.5.12: Harmony patch 槽位过滤器重新注册 (存档重载后槽位 IntPtr 变了)
            try { SlotFilterRegistry.RegisterMachine(item, id); }
            catch (Exception e) { PsApi.Warn(_log, $"save reattach slot-filter register failed ({id}): {e.Message}"); }

            return fixes;
        }

        /// <summary>v0.5.11: 存档重载后重包装槽位过滤器。</summary>
        private static void ReattachSlotFilters(GameItem item, string id)
        {
            if (!RecipeService.TryGetMachineSpecPublic(id, out var spec)) return;

            if (spec.InputWhitelist != null && spec.InputWhitelist.Count > 0)
            {
                int inIdx = spec.InputSlot ?? 2;
                try
                {
                    var slot = SlotBridge.GetSlot(item, inIdx);
                    if (slot != null)
                    {
                        var wl = SlotFilterService.BuildWhitelist(spec.InputWhitelist);
                        if (SlotFilterService.WrapSlotFilter(slot, wl, $"{id}:input({inIdx}) reattach", _pinned))
                            PsApi.Log(_log, $"save reattach: slot filter input -> {id}");
                    }
                }
                catch { }
            }

            if (spec.OutputWhitelist != null && spec.OutputWhitelist.Count > 0)
            {
                int outIdx = spec.OutputSlot ?? (spec.InputSlot ?? 2) + 1;
                try
                {
                    var slot = SlotBridge.GetSlot(item, outIdx);
                    if (slot != null)
                    {
                        var wl = SlotFilterService.BuildWhitelist(spec.OutputWhitelist);
                        if (SlotFilterService.WrapSlotFilter(slot, wl, $"{id}:output({outIdx}) reattach", _pinned))
                            PsApi.Log(_log, $"save reattach: slot filter output -> {id}");
                    }
                }
                catch { }
            }
        }

        private static CapturedMachine FindCaptureFor(GameItem item, string id)
        {
            if (_captured.Count == 0) return null;
            int uniq = -1;
            try { uniq = item.uniqueId; } catch { }
            foreach (var m in _captured)
            {
                if (m.Identifier != id) continue;
                if (uniq >= 0 && m.UniqueId == uniq) return m;
            }
            // Pointer fallback: the live shell may BE the decoded instance.
            foreach (var m in _captured)
            {
                if (m.Identifier != id || m.LiveItem == null) continue;
                try { if (m.LiveItem.Pointer == item.Pointer) return m; } catch { }
            }
            return null;
        }

        // ==================== machine rebuild & child migration ====================

        /// <summary>
        /// Replace an inert decoded machine shell with a directory-fresh machine
        /// (window/slots/recipes/cycle all wired by the normal build path).
        /// Captured children are migrated into the fresh machine's slots; when no
        /// capture exists, fall back to whatever the live shell still exposes.
        /// Returns false and leaves the shell untouched on any failure.
        /// </summary>
        private static bool RebuildMachineInstance(GameItem old, string id, CapturedMachine cap)
        {
            GameItem fresh = null;
            try { fresh = DirectoryMaster.Item(id, true); }
            catch (Exception e) { PsApi.Warn(_log, $"save reattach machine build failed ({id}): {e.Message}"); return false; }
            if (fresh == null) { PsApi.Warn(_log, $"save reattach machine build null ({id})"); return false; }
            try { if (fresh.contentWindow == null) { PsApi.Warn(_log, $"save reattach machine build lacks window ({id})"); return false; } }
            catch { return false; }

            GameInventory parentInv = FindParentInventory(old);
            if (parentInv == null) { PsApi.Warn(_log, $"save reattach machine has no parent inventory ({id}), shell kept"); return false; }

            // ★ Determine saved grid position: capture first (Fix B), else the shell's serialized modifiedShape.
            // v0.4.1 bug: read modifiedXOrigin/YOrigin (runtime cache, unserialized, always 0 on decoded shells) → position lost.
            int origX = 0, origY = 0; bool havePos = false;
            if (cap != null && cap.HasSavedPos) { origX = cap.SavedX; origY = cap.SavedY; havePos = true; }
            if (!havePos) { try { var ms = old.modifiedShape; if (ms != null) { origX = ms.minX; origY = ms.minY; havePos = true; } } catch { } }
            if (!havePos) PsApi.Warn(_log, $"save reattach: could not read position from {id}, using default (0,0)");

            // Pre-check that the parent can take the fresh machine before
            // anything is expelled.
            try
            {
                var marker = parentInv.TryInventorySlot(fresh, fresh.shape, fresh.state);
                if (marker == null) { PsApi.Warn(_log, $"save reattach parent rejects fresh machine ({id}), shell kept"); return false; }
            }
            catch (Exception e) { PsApi.Warn(_log, $"save reattach parent check failed ({id}): {e.Message}"); return false; }

            if (cap != null && cap.Children.Count > 0)
                InjectCapturedChildren(fresh, cap, id);
            else
                MigrateChildrenLiveFallback(old, fresh, id);

            // ★ Clone the shell's serialized shape (carries saved position/rotation) onto fresh BEFORE accept.
            // Native decode assigns modifiedShape from save before relink-accept; ValidateShapeState preserves it for vanilla.
            try
            {
                var oldShape = old.modifiedShape;
                if (oldShape != null) fresh.modifiedShape = oldShape.Clone();
            }
            catch (Exception e) { PsApi.Warn(_log, $"save reattach: failed to clone shape for {id}: {e.Message}"); }

            try
            {
                if (!parentInv.Expel(old)) { PsApi.Warn(_log, $"save reattach expel failed ({id}), shell kept"); return false; }
                if (!parentInv.UncheckedAccept(fresh))
                {
                    PsApi.Warn(_log, $"save reattach accept failed after expel ({id}); restoring shell");
                    try { parentInv.UncheckedAccept(old); } catch { }
                    return false;
                }
                // Belt-and-braces: re-assert saved position after accept
                // (ValidateShapeState recomputes modifiedShape from shape+rotation; it preserves position for vanilla,
                // but don't rely on it for rebuilt machines—just assert explicitly).
                try
                {
                    if (havePos)
                    {
                        var ms = fresh.modifiedShape;
                        bool off = ms == null || ms.minX != origX || ms.minY != origY;
                        if (off)
                        {
                            var b = ms != null ? new GridShapeBuilder(ms) : new GridShapeBuilder(fresh.shape);
                            b.SetPosition(origX, origY);
                            var rebuilt = b.Build();
                            if (rebuilt != null) fresh.modifiedShape = rebuilt;
                        }
                        fresh.modifiedXOrigin = origX;
                        fresh.modifiedYOrigin = origY;
                    }
                }
                catch (Exception e) { PsApi.Warn(_log, $"save reattach: failed to restore position to {id}: {e.Message}"); }
            }
            catch (Exception e) { PsApi.Warn(_log, $"save reattach swap failed ({id}): {e.Message}"); return false; }

            PsApi.Log(_log, $"save reattach: machine rebuilt with native window -> {id} pos={origX},{origY}");
            return true;
        }

        /// <summary>
        /// Migrate decode-captured children into the machine's slots. Recorded NODE INDEX (BFS) is resolved
        /// to the containing GameInventory via BFS replication, then UncheckedAccept (no filter, mirroring native).
        /// Falls back to FindAcceptingSlot if resolution fails or accepts rejects.
        /// </summary>
        private static int InjectCapturedChildren(GameItem machine, CapturedMachine cap, string id)
        {
            int moved = 0;
            foreach (var c in cap.Children)
            {
                string childId = c.Identifier ?? "?";
                GameItem child = ResolveChild(c, id);
                if (child == null)
                {
                    PsApi.Warn(_log, $"save reattach: child '{childId}' of {id} unrecoverable (dead instance, no identifier)");
                    continue;
                }
                try
                {
                    if (PlaceChild(machine, child, c.NodeIdx, id))
                    {
                        PsApi.Log(_log, $"save reattach: migrated '{childId}'(uid={c.UniqueId}) -> {id} slot {FindSlotIndexOrThrow(machine, child)}");
                        moved++;
                    }
                    else
                    {
                        PsApi.Warn(_log, $"save reattach: no slot accepts '{childId}' on {id}, left behind");
                    }
                }
                catch (Exception e) { PsApi.Warn(_log, $"save reattach migrate '{childId}' failed: {e.Message}"); }
            }
            if (moved == 0 && cap.Children.Count > 0)
                PsApi.Warn(_log, $"save reattach: WARNING — {cap.Children.Count} captured child(ren) of {id} could not be placed");
            return moved;
        }

        /// <summary>Prefer the decoded child instance (carries its saved tag state); respawn from identifier when the instance died.</summary>
        private static GameItem ResolveChild(CapturedChild c, string parentId)
        {
            if (c.LiveItem != null)
            {
                try { if (c.LiveItem.identifier != null) return c.LiveItem; } catch { /* dead wrapper */ }
            }
            if (!string.IsNullOrEmpty(c.Identifier))
            {
                try
                {
                    var gi = DirectoryMaster.Item(c.Identifier, true);
                    if (gi != null)
                    {
                        PsApi.Warn(_log, $"save reattach: child '{c.Identifier}' of {parentId} respawned fresh — per-instance state lost");
                        return gi;
                    }
                }
                catch (Exception e) { PsApi.Warn(_log, $"save reattach child respawn failed '{c.Identifier}': {e.Message}"); }
            }
            return null;
        }

        /// <summary>
        /// childItemInventoryNode 是 EncodeNode BFS 节点索引，不是 MachineHelper.GetSlot 号。
        /// BFS 规则 (与原生 EncodeNode/DecodeNodes 完全一致，已核对 ISIL):
        ///   从 machine 出发；根不索引；GameItem 节点跳过 (不索引且不遍历其子节点);
        ///   其余 GraphNodeStorage 按出队顺序从 0 编号 (去重)。
        /// 解析出的节点须为 GameInventory 才能直接 UncheckedAccept(与原生重链接一致，不过滤);
        /// 解析失败回退 FindAcceptingSlot。(Fix C)
        /// </summary>
        private static bool PlaceChild(GameItem machine, GameItem child, int preferredNodeIdx, string id)
        {
            if (preferredNodeIdx >= 0)
            {
                GameInventory slot = ResolveNodeIndex(machine, preferredNodeIdx);
                if (slot != null)
                {
                    bool accepted = false;
                    try
                    {
                        var src = FindParentInventory(child);
                        if (src != null) src.Expel(child);
                        accepted = slot.UncheckedAccept(child);
                    }
                    catch { }
                    if (accepted) return true;
                    PsApi.Warn(_log, $"save reattach: BFS node {preferredNodeIdx} rejected child on {id}, falling back");
                }
                else
                {
                    PsApi.Warn(_log, $"save reattach: BFS node {preferredNodeIdx} unresolved on {id}, falling back");
                }
            }
            var target = FindAcceptingSlot(machine, child);
            if (target == null) return false;
            var src2 = FindParentInventory(child);
            if (src2 != null) src2.Expel(child);
            return target.UncheckedAccept(child);
        }

        /// <summary>复刻原生 BFS 索引，返回第 nodeIdx 个非 GameItem 图节点 (GameInventory)。</summary>
        private static GameInventory ResolveNodeIndex(GameItem machine, int nodeIdx)
        {
            try
            {
                var queue = new Queue<GraphNodeStorage>();
                var kids = machine.children;
                if (kids == null) return null;
                foreach (var k in kids) if (k != null) queue.Enqueue(k);
                var seen = new HashSet<IntPtr>();
                int idx = 0;
                while (queue.Count > 0)
                {
                    var n = queue.Dequeue();
                    if (n == null) continue;
                    IntPtr p;
                    try { p = n.Pointer; } catch { continue; }
                    GameItem asItem = null;
                    try { asItem = n.TryCast<GameItem>(); } catch { }
                    if (asItem != null) continue;   // GameItem: 不索引不遍历 (skip entirely, don't enqueue children either)
                    if (seen.Add(p))  // Not seen yet? Index it.
                    {
                        if (idx == nodeIdx)
                        {
                            try { return n.TryCast<GameInventory>(); } catch { return null; }
                        }
                        idx++;
                    }
                    // Enqueue children regardless of whether we already saw this node (native behavior: tree structure, terminates).
                    Il2CppSystem.Collections.Generic.List<GraphNodeStorage> sub = null;
                    try { sub = n.children; } catch { }
                    if (sub != null) foreach (var c in sub) if (c != null) queue.Enqueue(c);
                }
            }
            catch { }
            return null;
        }

        /// <summary>Legacy fallback when no decode capture exists: collect whatever the live shell still exposes (child traversal, slot scan, raw graph walk).</summary>
        private static void MigrateChildrenLiveFallback(GameItem old, GameItem fresh, string id)
        {
            var seen = new HashSet<IntPtr>();
            var toMigrate = new List<GameItem>();

            Il2CppSystem.Collections.Generic.List<GameItem> childrenViaList = null;
            try { childrenViaList = old.FindAllChildItems(); } catch { }
            if (childrenViaList != null)
                foreach (var c in childrenViaList)
                    if (c != null && seen.Add(c.Pointer)) toMigrate.Add(c);

            for (int i = 0; i <= 3; i++)
            {
                GameInventory slot = null;
                try { slot = SlotBridge.GetSlot(old, i); } catch { }
                if (slot != null && slot.childItems != null)
                    foreach (var c in slot.childItems)
                        if (c != null && seen.Add(c.Pointer)) toMigrate.Add(c);
            }

            Il2CppSystem.Collections.Generic.List<GraphNodeStorage> graphChildren = null;
            try { graphChildren = old.children; } catch { }
            if (graphChildren != null)
                foreach (var n in graphChildren)
                {
                    if (n == null) continue;
                    GameItem gi = null;
                    try { gi = n.TryCast<GameItem>(); } catch { }
                    if (gi != null && seen.Add(gi.Pointer)) toMigrate.Add(gi);
                }

            if (toMigrate.Count == 0)
            {
                PsApi.Log(_log, $"save reattach: no children found on {id} (no capture), proceeding with empty fresh machine");
                return;
            }

            PsApi.Log(_log, $"save reattach: {toMigrate.Count} child(ren) found live on {id}, migrating...");
            foreach (var child in toMigrate)
            {
                string childId;
                int childUid = -1;
                try { childId = child.identifier; } catch { childId = "?"; }
                try { childUid = child.uniqueId; } catch { }
                try
                {
                    if (PlaceChild(fresh, child, -1, id))
                        PsApi.Log(_log, $"save reattach: migrated '{childId}'(uid={childUid}) -> {id} slot {FindSlotIndexOrThrow(fresh, child)}");
                    else
                        PsApi.Warn(_log, $"save reattach: no slot accepts '{childId}' on {id}, left behind");
                }
                catch (Exception e) { PsApi.Warn(_log, $"save reattach migrate '{childId}' failed: {e.Message}"); }
            }
        }

        private static int FindSlotIndexOrThrow(GameItem machine, GameItem childInSlot)
        {
            for (int i = 0; i <= 3; i++)
            {
                GameInventory slot = null;
                try { slot = SlotBridge.GetSlot(machine, i); } catch { }
                if (slot != null && slot.childItems != null && slot.childItems.Contains(childInSlot)) return i;
            }
            return -1;
        }

        private static GameInventory FindParentInventory(GameItem item)
        {
            try
            {
                var parents = item.parents;
                if (parents == null) return null;
                foreach (var p in parents)
                {
                    if (p == null) continue;
                    var inv = p.TryCast<GameInventory>();
                    if (inv != null) return inv;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Battery(0) -> module(1) -> input(2) -> output(3); first slot whose native filter accepts the item wins.  Slot 4 (manual) is never a target.</summary>
        private static GameInventory FindAcceptingSlot(GameItem machine, GameItem child)
        {
            for (int i = 0; i <= 3; i++)
            {
                GameInventory slot = null;
                try { slot = SlotBridge.GetSlot(machine, i); } catch { }
                if (slot == null) continue;
                try
                {
                    var marker = slot.TryInventorySlot(child, child.shape, child.state);
                    if (marker != null) return slot;
                }
                catch { }
            }
            return null;
        }
    }
}
