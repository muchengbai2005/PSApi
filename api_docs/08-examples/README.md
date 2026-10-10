# 08 · 实战讲解：34 个教学示例包

> 前七章是**零件图册**：每个字段、每个函数单独讲。
> 本章是**总装车间**：全书每个知识小节都配了一个**可以直接运行的最小示例包**，
> 放在仓库 [`examples/`](../../examples/) 目录下——读到哪、装到哪、跑到哪。
>
> 每个包都经过实跑验证（启动游戏 → 日志确认加载成功），包内 README 写明
> 演示知识点、文件清单、安装方法、预期日志与动手练习。

## 示例包怎么用

1. **装**：把 `examples/exNN_xxx` 整个文件夹复制到
   `UserData/PSApi/packs/` 下（可一次装多个）。
2. **跑**：启动游戏，控制台搜 `[exNN` 或看 `rescan(init)` 包计数。
3. **读**：先读包内 `README.md`（演示点 + 预期日志），再对照源文件看写法。
4. **改**：每个 README 尾部有"动手练习"——改一两个值重启看效果，比读十遍文档记得牢。

> 34 个包全部可以同时安装（互不冲突、均不依赖 gunworks/psconsole）。
> 物品全部标 `test: true`，按 **F12** 一次性发放，方便随时取用。

## 全 34 包导航（按章节分组）

### 01 入门 & 02 内容包系统

| 包 | 演示什么 | 对应文档 |
|---|---|---|
| [ex01_hello](../../examples/ex01_hello/README.md) | 最小可运行包：pack.json + 1 物品 + 1 脚本 | [01 · 第一个包](../01-getting-started/03-first-pack.md) |
| [ex02_dirs](../../examples/ex02_dirs/README.md) | 目录全景：9 个子目录各放一个最小文件 | [02 · 包结构](../02-pack/02-structure.md) |
| [ex03_deps_base](../../examples/ex03_deps_base/README.md) | 依赖对（被依赖方：物品库） | [02 · 分发](../02-pack/03-distribution.md) |
| [ex04_deps_user](../../examples/ex04_deps_user/README.md) | 依赖对（依赖方：声明 requires_after） | 同上 |

### 03 数据面 JSON

| 包 | 演示什么 | 对应文档 |
|---|---|---|
| [ex05_item_basic](../../examples/ex05_item_basic/README.md) | 物品基础字段：id/name/value/shape/tags | [03 · 物品](../03-items/02-items.md) |
| [ex06_item_template](../../examples/ex06_item_template/README.md) | template 模板继承：不写 icon 走原版切片 | 同上 |
| [ex07_item_uses](../../examples/ex07_item_uses/README.md) | useCount 次数体系：限次使用/耐久 | 同上 |
| [ex08_machines](../../examples/ex08_machines/README.md) | 机器三路线：furnace / custom / 进度机 | [03 · 机器](../03-items/03-machines.md) |
| [ex09_recipes](../../examples/ex09_recipes/README.md) | 配方：inputs/outputs/tag 输入/game: 前缀 | [03 · 配方](../03-items/04-recipes.md) |
| [ex10_qualities](../../examples/ex10_qualities/README.md) | 品质：tag 模式 + feature 模式 | [03 · 品质](../03-items/05-qualities.md) |
| [ex11_icons](../../examples/ex11_icons/README.md) | 图标：icons/ 根目录 + 子目录寻址 | [03 · 图标](../03-items/06-icons.md) |

### 04 PSScript 语言

| 包 | 演示什么 | 对应文档 |
|---|---|---|
| [ex12_pss_basics](../../examples/ex12_pss_basics/README.md) | 类型/var/const/运算/类型转换 | [04 · 值与变量](../04-psscript/03-values.md) |
| [ex13_pss_strings](../../examples/ex13_pss_strings/README.md) | 插值/转义/format/join/split | [04 · 字符串](../04-psscript/05-strings.md) |
| [ex14_pss_flow](../../examples/ex14_pss_flow/README.md) | if/elif/while/for-in/range/break/continue | [04 · 控制流](../04-psscript/06-control-flow.md) |
| [ex15_pss_funcs](../../examples/ex15_pss_funcs/README.md) | func 定义/默认参数/返回值/递归 | [04 · 函数](../04-psscript/07-functions.md) |
| [ex16_pss_containers](../../examples/ex16_pss_containers/README.md) | 数组/字典/map 字面量/嵌套 | [04 · 容器](../04-psscript/08-containers.md) |
| [ex17_pss_events](../../examples/ex17_pss_events/README.md) | 事件订阅：19 个别名/event 对象 | [04 · 事件](../04-psscript/09-events.md) |
| [ex18_pss_errors](../../examples/ex18_pss_errors/README.md) | 错误与防御：判空/越界/调试三问 | [04 · 错误](../04-psscript/10-errors.md) |

