using System;
using System.Collections.Generic;
using Il2Cpp;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// E4 客户句柄: 包装 Il2Cpp StoreClient, 随 customer_generated 事件发给脚本(event.client)。
    /// 读: id/name/faction/cash/intent; 方法: set_cash(n) / say(channel, text)。
    /// 所有访问现取现用(不缓存字段), 客户对象失效(指针空)给友好运行错误。
    /// 注意: 引用 Il2Cpp 类型, 仅游戏进程内实例化; 无头测试台用 PsHandle 假实现覆盖分派逻辑。
    /// </summary>
    internal sealed class PsClientHandle : PsHandle
    {
        /// <summary>say 通道 → StoreClient 对话字段(与 NpcManager 九通道一致)。</summary>
        private static readonly Dictionary<string, int> Channels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["main"] = 0, ["accept"] = 1, ["all_done"] = 2, ["repeat"] = 3,
            ["wrong_item"] = 4, ["right_item"] = 5, ["interogation"] = 6,
            ["glasse"] = 7, ["on_arrest"] = 8,
        };

        private readonly StoreClient _sc;

        internal PsClientHandle(StoreClient sc) { _sc = sc; }

        internal override string Kind => "client";

        /// <summary>M1：注入服务需原始 StoreClient（买池/货架通道判定）。</summary>
        internal StoreClient Raw => _sc;

        private StoreClient Need(int line)
        {
            if (_sc == null)
                throw new PsRuntimeError("客户句柄已失效(客户对象不存在)", line);
            return _sc;
        }

        internal override object GetMember(string name, int line)
        {
            switch (name)
            {
                case "id":
                    try { return Need(line).identifier; } catch (Exception e) { throw new PsRuntimeError("client.id 读取失败: " + e.Message, line); }
                case "name":
                    try { return Need(line).displayName; } catch (Exception e) { throw new PsRuntimeError("client.name 读取失败: " + e.Message, line); }
                case "faction":
                    try { return Need(line).clientFaction; } catch (Exception e) { throw new PsRuntimeError("client.faction 读取失败: " + e.Message, line); }
                case "cash":
                    try { return (long)Need(line).clientCash; } catch (Exception e) { throw new PsRuntimeError("client.cash 读取失败: " + e.Message, line); }
                case "intent":
                    try { return Need(line).clientIntent.ToString(); } catch (Exception e) { throw new PsRuntimeError("client.intent 读取失败: " + e.Message, line); }
                case "set_cash":
                    return PsBuiltins.BF("client.set_cash", (itp, a, ln) =>
                    {
                        PsBuiltins.Need(a, 1, 1, "client.set_cash(amount)", ln);
                        long amount = a[0] is long l ? l : a[0] is double d ? (long)d
                            : throw new PsRuntimeError($"client.set_cash 的参数须为数字, 实为 {PsValues.TypeName(a[0])}", ln);
                        try { Need(ln).clientCash = (int)amount; }
                        catch (Exception e) { throw new PsRuntimeError("client.set_cash 失败: " + e.Message, ln); }
                        return null;
                    });
                case "say":
                    return PsBuiltins.BF("client.say", (itp, a, ln) =>
                    {
                        PsBuiltins.Need(a, 2, 2, "client.say(channel, text)", ln);
                        string ch = a[0] as string;
                        if (string.IsNullOrEmpty(ch) || !Channels.TryGetValue(ch, out int kind))
                            throw new PsRuntimeError($"client.say 未知通道 '{a[0]}'(可用: {string.Join("/", Channels.Keys)})", ln);
                        Say(Need(ln), kind, PsValues.Fmt(a[1]), ln);
                        return null;
                    });
                default:
                    throw new PsRuntimeError(
                        $"客户句柄无成员 '{name}'(可用: id/name/faction/cash/intent/set_cash/say)", line);
            }
        }

        /// <summary>改对话通道文本(数组通道直接整段替换为单条; accept 同时写 LastLine 变体)。</summary>
        private static void Say(StoreClient sc, int kind, string text, int line)
        {
            try
            {
                var d = new Dialogue();
                d.SetText(sc.displayName, text);
                switch (kind)
                {
                    case 0: sc.mainDialogue = d; break;
                    case 1:
                        sc.acceptDealDialogue = d;
                        sc.acceptDealDialogueLastLine = d;
                        sc.acceptDealDialoguePlayed = false;
                        break;
                    case 2: sc.allDoneDialogue = d; break;
                    case 3: sc.repeatDialogue = d; break;
                    case 4: sc.placedWrongItemWhenSellingToDialogue = d; break;
                    case 5: sc.placeRightItemWhenSellingToDialogue = d; break;
                    case 6: sc.interogationDialogue = d; break;
                    case 7: sc.glasseDialogue = d; break;
                    case 8: sc.customOnArrestDialogue = d; break;
                }
            }
            catch (Exception e) { throw new PsRuntimeError("client.say 失败: " + e.Message, line); }
        }
    }

    /// <summary>
    /// E5 物品句柄: 包装 Il2Cpp GameItem, 由 items.find / machine.find 返回。
    /// 读: id/count/value; 无方法 (操作走 quality.*/machine.* 命名空间函数)。
    /// 所有访问现取现用(不缓存字段), 物品失效(指针空)给友好运行错误。
    /// </summary>
    internal sealed class PsItemHandle : PsHandle
    {
        private readonly GameItem _item;

        internal PsItemHandle(GameItem item) { _item = item; }

        internal override string Kind => "item";

        /// <summary>quality.*/machine.* 函数取底层游戏对象用; 失效抛带行号的友好错误。</summary>
        internal GameItem NeedItem(int line)
        {
            if (_item == null)
                throw new PsRuntimeError("物品句柄已失效(物品对象不存在)", line);
            return _item;
        }

        internal override object GetMember(string name, int line)
        {
            switch (name)
            {
                case "id":
                    try { return NeedItem(line).identifier; } catch (PsRuntimeError) { throw; } catch (Exception e) { throw new PsRuntimeError("item.id 读取失败: " + e.Message, line); }
                case "count":
                    try { return (long)NeedItem(line).unitCount; } catch (PsRuntimeError) { throw; } catch (Exception e) { throw new PsRuntimeError("item.count 读取失败: " + e.Message, line); }
                case "value":
                    try { return NeedItem(line).unitValue; } catch (PsRuntimeError) { throw; } catch (Exception e) { throw new PsRuntimeError("item.value 读取失败: " + e.Message, line); }
                case "uid":
                    try { return (long)NeedItem(line).uniqueId; } catch (PsRuntimeError) { throw; } catch (Exception e) { throw new PsRuntimeError("item.uid 读取失败: " + e.Message, line); }
                default:
                    throw new PsRuntimeError(
                        $"物品句柄无成员 '{name}'(可用: id/count/value/uid; 操作请用 quality.set/get/price_factor 或 machine.progress/producing)", line);
            }
        }
    }
}
