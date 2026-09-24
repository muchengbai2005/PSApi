# 07 · 03 原版注入深入

> [05 · 07 inject](../05-api-reference/07-inject.md) 是十个函数的调用手册。
> 本篇讲**机制**：每条注入通道挂在游戏的哪个原生路径上、什么时候真正执行、
> 为什么"每个客户只注入一次"、防双份怎么防的——理解了这些，注册了没效果、
> 想重复注入、想调权重这些问题的答案都是显然的。

## 一、心智模型：五条挂点，五种原生路径

`inject.*` 不是"生成内容"，而是"往游戏已有的消费路径里塞东西"。五条通道
各接一条原生路径，执行时机完全由游戏驱动：

| 通道 | 原生消费路径 | 执行时机 | 去重单位 |
|---|---|---|---|
| `buy_list` | 客户收购清单 `clientBuyingIdList` | ① 客户入队时（事件）② 打开交易 UI 前（补单） | 按 id 去重，可反复补 |
| `sell_shelf` | `AddDirectSellingItemToTable` 原生摆货 | ① main 对话链末行播完 ② 打开交易 UI 后（兜底） | **每客户实例一次** |
| `doctor` | 夜间博士商店 `frontInv` 网格 | 进店后轮询（0.5s 节流），货架填充完成时 | **每次访问一次** |
| `barter` | 野外商人 `barterSellInventory` | `ShowBarter` 打开时 | **每商人实例一次**（按指针） |
| `loot_pool` | 原版 `LootTable` 概率表 | 入池后由原版 Roll 消费（拾荒客带货/远征） | 表每局重建，自动重注 |

源码：`_psapi/PSApi.Events/InjectService.cs`（四通道）、`LootPoolService.cs`
（loot_pool）。前四条通道的注册条目在**加载期**就进内存表，真正的注入动作
全部发生在**对局内**由补丁/事件触发。

## 二、逐通道机制

### 1) buy_list —— 收购清单追加

两个触发点，语义都是"往这个客户想买的清单里加 id"：

```text
客户生成/入队 (customer_generated 事件)
   └→ InjectBuyingList: 意图过滤 → 脚本NPC跳过 → 逐条目追加(清单内去重)
打开交易 UI (NegociationUIManager.OpenUI Prefix)
   └→ 同上 —— 覆盖"非生成路径入队"的客户(剧情/剧情事件塞进来的)
```

- **意图门槛**：只有 `BUY` / `SELLNBUY` / `WHOLESALE` / `PROCUREMENT_OFFER`
  四种意图的客户会收到注入（`BuyingIntents` 集合）。给纯 SELL 客户注册
  buy_list 是无效的——他本来就不收货。
- **脚本 NPC 跳过**：`npc.register` 出来的客户，收购清单由脚本全权定义，
  全局 buy_list 不碰它们（`_npcs.FindEntry(cid)` 命中即返回）。
- **动态函数**：注册时传函数则每次触发现调 `fn(client)`，返回 id/数组
  （可按 `client.id` 给不同客户配不同清单）。函数出错按空清单处理+告警。
- **不掷概率**：buy_list 没有 chance——注册即必收。想要概率行为，用
  动态函数自己掷（`fn` 里返回空数组就是"这次不收"）。

### 2) sell_shelf —— SELL 客户的货架

这是最精巧的一条通道，因为它要对齐原版的摆货时机：

```text
客户 main 对话最后一行播完 (DialogUIManager.OnCurrentTextDisplayed Postfix)
   ├→ 原版: 链尾 endAction 里执行摆货 lambda(49 处原生同款)
   └→ PSApi: 同帧执行 StockShelf —— 与原版摆货完全同时刻
打开交易 UI (OpenUI Postfix)
   └→ 兜底: 极端路径(瞬移进店等)跳过对话钩子时补摆
```

**会话守卫**：`_shelfSessionKey` 记住"最后一个被摆货的客户指针"。每个客户
实例**只摆一次**——玩家把货拖走不会补，客户离店再进店（同指针复活）也不补。
这是防无限刷货的铁律。要主动再来一次？见下文"调节工作流"。

**防双份（v1.8.0）**：摆货前扫一遍"展示区 ∪ 后台"的非玩家拥有物品，按 id
计数，**只补差额**（`have=2, count=3` → 只摆 1 件）。所以同一客户即使守卫
被绕过（面板手动补货），也不会摆出双份。

**chance 与 count**：注册时的 `chance` 是"这次来访摆不摆这条"的独立判定；
`count` 支持 `"1-3"` 区间（上限 5），**每次注入独立掷**。两个随机层互不干扰。

### 3) doctor —— 夜间博士商店

```text
MapUIManager.VisitUpgradeMerchant Postfix → _doctorActive = true
Plugin.OnUpdate → Poll() 0.5s 节流:
   等 EmporiumEntry.Instance.frontInv 被场景填充(非空) → 注入一次 → _doctorDone
LeaveUpgradeMerchant Postfix → _doctorActive = false(下次访问重新来)
```

- 注入用 `PlaceItemInGrid` 三级降级：原生槽位接受（带 UI 通知）→
  `AcceptUnchecked` 落位 → `UncheckedAccept` 硬塞。
- 出错即 `_doctorDone = true`，本次访问不再重试（防刷屏）。

### 4) barter —— 野外商人

