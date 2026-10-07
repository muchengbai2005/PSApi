using System;
using Il2Cpp;
using MelonLoader;

namespace PSApi.Items
{
    /// <summary>
    /// 自定义机器工厂 (v0.6.0): machines/*.json 声明 "ui": "custom" 时, 不调用原版机器工厂,
    /// 自装配原生槽位窗口 (PixelWindow + GridPixelElement + GameSlotInventory 图节点树)。
    ///
    /// 装配序列 (仿 MachineFurnace.CreateMachineInventoryWindow + Furnace() 的 ISIL 还原):
    ///   1. new PixelWindow(w, h, canMove, title)
    ///   2. new GridPixelElement(2, 2, false)     —— 2 列 × 2 行; row 0 = 表头, row 1 = 槽位
    ///   3. 输入/输出槽 × 2: 类型由机器 JSON inputKind/outputKind 指定 —— "slot"(默认) GameSlotInventory
    ///      (单物品槽, 可容纳任意格子大小的 1 个物品) | "grid" GameGridInventory(w,h) (严格网格, 可放多物品,
    ///      仿原版熔炉输入格; 配方 count>1 时输入槽必须用 grid)。尺寸由 inputSize/outputSize "WxH" 指定, 缺省输入 3x4 / 输出 2x2
    ///   4. row 0 表头: new TagElement(-1, false).SetText(...)  (可选, 失败仅告警)
    ///   5. grid.AttachPos(element, x, y) 入座   —— 输入 (0,1), 输出 (1,1)
    ///   6. window.Attach(grid)
    ///   7. ItemDirectory.CreateEmptyItem(id) → item.SetContentWindow(window) (内部处理图节点链接)
    ///   8. 槽位零委托 —— 接受/拒放全部走 SlotFilterRegistry 排他(exclusive)注册 + 三 patch
    ///
    /// v0.6.3 血泪教训: 自装配槽位禁止赋任何 interop 委托 (mayInventoryAddItemFunc 等)。
    /// 原版 MayHaveValidInventorySlot 内部用 InvokeFuncExtensions.InvokeAllReduce (反射聚合)
    /// 调委托链, interop 委托 (target=Il2CppToMonoDelegateReference) 被 DynamicInvoke 时抛
    /// TargetException → 原生方法整个炸掉 → patch 没机会执行 → 拖拽死亡。
    /// 读卡器的成功路径本来就是"原生闭包 + 三 patch 放行", 自定义机器照此办理。
    ///
    /// 槽位编号 (SlotBridge.GetSlot = grid.GetElement(index, 1)): 输入 = 0, 输出 = 1。
    /// 机器 JSON 须配 inputSlot:0 / outputSlot:1, RecipeService.RunCycle 的隔夜加工才能找对槽位;
    /// inputWhitelist/outputWhitelist 必填 (排他注册的判定依据, 空则不注册 = 槽位完全不受管)。
    ///
    /// 隔夜驱动/进度/锁料不在此装配: BuildItem 随后走 RecipeService.Attach (onCycleEndSlotItemFunc)
    /// 与 ProgressRecipeService.TryAttach 的既有接线, 本工厂只管窗口与槽位。
    /// 双击开窗零补丁: contentWindow 赋值后原版 DoubleClickAction 原生打开。
    ///
    /// 幂等: Build 每次新建全套对象 (读档重建 SaveLoadPatches Fix A 路径会再次调用),
    /// 不依赖"只跑一次"。
    /// </summary>
    internal static class CustomMachineFactory
    {
        private static MelonLogger.Instance _log;

        internal static void Init(MelonLogger.Instance log) => _log = log;

        internal static GameItem Build(string itemId, RecipeService.MachineDefJson spec)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return null;

            GameItem item = null;
            try { item = ItemDirectory.CreateEmptyItem(itemId); }
            catch (Exception e) { PsApi.Warn(_log, $"custom machine create failed ({itemId}): {e.Message}"); return null; }
            if (item == null) { PsApi.Warn(_log, $"custom machine create null ({itemId})"); return null; }

