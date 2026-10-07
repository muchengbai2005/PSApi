# 05 · 05 store_event：商店事件

> 商店事件是原版的"今日运势"系统：开局抽事件（街区集市/帮派火并/舆论风波……），
> 影响当天客流与新闻。`store_event` 命名空间让你的包把**自定义事件**
> 注入原版三池参与抽选，或**定点排队**必出。

## 心智模型

原版有三个事件蓝图池：`normal`（常规）、`threat`（威胁）、`cosmetic`（风味）。
每天开局从池里按权重（`odd`）抽。你注册的自定义事件就是往池里加一张自己写的牌——
事件名、新闻文案、持续天数都由你定义；抽没抽中交给原版权重系统。
配套的 `store_event_started / ended` 桥接事件还能让你**感知包括原版在内的全部**
事件起止。

## 函数表

| 函数 | 签名 | 返回 |
|---|---|---|
| 注册 | `store_event.register(id[, config])` | `null` |
| 排队 | `store_event.queue(id[, delay_days = 0])` | `null` |

## store_event.register(id[, config])

把一张事件蓝图注册进池。**加载期顶层调用**（和 `inject.*` 一样属于注册类 API）。

- `id`：事件 id，**建议带包前缀**（如 `"my_pack:street_flyer"`）避免与原版/他包撞名。
  同 id 重复注册报运行错误（`store_event 'xxx' 已注册(重复 id)`）。
- `config`：可选 dict，全键如下（全部有默认值）：

| 键 | 类型 | 默认 | 含义 |
|---|---|---|---|
| `pool` | `string` | `"normal"` | 入哪个池：`normal` / `threat` / `cosmetic`（其他值报错） |
| `odd` | `int` | `20` | 抽选权重（与池内其他蓝图比大小） |
| `name` | `string` | = id | 事件显示名（UI/新闻标题用） |
| `news` | `string` | = name | 新闻标题（不填同 name） |
| `description` | `string` | `""` | 新闻描述文案 |
| `duration` | `int` | `1` | 持续天数 |
| `cooldown` | `int` | `0` | 冷却天数 |
| `hidden` | `bool` | `false` | 是否对玩家隐藏（不出现在事件 UI） |
| `importance` | `int` | `0` | 重要度（影响展示排序） |
| `slip` | `bool` | `false` | `true` 时事件激活会生成一张实体纸条物品（时长至少 1 天） |

未知配置键**警告并跳过**（日志可见），不会报错。

**池未就绪挂起**：主菜单期静态池还没建时，注册会先记账挂起，下个 `day_wake`
自动补注入（日志 `store_event 'xxx': 事件池未就绪, 挂起至场景就绪后注入`）。
所以顶层注册永远安全。

## store_event.queue(id[, delay_days])

不等抽选，**直接让事件发生**：

- `delay_days = 0`（默认）：立即激活（走原版 `ActivateEvent`，当天生效）。
- `delay_days > 0`：排到第 `当前天数 + delay_days` 天（走原版 `QueueFuturEvent`）。

前置条件：id 必须已 `register`；且需在**商店场景**中（否则报
`store_event.queue 当前不可用(不在商店场景中)`）。事件池没就绪时也报错，
提示稍后再 queue。

```pss
# 教学写法（对照 ex20_api_shop_events）: 首次开店必出一次"街头传单"
store_event.register("my_pack:street_flyer", {
    pool = "normal",
    odd = 20,
    name = "街头传单",
    description = "有人在附近派发传单, 顾客议论纷纷。",
    duration = 1,
    slip = true,
})

on shop_opened():
    if not state.has("flyer_queued"):
        state.set("flyer_queued", true)
        store_event.queue("my_pack:street_flyer")
```

注意 `state` 防重——`shop_opened` 一天可触发多次，不记账会连发。
可运行的商店事件对照包见
[ex20_api_shop_events](../../examples/ex20_api_shop_events/README.md)。

## 配套桥接事件：started / ended

框架每天 `day_wake` 时对当前激活事件做差集，发布两个总线事件（见
[04 · 09 事件与 on 块](../04-psscript/09-events.md)）：

| on 块 | payload | 时机 |
|---|---|---|
| `on store_event_started():` | `event.event_id` | 任一商店事件（含原版）当天开始 |
| `on store_event_ended():` | `event.event_id` | 任一商店事件结束 |

```pss
on store_event_started():
    log.info("商店事件开始: {event.event_id}")

on store_event_ended():
    log.info("商店事件结束: {event.event_id}")
```

`event_id` 对原版事件是原版 id，对你注册的就是你写的 id——据此可以做自己事件的
专属钩子（`if event.event_id == "my_pack:street_flyer": ...`）。

## 已知边界

- 事件的**玩法效果**（客流增减等）由原版事件系统自身驱动；自定义事件能控制的是
  文案/时长/池属/权重这些"蓝图参数"。想要"事件期间我的商品热卖"这类深度联动，
  通常用 `on store_event_started` + `state` 记标记，在 `shop.*` / `inject.*` 侧
  自行实现。
- `odd` 的实际命中率取决于同池全部蓝图的权重和——原版池会随游戏版本变化，
  想必出就用 `queue`。

---

**本篇完。** 下一篇：[06 · items / quality / machine：物品](06-items.md)。