`OverlayHandler.ShowBarter` Postfix，按**商人指针**去重
（`_injectedTraders`）。与货架同款"每实例一次"。注入后 `Validate(true)` +
`UpdateBarterImmediately()` 刷新 UI。

### 5) loot_pool —— 原版概率表（v1.10.0）

和前四条不同，这条**没有 patch、没有时机问题**：注册条目在 `OnUpdate`
轮询里等 `TableMaster.Instance.tableEntries` 就绪后直接写进原版
`LootTable.ProbabilityItems`。

- **表名**：原样命中或补 `Table` 后缀（`junk` → `junkTable`）；都不命中
  告警并列出全部可用表名。
- **权重是概率不是序数**：写进 `m_BaseProbability`，Roll 时按表内总和归一
  （原版表内总和 ≈ 1，所以 `0.03` ≈ 3%）。合法区间 `(0,1]`。
- **每局重建**：表不进存档，`TableMaster` 实例指针变了（新对局）自动撤旧
  重注；`inject.reset_tracking` / `tune(enabled=false)` 也走同一机制。
- **消费方**：拾荒客/小贼等 SELL 客户工厂的带货掷骰、远征产出。

## 三、共通纪律

1. **id 归一化**：`"game:xxx"` 剥前缀用裸 id；其他（含 `包:物品`）原样。
   与 `items.*` 同规则（[05 章规则 ①](../05-api-reference/README.md)）。
2. **存在性校验（v1.11.0）**：注入是**执行点校验**——对局内
   `DirectoryMaster` 就绪后才查 id 存在性；无效 id **跳过 + 统一格式告警一次**：

   ```text
   [psapi] 物品 id 'xxx' 在当前版本不存在, 已跳过 (来源: inject.sell_shelf)
   ```

   游戏版本更新删了原版物品时，你的注入静默失效但能从日志一眼看到。
3. **uses 机制（v1.9.0）**：`{uses=N}` 让注入的物品实例带使用次数
   （原生 `UseCountHelper`，用完即毁，不折价）。动态函数形式下 opts.uses
   统一覆盖函数返回值里的 uses。
4. **所有权标记**：注入的客户货物都是 `isOwned=false`——交易 UI 的
   "非玩家拥有"过滤认这个状态，拖走/买走即正常流转。

## 四、inject 还是 npc.register？（决策表）

| 你的目标 | 用 |
|---|---|
| 让**任意**收购客户偶尔也想收你的新物品 | `inject.buy_list` |
| 让**任意** SELL 客户的货架上偶尔出现你的物品 | `inject.sell_shelf` |
| 夜间博士/野外商人的货架加货 | `inject.doctor` / `inject.barter` |
| 让拾荒客/远征能随机带出你的物品 | `inject.loot_pool` |
| 一个**有名字、有对话、有排班**的固定商人 | `npc.register`（[04](04-npc-pipeline.md)/[05](05-npc-config.md) 篇） |

两系统互不干扰：inject 对脚本 NPC 自动跳过（buy_list 查注册表、
sell_shelf 查 `HasSellPlan`）。

## 五、调节工作流（管理面板联动）

注册不是终点。psapi_manager 的注入管理页（F6 面板）暴露的四个动作，
对应的脚本面函数（签名见 [05 · 07](../05-api-reference/07-inject.md)）：

```pss
# 看当前全部注册条目(四通道+loot_pool 平铺, idx 即序号)
rows = inject.list()

# 运行时开关/改数量(不进存档, 重启失效)
inject.tune(3, {enabled = false})        # 序号 3 的条目停用
inject.tune(3, {count = 2})              # count 覆盖(0=恢复注册值)

# 客户已消费过注入, 想让他再摆一次:
inject.reset_tracking()                  # 清会话守卫/商人去重/博士标记

# 当前正在交易的客户立刻补货(无视守卫):
inject.restock()                         # 买池补单 + 货架强制摆货
```

机制注解：`reset_tracking` 平移自 NpcManager 的同名概念——货架会话键、
野外商人指针集、博士标记全部复位，**下一个**客户/访问重新注入；
`restock` 是对**当前**客户立即动作（返回注入件数）。

## 六、补丁点位表（想理解"为什么安全"的开发者）

InjectService 全部 Harmony 补丁点位（均经 NpcManager 生产验证 + ISIL
核字节）：

| 补丁点 | 相位 | 用途 |
|---|---|---|
| `NegociationUIManager.OpenUI` | Prefix + Postfix | 买池补单 / 货架兜底 |
| `DialogUIManager.OnCurrentTextDisplayed` | Postfix | main 对话链末行 = 摆货主时机 |
| `MapUIManager.VisitUpgradeMerchant` / `LeaveUpgradeMerchant` | Postfix | 博士会话标记 |
| `OverlayHandler.ShowBarter` | Postfix | 野外商人注入 |

`OpenUI` 是大方法（安全）；`ExecuteEndAction` 本体仅 ~30 字节（触 40 字节
红线**不可 patch**），所以对话钩子挂的是它的主调用点
`OnCurrentTextDisplayed`——native 先执行原版 endAction 再返回，Postfix
因此与原版摆货同帧。

loot_pool 的两条 GC 红线（写屏障、字符串先分配）记录在
`LootPoolService.cs` 头注；对脚本开发者只需知道：**别担心，框架处理了**。

---

**本篇完。** 下一篇：[04 · NPC 生成管线与模板](04-npc-pipeline.md)
