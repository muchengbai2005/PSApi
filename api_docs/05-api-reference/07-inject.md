# 05 · 07 inject：物品池注入

> `inject` 把你的物品塞进游戏的**既有出货渠道**：客户收购清单、出售客户的货架、
> 夜间博士商店、野外以物易物商人、原版 LootTable 随机池。
> 不生成新 NPC、不改系统规则——只是让"已有的渠道"多出你的货。

## 五通道 + 管理函数总表

| 函数 | 通道 | 一句话 |
|---|---|---|
| `inject.buy_list(id \| [ids] \| fn(client))` | 买池 | 收购意图客户的收购清单追加你的物品 |
| `inject.sell_shelf(id \| fn(client)[, count[, chance[, {uses}]]])` | 货架 | 出售意图客户上架你的物品 |
| `inject.doctor(id[, count[, {uses}]])` | 博士 | 夜间博士商店出货 |
| `inject.barter(id[, count])` | 野外商人 | 以物易物商人货架 |
| `inject.loot_pool(表名, id, 权重)`（v1.10.0） | 随机池 | 原版 LootTable 池加权注入 |
| `inject.list()` | 管理 | 全部注入条目平铺 |
| `inject.tune(idx, {enabled, count})` | 管理 | 运行时开关/调数量 |
| `inject.reset_tracking()` | 管理 | 重置"已注入"记账 |
| `inject.restock_current()` | 管理 | 给当前客户手动补货 |
| `inject.force_client(ptr)` | 管理 | 给队列指定客户立即注入 |

注册类函数（`buy_list/sell_shelf/doctor/barter/loot_pool`）在**加载期顶层调用**；
管理类在对局内调用。可运行的注入对照包见
[ex22_api_inject](../../examples/ex22_api_inject/README.md)。

## inject.buy_list — 买池

参数三选一：单个 id、id 数组、或**函数** `fn(client) -> id / [ids]`（每次客户
入队时现算，`client` 是客户句柄，可读 `cash/faction` 等做条件）。

**生效时机**：客户**入队时**（`customer_generated`）追加进收购清单；
交易 UI 打开时再补一次单（覆盖非标准路径入队的客户）。
只影响**收购意图**的客户（BUY / SELLNBUY / WHOLESALE / PROCUREMENT_OFFER）；
脚本注册的 NPC（`npc.register`）跳过——它们的清单由脚本全权定义。
追加时**去重**（清单里已有就不加）。

```pss
# 静态: 所有收购客户都开始想收报纸
inject.buy_list("my_pack:my_ledger")

# 动态: 只有大客户想收
func dynamic_buy(client):
    if client.cash > 500:
        return ["my_pack:example_water"]
    return []
inject.buy_list(dynamic_buy)
```

## inject.sell_shelf — 出售客户货架

```pss
inject.sell_shelf(id[, count = 1[, chance = 1.0[, {uses = N}]]])
inject.sell_shelf(fn(client))    # 函数形式: 返回同 npc sell_items 格式的数组
```

| 参数 | 约束 | 含义 |
|---|---|---|
| `count` | `1..5` 的数，**或** `"min-max"` 区间串（如 `"1-3"`，v1.10.0；每次注入独立掷） | 上架件数 |
| `chance` | `(0, 1]` | 本次注入的命中概率（`1` = 必上） |
| `{uses = N}` | `N` 1..99（v1.9.0） | 摆上的是**次数物品**（用完即毁） |

**生效时机**：客户 main 对话链末行播完时原生摆货（与原版"说完话摆货"同帧），
交易 UI 打开兜底补一次。只影响**出售意图**客户（SELL / SELLNBUY）。
**每客户实例一次**（拖走货物不重摆，防无限刷货）；上架走原生 API，
展示区 ∪ 后台并集计数**只补差额**（防双份）。

函数形式每次注入时调用，返回值同 `npc.register` 的 `sell_items` 数组格式
（`id` / `"id:数量"` / `{id, count, chance, uses}` 等，详见
[08 NPC 基础](08-npc.md)），可以按客户出不同货。

## inject.doctor — 夜间博士商店

```pss
inject.doctor(id[, count = 1[, {uses = N}]])   # count 1..99
```

玩家进入夜间博士商店后，等货架被场景填充完（0.5s 轮询探测）注入一次；
**每次访问一次**。注入位置是博士商店展示区（frontInv），只补差额。

## inject.barter — 野外商人

```pss
inject.barter(id[, count = 1])   # count 1..99
```

野外以物易物商人的 `ShowBarter` 时注入其货架；**每商人实例一次**（按指针去重）。
没有 `chance`/`uses` 参数（语义上野外商人是必出固定货）。

