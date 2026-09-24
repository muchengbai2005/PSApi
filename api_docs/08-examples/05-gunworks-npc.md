# 05 · gunworks NPC 层：剧情、网络与收银台

> gunworks 的 NPC 层有 18 个注册：1 条主线剧情（guide）、14 人交易网络
> （cells）、2 个黑市商人（forger / cat_shadow）。四个脚本覆盖了
> npc 系统几乎全部能力：状态机剧情、池化买卖、对话选项收银、双通道调度。
>
> 事实来源：`UserData/PSApi/packs/gunworks/events/`（guide.pss / cells.pss /
> forger.pss / cat_shadow.pss，v0.13.0 磁盘实况）。

## 一、guide.pss：剧情状态机

朱利安剧情是"**一个 NPC 一条支线**"的完整样板。剧情三阶段用 state 里的
`stage` 键驱动：`pending`（未决）→ `accepted`（接受）或 `declined`（推脱三次）
或 `reported`（举报）。

### 双注册：同一人、两个 NPC

```pss
npc.register({
    id = "guide",                    # 未决阶段: 三选项对话
    ...
    intent = "DIALOGUE",
    dialogues = { main = [ { id = "guide_intro", texts = [...], choices = [...] }, ... ] },
    schedule = {mode = "manual"},
})
npc.register({
    id = "guide_pay",                # 已接受阶段: 周五叙旧领津贴
    ...
    schedule = {mode = "manual"},
})
```

**为什么拆两个 id**：对话、行为、出场逻辑完全不同，与其在每处 `if stage ==`
分支，不如让"阶段"直接对应"NPC 实体"。两个都是 `manual` 排班——
**谁来、什么时候来，全部由 day_wake 脚本说了算**：

```pss
on day_wake():
    if state.get("stage", "pending") == "accepted":
        npc.pool_scale("cell_*", 3)          # 每日幂等收敛: 革命军池×3
    if time.weekday() != 4:
        return                                # 只在周五
    var stage = state.get("stage", "pending")
    if stage == "pending":
        npc.schedule("gunworks:guide", {put_first = true})
    elif stage == "accepted":
        player.add_cash(100)                  # 周津贴直接入账
        npc.schedule("gunworks:guide_pay", {put_first = true})
```

两个细节值得抄：

- **`npc.pool_scale("cell_*", 3)` 放在 day_wake 每日执行**——pool_scale 幂等
  （每日重挂同权重），放这里等于"换存档/重进对局后自动恢复"，不依赖
  剧情触发那一次。
- **津贴在 day_wake 结算而不是对话 endAction**——玩家跳过对话也拿钱，
  与原版 RevDeal 的差异在文件头注里明确记录了。

### 三选项结算：dialogue_choice 事件

选项点击只触发 `dialogue_choice` 事件，效果全在脚本里：

```pss
on dialogue_choice():
    if event.dialogue_id != "gunworks:guide_intro":
        return                          # 只认自己的对话
    if event.choice == 0:               # 接受
        player.add_cash(200)
        rep.add("rev", 10)
        crime.commit("crime_gunworks_deal", 200, "涉嫌通敌")
        state.set("stage", "accepted")
        ...
    elif event.choice == 1:             # 我再想想
        var n = state.get("defer_count", 0) + 1
        state.set("defer_count", n)
        if n >= 3:
            state.set("stage", "declined")
    else:                               # 举报
        rep.add("sec", 15)
        state.set("stage", "reported")
```

接受分支里的**见面礼**展示了 `items.give_counter` 的幂等发放模式：

```pss
if state.get("gifted", 0) == 0:
    state.set("gifted", 1)
    var ok = items.give_counter("gunworks:gw_bench", 1)
    ok = items.give_counter("gunworks:permit_forged", 1, 10) and ok   # 10 天款
    var n = rand(3, 4)
    for i in range(n):
        ok = items.give_counter(GIFT_PARTS[rand(0, 8)], 1) and ok
```

`state["gifted"]` + `give_counter`（货摆门口柜台、贴原版送礼路径）双保险：
dialogue_choice 事件理论只来一次，但防重放的代价只要两行。

