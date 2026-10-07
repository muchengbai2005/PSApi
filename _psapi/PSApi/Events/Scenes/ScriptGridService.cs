using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using PSApi.Events.PsScript;
using PSApi.Events.UI;
using PSApi.Items;
using UnityEngine;

namespace PSApi.Events.Scenes
{
    /// <summary>
    /// P3 (v1.28.0) script 驱动场景的物品网格能力服务 — grid.* builtin 的后端。
    /// 代码路径全部搬自已实证的网格实现 (v1.25.1/v1.25.2, 用户实测), 不新造轮子:
    ///   物资点真容器 (PixelWindow+GameGridInventory, 动态行数=总占格→RaidCore.ContainerRows,
    ///     逐件 stack=1 禁堆叠, TryFindOneValidInventorySlot 自动寻位严禁重叠,
    ///     瀑布兜底 容器→地面→物品箱胸式槽→销毁, 关窗剩余销毁 + on_close 回调 taken/left);
    ///   地面网格窗 (持久只建一次, 左缘贴物品箱右缘+gap 底边对齐, 场景撤离遗留销毁);
    ///   物品箱跟随 (机器面板只 Show/Hide/钉位 — 结构恒定铁律: 元素树零增删);
    ///   口袋兜底窗 (v1.37.0: box_follow 失败 = 没带物品箱时的独立口袋网格, 默认 2x3 贴原箱位,
    ///     撤离时内容保 NBT 搬到店里称重台);
    ///   原版外出物品栏隐藏 (藏/还配对, Poll 压回, 场景撤离自动恢复);
    ///   ctrl+左键快捷转移重定向 (外出栏被我们藏了, 原版目标窗=隐藏窗=实质遗失;
    ///     直插路径 TryFindOneValidInventorySlot+Expel+AcceptUnchecked, v1.25.2 根因修复同款)。
    /// 通用化: 物品箱机器 id / 地面窗标题等由 pss 传参 (v1.33.0 起本服务为唯一实现)。
    /// 生命周期: SceneService.DoEnter 防御清残留 / DoExit 销毁容器+地面遗留物品并恢复外出栏
    /// / OnSceneLeft 只清引用; Poll 做容器 X 关窗检测 (0.5s 宽限) 与窗口健康 (被关重开+钉回)。
    /// </summary>
    internal sealed class ScriptGridService
    {
        private readonly MelonLogger.Instance _logger;

        internal SceneService Scenes;                 // Plugin 接线
        internal PsUI.PsUiService Ui;                 // 机器槽位读取 (Plugin 接线)
        internal SceneLogService LogSvc;              // 信息栏 (Plugin 接线)

        // ---- 容器窗 (一次一个) ----
        private PixelWindow _containerWin;
        private GameGridInventory _containerInv;
        private int _containerSpawned;
        private float _containerGraceUntil;
        private PsCallable _containerOnClose;
        private Interpreter _containerItp;

        // ---- 地面网格窗 (持久) ----
        private PixelWindow _groundWin;
        private GameGridInventory _groundInv;
        private int _groundCols, _groundRows;
        private string _groundTitle = "地面";

        // ---- 口袋兜底窗 (v1.37.0: box_follow 失败 = 没带物品箱时的独立口袋网格, 撤离自动搬称重台) ----
        private PixelWindow _pocketWin;
        private GameGridInventory _pocketInv;

        // ---- 物品箱跟随 ----
        private GameItem _boxItem;
        private PixelWindow _boxWin;
        private float _boxPinW = -1f, _boxPinH = -1f;   // 上次钉位时箱窗 rect 尺寸 (v1.29.1 布局延迟重钉; float 对不用 Vector2 — 无头测试台构造服务时 Il2Cpp Vector2 cctor 崩)

        private bool _afterhourHidden;
        private float _uiGraceUntil;
        private float _uiHealthAcc;
        private readonly System.Random _rng = new System.Random();   // v1.45.0: ground_steal 随机抽件

        internal ScriptGridService(MelonLogger.Instance logger) { _logger = logger; }

        internal bool InScene => Scenes != null && Scenes.Active != null;
        internal bool ContainerOpen => _containerWin != null || _containerInv != null;
        /// <summary>战斗服务等读箱槽用 (武器链 s_main/s_melee/s_throw*/s_chest)。</summary>
        internal GameItem BoxItem => _boxItem;
        /// <summary>ctrl 快捷转移重定向门控 (QuickTransferPatches): 外出栏是我们藏的才需要接管。</summary>
        internal bool RedirectNeeded => InScene && _afterhourHidden;

        private void Log(string text)
        {
            try { LogSvc?.Log(Scenes?.Active?.FullId, text); } catch { }
        }

        // ==================== 容器窗 (拍板 6/7 同款: 真网格 + 动态行数 + 瀑布兜底) ====================

