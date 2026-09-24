# 05 · 04 shop：商店查询

> `shop` 是**只读为主**的商店观测台：今天队列里排着谁、吸引力多高、
> 当前客户想买什么、展示柜里摆着什么。配合 `inject.force_client` 还能主动补货。
> 查询类函数**不在对局时给兜底值**（`false`/`null`/空表），不报错。

## 函数表

| 函数 | 签名 | 返回 | 一句话 |
|---|---|---|---|
| 状态总览 | `shop.status()` | `dict` | 在不在对局/距交租/队列长度/当前客户 |
| 吸引力 | `shop.attract()` | `dict \| null` | 吸引力数值与各档位阈值 |
| 当前客户详情 | `shop.current_client()` | `dict \| null` | 意图/现金/预算/收购清单/携带货物 |
| 今日队列 | `shop.queue()` | `[dict]` | 排队客户逐个详情（含 `ptr`） |
| 展示柜物品 | `shop.showcase_items()` | `[物品句柄]` | 展示柜内全部物品（v1.7.0） |
| 禁售 | `shop.block_sale(id)` | `null` | 注册后任何客户都不买此 id（v1.7.0） |
| 解禁 | `shop.unblock_sale(id)` | `null` | 移出禁售注册表（v1.7.0） |

## shop.status() — 状态总览

任何时候可调；返回字段：

| 字段 | 类型 | 含义 |
|---|---|---|
| `in_game` | `bool` | 是否在对局中（不在则后面字段全是默认值） |
| `day_until_rent` | `int` | 距交租还有几天 |
| `queue` | `int` | 今日待客队列长度 |
| `current_name` | `string` | 当前客户名字（无为空串） |
| `current_intent` | `string` | 当前客户意图中文标签 |

```pss
on shop_opened():
    var s = shop.status()
    if s["in_game"]:
        log.info("距交租 {s['day_until_rent']} 天, 队列 {s['queue']} 人, "
               + "当前客户 {s['current_name']}({s['current_intent']})")
```

## shop.attract() — 吸引力与档位

返回吸引力可视化数据；**不在对局 = `null`**。字段：

| 字段 | 类型 | 含义 |
|---|---|---|
| `value` | `int` | 商店基础吸引力 |
| `spawn_chance` | `int` | 当前随机客户生成率 |
| `normal_count` | `int` | 当前普通常客数量 |
| `t1` / `t2` / `t3` | `int` | 常客数量 +1/+2/+3 档的吸引力阈值 |
| `extra10` / `extra25` / `extra50` | `int` | 客户预算 +10%/+25%/+50% 的阈值 |
| `rich400` / `rich650` | `int` | 富客（预算 400/650 档）阈值 |

阈值类字段是游戏的静态设定（读不到时为 `-1`），做"还差多少吸引力到下一档"
提示面板时很好用。

## shop.current_client() — 当前客户详情

店内在服务的客户（`currentClientInstance`，兜底 `lastVisitedClient`——夜间商人等
特殊客户走兜底）；**无当前客户/不在对局 = `null`**。字段：

| 字段 | 类型 | 含义 |
|---|---|---|
| `name` | `string` | 显示名 |
| `intent_key` | `string` | 意图枚举名（如 `SELL`） |
| `intent` | `string` | 意图中文标签（见下表） |
| `cash` | `int` | 身上现金 |
| `has_budget` | `bool` | 是否使用独立预算 |
| `budget` | `int` | 预算（`has_budget` 为真才有意义） |
| `faction` | `string` | 所属势力 factionId（可为空串） |
| `buying` | `[string]` | 想收购物品 id 列表 |
| `tags` | `[string]` | 收购标签列表（按 tag 匹配的收购） |
| `carried` | `[string]` | 客户携带的货物 id（卖给你的东西） |
| `front` | `[string]` | 展示区里**非玩家拥有**的货物 id（最多列 20 条） |
| `front_total` | `int` | 非玩家拥有货物总件数 |

**意图中文标签全表**（`intent_key` ↔ `intent`）：

| key | 中文 | key | 中文 |
|---|---|---|---|
| `BUY` | 收购 | `RENT` | 收租 |
| `SELL` | 出售 | `LOAN_SHARK` | 放贷 |
| `SELLNBUY` | 买卖兼有 | `SPECIAL` | 特殊 |
| `INSPECTION` | 检查 | `INFORMATION_DEALER` | 情报贩子 |
| `DIALOGUE` | 对话 | `PROCUREMENT_OFFER` | 采购委托 |
| `BARTER` | 以物易物 | `PROCUREMENT_COLLECT` | 取货 |
| `WHOLESALE` | 批发 | `APPRAISAL_SERVICE` | 鉴定 |
| `GUNSMITH` | 枪匠 | `EXPEDITION` | 远征 |

## shop.queue() — 今日待客队列

按服务顺序返回今日队列；**不在对局 = 空表**。每行字段：

| 字段 | 类型 | 含义 |
|---|---|---|
| `idx` | `int` | 队内序号 |
| `name` | `string` | 显示名 |
| `intent_key` / `intent` | `string` | 意图（同上表） |
| `cash` | `int` | 现金 |
| `ptr` | `int` | 客户指针——**`inject.force_client(ptr)` 的定位键，仅当日有效** |
| `buying` | `[string]` | 收购清单 |
| `tags` | `[string]` | 收购标签 |

```pss
on shop_opened():
    for q in shop.queue():
        log.info("#{q['idx']} {q['name']}({q['intent']}) 现金{q['cash']} 想收 {q['buying']}")
```

> `ptr` 是指针数值，不要存进 `state` 跨天用（客户对象随时销毁，指针会失效）；
> 标准用法是同一轮对话/面板里 `queue()` 拿到后立刻传给 `inject.force_client`。

## shop.showcase_items() — 展示柜枚举（v1.7.0）

返回展示柜（商品展示区）内全部物品的**物品句柄**数组（空柜 = 空表）。
句柄读 `id/count/value`，见 [10 句柄](10-handles.md)。

```pss
for it in shop.showcase_items():
    log.info("展示柜: {it.id} x{it.count} 单价{it.value}")
```

## shop.block_sale / unblock_sale — 禁售注册表（v1.7.0）

```pss
shop.block_sale("my_pack:fake_cert")   # 注册后任何客户都不买它
shop.unblock_sale("my_pack:fake_cert") # 解除
```

- 拦截范围：展示柜自动售货 + 柜台交易两条路径都拦。
- **仅内存**，不进存档——包脚本**顶层调用一次**即可（加载期注册，重启后自动恢复），
  无需放进事件里。
- 典型用途：仿制证书这类"摆出来给人看但系统会误卖掉"的剧情物品。
- id 走统一归一化（`"game:"` 前缀剥离），原版/包物品都能用。

## 本篇函数速查

```pss
shop.status()               # {in_game, day_until_rent, queue, current_name, current_intent}
shop.attract()              # {value, spawn_chance, normal_count, t1..t3, extra10/25/50, rich400/650} | null
shop.current_client()       # 客户详情 dict | null
shop.queue()                # [{idx, name, intent, intent_key, cash, ptr, buying, tags}]
shop.showcase_items()       # [物品句柄]
shop.block_sale(id)         # 禁售
shop.unblock_sale(id)       # 解禁
```

---

**本篇完。** 下一篇：[05 · store_event：商店事件](05-store-event.md)。