> [待确认] `crime.commit` 的第一参 `crime_id`（此处自定义
> `"crime_gunworks_deal"`）有效值集合在源码中未见枚举校验说明，
> 自定义串是否进入治安档案 UI 的展示名未见实证（见 [09 附录 · 待确认清单](../09-appendix/06-unresolved.md)）。

## 二、cells.pss：14 人交易网络

cells.pss 是**原版 NpcManager JSON 规则的手写脚本复刻**（迁移自
revolutionaries.json），14 个 NPC 按"下层/上层/治安部/黑市/本部/卡特尔"
六个阵营展开。每个注册都是完整的商人配置，以马洛夫（老枪）为例拆解：

```pss
npc.register({
    id = "cell_gunsmith",
    name = "马洛夫",
    sprite = "maleScav5",                     # 指定皮肤(probe 实测键)
    base_template = "lower_level_gunsmith",   # 247 有效模板之一
    faction = "lower",
    intent = "sellnbuy",
    budget = [1200, 400],                     # 独立预算 [额, 浮动]
    price = {sell_single = 0.9, sell_bulk = 0.8, buy = 1.05},
    clear_buying = false,                     # 保留模板收购清单, 与 buying_ids 合并
    buying_ids = ["box_cutter", "kitchen_knife", "gun_part",
        "gunworks:datacard_barrel", ...],     # 必收: 原版裸 id + 模组带前缀 id 混排
    buy_count = 1,                            # buy_pool 当日掷 1 种
    buy_pool = ["gunworks:gw_pistol:3", "gunworks:gw_autopistol:2", "gunworks:gw_smg:2"],
    sell_items = ["screwdriver:1:0.5", "stock:1:0.35"],   # 静态层: 每条独立掷
    sell_count = "4-6",                       # 池化层: 加权不放回抽 4-6 种
    sell_pool = ["gunworks:pistol_parts:10", ..., "match_barrel:1"],
    dialogues = { main = [ {texts = [...{{buy_list}}...]}, ... ], ... },
    pools = [{name = "client", weight_pct = 5}, {name = "sell", weight_pct = 8}],
})
```

### 三层买卖结构（v0.11.0 的最终形态）

| 层 | 键 | 语义 |
|---|---|---|
| 必收 | `buying_ids` | 每天必收，台词写死 |
| 当日随机收 | `buy_pool` + `buy_count` | 加权不放回掷 N 种，台词 `{buy_list}` 点名 |
| 静态卖 | `sell_items` | `"id:数量:概率"` 每条独立掷骰（掩护商品层） |
| 池化卖 | `sell_pool` + `sell_count` | 加权不放回抽 N 种、每种 1-2 件（主力商品层） |

**买/卖同 id 不允许重叠**——瑞特把解放步枪从 sell_items 移进 buy_pool 的
迁移注释就是这条规则的实例。

### 声望奖励：trade_completed 记账

原版 JSON 的 `reputationBonus` 字段没有脚本对应键，gunworks 用一张表
+ 一个 handler 平移：

```pss
var CELL_REP = {
    "cell_gunsmith" = {rep = "ll", items = {box_cutter = 2, kitchen_knife = 2,
        gun_part = 3, "gunworks:datacard_barrel" = 2, ...}},
    "cell_contact"  = {rep = "rev", items = {shotgun = 10, smg = 12, revolver = 10}},
    ...
}

on trade_completed():
    if event.direction != "sell":        # 只记玩家卖出
        return
    var cfg = CELL_REP[event.npc_id]
    if cfg == null:
        return
    var pts = cfg.items[event.item_id]
    if pts == null:
        return
    rep.add(cfg.rep, pts)
```

玩家把壁垒霰弹枪卖给诺曼 → rev 声望 +10，原版「你获得了声望」提示照常弹出。
**这是"框架没有的能力，用事件 handler 补"的标准打法。**

### 价格声明

全部 14 家显式 `price = {sell_single = 0.9, sell_bulk = 0.8, buy = 1.05}`
（单买九折/批发八折/收购 +5%）。注释里记录了原因：v1.13.0 起框架缺省全 1.0、
且克隆模板自动白板化——奸商模板的违禁品压价不再泄漏，想要"对齐原版奸商手感"
就得自己声明。

## 三、forger.pss：对话选项当收银台

