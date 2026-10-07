# ex19_api_state_world · 示例 19：状态存储与世界查询

> **演示知识点**（对应文档 [02-state.md](../../api_docs/05-api-reference/02-state.md) ·
> [03-world.md](../../api_docs/05-api-reference/03-world.md)）：
> `state` 按存档槽隔离的 KV 存储（get/set/has/del） · `time.day()/rel_day()/weekday()`
> 三时间查询 · `pack.list()` 内容包清单 · `rep.get("bm")` 派系声望 ·
> 变量跨 tick 保留 vs `state` 跨会话保留 · API 的对局限制（哪些主菜单能调、哪些要进对局）。

## 文件清单

```text
ex19_api_state_world/
├── pack.json              ← 包清单
└── events/
    └── world.pss          ← 1 个脚本: 顶层 state 记账 + 4 个事件订阅
```

## 安装

把整个 `ex19_api_state_world` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex19_api_state_world/
```

## 验证（3 分钟）

1. 启动游戏，控制台（或 `MelonLoader/Latest.log`）应出现：

```text
[ex19_api_state_world] 启动第 1 次(state 计数, global 槽), has(boot_count)=true
```

2. 读档/新开任意存档（**进对局**后 `game_loaded` 触发）：

```text
[ex19_api_state_world] 对局就绪: 开业第 1 天(周1), 绝对日 12, 存档槽 1
[ex19_api_state_world] 已加载内容包 2 个:
[ex19_api_state_world]   · ex19_api_state_world v1.0.0 [folder] 示例 19 · 状态与世界查询
```

   （绝对日/包数以你的实际环境为准。）等 5 秒会看到 `tick 第 5 次, 计数已存入 state`。
3. **重启游戏**：第一条日志变成 `启动第 2 次`，且进对局后能读回上次会话的
   `tick 里程碑` —— `state` 是持久的，这就是它和普通变量的本质区别。
4. 晚上睡觉到第二天，`day_wake` 触发：

```text
[ex19_api_state_world] 醒来: 开业第 2 天(周2), 上次记录 1, 黑市声望 0
```

   换一个存档槽再进，`last_rel_day` 各记各的 —— **按槽隔离**。

## 逐文件讲解

### events/world.pss —— 四个命名空间各就各位

- **顶层（主菜单阶段执行）**：只碰 `state`/`pack` 这类任何时候可调的 API。
  `state.get(key, 默认值)` 是"读 + 兜底"一体；`state.has(key)` 查存在。
  主菜单读不到存档槽，此时写入落在 `global` 槽（文件
  `UserData/PSApi/state/global/ex19_api_state_world.json`）。
- **`on game_loaded()`**：读档/新开对局完成，已在存档场景 → `time.*` 放心用。
  `rel_day()` 是"开业第 N 天"（第一天就是 1），`weekday()` 0=周一…4=周五 6=周日，
  `day()` 是跨存档稳定的绝对日。`pack.list()` 返回
  `{id, version, source, name, valid}` 条目数组，`source` 是 `folder`/`dll`。
- **`on tick()`**：`tick_count` 演示**变量跨 tick 保留**（重启归零）；每 5 秒把
  计数 `state.set` 进存档演示**跨会话保留**（重启仍在）。两级"记忆"按需选。
- **`on day_wake()`**：`rep.get("bm")` 读黑市声望（六势力简写：sec/rev/bm/cartel/ul/ll）。
  `rep.get` 在场景刚加载、声望表未建档时会报错，所以放在 `day_wake` 这个
  "稳态"时机调用——这是 API 对局限制的标准处理思路。

## 动手练习

1. 把 `world.pss` 里 `tick_count % 5 == 0` 的 5 改成 3，观察日志密度与
   `tick_milestone` 的值。
2. 在 `day_wake` 里加一行 `rep.add("bm", 5)`，连续睡两天，观察声望数值变化。
3. 在顶层加 `state.del("boot_count")` 再注释掉交替重启，体会 `del` 的效果
   （`del` 等价于 `set(key, null)`）。

## 下一个示例

- [ex20_api_shop_events](../ex20_api_shop_events/README.md) —— shop 商店观测台 + store_event 自定义商店事件
