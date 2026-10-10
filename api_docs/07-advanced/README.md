# 07 · 进阶：总览

> 前六章给了你**零件**：包结构、JSON 数据、PSScript、API 函数、PSUI 面板。
> 本章给**图纸**：state 落到磁盘的哪个文件、inject 的五条挂点各自接在游戏
> 哪个原生路径上、一个 npc.register 从注册到进店排队经历什么、出了问题去哪看日志——
> 以及一个把"涂格子"变成物品包的可视化工具。

## 本章导航

| 篇 | 主题 | 你能学到 |
|---|---|---|
| [02 存档状态深入](02-state-deep.md) | state 机制 | 磁盘布局、槽位隔离、值编码、落盘时机与崩溃窗口 |
| [03 原版注入深入](03-inject-deep.md) | inject 机制 | 五条挂点的原生路径、会话守卫、防双份、调参工作流 |
| [04 NPC 生成管线与模板](04-npc-pipeline.md) | npc · 上 | 五相位钩子、模板克隆流程、**247 个 base_template 有效 id 总表** |
| [05 NPC 配置深度](05-npc-config.md) | npc · 下 | 九通道对话、选项与事件、价格体系、排班语义、设计约束 |
| [06 调试排障](06-debugging.md) | debug | 日志地图、启动链路、症状→排查对照表、F6/F12 工具 |
| [07 item_editor](07-item-editor.md) | 工具 | 可视化物品编辑器：涂格子→绑图→导出→入包全流程 |
| [08 自定义外出场景](08-scenes-raid.md) | scenes · 搜打撤 | scene.json 键表、script/picker 驱动、_raid 库与 DATA 表、combat.\* 公式、布局四层覆写、M 写回/F10 热重载 |

## 和函数手册的分工

[05 内置 API 参考](../05-api-reference/README.md)回答"**这个函数怎么调**"；
本章回答"**它背后发生了什么、为什么这样设计、出问题怎么查**"。
写简单模组只需 05；想造剧情 NPC、做经济系统、排查疑难时回来读本章。

```text
脚本调用                    机制层(本章)                       游戏原生层
──────────                 ──────────────                    ──────────
state.set(k, v)  ──→  SaveStates(槽隔离 KV)  ──→  UserData/PSApi/state/<槽>/<包>.json
inject.sell_shelf ──→ 摆货计划+会话守卫      ──→  AddDirectSellingItemToTable(原生摆货)
npc.register     ──→  五相位生成管线         ──→  StoreClientListDict.CreateStoreClient
                   (模板克隆 → 白板化 → 配置覆盖 → 入队)
```

## 本章的事实来源

本章所有机制描述以真实源码为准（v2.0.0 起统一在 `_psapi/PSApi/` 单工程）：
`_psapi/PSApi/Shared/SaveStates.cs`、
`_psapi/PSApi/Events/InjectService.cs`、`NpcService.cs`、`LootPoolService.cs`、
`Plugin.cs`、`GameHooks.cs`；NPC 模板 id 总表提取自游戏
`StoreClientListDict..cctor`（cpp2il 反编译 dump），并经
[ex30_npc_full](../../examples/ex30_npc_full/README.md) 教学包实测验证。推断性表述在正文标注。

## 版本速查

当前游戏内置 **PSApi v2.0.8**（单 dll）。本章涉及的
关键版本节点：v1.8.0 摆货并集防双份、v1.9.0 `uses` 机制与 `auto_leave`、
v1.10.0 `loot_pool` 与数量区间、v1.11.0 `buy_pool`/存在性校验/`{buy_list}`、
v1.12.0 价格体系重写、v1.13.0 价格缺省中立、v1.13.1 收购特性方向闸门。

---

下一篇：[02 · 存档状态深入](02-state-deep.md)