伪造师费边是**纯对话 NPC**（`intent = "DIALOGUE"`，不摆摊），把对话选项
当收银台用。核心是 v1.11.0 的 `choices[].cond`（谓词，falsy 不显示）：

```pss
func forge_full_ok():
    return player.cash() >= 300

func forge_parts_ok():
    return player.cash() >= 150

choices = [
    {label = "证件全套（300块: 证书+三件货）", ..., cond = forge_full_ok},
    {label = "只要货（150块: 随机三件）", ..., cond = forge_parts_ok},
    {label = "再逛逛", ...},
]
```

**坑**：cond 隐藏选项会使运行时下标前移——现金 150-299 时玩家看到的
"选项 0"其实是"只要货"。所以结算必须**运行时索引 + 现金双重判定**：

```pss
on dialogue_choice():
    if event.dialogue_id != "gunworks:forger_intro":
        return
    var cash = player.cash()
    if event.choice == 0 and cash >= 300:          # 全套(300)
        ...
        player.add_cash(0 - 300)
        items.give("gunworks:permit_forged", 1, days)
        forge_give_goods(3)
        return
    if (event.choice == 0 and cash >= 150) or (event.choice == 1 and cash >= 300):
        player.add_cash(0 - 150)                   # 只要货(150)
        forge_give_goods(3)
        return
```

`forge_give_goods(n)` 本身是**加权不放回抽样的手写实现**（拒绝采样，guard
上限 200 防死循环）——当 sell_pool/buy_pool 的框架实现不适用（比如要在
对话里即时发放并记日志）时，这就是参考代码。

周行程调度同样轻巧：每周一 day_wake 掷 `forger_n ∈ {1,2}` 天 + 随机 weekday，
当天 `npc.schedule("gunworks:forger")` 进城。

## 四、cat_shadow.pss：双通道出场 + 句柄对话

老猫·影演示**调度与加权池并行**：

```pss
npc.register({
    id = "cat_shadow",
    ...
    schedule = {mode = "every_ndays", interval = 2, chance = 0.8},  # 通道一: 隔2天80%
    can_spawn = can_cat_come,          # 谓词: 黑市声望非负才来
    ...
})
npc.pool_add("bm", "cat_shadow", 5.0)  # 通道二: bm 池 5% 权重
```

两个通道独立生效、互不去重（可能同一天既被抽中又到期）——设计上接受
这种重叠，因为黑市商人"来得勤"符合人设。`can_spawn` 谓词把声望门槛
挂在生成管线最后一关（[07 · 管线](../07-advanced/04-npc-pipeline.md)）。

它还是 **customer_generated + 客户句柄**的样板：

```pss
on customer_generated():
    if event.id == "cat_shadow":
        log.info("[gunworks] 老猫·影到店: 现金 {event.client.cash}, 来源 {event.source}")
        event.client.say("main", "第 {time.rel_day()} 天的新货, 别问来路")
```

`event.client` 句柄可读字段（cash）也能替他说话（say）——生成瞬间
插一句开场白，比等玩家点开对话更"活"。

## 五、18 个注册怎么验

启动日志应有 **18 行** `npc '...' registered (pack=gunworks, ...)`：
14 细胞 + guide + guide_pay + forger + cat_shadow。少一行 = 该脚本在
register 之前抛了异常（看上一行报错）；到店判定 grep `npc eval: <id>`
（每天每 NPC 恰好一行，见 [07 · 管线](../07-advanced/04-npc-pipeline.md)）。

## 六、抄什么

| 你想要 | 抄这个 |
|---|---|
| 剧情支线（阶段推进） | guide.pss 双注册 + stage 键 + day_wake 调度 |
| 剧情影响世界 | `npc.pool_scale("前缀*", 3)` 每日幂等收敛 |
| 选项收银台 | forger.pss cond 谓词 + 双重判定结算 |
| 池化买卖（每日货架不重样） | cells.pss 马洛夫的四键组合 |
| 框架外能力补齐 | CELL_REP 表 + trade_completed |
| 生成瞬间开场白 | cat_shadow.pss 的 customer_generated |

---

**本篇完。** 下一篇：[06 · gunworks 机器层](06-gunworks-machines.md)
