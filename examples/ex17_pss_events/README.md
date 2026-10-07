# ex17_pss_events · 示例 17：事件全景（PSScript 语言篇 6/7）

> **演示知识点**（对应文档 [04-psscript/09-events.md](../../api_docs/04-psscript/09-events.md)）：
> `on <别名>()` 订阅全部 **19 个**桥接事件（以源码 `Engine.EventAliases` 为准，
> 文档里的"15 个"是自定义外出场景加入前的旧数）· 载荷注入全局 `event`（dict）·
> `bus.emit` 自定义事件 + **字符串形式** `on "pack:event":` 订阅 · 两阶段加载顺序保证 ·
> tick 计数器限流。

## 文件清单

```text
ex17_pss_events/
├── pack.json              ← 包清单: id/name/version/...
└── events/
    ├── all_events.pss     ← 订阅全部 19 个事件别名, 各打一行 log (tick 只 log 前 3 次)
    ├── a_emit.pss         ← bus.emit 发出方: 顶层一次 + game_loaded 一次
    └── b_receive.pss       ← 字符串形式订阅方, 收到 ping 打一行
```

## 安装

把整个 `ex17_pss_events` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex17_pss_events/
```

## 验证（3 分钟）

1. 启动游戏，控制台应出现发出/接收配对（`lucky` 是 1~6 随机数）：

```text
[PSApi] [pss ex17_pss_events] [ex17_pss_events] a_emit.pss 顶层: bus.emit 发出 ping
[PSApi] [pss ex17_pss_events] [ex17_pss_events] 收到 ping! from=顶层语句 lucky=4 save_slot=null
[PSApi] [pss ex17_pss_events] [ex17_pss_events] all_events.pss 已加载 — 已订阅 19 个事件, 进游戏看事件流
```

2. 进一个开店存档：等 3 秒看到 3 行 `[tick]`；开店/卖货/睡觉各阶段会依次打出
   `[shop_opened]`、`[trade_completed]`、`[day_sleep]` 等行——事件流随玩法展开。

## 逐文件讲解

### events/all_events.pss —— 19 个 on 块与 event 载荷

- 事件数据不通过参数传入，统一注入全局 `event`（dict）：
  字符串载荷包装成 `event.value`（`scene_loaded`），
  dict 载荷直接读字段（`event.day` / `event.client` / `event.amount` …）。
- `tick` 每秒一次，用 `tick_seen` 计数器只 log 前 3 次——高频事件限流的标准手法。
- 表外标识符事件名不会编译报错，只打"未知事件"警告（面向未来的容错设计）。

### events/a_emit.pss + b_receive.pss —— 自定义事件一发一收

- 发出方 `bus.emit("ex17_pss_events:ping", {from = ..., lucky = rand(1, 6)})`：
  事件名建议 `包id:事件名`，载荷必须是 dict（或省略）。
- 接收方必须写 `on "ex17_pss_events:ping":`（字符串原样订阅）；
  标识符形式会查别名表接不到，字符串里也不能写插值。
- **加载顺序保证**：引擎先登记所有包的全部 on 块，再执行顶层语句——
  所以顶层 emit 时订阅方必定已就位，b_receive.pss 排在 a_emit.pss 之后也照收。

### 哪些事件只在开店存档里触发？

| 分组 | 事件 | 触发条件 |
|---|---|---|
| 主菜单/读档就有 | `game_loaded`、`scene_loaded` | 读档完成、进/切场景 |
| 只在对局(开店存档)内 | `tick`、`day_wake`、`day_sleep`、`shop_opened`、`shop_closed`、`store_leaving`、`store_returning`、`night_services`、`store_event_started/ended`、`customer_generated`、`dialogue_choice`、`trade_completed` | 要开店拉卷帘门、客户进门、买卖、出门回店、睡觉过夜等玩法动作 |
| 需要外出场景玩法 | `scene_enter`、`scene_leave`、`scene_interact`、`scene_loot` | 打开原版选图面板时 enter/leave 也会发（框架把选图当内置 picker 场景）；interact/loot 要装提供自定义外出场景的包才会触发 |

## 动手练习

1. 给 ping 的 payload 加一个字段（如 `{note = "hi"}`），在 b_receive.pss 里打印它。
2. 把 tick 的限流从 3 次改成 5 次，多等两秒验证。
3. 在 all_events.pss 末尾加 `on not_a_real_event(): pass`，重启看"未知事件"警告长什么样。

## 下一个示例

- [ex18_pss_errors](../ex18_pss_errors/README.md) —— 错误与排障（语言篇收官）