            PixelWindow window = CreateWindow(itemId);
            if (window == null) return null;   // 无窗口 = 双击打不开 → 判失败, 调用方回退 plain item

            GridPixelElement grid = null;
            try { grid = new GridPixelElement(2, 2, false); }
            catch (Exception e) { PsApi.Warn(_log, $"custom machine grid failed ({itemId}): {e.Message}"); return null; }
            if (grid == null) { PsApi.Warn(_log, $"custom machine grid null ({itemId})"); return null; }

            // row 0 表头 (可选装饰, 失败不阻塞)
            AttachHeader(grid, 0, "输入", itemId);
            AttachHeader(grid, 1, "产出", itemId);

            // row 1 槽位: (0,1)=输入 (1,1)=输出; 尺寸由 inputSize/outputSize "WxH" 指定, 缺省输入 3x4 / 输出 2x2
            // 类型由 inputKind/outputKind 指定: "slot"(默认, 单物品槽 GameSlotInventory) | "grid"(网格 GameGridInventory, 可放多物品, 仿原版熔炉输入格)
            var (inW, inH) = ParseSlotSize(spec?.InputSize, 3, 4, "inputSize", itemId);
            var (outW, outH) = ParseSlotSize(spec?.OutputSize, 2, 2, "outputSize", itemId);
            GameInventory input = null, output = null;
            try
            {
                input = CreateSlot(spec?.InputKind, inW, inH, "input", itemId);
                output = CreateSlot(spec?.OutputKind, outW, outH, "output", itemId);
            }
            catch (Exception e) { PsApi.Warn(_log, $"custom machine slots failed ({itemId}): {e.Message}"); return null; }
            if (input == null || output == null) { PsApi.Warn(_log, $"custom machine slots null ({itemId})"); return null; }

            try
            {
                // interop 包装层类继承被压平, AttachPos(PixelElement) 需 TryCast 走原生类型检查
                var inputEl = input.TryCast<PixelElement>();
                var outputEl = output.TryCast<PixelElement>();
                var gridEl = grid.TryCast<PixelElement>();
                if (inputEl == null || outputEl == null || gridEl == null)
                {
                    PsApi.Warn(_log, $"custom machine cast failed ({itemId}): in={inputEl == null} out={outputEl == null} grid={gridEl == null}");
                    return null;
                }
                if (!grid.AttachPos(inputEl, 0, 1)) PsApi.Warn(_log, $"custom machine input seat failed ({itemId})");
                if (!grid.AttachPos(outputEl, 1, 1)) PsApi.Warn(_log, $"custom machine output seat failed ({itemId})");
                if (!window.Attach(gridEl)) PsApi.Warn(_log, $"custom machine window attach failed ({itemId})");
            }
            catch (Exception e) { PsApi.Warn(_log, $"custom machine graph attach failed ({itemId}): {e.Message}"); return null; }

            try { item.SetContentWindow(window); }
            catch (Exception e) { PsApi.Warn(_log, $"custom machine SetContentWindow failed ({itemId}): {e.Message}"); return null; }

            WireSlots(item, itemId, spec, input, output);
            PsApi.Log(_log, $"custom machine built: {itemId} (2-col grid, in=0 {KindName(spec?.InputKind)} {inW}x{inH} out=1 {KindName(spec?.OutputKind)} {outW}x{outH}, zero-delegate slots, exclusive filter via SlotFilterRegistry)");
            return item;
        }

        private static string KindName(string kind)
            => string.Equals((kind ?? "").Trim(), "grid", StringComparison.OrdinalIgnoreCase) ? "grid" : "slot";

