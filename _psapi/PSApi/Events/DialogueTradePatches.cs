using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;

namespace PSApi.Events
{
    /// <summary>
    /// E7 补丁事件: dialogue_choice / trade_completed (官方 ModHook 无对应事件, 只能补丁)。
    /// 补丁点字节数经 Cpp2IL ISIL 反汇编核对 (UserData/cpp2il_isil/IsilDump/Assembly-CSharp/):
    ///   DialogUIManager.SelectChoice  数百字节(完整选项结算流程)   — 安全
    ///   PlayerStore.OnItemBought      数百字节                       — 安全 (玩家从客户买入; 内部转调 StoreClient.OnItemSold)
    ///   PlayerStore.SellItem          ~230 行 ISIL                   — 安全 (玩家卖给客户; 内部转调 StoreClient.OnItemBought)
    ///   StoreClient.OnItemBought      ~26 字节纯转发                 — 不 patch (40 字节红线), 走 PlayerStore.SellItem 代替
    /// 事件 dict:
    ///   psapi.dialogue.choice  { dialogue_id, title, choice, npc_id, npc_name }
    ///     dialogue_id: 脚本包 npc.register 对话注册了 id 的 = "包:配置id" (NpcService.DialogueIds 按 Dialogue 指针查);
    ///     其余(原版/NpcManager 等) = Dialogue.title(说话人名)。
    ///   psapi.trade.completed  { direction("buy"=玩家买入/"sell"=玩家卖出), item_id, amount, npc_id, npc_name }
    ///     柜台议价成交路径; 桌面直接卖(SellItemFromTable 内部转调 SellItem, v1.13.4 起深度计闸门拦下)不触发。
    ///     本构建成交方法按探测+提交双调(见 PublishTrade 上方注释) → 同物品指针+同金额 1s 窗口去重, 一笔一发。
    /// </summary>
    internal static class DialogueTradePatches
    {
        // ---- dialogue_choice: SelectChoice 会原地换 currentDialog 为后续对话, 故 Prefix 捕获 + Postfix 发布 ----

        private static bool _choiceValid;
        private static string _choiceDlgId;
        private static string _choiceTitle;
        private static int _choiceIndex;

        [HarmonyPatch(typeof(DialogUIManager), "SelectChoice")]
        private static class PatchDialogueChoice
        {
            private static void Prefix(DialogUIManager __instance, int index)
            {
                _choiceValid = false;
                try
                {
                    var dlg = __instance is null ? null : __instance.currentDialog;
                    if (dlg is null) return;
                    var choices = dlg.choices;
                    if (choices is null || index < 0 || index >= choices.Count) return;
                    _choiceTitle = dlg.title;
                    _choiceDlgId = null;
                    try
                    {
                        if (NpcService.DialogueIds.TryGetValue(dlg.Pointer.ToInt64(), out var regId))
                            _choiceDlgId = regId;
                    }
                    catch { }
                    _choiceIndex = index;
                    _choiceValid = true;
                }
                catch { }
            }

            private static void Postfix()
            {
                if (!_choiceValid) return;
                _choiceValid = false;
                try
                {
                    var bus = EventsPlugin.Bus;
                    if (bus == null) return;
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["dialogue_id"] = _choiceDlgId ?? _choiceTitle ?? "",
                        ["title"] = _choiceTitle ?? "",
                        ["choice"] = (long)_choiceIndex,
                    };
                    FillCurrentClient(dict);
                    bus.Publish("psapi.dialogue.choice", dict);
                }
                catch (Exception e) { EventsPlugin.LogWarn("dialogue_choice publish failed: " + e.Message); }
            }
        }

        // ---- trade_completed ----

        /// <summary>玩家从客户买入 (PlayerStore.OnItemBought → StoreClient.OnItemSold)。</summary>
        [HarmonyPatch(typeof(PlayerStore), "OnItemBought")]
        private static class PatchTradeBuy
        {
            private static void Postfix(GameItem gameItem, int cost)
                => PublishTrade("buy", gameItem, cost);
        }

