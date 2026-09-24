# 05 · 08 npc 基础：自定义客户

> `npc` 命名空间把**自定义客户**注入游戏生成管线：常客、剧情人物、供货商……
> 本篇是 **API 签名与配置键总表**——够你照着 `example_hello` 抄出能跑的 NPC；
> 生成管线五相位、对话选项、条件函数等**完整参考在进阶章**（[07 进阶](../07-advanced/README.md)）。

## 函数表

| 函数 | 签名 | 返回 | 一句话 |
|---|---|---|---|
| 注册 | `npc.register(config)` | `null` | 定义一个 NPC（config 见下表） |
| 池加权 | `npc.pool_add(pool, id, weight_pct)` | `null` | 把已注册 NPC 挂进原生生成池 |
| 权重缩放 | `npc.pool_scale(id 或 "前缀*", factor)` | `int`（命中数） | 批量调池权重 |
| 手动排班 | `npc.schedule(id[, {put_first = false}])` | `null` | 挂起强制生成 |

注册类函数在**加载期顶层调用**。

## npc.register(config)

`config` 是一个大 dict。全部合法键（27 个，按功能分组；未知键**警告跳过**）：

### 身份

| 键 | 类型 | 必填 | 含义 |
|---|---|---|---|
| `id` | `string` | ✔ | NPC id（脚本侧/排班/池引用用它；建议带辨识度前缀） |
| `name` | `string` | ✔ | 显示名（店里排队 UI、对话署名） |
| `base_template` | `string` | ✔ | 基础模板 id（生成时套用的原版客户模板；**必须取自 247 个有效键**，全表见 [07 进阶 · 模板总表](../07-advanced/04-npc-pipeline.md)） |
| `faction` | `string` | | 势力（`scav`/`lower`/`security`/`upper`/`tourist`/`rev`/`black_market`/`cartel`，缺省按模板） |
| `sprite` / `sprites` | `string` / `[string]` | | 立绘覆盖（单张/多张轮换） |

### 交易

| 键 | 类型 | 含义 |
|---|---|---|
| `intent` | `string` | 意图（BUY/SELL/SELLNBUY/DIALOGUE… 全表见 [04 shop](04-shop.md)） |
| `cash` | `int` | 身上现金 |
| `budget` | `[amount, range]` | 独立预算 `[额, 浮动]` |
| `price` | `{sell_single, sell_bulk, buy}` | 三向价格系数，缺省全部 `1.0`（v1.13.0 起）：`sell_single` = 玩家单买价系数、`sell_bulk` = 批发整桌价系数、`buy` = NPC 收购价系数 |
| `multi_buy_disabled` | `bool` | 每次只收一件（登记销毁式玩法） |
| `no_contraband` | `bool` | 不收违禁品 |

> 旧键 `buy_price_mod` / `sell_price_mod`（v1.12.0 起废弃）仍可解析，只用于告警迁移。

### 收购（他想买什么）

| 键 | 类型 | 含义 |
|---|---|---|
| `buying_ids` | `[string]` | 收购物品 id 清单 |
| `buying_tags` | `[string]` | 收购标签 |
| `buying_features` | `[string]` | 收购特性匹配 |
| `black_ids` / `black_tags` | `[string]` | 黑名单（id/标签） |
| `clear_buying` | `bool`（默认 `true`） | 是否清空模板自带收购清单 |
| `buy_pool` | `[{id, weight}]` | 收购候选池（加权抽选，v1.11.0；须搭配 `buy_count`） |
| `buy_count` | `int` 或 `[min, max]` | 当日收购种数（配 `buy_pool`） |

### 出售（他卖给你什么）

| 键 | 类型 | 含义 |
|---|---|---|
| `sell_items` | 清单或函数 | 静态清单（`"id"` / `"id:数量"` / `"id:数量:概率"`（数量支持 `"min-max"` 区间）或 `{id, count, p}` 混排）或 `fn() ->` 同格式数组（每次生成现算） |
| `sell_pool` | `[{id, weight}]` | 加权候选池（v1.10.0；须搭配 `sell_count`） |
| `sell_count` | `int` 或 `[min, max]` | 上架种数（配 `sell_pool`） |

### 对话