        /// <summary>grid.open_container 后端: 开物资点真容器窗, loot 开窗即真实生成
        /// (逐件 stack=1 自动寻位严禁重叠, 放不下瀑布兜底)。已开着容器 = Warn + false。</summary>
        internal bool OpenContainer(string title, List<KeyValuePair<string, int>> items,
            PsCallable onClose, Interpreter itp, int line)
        {
            if (!InScene)
            {
                PsApi.Warn(_logger, "[grid] open_container 不在任何自定义场景里调用, 已忽略");
                return false;
            }
            if (ContainerOpen)
            {
                PsApi.Warn(_logger, "[grid] open_container: 上一个容器还没关 (先 close_container)");
                return false;
            }
            // 安全: 旧容器残留先销毁 (正常不会发生 — 上面有守卫)
            DestroyItems(_containerInv);
            HideWin(ref _containerWin);
            _containerInv = null;

            var lc = Raid.RaidLayout.Active.Container;
            int cols = Raid.RaidLayout.ClampGrid(lc.W);
            int totalCells = 0;
            foreach (var e in items) totalCells += ProbeCellCount(e.Key) * Raid.RaidCore.ClampLootCount(e.Value);
            int rows = Raid.RaidLayout.ClampGrid(Raid.RaidCore.ContainerRows(totalCells, cols));
            GameGridInventory inv = null;
            try { inv = new GameGridInventory(cols, rows); }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] 容器网格构造失败: " + e.Message); }
            if (inv == null) { Log("容器打不开 (网格构造失败)"); return false; }
            _containerInv = inv;

            _containerWin = new PixelWindow(2, 2, true, title + " (关窗即搜完, 剩余销毁)");
            if (_containerWin == null)
            {
                PsApi.Warn(_logger, "[grid] 容器窗 ctor null");
                _containerInv = null;
                Log("容器打不开 (窗口构造失败)");
                return false;
            }
            try { _containerWin.Attach(inv.TryCast<PixelElement>()); }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] 容器网格入座失败: " + e.Message); }
            _containerWin.Show();
            PixelPin.PinWindow(_containerWin, lc.X, lc.Y);
            _containerGraceUntil = Time.unscaledTime + 0.5f;
            _containerOnClose = onClose;
            _containerItp = itp;

