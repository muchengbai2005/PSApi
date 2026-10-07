# 05 · 内置 API 参考：总览

> 上一章（[04 PSScript 语言](../04-psscript/README.md)）讲的是**语言**：变量、函数、事件。
> 本章是**游戏层**——PSScript 脚本里能调的全部内置命名空间：
> `state` 存档、`time/player/rep/crime/power` 世界数值、`shop` 商店查询、
> `store_event` 商店事件、`items/quality/machine` 物品、`npc` 自定义客户、
> `ui` 界面、`inject` 物品池注入。
> 语言层的 `log` 与 `bus` 见 [04 · 09 事件与 on 块](../04-psscript/09-events.md)，此处不重复。

## 本章导航

| 篇 | 命名空间 | 你能学到 |
|---|---|---|
| [02 state](02-state.md) | `state` | 按存档槽隔离的 KV 存储：记账、防重、跨天状态 |
| [03 世界查询](03-world.md) | `time` `player` `rep` `crime` `power` | 天数/现金/声望/治安档案/五势力数值 |
| [04 商店查询](04-shop.md) | `shop` | 状态总览/吸引力/当前客户/今日队列/展示柜/禁售 |
| [05 商店事件](05-store-event.md) | `store_event` | 注册/排队自定义商店事件 + started/ended 回调 |
| [06 物品](06-items.md) | `items` `quality` `machine` | 发放/检索/品质/机器进度/电力/使用次数/物品目录 |
| [07 物品池注入](07-inject.md) | `inject` | 四通道注入 + loot_pool + 运行时调节 |
| [08 NPC 基础](08-npc.md) | `npc` | register 配置键总表、池加权、手动排班（完整参考在进阶章） |
| [09 UI 函数](09-ui.md) | `ui` | 面板开关/动态修改/动态构建/机器绑定面板（.psui 格式见下一章） |
| [10 句柄](10-handles.md) | （对象，非命名空间） | 客户句柄与物品句柄的成员、方法、失效行为 |

## 命名空间全景

写脚本时按下表速查。**"注册"** 指脚本环境里这个名字是否存在；
**"可用"** 指调用能否成功——很多 API 依赖游戏场景，在主菜单或加载期调用会得到
`运行错误: xxx 当前不可用(不在存档场景中)` 一类友好报错。

| 命名空间 | 函数数 | 注册条件 | 运行时可用性要点 | 篇 |
|---|---|---|---|---|
| `state` | 4 | 无条件 | 任何时候；主菜单期写入落到 `global` 槽 | [02](02-state.md) |
| `time` | 3 | 无条件 | 需存档场景，否则运行错误 | [03](03-world.md) |
| `player` | 2 | 无条件 | 需存档场景 | [03](03-world.md) |
| `rep` | 2 | 无条件 | 需存档场景且声望表已建 | [03](03-world.md) |
| `crime` | 2 | 无条件 | `commit` 需存档场景；`exempt_guns` 任何时候 | [03](03-world.md) |
| `power` | 2 | 无条件 | 需商店场景（StoreOperationManager） | [03](03-world.md) |
| `shop` | 7 | 无条件 | 查询类不在对局给兜底值（`false`/`null`/空表），不报错 | [04](04-shop.md) |
| `store_event` | 2 | 需事件服务 | 注册任意时机；`queue` 需商店场景 | [05](05-store-event.md) |
| `items` | 16 | 无条件 | 发放/检索类不在对局返回 `false`/`null`/空表；`value/name` 给 `-1`/`null` | [06](06-items.md) |
| `quality` | 3 | 无条件 | 需对局内对象 | [06](06-items.md) |
| `machine` | 3 | 无条件 | 需对局内对象 | [06](06-items.md) |
| `npc` | 4 | 需 NPC 服务 | 注册任意时机（纯托管）；生成在对局内 | [08](08-npc.md) |
| `ui` | 27 | 需 UI 服务 | 开面板/改元素需对局内（CustomUIManager） | [09](09-ui.md) |
| `inject` | 10 | 需注入服务 | 注册任意时机；注入动作发生在对局内 | [07](07-inject.md) |

