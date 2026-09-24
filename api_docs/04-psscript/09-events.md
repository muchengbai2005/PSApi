# 04-09 · 事件与 on 块

> 事件是 PSScript 与游戏交互的核心通道：游戏里发生的事（开店、醒来、成交、
> 客户进店）都会广播成事件，你的 `on` 块订阅它们。本篇讲 on 块语法、
> 全部 15 个桥接事件的载荷表、`event` 变量、以及 `bus.emit` 自定义事件。

## on 块：订阅一个事件

```pss
# 标识符形式: 引擎内置事件(查别名表)
on shop_opened():
    log.info("开店了, 今天是第 {event.rel_day} 天")

# 字符串形式: 自定义事件(原样订阅, 不查表)
on "my_pack:deal_done":
    log.info("一笔交易完成, 买家 {event.buyer}")
```

语法规则：

- **只能在文件顶层**（函数里、if 里写 on 都是编译错误）。
- 事件名两种形式：**标识符**（下划线风格，查内置别名表）或**字符串**（原样订阅）。
- 括号必须是空括号 `()`——事件数据不通过参数传入，而是经 **`event` 变量**注入。
- 同一文件可以有任意多个 on 块；同一事件也可以被多个 on 块订阅（都会执行）。

## event 变量：载荷怎么读

handler 执行前，引擎把事件载荷注入到包全局的 `event` 变量（dict）：

```pss
on day_wake():
    # 载荷是 {"day": 12, "rel_day": 3, "weekday": 4}
    log.info("新的一天: 总天数 {event.day}, 开业第 {event.rel_day} 天")
```

载荷的包装规则（源码 `BuildEventDict`）：

| 载荷原始类型 | event 变量 |
|---|---|
| dict | 原样（字段直接 `event.字段名`） |
| 字符串 | `{"value": "字符串"}`（读 `event.value`） |
| null / 无载荷 | 空 dict `{}` |
| 其他标量 | `{"value": 值}` |

因为 dict 缺键返回 null，`event.xxx` 读不存在的字段不会炸——所以才有
`if event.save_slot:` 这种防御写法。**用之前查下面的载荷表**，别靠猜。

## 15 个桥接事件全表

标识符事件名经别名表映射到内部事件 id。全部事件（载荷字段来自源码实测）：

| on 写法 | 内部 id | 触发时机 | event 载荷 |
|---|---|---|---|
| `scene_loaded()` | psapi.scene.loaded | 每次进/切场景 | `value`: 场景名字符串 |
| `game_loaded()` | psapi.game.loaded | 读档/新开对局完成 | `save_slot`: 槽位号, `run_id`: 本局标识 |
| `day_wake()` | psapi.day.wake | 每天早晨醒来 | `day`: 总天数, `rel_day`: 开业第几天, `weekday`: 周几(0=周一, 4=周五) |
| `day_sleep()` | psapi.day.sleep | 每晚睡觉 | 同上 |
| `shop_opened()` | psapi.shop.opened | 卷帘门拉开(开店) | 同上 |
| `shop_closed()` | psapi.shop.closed | 卷帘门关闭(打烊) | 同上 |
| `store_leaving()` | psapi.store.leaving | 离开商店出门 | `day` |
| `store_returning()` | psapi.store.returning | 回到商店 | `day` |
| `night_services()` | psapi.night.services | 夜间结算时刻 | `day`, `rel_day`, `weekday` |
| `store_event_started()` | psapi.store_event.started | 商店事件激活 | `event_id` |
| `store_event_ended()` | psapi.store_event.ended | 商店事件结束 | `event_id` |
| `customer_generated()` | psapi.customer.generated | 每位客户生成(进门) | `client`: 客户句柄, `id`, `name`, `source`(来源) |
| `dialogue_choice()` | psapi.dialogue.choice | 玩家做出对话选项 | `dialogue_id`, `title`, `choice`: 选项序号, `npc_id`, `npc_name` |
| `trade_completed()` | psapi.trade.completed | 每笔买卖成交 | `direction`: "buy"/"sell", `item_id`, `amount`: 金额, `npc_id`, `npc_name` |
| `tick()` | psapi.tick | **每秒一次**（对局内） | `day`, `rel_day`, `weekday` |

字段语义（源码 `GameHooks.GameDay`）：

- `day` = 游戏内部总天数计数（`StoreStation.GetDayCounter()`）；
- `rel_day` = **开业日 = 第 1 天** 的相对天数；
- `weekday` = `(rel_day-1) % 7`，**0 = 周一，4 = 周五**。
- `time` 命名空间也提供 `time.day()/rel_day()/weekday()`，与事件载荷同源
  （详见 [05 API 参考](../05-api-reference/README.md)）。

### 未知事件名的容错

写了表外的标识符事件名，编译**不报错**：引擎按 `psapi.<名>.替换下划线` 订阅并打警告：