            // ---- 开窗即真实生成 loot, 逐件 stack=1 自动寻位 (严禁重叠); 放不下 → 瀑布兜底 ----
            int made = 0, fell = 0, lost = 0;
            foreach (var e in items)
            {
                string dname = ItemsFacade.DisplayName(e.Key) ?? e.Key;
                int pieces = Raid.RaidCore.ClampLootCount(e.Value);
                for (int i = 0; i < pieces; i++)
                {
                    var it = SpawnItem(e.Key);
                    if (it == null) { lost++; continue; }
                    if (TryAutoPlaceGrid(inv, it)) { made++; continue; }
                    if (TryAutoPlaceGrid(EnsureGround(), it)) { fell++; Log($"{dname} 容器塞不下, 掉到了地上"); continue; }
                    if (TryPlaceInChest(it)) { fell++; Log($"{dname} 容器塞不下, 塞进了物品箱背包"); continue; }
                    try { ItemsFacade.ConsumeItem(it); } catch { }
                    lost++;
                    Log($"{dname} 实在没地方放, 遗失了");
                    PsApi.Warn(_logger, $"[grid] 瀑布兜底销毁: {dname} (容器/地面/背包全满)");
                }
            }
            _containerSpawned = made;
            if (fell > 0) PsApi.Log(_logger, $"[grid] 容器溢出瀑布兜底 {fell} 件 (总占格 {totalCells}, 网格 {cols}x{rows})");
            if (lost > 0) PsApi.Warn(_logger, $"[grid] 容器 loot 遗失 {lost} 件 (id 缺失或全路径满)");
            if (made == 0 && fell == 0)
            {
                Log("什么也没保住, 这里已经空了");
                CloseContainer("empty");
            }
            return true;
        }

        /// <summary>关窗即搜完: 容器内剩余物品销毁 + on_close 回调 {taken, left, reason}。
        /// 返回 {taken, left}; 没有开着的容器 = null。</summary>
        internal Dictionary<string, object> CloseContainer(string reason)
        {
            if (!ContainerOpen) return null;
            int spawned = _containerSpawned;
            int left = DestroyItems(_containerInv);
            HideWin(ref _containerWin);
            _containerInv = null;
            _containerSpawned = 0;
            int taken = Math.Max(0, spawned - left);
            Log(left > 0 ? $"你离开了这里 ({left} 件剩余物资被销毁)" : "搜完了, 一点残渣都不剩");
            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["taken"] = (long)taken,
                ["left"] = (long)left,
            };
            var cb = _containerOnClose;
            var itp = _containerItp;
            _containerOnClose = null;
            _containerItp = null;
            if (cb != null && itp != null)
            {
                var arg = new Dictionary<string, object>(result, StringComparer.Ordinal)
                {
                    ["reason"] = reason ?? "",
                };
                try
                {
                    itp.BeginRun();
                    itp.CallCallable(cb, cb is ScriptFunc sf && sf.Params.Count == 0
                        ? new List<object>() : new List<object> { arg }, 0);
                }
                catch (Exception e) { PsApi.Warn(_logger, "[grid] 容器 on_close 回调异常: " + e.Message); }
            }
            return result;
        }

        // ==================== 地面网格窗 ====================

        /// <summary>grid.ground_show 后端: 地面网格窗幂等确保 (持久只建一次 — 网格物品不能随窗口重建丢)。
        /// 格数/钉位走 RaidLayout ground 区 (左缘贴物品箱右缘+gap, 箱缺失回退布局 x/y)。</summary>
        internal bool GroundShow(string title)
        {
            if (!InScene)
            {
                PsApi.Warn(_logger, "[grid] ground_show 不在任何自定义场景里调用, 已忽略");
                return false;
            }
            if (!string.IsNullOrWhiteSpace(title)) _groundTitle = title;
            return EnsureGround() != null;
        }

        private GameGridInventory EnsureGround()
        {
            if (_groundWin != null && _groundInv != null) return _groundInv;
            if (_groundInv == null)
            {
                var lg = Raid.RaidLayout.Active.Ground;
                _groundCols = Raid.RaidLayout.ClampGrid(lg.W);
                _groundRows = Raid.RaidLayout.ClampGrid(lg.H);
                try { _groundInv = new GameGridInventory(_groundCols, _groundRows); }
                catch (Exception e) { PsApi.Warn(_logger, "[grid] 地面网格构造失败: " + e.Message); }
                if (_groundInv == null) return null;
            }
            _groundWin = new PixelWindow(2, 2, true, _groundTitle);
            if (_groundWin == null) { PsApi.Warn(_logger, "[grid] 地面窗 ctor null"); _groundInv = null; return null; }
            // v1.29.1: 标题栏宽度保障 (长中文标题溢出压 X — "清"字被盖实测), 顶行垫空占位撑窗
            try { PixelPin.AttachWithTitleWidth(_groundWin, _groundInv.TryCast<PixelElement>(), _groundTitle); }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] 地面网格入座失败: " + e.Message); }
            _groundWin.Show();
            PinGroundToBox();
            return _groundInv;
        }

        private void PinGroundToBox()
        {
            if (_groundWin == null) return;
            var lg = Raid.RaidLayout.Active.Ground;
            // v1.30.0: mode="free" (M 键写回拖好的位置) → 跳过贴箱, 永远按 x/y 绝对钉位
            if (lg.Mode == "free" || _boxWin == null || !PixelPin.PinRightOf(_groundWin, _boxWin, lg.Gap))
                PixelPin.PinWindow(_groundWin, lg.X, lg.Y);
        }

        /// <summary>v1.30.0: M 键写回 (SceneService.WriteBackLayoutPins 调用) — 收集开着的图元窗
        /// 当前屏比位置进 pins (箱=中心 x + 底边 y; 地面=中心 + mode="free"; 容器开着=中心)。</summary>
        internal void CollectLayoutPins(Dictionary<string, Dictionary<string, object>> pins)
        {
            if (pins == null) return;
            if (_boxWin != null
                && PixelPin.WindowCenterScreenRel(_boxWin, out float bx, out _)
                && PixelPin.WindowBottomScreenRel(_boxWin, out _, out float by))
                pins["box"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["x"] = bx, ["y"] = by };
            if (_groundWin != null && PixelPin.WindowCenterScreenRel(_groundWin, out float gx, out float gy))
                pins["ground"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["x"] = gx, ["y"] = gy, ["mode"] = "free" };
            if (_containerWin != null && PixelPin.WindowCenterScreenRel(_containerWin, out float cx, out float cy))
                pins["container"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["x"] = cx, ["y"] = cy };
        }

        /// <summary>v1.30.0: M 写回/F10 后的即时重钉 (布局已 Reload): 箱/地面重钉 + 开着的容器也重钉
        /// (容器常态只在开窗时钉位, 这里让写回的新 container.x/y 对当前开着的窗立即生效)。</summary>
        internal void RepinFromLayout()
        {
            if (!InScene) return;
            if (_boxWin != null) PinBoxWindow();
            if (_groundWin != null) PinGroundToBox();
            if (_pocketWin != null) PinPocket();   // v1.37.0: 口袋兜底窗
            if (_containerWin != null)
            {
                var lc = Raid.RaidLayout.Active.Container;
                PixelPin.PinWindow(_containerWin, lc.X, lc.Y);
            }
        }

        /// <summary>grid.ground_count 后端: 地面物品件数 (不在场景/未建 = 0)。</summary>
        internal int GroundCount()
        {
            if (_groundInv == null) return 0;
            try { return ItemsFacade.SlotItems(_groundInv).Count; } catch { return 0; }
        }

        /// <summary>grid.ground_clear 后端: 地面物品全部销毁 (前进/到底自动清空同款) → 销毁件数。</summary>
        internal int GroundClear() => DestroyItems(_groundInv);

        /// <summary>grid.ground_steal 后端 (v1.45.0, gunworks v0.40.0 §4.6 掠窃者): 随机拿走地面 1 件
        /// (ConsumeItem 销毁, 与地面清空同路径) → {id, count} (count=该件堆叠数, 缺省 1); 地面空 = null。</summary>
        internal Dictionary<string, object> GroundSteal()
        {
            if (_groundInv == null) return null;
            List<GameItem> items = null;
            try { items = ItemsFacade.SlotItems(_groundInv); } catch { }
            if (items == null || items.Count == 0) return null;
            var it = items[_rng.Next(items.Count)];
            string id = null;
            int count = 1;
            try { id = it.identifier; } catch { }
            try { count = Math.Max(1, (int)it.unitCount); } catch { }
            try { ItemsFacade.ConsumeItem(it); } catch { }
            if (string.IsNullOrWhiteSpace(id)) return null;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = id, ["count"] = (long)count,
            };
        }

        /// <summary>grid.ground_place 后端: 放 N 件物品到地面 (逐件 stack=1 自动寻位;
        /// 地面满 → 瀑布入物品箱胸式槽 → 再满销毁)。返回 {ground, chest, lost}。</summary>
        internal Dictionary<string, object> GroundPlace(string itemId, int count)
        {
            long ground = 0, chest = 0, lost = 0;
            if (!InScene) return new Dictionary<string, object> { ["ground"] = ground, ["chest"] = chest, ["lost"] = (long)count };
            var inv = EnsureGround();
            string dname = ItemsFacade.DisplayName(itemId) ?? itemId;
            int pieces = Raid.RaidCore.ClampLootCount(count);
            for (int i = 0; i < pieces; i++)
            {
                var it = SpawnItem(itemId);
                if (it == null) { lost++; break; }   // id 缺失已 Warn, 同 id 后续件不必再试
                if (TryAutoPlaceGrid(inv, it)) { ground++; continue; }
                if (TryPlaceInChest(it)) { chest++; Log($"{dname} 地面放不下, 塞进了物品箱背包"); continue; }
                try { ItemsFacade.ConsumeItem(it); } catch { }
                lost++;
                Log($"{dname} 实在没地方放, 遗失了");
                PsApi.Warn(_logger, $"[grid] 地面放置瀑布兜底销毁: {dname} (地面/背包全满)");
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ground"] = ground, ["chest"] = chest, ["lost"] = lost,
            };
        }

        // ==================== 物品箱跟随 (拍板 5 同款: 只 Show/Hide/钉位, 结构恒定) ====================

        /// <summary>grid.box_follow 后端: 找玩家拥有的机器 (默认旅行物品箱) → 面板 Show + 底边锚点钉位。
        /// 找不到/面板不可用 → Warn + 口袋兜底窗 (v1.37.0, 撤离时内容自动搬称重台) + false (箱槽门控仍可用 _boxItem)。</summary>
        internal bool BoxFollow(string machineId)
        {
            if (!InScene)
            {
                PsApi.Warn(_logger, "[grid] box_follow 不在任何自定义场景里调用, 已忽略");
                return false;
            }
            _boxItem = null;
            _boxWin = null;
            _boxPinW = -1f; _boxPinH = -1f;
            try { _boxItem = ItemsFacade.FindMachine(machineId); }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] 查找物品箱异常: " + e.Message); }
            if (_boxItem == null)
            {
                PsApi.Warn(_logger, $"[grid] 未找到玩家拥有的物品箱 ({machineId}), 降级为口袋兜底窗");
                EnsurePocket();
                return false;
            }
            try { _boxWin = _boxItem.contentWindow; } catch { }
            if (_boxWin == null)
            {
                PsApi.Warn(_logger, "[grid] 物品箱面板窗口不可用 (contentWindow=null), 降级为口袋兜底窗");
                EnsurePocket();
                return false;
            }
            try
            {
                _boxWin.Show();
                PinBoxWindow();
                PsApi.Log(_logger, "[grid] 物品箱面板已显示并钉位");
                return true;
            }
            catch (Exception e)
            {
                PsApi.Warn(_logger, "[grid] 物品箱面板显示失败, 降级为口袋兜底窗: " + e.Message);
                _boxWin = null;
                EnsurePocket();
                return false;
            }
        }

        /// <summary>grid.box_show 后端: 战斗锁等场景显隐物品箱窗 (数据层不动)。
        /// v1.37.0: 口袋兜底窗随同显隐 (没带箱时它是唯一随身容器); 任一窗受影响 = true。</summary>
        internal bool BoxShow(bool visible)
        {
            bool any = false;
            if (_boxWin != null)
            {
                try
                {
                    if (visible) { _boxWin.Show(); PinBoxWindow(); PinGroundToBox(); }
                    else _boxWin.Hide();
                    any = true;
                }
                catch { }
            }
            if (_pocketWin != null)
            {
                try
                {
                    if (visible) { _pocketWin.Show(); PinPocket(); }
                    else _pocketWin.Hide();
                    any = true;
                }
                catch { }
            }
            return any;
        }

        /// <summary>v1.29.0 实时战斗锁: 地面网格窗显隐 (Hide/Show 配对; 未建窗 = false)。
        /// 与 BoxShow 同规 — 数据层不动, Show 时重新钉位。</summary>
        internal bool SetGroundVisible(bool visible)
        {
            if (_groundWin == null) return false;
            try
            {
                if (visible) { _groundWin.Show(); PinGroundToBox(); }
                else _groundWin.Hide();
                return true;
            }
            catch { return false; }
        }

        /// <summary>v1.29.1: 箱窗 rect 尺寸相对上次钉位时是否变化 (>1px) — 机器面板排版
        /// 稳定检测; 未钉过 (-1) = false。</summary>
        private bool BoxSizeChanged()
        {
            if (_boxPinW < 0f) return false;
            try
            {
                var rt = _boxWin.rectTransform;
                if (rt is null) return false;
                var sz = rt.rect.size;
                return Math.Abs(sz.x - _boxPinW) > 1f || Math.Abs(sz.y - _boxPinH) > 1f;
            }
            catch { return false; }
        }

        private void PinBoxWindow()
        {
            if (_boxWin == null) return;
            var lb = Raid.RaidLayout.Active.Box;
            PixelPin.PinWindowBottom(_boxWin, lb.X, lb.Y);
            try { var rt = _boxWin.rectTransform; if (!(rt is null)) { _boxPinW = rt.rect.width; _boxPinH = rt.rect.height; } } catch { }
        }

        // ==================== 口袋兜底窗 (v1.37.0) ====================

        /// <summary>口袋兜底窗幂等确保 (box_follow 失败 = 没带物品箱的降级: 独立小网格窗,
        /// 格数/钉位走 RaidLayout pocket 区, 默认 2x3 贴原箱位底边锚点; 持久只建一次同地面窗 —
        /// 网格物品不能随窗口重建丢; 撤离时内容自动搬到店里称重台, 见 FlushPocketToCounter)。</summary>
        internal GameGridInventory EnsurePocket()
        {
            if (_pocketWin != null && _pocketInv != null) return _pocketInv;
            if (_pocketInv == null)
            {
                var lp = Raid.RaidLayout.Active.Pocket;
                try { _pocketInv = new GameGridInventory(Raid.RaidLayout.ClampGrid(lp.W), Raid.RaidLayout.ClampGrid(lp.H)); }
                catch (Exception e) { PsApi.Warn(_logger, "[grid] 口袋网格构造失败: " + e.Message); }
                if (_pocketInv == null) return null;
            }
            _pocketWin = new PixelWindow(2, 2, true, "口袋");
            if (_pocketWin == null) { PsApi.Warn(_logger, "[grid] 口袋窗 ctor null"); _pocketInv = null; return null; }
            try { PixelPin.AttachWithTitleWidth(_pocketWin, _pocketInv.TryCast<PixelElement>(), "口袋"); }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] 口袋网格入座失败: " + e.Message); }
            _pocketWin.Show();
            PinPocket();
            return _pocketInv;
        }

        private void PinPocket()
        {
            if (_pocketWin == null) return;
            var lp = Raid.RaidLayout.Active.Pocket;
            PixelPin.PinWindowBottom(_pocketWin, lp.X, lp.Y);
        }

        /// <summary>口袋内容搬到店里称重台 (v1.37.0 撤离兜底: 保 NBT 实例搬移, 逐件 MoveToCounter,
        /// 摆柜失败内部兜底退回后仓) → 搬动件数; 口袋空/未建 = 0 (不写日志)。</summary>
        private int FlushPocketToCounter()
        {
            if (_pocketInv == null) return 0;
            List<GameItem> items = null;
            try { items = ItemsFacade.SlotItems(_pocketInv); } catch { }
            if (items == null || items.Count == 0) return 0;
            int n = 0;
            foreach (var it in items)
            {
                try { if (ItemsFacade.MoveToCounter(it)) n++; }
                catch (Exception e) { PsApi.Warn(_logger, "[grid] 口袋物品搬称重台失败: " + e.Message); }
            }
            Log($"口袋里的 {n} 件东西放到了店里称重台上");
            PsApi.Log(_logger, $"[grid] 口袋撤离兜底: {n}/{items.Count} 件搬到称重台");
            return n;
        }

        /// <summary>grid.box_slot 后端: 读物品箱机器槽位物品 (s_main/s_melee/s_throw1-3/s_chest)。</summary>
        internal GameItem BoxSlot(string slotId)
        {
            if (_boxItem == null || Ui == null || string.IsNullOrEmpty(slotId)) return null;
            try { return Ui.TryMachineSlotItem(_boxItem, slotId); } catch { return null; }
        }

        /// <summary>grid.box_has 后端: 物品箱树递归检定 (机器槽/胸式槽背包内容都挂 childItems)。</summary>
        internal bool BoxHas(string itemId) => ItemTreeHas(_boxItem, itemId);

        /// <summary>箱槽递归检定 (FindAllChildItems = 物品全部后代, Shift+F12 容器图探针实证)。</summary>
        internal static bool ItemTreeHas(GameItem item, string id)
        {
            if (item == null || string.IsNullOrEmpty(id)) return false;
            try { if (string.Equals(item.identifier, id, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
            Il2CppSystem.Collections.Generic.List<GameItem> children = null;
            try { children = item.FindAllChildItems(); } catch { }
            if (children == null) return false;
            foreach (var c in children)
            {
                if (c == null) continue;
                try { if (string.Equals(c.identifier, id, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
            }
            return false;
        }

        // ==================== 原版外出物品栏 ====================

        /// <summary>grid.afterhour_has 后端: 外出栏原版 IsAfterhourHaveOwnedItems 范式检定。</summary>
        internal bool AfterhourHas(string itemId)
        {
            try
            {
                var ee = EmporiumEntry.Instance;
                if (ee != null && ee.IsAfterhourHaveOwnedItems(itemId)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>grid.has_tool 后端: 工具门控双源 (外出栏 + 物品箱树递归)。</summary>
        internal bool HasTool(string toolId) => AfterhourHas(toolId) || BoxHas(toolId);

        /// <summary>grid.hide_afterhour 后端: 场景内隐藏原版外出物品栏 (只动显隐; 数据层门控不受影响)。
        /// 藏/还配对: 场景撤离自动恢复, Poll 压回外部复活。</summary>
        internal bool HideAfterhour()
        {
            if (!InScene)
            {
                PsApi.Warn(_logger, "[grid] hide_afterhour 不在任何自定义场景里调用, 已忽略");
                return false;
            }
            try
            {
                var aw = EmporiumEntry.Instance?.afterhourWindow;
                if (aw != null && aw.IsVisible())
                {
                    aw.Hide();
                    _afterhourHidden = true;
                    PsApi.Log(_logger, "[grid] 原版外出物品栏已隐藏 (场景内不显示, 撤离恢复)");
                    return true;
                }
            }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] 外出物品栏隐藏失败: " + e.Message); }
            return false;
        }

        private void RestoreAfterhour()
        {
            if (!_afterhourHidden) return;
            _afterhourHidden = false;
            try
            {
                var aw = EmporiumEntry.Instance?.afterhourWindow;
                if (aw != null && !aw.IsVisible()) aw.Show();
                PsApi.Log(_logger, "[grid] 原版外出物品栏已恢复显示");
            }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] 外出物品栏恢复失败: " + e.Message); }
        }

        /// <summary>grid.afterhour_place 后端: 放物品进外出背包 (CreateIntoSlot 同 RollEvent 捡东西路径)。</summary>
        internal bool AfterhourPlace(string itemId, int count)
        {
            GameGridInventory afterhour = null;
            try { afterhour = EmporiumEntry.Instance?.afterhourInventory; } catch { }
            if (afterhour == null) return false;
            try { return ItemsFacade.CreateIntoSlot(afterhour, itemId, count) != null; } catch { return false; }
        }

        // ==================== 物品生成 / 自动寻位 / 瀑布兜底 (v1.25.0 实证同款) ====================

        /// <summary>建一个未入格的新物品实例 (永远单件 stack=1); 失败 = null + Warn。</summary>
        internal GameItem SpawnItem(string itemId)
        {
            try
            {
                var item = DirectoryMaster.Item(ItemsFacade.NormalizeId(itemId), true);
                if (item == null) { PsApi.Warn(_logger, "[grid] 物品 id 不存在: " + itemId); return null; }
                return item;
            }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] 物品生成失败 " + itemId + ": " + e.Message); return null; }
        }

        /// <summary>自动寻位入网格 (严禁重叠): TryFindOneValidInventorySlot → SlotMarker.AcceptUnchecked。</summary>
        internal bool TryAutoPlaceGrid(GameGridInventory grid, GameItem item)
        {
            if (grid == null || item == null) return false;
            try
            {
                SlotMarker marker = null;
                try { marker = grid.TryFindOneValidInventorySlot(item, false); } catch { }
                if (marker == null) return false;
                marker.AcceptUnchecked();
                return true;
            }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] 自动寻位入格失败: " + e.Message); return false; }
        }

        /// <summary>瀑布第二级: 入物品箱胸式槽 (s_chest) 背包 (GameItem 版寻位)。</summary>
        internal bool TryPlaceInChest(GameItem item)
        {
            if (item == null) return false;
            try
            {
                var chest = BoxSlot("s_chest");
                if (chest == null) return false;
                SlotMarker marker = null;
                try { marker = chest.TryFindOneValidInventorySlot(item); } catch { }
                if (marker == null) return false;
                marker.AcceptUnchecked();
                return true;
            }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] 物品入物品箱背包失败: " + e.Message); return false; }
        }

        /// <summary>探针测占格: 建临时实例读 GridShape 占用格数, 用完即销毁; 任何失败 = 1 (宁多勿少)。</summary>
        internal int ProbeCellCount(string itemId)
        {
            GameItem probe = null;
            try
            {
                probe = DirectoryMaster.Item(ItemsFacade.NormalizeId(itemId), true);
                if (probe == null) return 1;
                return ItemCellCount(probe);
            }
            catch { return 1; }
            finally
            {
                if (probe != null) try { ItemsFacade.ConsumeItem(probe); } catch { }
            }
        }

        /// <summary>物品 GridShape 占用格数 (InventorySorter 同款)。</summary>
        internal static int ItemCellCount(GameItem item)
        {
            try
            {
                GridShape shape = null;
                try { shape = item.modifiedShape; } catch { }
                if (shape == null) { try { shape = item.shape; } catch { } }
                if (shape == null) return 1;
                int n = 0;
                for (int y = shape.minY; y <= shape.maxY; y++)
                    for (int x = shape.minX; x <= shape.maxX; x++)
                        if (shape.Get(x, y) != 0) n++;
                return Math.Max(1, n);
            }
            catch { return 1; }
        }

        /// <summary>销毁网格内全部物品 (Expel 出所有所属库存 + DestroyGameItems); 返回销毁件数。</summary>
        internal int DestroyItems(GameInventory inv)
        {
            if (inv == null) return 0;
            List<GameItem> items = null;
            try { items = ItemsFacade.SlotItems(inv); } catch { }
            if (items == null || items.Count == 0) return 0;
            int n = 0;
            foreach (var it in items)
            {
                try { if (ItemsFacade.ConsumeItem(it)) n++; }
                catch (Exception e) { PsApi.Warn(_logger, "[grid] 物品销毁失败: " + e.Message); }
            }
            return n;
        }

        // ==================== ctrl+左键快捷转移重定向 (v1.25.2 同款) ====================

        /// <summary>快捷转移重定向 (QuickTransferPatches 门控后调用): 目标改为地面窗 (物品已在地面窗上
        /// 时改为物品箱胸式背包), 保持"ctrl+左键 = 送到另一边"; 放不下/找不到目标 = 拦截+信息栏提示,
        /// 物品原地不动。返回 true = 已接管 (无论成败原版逻辑都必须跳过 — 原版目标是被 Hide 的外出栏)。</summary>
        internal bool TryQuickTransferRedirect(GameItem currentItem)
        {
            if (!RedirectNeeded) return false;
            var elem = RenderHandler.RaycastElement<GameItemElement>(UnityEngine.Input.mousePosition, null);
            if (elem is null) return true;
            if (currentItem is null || elem.Pointer != currentItem.Pointer) return true;
            GameItem item = elem;
            bool mayRemove = false;
            try { mayRemove = item.MayRemove(); } catch { }
            if (!mayRemove)
            {
                Log("快捷转移: 这个物品现在拿不动");
                return true;
            }
            PixelWindow parentWin = null;
            try { parentWin = elem.TryCast<PixelElement>()?.handler?.FindParentWindow(); } catch { }
            bool fromGround = parentWin != null && _groundWin != null && parentWin.Pointer == _groundWin.Pointer;
            if (fromGround)
            {
                bool boxVisible = false;
                try { boxVisible = _boxWin != null && _boxWin.IsVisible(); } catch { }
                if (!boxVisible) { Log("快捷转移: 物品箱现在不可用, 已取消 (物品原地不动)"); return true; }
                if (QuickMoveItem(item, true)) { Log("快捷转移: 已放回物品箱"); return true; }
                Log("快捷转移: 物品箱背包放不下, 已取消 (物品原地不动)");
                return true;
            }
            if (QuickMoveItem(item, false)) { Log("快捷转移: 已送到地面"); return true; }
            Log("快捷转移: 地面放不下, 已取消 (物品原地不动)");
            return true;
        }

        /// <summary>快捷转移直插移动 (v1.25.2): 目标先寻位 (拿不到位 = false, 物品原地不动) →
        /// Expel 出全部所属库存 → AcceptUnchecked 落位。toChest=true 入物品箱胸式槽背包;
        /// false 入地面网格。</summary>
        private bool QuickMoveItem(GameItem item, bool toChest)
        {
            SlotMarker marker = null;
            if (toChest)
            {
                GameItem chest = null;
                try { chest = BoxSlot("s_chest"); } catch { }
                if (chest == null) return false;
                try { marker = chest.TryFindOneValidInventorySlot(item); } catch { }
            }
            else
            {
                if (_groundInv == null) EnsureGround();
                if (_groundInv == null) return false;
                try { marker = _groundInv.TryFindOneValidInventorySlot(item, false); } catch { }
            }
            if (marker == null) return false;
            try
            {
                var parents = item.parents;
                if (parents != null)
                    for (int i = parents.Count - 1; i >= 0; i--)
                    {
                        var p = parents[i];
                        if (p == null) continue;
                        GameInventory inv = null;
                        try { inv = p.TryCast<GameInventory>(); } catch { }
                        if (inv != null) { try { inv.Expel(item); } catch { } }
                    }
            }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] 快捷转移出源异常: " + e.Message); }
            try { marker.AcceptUnchecked(); return true; }
            catch (Exception e)
            {
                PsApi.Warn(_logger, "[grid] 快捷转移落位异常 (物品已出源, 尝试瀑布回插): " + e.Message);
                if (!toChest && TryPlaceInChest(item)) return true;
                if (toChest && TryAutoPlaceGrid(_groundInv, item)) return true;
                PsApi.Warn(_logger, "[grid] 快捷转移瀑布回插也失败, 物品可能处于游离状态, 请玩家检查地面/背包");
                return false;
            }
        }

        // ==================== Poll / 生命周期 ====================

        internal void Poll()
        {
            if (!InScene) return;
            // ---- 容器窗被 X 关掉 → 走关窗即搜完 (0.5s 宽限防 IsVisible 过渡态误报) ----
            if (_containerWin != null)
            {
                bool vis = false;
                try { vis = _containerWin.IsVisible(); } catch { }
                if (!vis && Time.unscaledTime >= _containerGraceUntil) CloseContainer("x");
            }
            // ---- 健康检查 (节流 0.5s): 地面窗/物品箱被关重开+钉回; 外出栏保持隐藏 ----
            _uiHealthAcc += Time.unscaledDeltaTime;
            if (_uiHealthAcc < 0.5f) return;
            _uiHealthAcc = 0f;
            try
            {
                if (Time.unscaledTime < _uiGraceUntil) return;
                if (_containerWin != null) return;   // 容器开关由上面分支处理
                if (_groundWin != null && !WinVisible(_groundWin))
                {
                    try { _groundWin.Show(); PinGroundToBox(); } catch { }
                }
                if (_pocketWin != null && !WinVisible(_pocketWin))   // v1.37.0: 口袋兜底窗被关重开+钉回
                {
                    try { _pocketWin.Show(); PinPocket(); } catch { }
                }
                if (_boxWin != null && !WinVisible(_boxWin))
                {
                    try { _boxWin.Show(); PinBoxWindow(); PinGroundToBox(); } catch { }
                }
                else if (_boxWin != null && BoxSizeChanged())
                {
                    // v1.29.1: 机器面板排版延迟 (Show 当帧 rect 仍是 ctor 迷你尺寸, 钉位用错半高/半宽
                    // → 整窗偏右下沉出屏幕底, "已装备"行被裁实测) — 尺寸稳定后自动重钉一次
                    try { PinBoxWindow(); PinGroundToBox(); } catch { }
                }
                if (_afterhourHidden)
                {
                    try
                    {
                        var aw = EmporiumEntry.Instance?.afterhourWindow;
                        if (aw != null && aw.IsVisible()) aw.Hide();
                    }
                    catch { }
                }
            }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] UI 健康检查异常: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>RefreshUI 宽限标记 (pss 任何 grid 操作后调用, 防健康检查过渡态误动)。</summary>
        internal void MarkUiGrace() { _uiGraceUntil = Time.unscaledTime + 0.5f; }

        /// <summary>场景进入 (DoEnter): 防御性清残留 (崩溃后重进保险; 正常为空)。</summary>
        internal void OnSceneEnter()
        {
            try
            {
                if (ContainerOpen || _groundWin != null || _groundInv != null || _boxWin != null
                    || _pocketWin != null || _pocketInv != null || _afterhourHidden)
                {
                    PsApi.Warn(_logger, "[grid] 进入场景发现残留状态, 防御性清理");
                    CleanupRuntime();
                }
            }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] OnSceneEnter 清理异常: " + e.Message); }
        }

        /// <summary>场景撤离 (DoExit): 容器/地面遗留物品全部销毁 (任何离开路径都清, 防孤儿真物品脏档)
        /// + 口袋内容搬到店里称重台 (v1.37.0 口袋兜底, 保 NBT) + 窗口隐藏 + 外出栏恢复 + 回调引用清。</summary>
        internal void OnSceneExit()
        {
            try
            {
                DestroyItems(_containerInv);
                DestroyItems(_groundInv);
                FlushPocketToCounter();                 // v1.37.0: 口袋里的东西放到店里称重台 (毁窗之前)
                HideWin(ref _containerWin); _containerInv = null; _containerSpawned = 0;
                HideWin(ref _groundWin); _groundInv = null;
                HideWin(ref _pocketWin); _pocketInv = null;
                _containerOnClose = null; _containerItp = null;
                try { if (_boxWin != null) _boxWin.Hide(); } catch { }
                _boxWin = null; _boxItem = null; _boxPinW = -1f; _boxPinH = -1f;
                RestoreAfterhour();
            }
            catch (Exception e) { PsApi.Warn(_logger, "[grid] OnSceneExit 清理异常: " + e.Message); }
        }

        /// <summary>Unity 场景卸载: 运行时对象已销毁, 只清引用。</summary>
        internal void OnSceneLeft()
        {
            _afterhourHidden = false;
            _containerWin = null; _containerInv = null; _containerSpawned = 0;
            _containerOnClose = null; _containerItp = null;
            _groundWin = null; _groundInv = null;
            _pocketWin = null; _pocketInv = null;
            _boxWin = null; _boxItem = null; _boxPinW = -1f; _boxPinH = -1f;
        }

        private void CleanupRuntime()
        {
            DestroyItems(_containerInv);
            DestroyItems(_groundInv);
            FlushPocketToCounter();                     // v1.37.0: 残留口袋内容同样搬称重台 (不销毁)
            HideWin(ref _containerWin); _containerInv = null; _containerSpawned = 0;
            HideWin(ref _groundWin); _groundInv = null;
            HideWin(ref _pocketWin); _pocketInv = null;
            _containerOnClose = null; _containerItp = null;
            try { if (_boxWin != null) _boxWin.Hide(); } catch { }
            _boxWin = null; _boxItem = null; _boxPinW = -1f; _boxPinH = -1f;
            RestoreAfterhour();
        }

        private static bool WinVisible(PixelWindow w)
        {
            if (w == null) return false;
            try { return w.IsVisible(); } catch { return false; }
        }

        private static void HideWin(ref PixelWindow w)
        {
            try { if (w != null) w.Hide(); } catch { }
            w = null;
        }
    }
}
