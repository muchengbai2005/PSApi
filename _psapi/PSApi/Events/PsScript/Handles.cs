using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using PSApi.Items;

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
        /// <summary>say 通道 → StoreClient 对话字段(与 NpcManager 九通道一致; v2.0.3 追加 accept_sell=9,
        /// 无 StoreClient 字段 → 写 NpcService.SellAcceptChains 侧表)。</summary>
        private static readonly Dictionary<string, int> Channels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["main"] = 0, ["accept"] = 1, ["all_done"] = 2, ["repeat"] = 3,
            ["wrong_item"] = 4, ["right_item"] = 5, ["interogation"] = 6,
            ["glasse"] = 7, ["on_arrest"] = 8, ["accept_sell"] = 9,
        };

        private readonly StoreClient _sc;
        private readonly MelonLogger.Instance _logger; // 可空: 仅 customer_generated 路径传入 (say 覆盖告警用)

        internal PsClientHandle(StoreClient sc, MelonLogger.Instance logger = null) { _sc = sc; _logger = logger; }

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

        /// <summary>改对话通道文本(数组通道直接整段替换为单条; accept 同时写 LastLine 变体)。
        /// v2.0.1: main 通道替换前查旧链尾选项 — 整段替换会连选项一起销毁(实测地雷: gunworks 老闫
        /// customer_generated 里 say("main") 覆盖带选项的对话链 → 选项从未渲染), 命中即告警。</summary>
        private void Say(StoreClient sc, int kind, string text, int line)
        {
            try
            {
                if (kind == 0)
                {
                    try
                    {
                        Dialogue tail = sc.mainDialogue;
                        while (tail != null && tail.nextDialogue != null) tail = tail.nextDialogue;
                        int n = 0;
                        try { n = tail?.choices?.Count ?? 0; } catch { }
                        if (n > 0)
                            PsApi.Warn(_logger, $"client.say('main') 覆盖了 '{sc.displayName}' 带 {n} 个选项的对话链, 选项已丢失 — 引导/问候文本请写进 npc.register 的 dialogues.main.texts, 或改用其他通道");
                    }
                    catch { }
                }
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
                    case 9:
                        try { NpcService.SellAcceptChains[sc.Pointer.ToInt64()] = d; } catch { }
                        break;
                }
            }
            catch (Exception e) { throw new PsRuntimeError("client.say 失败: " + e.Message, line); }
        }
    }

    /// <summary>
    /// E5 物品句柄: 包装 Il2Cpp GameItem, 由 items.find / machine.find 返回。
    /// 读: id/count/value/uid; v1.15.0: + name/desc/tags; 方法: get_data/set_data/set_name/set_desc/set_value/set_uses
    /// (实例定制, 与 items.give opts 同一套 ItemsFacade 路径)。
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

        private static string AsKey(object v, string fn, int line)
        {
            if (v is string s && !string.IsNullOrWhiteSpace(s)) return s;
            throw new PsRuntimeError($"{fn} 的 key 须为非空字符串, 实为 {PsValues.TypeName(v)}", line);
        }

        private static long AsNum(object v, string fn, int line)
        {
            if (v is long l) return l;
            if (v is double d) return (long)d;
            throw new PsRuntimeError($"{fn} 的参数须为数字, 实为 {PsValues.TypeName(v)}", line);
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
                case "name":
                    try { return NeedItem(line).name; } catch (PsRuntimeError) { throw; } catch (Exception e) { throw new PsRuntimeError("item.name 读取失败: " + e.Message, line); }
                case "desc":
                    try { return NeedItem(line).shortDescription; } catch (PsRuntimeError) { throw; } catch (Exception e) { throw new PsRuntimeError("item.desc 读取失败: " + e.Message, line); }
                case "tags":
                    try
                    {
                        var t = NeedItem(line).itemTypes;
                        var r = new List<object>();
                        if (t != null) foreach (var s in t) r.Add(s);
                        return r;
                    }
                    catch (PsRuntimeError) { throw; } catch (Exception e) { throw new PsRuntimeError("item.tags 读取失败: " + e.Message, line); }
                case "get_data":
                    return PsBuiltins.BF("item.get_data", (itp, a, ln) =>
                    {
                        PsBuiltins.Need(a, 1, 2, "item.get_data(key[, 默认=null]) → 自定义数据 (无键=默认值; 嵌套 dict/list 经 j:JSON 往返)", ln);
                        string key = AsKey(a[0], "item.get_data", ln);
                        try
                        {
                            var v = ItemsFacade.GetData(NeedItem(ln), key);
                            // v1.16.2: j: 编码的嵌套数据以 JsonNode 返回, 转 pss dict/list/标量
                            if (v is System.Text.Json.Nodes.JsonNode jn) v = ScriptJson.FromNode(jn);
                            return v ?? (a.Count == 2 ? a[1] : null);
                        }
                        catch (PsRuntimeError) { throw; }
                        catch (Exception e) { throw new PsRuntimeError("item.get_data 失败: " + e.Message, ln); }
                    });
                case "set_data":
                    return PsBuiltins.BF("item.set_data", (itp, a, ln) =>
                    {
                        PsBuiltins.Need(a, 2, 2, "item.set_data(key, value) → bool (value=null 删除; 值支持 字符串/数字/bool/dict/list; dict/list 走 j:JSON 持久化)", ln);
                        string key = AsKey(a[0], "item.set_data", ln);
                        if (a[1] != null && a[1] is not string && a[1] is not long && a[1] is not double && a[1] is not bool
                            && a[1] is not Dictionary<string, object> && a[1] is not List<object>)
                            throw new PsRuntimeError($"item.set_data 的 value 仅支持 字符串/数字/bool/dict/list/null, 实为 {PsValues.TypeName(a[1])}", ln);
                        try { return ItemsFacade.SetData(NeedItem(ln), key, a[1]); }
                        catch (PsRuntimeError) { throw; }
                        catch (Exception e) { throw new PsRuntimeError("item.set_data 失败: " + e.Message, ln); }
                    });
                case "set_name":
                    return PsBuiltins.BF("item.set_name", (itp, a, ln) =>
                    {
                        PsBuiltins.Need(a, 1, 1, "item.set_name(s) → bool (改实例显示名)", ln);
                        try { return ItemsFacade.SetName(NeedItem(ln), PsValues.Fmt(a[0])); }
                        catch (PsRuntimeError) { throw; }
                        catch (Exception e) { throw new PsRuntimeError("item.set_name 失败: " + e.Message, ln); }
                    });
                case "set_desc":
                    return PsBuiltins.BF("item.set_desc", (itp, a, ln) =>
                    {
                        PsBuiltins.Need(a, 1, 1, "item.set_desc(s) → bool (改实例短描述)", ln);
                        try { return ItemsFacade.SetDesc(NeedItem(ln), PsValues.Fmt(a[0])); }
                        catch (PsRuntimeError) { throw; }
                        catch (Exception e) { throw new PsRuntimeError("item.set_desc 失败: " + e.Message, ln); }
                    });
                case "set_value":
                    return PsBuiltins.BF("item.set_value", (itp, a, ln) =>
                    {
                        PsBuiltins.Need(a, 1, 1, "item.set_value(n) → bool (改实例单价, 0..99999999)", ln);
                        long n = AsNum(a[0], "item.set_value", ln);
                        if (n < 0 || n > 99999999)
                            throw new PsRuntimeError($"item.set_value 须在 0..99999999, 实为 {n}", ln);
                        try { return ItemsFacade.SetValue(NeedItem(ln), n); }
                        catch (PsRuntimeError) { throw; }
                        catch (Exception e) { throw new PsRuntimeError("item.set_value 失败: " + e.Message, ln); }
                    });
                case "set_uses":
                    return PsBuiltins.BF("item.set_uses", (itp, a, ln) =>
                    {
                        PsBuiltins.Need(a, 1, 1, "item.set_uses(n) → bool (已有次数机制改当前值, 无则启用上限=n; 1..9999)", ln);
                        long n = AsNum(a[0], "item.set_uses", ln);
                        if (n < 1 || n > 9999)
                            throw new PsRuntimeError($"item.set_uses 须在 1..9999, 实为 {n}", ln);
                        try { return ItemsFacade.UseCountSet(NeedItem(ln), (int)n); }
                        catch (PsRuntimeError) { throw; }
                        catch (Exception e) { throw new PsRuntimeError("item.set_uses 失败: " + e.Message, ln); }
                    });
                default:
                    throw new PsRuntimeError(
                        $"物品句柄无成员 '{name}'(可用: id/count/value/uid/name/desc/tags/get_data/set_data/set_name/set_desc/set_value/set_uses; 品质与机器操作请用 quality.set/get/price_factor 或 machine.progress/producing)", line);
            }
        }
    }
}