        /// <summary>玩家卖给客户 (PlayerStore.SellItem → StoreClient.OnItemBought; 金额取物品已议价)。
        /// v1.13.4: 桌面直卖(SellItemFromTable 内部转调 SellItem, 9-18 ISIL 实证)不再触发 —
        /// 与文档承诺一致(议价成交才发 trade_completed; 直卖金额未议价, 发了会误导声望结算)。</summary>
        [HarmonyPatch(typeof(PlayerStore), "SellItem")]
        private static class PatchTradeSell
        {
            private static void Postfix(GameItem item)
            {
                if (_fromTableDepth > 0) return; // 桌面直卖路径: 不发
                long amount = 0;
                try { if (!(item is null)) amount = item.GetNegociatedValue(); } catch { }
                PublishTrade("sell", item, amount);
            }
        }

        // ---- 桌面直卖识别(v1.13.4): SellItemFromTable 方法体内转调 SellItem, 深度计圈出转调窗口 ----
        // 用 Finalizer 而非 Postfix 收深度: 方法抛异常也必收(返回原异常不吞)。
        private static int _fromTableDepth;

        [HarmonyPatch(typeof(PlayerStore), "SellItemFromTable")]
        private static class PatchSellFromTable
        {
            private static void Prefix() => _fromTableDepth++;
            private static Exception Finalizer(Exception __exception) { _fromTableDepth--; return __exception; }
        }

        // ---- 双发抑制 ----
        // 实测(2026-09-18): 本构建的成交链路方法按"探测+提交"模式被游戏双调 —— OnItemBought/SellItem
        // 每笔成交各调两次(同物品同金额, 间隔 ~1ms); 同款双调遍布 TryAcceptOnce/GetNextClient/OnLateShutterOpen
        // (各版本日志可查, E7 之前即存在, 非补丁重复)。静态调用图每条托管路径只调一次, 无干净的替代挂载点
        // (上游 UI 入口 5+ 个且同样被双调; 下游 StoreClient.OnItemSold/OnItemBought 是 ~26B 转发器触红线)。
        // 同一 GameItem 实例不可能被合法成交两次(所有权已转移) → 同方向+同物品指针+同金额, 1s 窗口内只发一次。
        // 批发 N 件是 N 个不同实例, 不受影响; 连续买两件同 id 物品也是不同实例, 不受影响。
        private static string _lastDir;
        private static long _lastItemPtr;
        private static long _lastAmount;
        private static long _lastTickMs = -1;

        private static bool IsDuplicateTrade(string direction, long ptr, long amount)
        {
            if (ptr == 0) return false; // 拿不到指针的物品不去重, 宁多勿漏
            long now = Environment.TickCount64;
            if (direction == _lastDir && ptr == _lastItemPtr && amount == _lastAmount && now - _lastTickMs < 1000)
                return true;
            _lastDir = direction; _lastItemPtr = ptr; _lastAmount = amount; _lastTickMs = now;
            return false;
        }

        private static void PublishTrade(string direction, GameItem item, long amount)
        {
            try
            {
                var bus = EventsPlugin.Bus;
                if (bus == null) return;
                long ptr = 0;
                string itemId = "";
                try { if (!(item is null)) { ptr = item.Pointer.ToInt64(); itemId = item.identifier ?? ""; } } catch { }
                if (IsDuplicateTrade(direction, ptr, amount)) return;
                var dict = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["direction"] = direction,
                    ["item_id"] = itemId,
                    ["amount"] = amount,
                };
                FillCurrentClient(dict);
                bus.Publish("psapi.trade.completed", dict);
            }
            catch (Exception e) { EventsPlugin.LogWarn("trade_completed publish failed: " + e.Message); }
        }

        /// <summary>当前柜台客户 → npc_id/npc_name (无当前客户 = 空串, 字段恒存在)。</summary>
        private static void FillCurrentClient(Dictionary<string, object> dict)
        {
            string id = "", name = "";
            try
            {
                var ps = PlayerStore.instance; // 静态字段
                var sc = ps is null ? null : ps.currentClientInstance?.storeClient;
                if (!(sc is null)) { id = sc.identifier ?? ""; name = sc.displayName ?? ""; }
            }
            catch { }
            dict["npc_id"] = id;
            dict["npc_name"] = name;
        }
    }
}