### 05 API 参考

| 包 | 演示什么 | 对应文档 |
|---|---|---|
| [ex19_api_state_world](../../examples/ex19_api_state_world/README.md) | state 三件套 + time 全函数 | [05 · state](../05-api-reference/02-state.md) / [05 · world](../05-api-reference/03-world.md) |
| [ex20_api_shop_events](../../examples/ex20_api_shop_events/README.md) | 商店事件：shop_opened/trade_completed | [05 · 商店](../05-api-reference/04-shop.md) |
| [ex21_api_items](../../examples/ex21_api_items/README.md) | items API：grant/machine.find/find_all | [05 · items](../05-api-reference/06-items.md) |
| [ex22_api_inject](../../examples/ex22_api_inject/README.md) | inject 三通道：sell_shelf/doctor/loot_pool | [05 · inject](../05-api-reference/07-inject.md) |
| [ex23_api_npc](../../examples/ex23_api_npc/README.md) | npc API：register 最小写法 | [05 · npc](../05-api-reference/08-npc.md) |
| [ex24_api_ui_handles](../../examples/ex24_api_ui_handles/README.md) | ui API/句柄定位与读写 | [05 · ui](../05-api-reference/09-ui.md) / [05 · 句柄](../05-api-reference/10-handles.md) |

### 06 PSUI 界面

| 包 | 演示什么 | 对应文档 |
|---|---|---|
| [ex25_psui_basic](../../examples/ex25_psui_basic/README.md) | 全控件静态面板（F8 打开） | [06 · 面板文件](../06-psui/02-panel-file.md) / [06 · 控件](../06-psui/03-elements.md) |
| [ex26_psui_callbacks](../../examples/ex26_psui_callbacks/README.md) | 控件回调：on_click/on_change 双向绑定 | [06 · 回调](../06-psui/05-callbacks.md) |
| [ex27_psui_dynamic](../../examples/ex27_psui_dynamic/README.md) | 动态构建：on_build 命令式填充 | [06 · 动态](../06-psui/06-dynamic.md) |
| [ex28_psui_slots](../../examples/ex28_psui_slots/README.md) | 槽位面板：slot/grid_slot/strict_footprint | [06 · 槽位](../06-psui/07-slots.md) |
| [ex29_psui_machine](../../examples/ex29_psui_machine/README.md) | PSUI 机器：双击开面板 + 槽内物品 | [06 · 机器面板](../06-psui/08-machine-panels.md) |

### 07 进阶

| 包 | 演示什么 | 对应文档 |
|---|---|---|
| [ex30_npc_full](../../examples/ex30_npc_full/README.md) | NPC 全配置：卖货/收购/剧情对话三型 | [07 · NPC 配置](../07-advanced/05-npc-config.md) |
| [ex31_scene_basic](../../examples/ex31_scene_basic/README.md) | 场景：scene.json + 背景图 + 入口 | [07 · 场景](../07-advanced/08-scenes-raid.md) |
| [ex32_scene_combat](../../examples/ex32_scene_combat/README.md) | 战斗场景：危险度/遭遇/战利品 | 同上 |
| [ex33_state_deep](../../examples/ex33_state_deep/README.md) | state 深入：槽位隔离/落盘时机/防重 | [07 · state 深入](../07-advanced/02-state-deep.md) |
| [ex34_mini_mod](../../examples/ex34_mini_mod/README.md) | 迷你 mod：温室 = 数据+机器+脚本综合 | 全书综合 |

## 本章导航

| 篇 | 主题 | 你能学到 |
|---|---|---|
| [02 示例包解剖](02-anatomy.md) | 全局 | 从 ex01 到 ex34 的目录对比、启动链路、日志验收 |
| [08 psapi_manager 带读](08-psapi-manager.md) | 系统包 | F6 管理面板：静态骨架 + on_build 动态填充 + tick 刷新 |
| [09 从 0 到 1](09-from-zero.md) | 工作流 | 拿 ex01 当模板做自己的包：起步→增量→验收→分发 |

> 历史说明：v2.0.0 之前本章还带读 `example_hello` 与 `gunworks` 两个随包分发的
> 示例。v2.0.0 起两者不再随仓库分发，教学内容全部由上面 34 个
> examples/ 教学包承担（更小、更聚焦、逐小节对应）。gunworks 作为独立
> mod 另行发布，其源码不在本仓库。

## 版本事实

- 全部示例包随仓库 **examples/** 目录分发，兼容 **PSApi v2.0.8**（单 dll）。
- 全部示例包已实跑验证：37/37 包加载、零 compile error（2026-10-07，
  详见各包 README 的"验证"节）。

---

下一篇：[02 · 示例包解剖](02-anatomy.md)
