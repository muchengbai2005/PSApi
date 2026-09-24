# 05 · 10 句柄参考：client 与 item

> 句柄（handle）是**指向游戏内活对象的引用**：客户句柄包着店里的客户，
> 物品句柄包着一件具体物品。它们不是 dict、不是命名空间——是一类特殊值，
> 用 `.` 读成员、调方法，但**只读成员、无自定义字段**。

## 哪里会拿到句柄

| 句柄 | 来源 |
|---|---|
| 客户句柄 | `on customer_generated():` 的 `event.client`；`inject` 动态函数的 `client` 参数 |
| 物品句柄 | `items.find` / `items.find_all` / `machine.find` / `shop.showcase_items` / `ui.get_slot_item(s)` / `ui.machine_spawn` / 机器面板槽位读值 |

## 客户句柄

包装游戏 `StoreClient`。四个只读成员 + 两个方法：

| 成员 | 返回 | 含义 |
|---|---|---|
| `client.id` | `string` | 客户 id |
| `client.name` | `string` | 显示名 |
| `client.faction` | `string` | 所属势力 |
| `client.cash` | `int` | 身上现金 |
| `client.intent` | `string` | 意图枚举名（如 `"SELL"`；中文标签换 [`shop.*`](04-shop.md)） |

| 方法 | 签名 | 作用 |
|---|---|---|
| 改现金 | `client.set_cash(amount)` | 直接设现金（`(int)` 截断） |
| 改台词 | `client.say(channel, text)` | 替换该通道的整段对话 |

```pss
on customer_generated():
    var c = event.client
    log.info("{c.name} 进店({c.intent}), 带了 {c.cash} 块")
    if c.intent == "SELL" and c.cash < 100:
        c.set_cash(100)                       # 穷客户兜底
        c.say("main", "最近手头紧, 但我带了点好货。")
```

### say 的九个通道

`channel` 对应客户对话系统的九个槽位（大小写不敏感；未知通道报错并列出全部可用名）：

| 通道 | 播出时机 |
|---|---|
| `main` | 进店开场白 |
| `accept` | 成交时（同时写"最后一句"变体，保证成交必播新词） |
| `all_done` | 全部搞定离店时 |
| `repeat` | 玩家重复搭话 |
| `wrong_item` | 卖家摆错货时 |
| `right_item` | 摆对货时 |
| `interogation` | 被治安盘查时 |
| `glasse` | 戴眼镜检查时 |
| `on_arrest` | 被逮捕时 |

注意 `say` 是**整段替换**：原生多段对话会被替换为这一条单段（署名自动用客户名）。
要恢复原生台词只能重进对局。

## 物品句柄

包装游戏 `GameItem`。四个只读成员，**无方法**——操作全走命名空间函数
（`quality.set` / `machine.progress` / `items.consume` ……）：

| 成员 | 返回 | 含义 |
|---|---|---|
| `item.id` | `string` | 物品 id |
| `item.count` | `int` | 堆叠数量 |
| `item.value` | `int` | 单价 |
| `item.uid` | `int` | 存档内唯一号（`ui.machine_find_uid` 按它找机器） |

```pss
var it = items.find("game:scrap_metal")
if it != null:
    log.info("找到 {it.id} x{it.count}, 单价 {it.value}, uid={it.uid}")
    var ok = items.consume(it)          # 操作走命名空间, 不走句柄
```

## 失效行为（重要）

句柄是**活指针**：客户离店/对局结束、物品被卖出/销毁后，底层对象不复存在。
此后访问任何成员，得到带行号的友好运行错误而不是闪退：

```
[pss] 运行错误: 第 12 行 in handler: 客户句柄已失效(客户对象不存在)
[pss] 运行错误: 第 34 行 in handler: 物品句柄已失效(物品对象不存在)
```

防御性写法三条：

1. **判空再用**——查找类函数找不到时返回 `null`（不是报错）：
   ```pss
   var it = items.find("game:scrap_metal")
   if it == null:
       return
   ```
2. **尽快用完**——句柄当轮事件/当轮对话内用掉；跨天持有请存 `id` 或 `uid`，
   用时重新 `find`。
3. **别把句柄存进 state**——句柄不可序列化（见 [02 state](02-state.md)），
   存 `id`/`uid` 才是持久引用。

## 句柄 vs 命名空间：一张分工表

| 想做的事 | 用什么 |
|---|---|
| 看物品是什么/多少/多少钱 | `item.id / count / value` |
| 给物品打/读品质 | `quality.set(item, ...) / get(item)` |
| 看机器进度/产出 | `machine.progress(item) / producing(item)` |
| 销毁物品 | `items.consume(item)` |
| 抽电池/用次数 | `items.power_draw(item, n) / items.use(item)` |
| 看客户是谁/多有钱 | `client.name / cash` |
| 改客户现金/台词 | `client.set_cash(n) / say(通道, 文本)` |
| 客户详细信息（预算/收购单） | [`shop.current_client()`](04-shop.md)（返回 dict，不是句柄） |

---

**本篇完。** 内置 API 参考到此收束。接下来：[06 · PSUI 教程](../06-psui/README.md)——
自己写一张 `.psui` 面板，把本章的 `ui.*` 函数全部用起来。