```pss
on christmas_parade():        # 不在别名表
    pass
# 控制台: [pss] xxx.pss:3: 未知事件 'christmas_parade', 按 'psapi.christmas.parade' 订阅(未来桥接该事件后自动生效)
```

这是**面向未来**的设计：PSApi 后续版本桥接新事件后，老脚本自动生效。
但当下它不会触发——想现在就生效用 `bus.emit` 自定义事件。

## handler 的执行语义

1. **触发即执行**：事件发生时（游戏主线程，同步），引擎注入 `event` 后执行你的块。
2. **每次触发重置预算**：步数/递归熔断按"每次触发"独立计（见 [10 错误与调试](10-errors.md)）。
3. **`return` 提前结束本次 handler**（不返回值给谁，纯粹是控制流）：
   ```pss
   on shop_opened():
       var key = "demo_day" + str(time.day())
       if state.has(key):
           return                  # 今天已经处理过, 直接退出
       state.set(key, true)
       # ... 首次逻辑
   ```
   （e5_demo.pss 的"每日一次"防重就是这段的真实原型。）
4. **异常隔离**：你的 handler 抛运行错误只记日志（`[pss] 文件:行 in 事件名: 消息`），
   不影响同事件的其他订阅者，更不会崩游戏。
5. **event 是共享名**：`event` 注入在包全局，handler 结束后恢复旧值。不要把
   重要数据存在叫 `event` 的变量里——每次事件触发都会被覆盖再还原。

## bus.emit：自定义事件

脚本可以广播自己的事件，这是**跨包协作**与"信号解耦"的唯一通道：

```pss
# 发出方: 事件名建议 "包id:事件名", 载荷必须是 dict(可省略)
bus.emit("my_pack:night_beacon", {day = time.day()})
bus.emit("my_pack:simple_signal")            # 无载荷 = 空 dict

# 接收方(本包或其他包): 字符串形式订阅
on "my_pack:night_beacon":
    log.info("收到信标, day={event.day}")
```

规则（源码 `PsBuiltins.CreatePackEnv`）：

- 事件名须为非空字符串；**建议 `包id:事件名` 命名**避免撞车（e3_demo.pss 即此风格）。
- 载荷必须是 dict（或省略）；发字符串/数字会被拒绝（`bus.emit() 的 payload 须为 dict`）。
- 订阅方须用**字符串形式** `on "..."`（标识符形式会查别名表+加 `psapi.` 前缀，接不到）。
- 字符串事件名**不支持插值**（`on "{PACK}:x":` 是编译错误：事件名不支持插值），
  也不可为空串。
- **跨包隔离只针对变量/函数**，bus.emit 广播所有人都能订。

真实例子（e3_demo.pss）：夜间结算每 3 天发一次信标，订阅方计数存档：

```pss
on night_services():
    if time.day() % 3 == 0:
        bus.emit("example_hello:night_beacon", {day = time.day()})

on "example_hello:night_beacon":
    var n = state.get("beacon_count", 0) + 1
    state.set("beacon_count", n)
    log.info("收到 night_beacon(第 {n} 次): day={event.day}")
```

### 加载顺序保证

引擎**先登记所有包的全部 on 块，再执行顶层语句**（见
[02 第一个脚本](02-first-script.md)）。因此顶层 `bus.emit` 发出的事件，
同批加载的订阅者必定已就位——你不需要操心"A 包加载在 B 包后面"这类顺序问题。

## 事件 + state：实现"只做一次"

事件会反复触发，而很多逻辑只想执行一次。`state`（按存档槽隔离的 KV 存储，
详见 [05 API 参考](../05-api-reference/README.md)）是标准答案：

```pss
on shop_opened():
    if not state.has("flyer_queued"):
        state.set("flyer_queued", true)
        store_event.queue("example_hello:street_flyer")
```

两种变体：

```pss
# 变体1: 每天一次(用日期做键)
on shop_opened():
    var key = "daily_" + str(time.day())
    if state.has(key):
        return
    state.set(key, true)

# 变体2: 计数器(每次触发 +1)
on trade_completed():
    var n = state.get("trade_count", 0) + 1
    state.set("trade_count", n)
```

## 事件与 PSUI 回调的关系

PSUI 面板上的按钮/开关不通过 `on` 订阅，而是在 `.psui` 文件里**引用函数名**：

```pss
# .psui 里: button 的 on_click = "e6_give_news" (字符串引用包内函数)
func e6_give_news():              # 被 UI 调用, 不是事件 handler
    items.give("game:newspaper", 1)
```

机制：引擎启动时校验 `.psui` 引用的函数在包脚本里是否存在（缺失只警告）；
点击时直接调用该函数。回调参数约定（如 on_change 收 `{elem, value}` dict）
详见 [06 PSUI 教程](../06-psui/README.md)。**本节只需记住：UI 回调是函数，不是事件。**

---

下一篇：[10 · 错误与调试](10-errors.md)
