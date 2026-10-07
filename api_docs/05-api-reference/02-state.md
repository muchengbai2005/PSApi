# 05 · 02 state：存档状态

> `state` 是 PS-API 给脚本的**按存档槽隔离的 KV 存储**：
> 一个键一个值，值自动 JSON 序列化，随存档槽落盘，换存档互不串扰。
> 它是模组脚本的"记忆"——记账、防重、跨天状态、剧情进度全靠它。

## 它解决什么问题

你给玩家发过一次新手礼包，不希望他重进游戏再领一次；
你的剧情走到第三章，希望读档后从第三章继续；
玩家在存档 A 做的事，不能影响存档 B。这三件事就是 `state` 的三件事：
**持久**（写进文件）、**按槽隔离**（换档自动换库）、**脚本值友好**（dict/list 直接存）。

## 函数表

| 函数 | 签名 | 返回 |
|---|---|---|
| 读 | `state.get(key[, 默认值])` | 存的值（自动反序列化）；无此键且给了默认值 → 默认值；无默认值 → `null` |
| 写 | `state.set(key, value)` | `null`（value 会经 JSON 编码后存储） |
| 有无 | `state.has(key)` | `bool` |
| 删 | `state.del(key)` | `null` |

四个函数任何时候都可调用（主菜单也行，只是写进 `global` 槽，见下节）。

## 值的序列化

`state.set` 存任何 PSScript 值（int/float/bool/string/array/dict/null）时自动做
JSON 编码，`state.get` 读回时自动解码——**存什么类型，读回什么类型**：

```pss
state.set("score", 42)                 # 存 int
state.set("tags", ["a", "b"])          # 存数组
state.set("meta", {"ok": true, "n": 3})  # 存字典

state.get("score")    # 42 (int)
state.get("tags")     # ["a", "b"] (array)
state.get("meta")     # {"ok": true, "n": 3} (dict)
state.get("nope")     # null
state.get("nope", 0)  # 0 (默认值)
```

函数、命名空间、句柄**不能存**（不可 JSON 序列化）——存了会在编码时报运行错误。
想存"指向某物品"，存它的 `id` 或 `uid`（见 [10 句柄](10-handles.md)），用时可再 `items.find` 找回。

## 存储位置与存档槽隔离

- **文件**：`UserData/PSApi/state/<槽位>/<包id>.json`——**每个包一个文件**，
  包与包之间完全隔离，你只能看见自己的键值。
- **槽位**：取自当前存档（`saveSlotId`）；主菜单等读不到槽位的场合归入 `global`。
- **换档自动重载**：切换存档槽时框架先落盘旧槽、再从新槽加载——脚本侧无感。
- **落盘时机**：框架在**游戏退出 / 切换存档槽**等时点统一落盘（模组侧没有暴露
  手动 flush 函数）。注意：游戏内 SAVE **不**触发落盘，`state` 数据活在内存里；
  极端情况（游戏崩溃）会丢掉本次会话未落盘的写入。

实际文件长这样（缩进 JSON，可直接人工检查/编辑）：

```json
{
  "e2_boot_seen": 3,
  "e2_last_load": "{\"ok\": true, \"tags\": [\"e2\", \"demo\"]}",
  "friday_paid_day": 12
}
```

注意：外层 JSON 的 value 一律是**字符串**（内层才是你的值的 JSON 编码）。
这是框架的骨架设计（string→string，规避嵌套序列化的坑），脚本侧完全无感。

## 经典范式：防重记账

"每周五津贴"是标准写法（对照教学包
[ex19_api_state_world](../../examples/ex19_api_state_world/README.md)）——
**先查记账、后办事、再记账**：

```pss
on day_wake():
    if time.weekday() == 4:                      # 周五（0=周一, 4=周五）
        var paid_day = state.get("friday_paid_day", -1)
        if paid_day != time.rel_day():           # 今天还没发过
            player.add_cash(100)
            state.set("friday_paid_day", time.rel_day())
            log.info("周五津贴 +100, 现金 {player.cash()}")
```

"首次开店激活一次事件"同款（对照
[ex20_api_shop_events](../../examples/ex20_api_shop_events/README.md)）：

```pss
on shop_opened():
    if not state.has("flyer_queued"):
        state.set("flyer_queued", true)
        store_event.queue("my_pack:street_flyer")
```

为什么需要记账：`day_wake` / `shop_opened` 等事件**不保证一天只触发一次**
（重进商店、读档重开都会再触发），用 `state` 按"天"记号才能保证幂等。
更进一步的写法——把天编进键名，每个键天然只用一次：

```pss
var key = "daily_" + str(time.day())
if state.has(key):
    return
state.set(key, true)
# ……每日一次的演示逻辑
```

## 设计备忘

- **键名建议带包/功能前缀**（如 `myquest_chapter`），因为你共享一个文件命名空间
  （虽然文件按包隔离，但同一包内不同脚本文件的键会混在一起）。
- **`state.del(key)` 内部等价于 `set(key, null)`**：从字典移除该键。
- **不要在 `state` 里存大对象**（如几百项的数组）：每次 set 全量重写文件。
- `state` 与游戏存档是**两套系统**：游戏读档回到过去，`state` 不会跟着回滚——
  它更像是"绑在这个存档槽上的模组数据"。剧情向模组要注意这一点：读档回退
  游戏进度但 `state` 记账仍在，属预期行为。

---

**本篇完。** 下一篇：[03 · 世界查询：time / player / rep / crime / power](03-world.md)。