| 键 | 类型 | 含义 |
|---|---|---|
| `dialogues` | `{通道: [候选]}` | 各通道台词；通道 = `main/accept/all_done/repeat/wrong_item/right_item/interogation/glasse/on_arrest`（九通道详见 [10 句柄](10-handles.md)）；候选为字符串（单段）或 `{texts = [段1, 段2, ...]}`（多段链）或 `{id, choices = [...]}`（main 通道选项） |
| `auto_leave` | `bool`（默认 `true`） | `false` = 说完话不走（驻店 NPC 用） |

### 出现规律

| 键 | 类型 | 含义 |
|---|---|---|
| `schedule` | `{mode, ...}` | 排班：`manual`（只手动/池触发）/ `daily` / `every_ndays {interval}` / `specific_day {day}` / `random {chance}` / `once`；均支持 `max_times` 上限与 `put_first` |
| `can_spawn` | `fn() -> bool` | 生成前条件函数（返回 falsy 则今天不出） |
| `register` | `bool` | 是否进游戏客户字典 |
| `pools` | `[{name, weight_pct}]` | 挂原生池（等价 `npc.pool_add`） |

### 最小可跑示例

```pss
npc.register({
    id = "my_pack:regular_old_zhao",
    name = "老赵",
    base_template = "junker2",        # 废品贩子模板(247 有效键之一, gunworks 包实证)
    faction = "lower",
    intent = "SELLNBUY",              # 买卖兼有
    cash = 800,
    buying_ids = ["scrap_metal", "newspaper"],   # 原版物品用裸 id
    sell_items = ["bandage_item:1:0.5"],   # 50% 概率卖一个基础绷带
    dialogues = {
        main = "老赵来了, 今天有好货。",
        accept = "成交。",
    },
    schedule = {mode = "daily"},      # 每天都可能来
})
```

> ⚠️ **两个静默坑**（详见 [07 进阶 · 生成管线](../07-advanced/04-npc-pipeline.md)）：
> ① `base_template` 填了 247 个有效键以外的值**不会报错**——游戏侧静默回退成拾荒者（scavGeneral），PSApi 日志无警告，只看得出"长得不对"。
> ② 物品 id 在 npc 配置里**不做 `game:` 前缀归一化**（inject.* 侧会剥掉该前缀，NPC 侧不会）——写 `"game:scrap_metal"` 会被当作不存在的 id 跳过并告警。原版物品写裸 id、模组物品写 `包id:名`。

## npc.pool_add(pool, id, weight_pct)

把已注册 NPC 挂进**原版生成池**（未注册报错）。五个池：

| 池 | 触发场景 |
|---|---|
| `client` | 常规客户生成 |
| `buy` | 收购向客户 |
| `sell` | 出售向客户 |
| `upper` | 上层区客户 |
| `bm` | 黑市客户 |

`weight_pct` 是百分比权重（相对池内原生条目）。

```pss
npc.pool_add("client", "my_pack:regular_old_zhao", 5.0)   # 5% 权重
```

## npc.pool_scale(id 或 "前缀*", factor)

按 id（精确）或前缀通配（`"my_pack:rev_cell_*"`）缩放已注册 NPC 的**全部池权重**
（乘以 factor；已挂载的条目当场重挂）。典型玩法：剧情"接受"后某阵营客户出现率 ×3。
返回命中 NPC 数；`factor` 须 > 0。

## npc.schedule(id[, {put_first}])

不等排班/池抽选，**下一次开店建队时强制生成**这个 NPC（`schedule` 字段被无视）。
`id` 接受全限定或裸 id；`put_first = true` 则排到队首。适合"剧情推进到这了，
明天这人必须来"的场合。未注册报错。

```pss
# 玩家在对话里选了第 2 个选项(选项序号从 0 数) → 明天老赵排头位进店
on dialogue_choice():
    if event.npc_id == "my_pack:story_merchant" and event.choice == 1:
        npc.schedule("my_pack:regular_old_zhao", {put_first = true})
```

## 与相邻系统的关系

- **inject vs npc**：`inject.*` 改的是**既有任意客户**的货；`npc.register` 造的是
  **你自己的客户**。两者互不干扰（inject 对脚本 NPC 自动跳过）。
- **句柄**：你的 NPC 生成后同样走 `customer_generated` 事件，`event.client` 句柄
  可读可 `say`（见 [10 句柄](10-handles.md)）。
- **排班记账**：`once` / `max_times` 由框架按**存档槽**记账（与你的 `state` 同库不同文件），
  读档回退不回滚已耗次数，属预期。

---

**本篇完。** 下一篇：[09 · ui：界面函数](09-ui.md)。