> "需 xx 服务"的四个命名空间在正常游戏（PSApi.Events 完整加载）中**总是存在**，
> 条件注册只影响框架自己的无头测试台。玩家实际使用时无需关心。

## 通用约定

全部游戏层 API 遵守同一套纪律（源码级保证，不是君子协定）：

1. **参数校验先于游戏调用。** 参数个数/类型不对，在碰任何游戏对象之前就抛
   `PsRuntimeError`，错误消息带正确用法提示与行号。例如
   `items.give(123)` → `items.give 的 id 参数须为非空字符串, 实为 int`。
2. **游戏调用包 try/catch。** 游戏内部异常被转成带行号的运行错误，不会闪退游戏。
3. **单例缺失给友好错误。** 不在存档/商店场景时，报
   `xxx 当前不可用(不在存档场景中)`，而不是空引用崩溃。
4. **查询类尽量兜底。** `shop.*` / `items.*` 的多数查询不在对局时返回
   `false` / `null` / 空表 / `-1`，让你可以无保护地写 `if` 判断。
5. **错误格式**：`[pss] 运行错误: 第 N 行 in 顶层/handler: 消息`（详见
   [04 · 10 错误与调试](../04-psscript/10-errors.md)）。

## 三条贯穿全章的规则

**① 物品 id 的写法。** 接受三种形式，框架统一归一化（`"game:"` 前缀大小写不敏感，剥掉后使用）：

| 写法 | 含义 | 适用 |
|---|---|---|
| `"game:newspaper"` | 原版物品（显式前缀） | 一律可用 |
| `"newspaper"` | 原版物品（裸 id） | 一律可用 |
| `"my_pack:my_item"` | 包物品（全限定） | 包物品必须带包前缀 |

**② "顶层"与"handler"两个执行时机。** 脚本顶层代码在**加载期**执行一次——主菜单、
无对局，此时只有注册类 API（`inject.*` 注册、`npc.register`、`store_event.register`）
和 `state.*` 适合调用；查询/修改类请放进 `on day_wake():`、`on shop_opened():` 等
事件块，在对局内触发。各函数的"时机"栏会标注。

**③ 句柄是活的指针。** `items.find()` 返回的物品句柄、事件里的 `event.client` 客户句柄，
指向游戏内对象。对象被销毁/客户离店后再访问，报
`物品句柄已失效` / `客户句柄已失效` 友好错误——拿到句柄后**尽快用**，不要跨天存。
句柄成员总表见 [10 句柄](10-handles.md)。

## 版本速查

本章标注"自 vX.Y.Z"的函数为后期新增（v1.x/v0.9.x 为合并前 Events/Items
双组件的能力版本号，能力在合并后全量保留），当前游戏内置版本：
**PSApi v2.0.0**（单 dll，合并前能力史推进至 Events v1.48.8 / Items v0.9.21）。

| 版本 | 新增/变更（本章相关） |
|---|---|
| v1.4.0 | `ui.machine_*` 机器绑定面板系列 |
| v1.6.0 | `items.power / power_draw / uses / uses_max / use / use_init` |
| v1.7.0 | `items.find_all`、`shop.showcase_items / block_sale / unblock_sale`、`crime.exempt_guns` |
| v1.9.0 | `inject.sell_shelf / doctor` 的 `{uses=N}` 选项；`npc` `auto_leave` 键 |
| v1.9.1 | `items.give_counter`（v0.9.2 Items 侧） |
| v1.10.0 | `inject.loot_pool`；`inject.sell_shelf` 的 count 支持 `"min-max"` 区间 |
| v1.11.0 | `items.name`；注入类 API 执行点存在性校验（无效 id 跳过+告警一次） |
| v1.13.0 | `npc` `price` 键缺省改为中立 1.0（框架不内置折扣） |

---

**本章完。** 下一篇：[02 · state：存档状态](02-state.md)。