        /// <summary>解析 "WxH" 槽位尺寸 (如 "3x4"); 空用默认, 非法回退默认并告警。</summary>
        private static (int w, int h) ParseSlotSize(string raw, int defW, int defH, string field, string itemId)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (defW, defH);
            var parts = raw.Trim().Split('x', 'X');
            if (parts.Length == 2
                && int.TryParse(parts[0].Trim(), out int w) && int.TryParse(parts[1].Trim(), out int h)
                && w > 0 && h > 0)
                return (w, h);
            PsApi.Warn(_log, $"custom machine {field} invalid ({itemId}): '{raw}', fallback {defW}x{defH}");
            return (defW, defH);
        }

        /// <summary>v0.6.4: 槽位类型。"grid" → GameGridInventory (严格网格, 可放多物品, 仿原版熔炉输入格);
        /// 其他/缺省 → GameSlotInventory (单物品槽, 可容纳任意格子大小的 1 个物品)。</summary>
        private static GameInventory CreateSlot(string kind, int w, int h, string label, string itemId)
        {
            bool grid = string.Equals((kind ?? "").Trim(), "grid", StringComparison.OrdinalIgnoreCase);
            if (!grid && !string.IsNullOrWhiteSpace(kind)
                && !string.Equals(kind.Trim(), "slot", StringComparison.OrdinalIgnoreCase))
                PsApi.Warn(_log, $"custom machine {label}Kind unknown ({itemId}): '{kind}', fallback 'slot'");
            return grid
                ? (GameInventory)new GameGridInventory(w, h)
                : new GameSlotInventory(w, h, false, false);
        }

        /// <summary>窗口 ctor: 优先带尺寸 (仿原版工厂传 width/height), 异常回退 (canMove, title)。</summary>
        private static PixelWindow CreateWindow(string itemId)
        {
            try { return new PixelWindow(2, 2, true, null); }
            catch (Exception e) { PsApi.Warn(_log, $"custom machine sized window ctor failed ({itemId}), fallback: {e.Message}"); }
            try { return new PixelWindow(true, null); }
            catch (Exception e) { PsApi.Warn(_log, $"custom machine window ctor failed ({itemId}): {e.Message}"); return null; }
        }

        private static void AttachHeader(GridPixelElement grid, int x, string text, string itemId)
        {
            try
            {
                var tag = new TagElement(-1, false).SetText(text, 10000, RenderHandler.ColorPalette.White);
                // interop 包装层类继承被压平 (TagElement/GameSlotInventory 不显式继承 PixelElement),
                // 用 TryCast 走原生类型检查入座
                var el = tag == null ? null : tag.TryCast<PixelElement>();
                if (el != null) grid.AttachPos(el, x, 0);
                else PsApi.Warn(_log, $"custom machine header cast failed ({itemId}, x={x})");
            }
            catch (Exception e) { PsApi.Warn(_log, $"custom machine header failed ({itemId}, x={x}): {e.Message}"); }
        }

        /// <summary>v0.6.3: 槽位零委托。此处只做配置校验 —— 接受/拒放全部交给 SlotFilterRegistry
        /// 排他注册 (RegisterMachine 在 BuildItem/存档重挂后对 ui=custom 机器自动 exclusive=true)。
        /// 缺 inputWhitelist 的自定义机器输入槽会无人接管 (原生空链全拒/全放皆不可靠), 必须告警。</summary>
        private static void WireSlots(GameItem item, string itemId, RecipeService.MachineDefJson spec,
            GameInventory input, GameInventory output)
        {
            if (spec?.InputWhitelist == null || spec.InputWhitelist.Count == 0)
                PsApi.Warn(_log, $"custom machine no inputWhitelist ({itemId}): input slot unfiltered — add inputWhitelist to machines/*.json");
            if (spec?.OutputWhitelist == null || spec.OutputWhitelist.Count == 0)
                PsApi.Warn(_log, $"custom machine no outputWhitelist ({itemId}): output slot unfiltered — add outputWhitelist to machines/*.json");
        }
    }
}