## inject.loot_pool — 原版随机池（v1.10.0）

```pss
inject.loot_pool(表名, 物品id, 权重)    # 权重 (0,1]
```

- **表名**：原版 LootTable 名；短名自动补 `Table` 后缀（写 `junk` 解析为
  `junkTable`）。未知表名会在日志列出当前全部可用表名，据此排查。
- **权重**：`(0, 1]`，按**表内总和归一化**——原版表内权重和约等于 1，
  所以写 `0.03` 大致就是 3% 的抽取概率。
- **消费方**：拾荒客/小贼/Eleanor5 等 SELL 客户的带货工厂 + 远征掷骰。
  也就是"你的物品开始随机出现在随机的人手里"。
- 表每局重建、不进存档；框架按局标识自动撤旧重注，脚本无需关心。

```pss
inject.loot_pool("junk", "my_pack:my_gizmo", 0.03)   # 垃圾池 3%
```

## 注入时机一图流

```
客户生成入队 ──→ buy_list 追加收购清单(去重)
交易UI打开 ────→ buy_list 补单 + sell_shelf 兜底摆货
main对话链末行 → sell_shelf 原生摆货(主时机, 每客户一次)
进博士商店 ────→ doctor 等货架填充后注入(每次访问一次)
开野外商人 ────→ barter 注入(每商人一次)
(随局) LootTable ─→ loot_pool 条目参与 SELL 客户带货/远征掷骰
```

## 管理函数

**`inject.list()` → 注入条目平铺**（顺序固定：buy→shelf→doctor→barter→loot_pool，
`idx` 即注入序号，当次会话内稳定）：

| 字段 | 含义 |
|---|---|
| `idx` | 序号（`tune` 用这个） |
| `channel` | `buy_list` / `sell_shelf` / `doctor` / `barter` / `loot_pool` |
| `pack` | 注册它的包 id |
| `id` | 物品 id（buy 多 id 逗号连；函数形式 = `"(动态函数)"`；loot_pool = `"表名 ← 物品id"`） |
| `count` / `count_max` | 数量 / 区间上限（`count_max > count` = "min-max" 区间；`0` = 固定） |
| `chance` | 命中率百分比（loot_pool 行 = 权重百分比） |
| `dynamic` | 是否函数形式 |
| `enabled` | 开关状态 |
| `uses` | 次数物品设定（`0` = 普通） |

**`inject.tune(idx, {enabled = bool, count = n})`**

运行时调节：`enabled` 开关条目；`count` 覆盖数量（`1..99`，`0` = 恢复注册值；
函数形式与 buy_list 不受 count 影响）。**loot_pool 条目仅支持 `enabled`**
（权重注册时定）。`idx` 越界报运行错误。改动**不进存档**（重启回注册态），
面板类脚本要持久化请自行写 `state`。

**`inject.reset_tracking()`**

清"已注入"记账（货架会话键/野外商人去重/博士标记）——之后**同一些客户会重新注入**。
loot_pool 条目下帧撤旧重注。想"每天重摆一次货"就在 `on day_wake():` 里调它。

**`inject.restock_current()` → 注入件数**

给**当前客户**手动补货：买池补单 + 货架强制摆货（无视会话守卫）。
无当前客户 = `0`。

**`inject.force_client(ptr)` → `bool`**

对**今日队列**里 `ptr` 指定的客户立即注入（买池+货架，无视守卫）。
`ptr` 从 [`shop.queue()`](04-shop.md) 条目里取；客户已离队 = `false`。

## 无效 id 的行为

注入执行点（对局内）会做**存在性校验**：物品 id 在当前版本不存在 → 跳过 + 日志
告警一次（`[psapi] 物品 id 'xxx' 在当前版本不存在, 已跳过 (来源: inject.xxx)`），
**不报错不中断**。游戏版本更新删掉某物品时，你的模组静默降级而不是炸档。

## 本篇函数速查

```pss
inject.buy_list(id | [ids] | fn(client))
inject.sell_shelf(id | fn(client)[, count | "min-max"[, chance[, {uses}]]])
inject.doctor(id[, count[, {uses}]])
inject.barter(id[, count])
inject.loot_pool(表名, id, 权重)
inject.list()                        # [{idx, channel, pack, id, count, count_max, chance, dynamic, enabled, uses}]
inject.tune(idx, {enabled, count})
inject.reset_tracking()
inject.restock_current()            # → 件数
inject.force_client(ptr)            # → bool
```

---

**本篇完。** 下一篇：[08 · npc 基础](08-npc.md)。
