using System;
using Il2Cpp;

namespace PSApi.Items
{
    /// <summary>
    /// v0.9.19 槽位放置几何守卫 (实测 bug: 旅行物品箱口袋 2x3 放 1x3 细长物品, 头部伸出槽位上缘,
    /// 拖拽高亮红色但仍被强行放下)。
    ///
    /// 根因: SlotFilterPatches.TryAcceptOncePatch 的白名单强制放行路径
    /// (SlotMarker.AcceptUnchecked → 槽级 UncheckedAccept 兜底) 完全绕过原生网格判定 —
    /// UncheckedAccept 不校验 footprint 是否越界/压 shape 洞/压已占格, 也不重设物品位置
    /// (SaveLoadPatches 头注: "UncheckedAccept does NOT assign position"),
    /// 物品按拖拽时的瞬时形状坐标落位 → 头部悬在槽外。
    ///
    /// 修复: 强制放行前先过本守卫 — footprint 任一占用格越出槽矩形 / 压 shape 洞 / 与已占格冲突
    /// 一律硬拒 (__result=0, 物品回弹), "红=禁放" 真正成立。
    ///
    /// 分层: FitsRectAndHoles = 纯函数 (无头可测, 越界+洞);
    /// MarkerFits/GridMarkerFits/SlotMarkerFits = 原生适配层 (读 GridShape, 重叠走原生
    /// CheckShapePlacement)。读取失败一律保守放行 (守卫只拦"确认非法", 不误伤正常路径)。
    /// </summary>
    public static class GridPlacementGuard
    {
        /// <summary>纯函数: 物品 footprint (锚点 itemX,itemY + itemW×itemH + itemCells 行优先占用串,
        /// null=实心) 放入 invW×invH 槽 (invCells 行优先 0=可放 1=洞, null=无洞) 是否合法。
        /// 每一占用格都必须在矩形内且不压洞。</summary>
        public static bool FitsRectAndHoles(int invW, int invH, byte[] invCells,
            int itemX, int itemY, int itemW, int itemH, byte[] itemCells)
        {
            if (invW <= 0 || invH <= 0 || itemW <= 0 || itemH <= 0) return false;
            for (int dy = 0; dy < itemH; dy++)
            {
                for (int dx = 0; dx < itemW; dx++)
                {
                    if (itemCells != null && itemCells[dy * itemW + dx] == 0) continue;   // 物品镂空格不占位
                    int gx = itemX + dx, gy = itemY + dy;
                    if (gx < 0 || gy < 0 || gx >= invW || gy >= invH) return false;      // 越界
                    if (invCells != null && invCells[gy * invW + gx] != 0) return false; // shape 洞
                }
            }
            return true;
        }

        /// <summary>放置守卫入口 (TryAcceptOncePatch 用): 按槽类型分派网格/固定槽校验。
        /// true = 合法或无法判定 (放行); false = 确认非法 (必须拒)。
        /// stackTarget: 同 id 叠放的目标物品 (marker.targetItem) — 重叠判定时一并忽略,
        /// 否则叠放会被误判"压已占格"拒掉。</summary>
        public static bool MarkerFits(GameInventory inv, GameItem item, GridShape markerShape, GameItem stackTarget = null, bool slotStrict = false)
        {
            if (inv == null || item == null) return true;
            try
            {
                var grid = inv.TryCast<GameGridInventory>();
                if (grid != null) return GridMarkerFits(grid, item, markerShape, stackTarget);
                var slot = inv.TryCast<GameSlotInventory>();
                if (slot != null) return SlotMarkerFits(slot, item, markerShape, slotStrict);
            }
            catch { }
            return true;
        }

        /// <summary>网格槽 (GameGridInventory/GameGridScrollableInventory): markerShape 落位的
        /// 越界+shape 洞 (纯函数) + 已占格冲突 (原生 CheckShapePlacement, 与原生 TryInventorySlot 同判定)。</summary>
        public static bool GridMarkerFits(GameGridInventory grid, GameItem item, GridShape markerShape, GameItem stackTarget = null)
        {
            if (grid == null || item == null) return true;
            GridShape invShape = null;
            try { invShape = grid.inventoryShape; } catch { }
            if (invShape == null) return true;   // 无 shape 信息 → 无法判定, 保守放行
            var shape = markerShape;
            if (shape == null) { try { shape = item.shape; } catch { } }
            if (shape == null) return true;

            try
            {
                int invMinX = invShape.minX, invMinY = invShape.minY;
                int invW = invShape.globalWidth, invH = invShape.globalHeight;
                if (invW > 0 && invH > 0 && invW * invH <= 4096)
                {
                    var invCells = new byte[invW * invH];
                    for (int y = 0; y < invH; y++)
                        for (int x = 0; x < invW; x++)
                            invCells[y * invW + x] = invShape.Get(invMinX + x, invMinY + y);
                    int mx = shape.minX, my = shape.minY;
                    int w = shape.globalWidth, h = shape.globalHeight;
                    if (w > 0 && h > 0 && w * h <= 4096)
                    {
                        var cells = new byte[w * h];
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                                cells[y * w + x] = shape.Get(mx + x, my + y);
                        if (!FitsRectAndHoles(invW, invH, invCells, mx - invMinX, my - invMinY, w, h, cells))
                            return false;   // 确认越界/压洞 → 硬拒
                    }
                }
            }
            catch { return true; }   // 读取失败保守放行

            // 重叠判定: 原生 CheckShapePlacement (忽略自身 + 叠放目标); 调用失败不因此拒 (纯函数已兜越界/洞)
            try
            {
                var ignore = new Il2CppSystem.Collections.Generic.List<GameItem>();
                ignore.Add(item);
                if (stackTarget != null) ignore.Add(stackTarget);
                if (!grid.CheckShapePlacement(item, shape, ignore)) return false;
            }
            catch { }
            return true;
        }

        /// <summary>纯函数 (v0.9.20): 固定 slot 的 footprint 判定 — strict=false (默认) 恒放行:
        /// slot 类型语义就是「视觉一格, 任何 footprint 都能放」 (鉴定机 2x2 收 10x3 狙击枪、
        /// 打印机罐槽 1x1 收 1x2 液罐); strict=true (psui strict_footprint: true) 才验外接矩形≤槽格数。</summary>
        public static bool SlotFitsByFootprint(int slotW, int slotH, int itemW, int itemH, bool strict)
            => !strict || (itemW <= slotW && itemH <= slotH);

        /// <summary>固定槽 (GameSlotInventory): 默认不校验 (slot = 一格任意大小, v0.9.20 用户拍板 —
        /// v0.9.19 的强制 footprint≤槽格数是回归: 主武器槽 2x2 收 10x3 枪/罐槽 1x1 收 1x2 罐全被误拒);
        /// 仅 strict_footprint 槽验外接矩形。固定槽无 shape 洞概念。</summary>
        public static bool SlotMarkerFits(GameSlotInventory slot, GameItem item, GridShape markerShape, bool strict = false)
        {
            if (!strict) return true;
            if (slot == null || item == null) return true;
            int sw = 0, sh = 0;
            try { sw = slot.defaultGridWidth; sh = slot.defaultGridHeight; } catch { }
            if (sw <= 0 || sh <= 0) return true;
            var shape = markerShape;
            if (shape == null) { try { shape = item.shape; } catch { } }
            if (shape == null) return true;
            try { return SlotFitsByFootprint(sw, sh, shape.globalWidth, shape.globalHeight, true); }
            catch { return true; }
        }
    }
}
