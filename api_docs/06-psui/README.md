# 06 · PSUI：总览

> PSUI 是 PS-API 的**界面系统**：用声明式的 `.psui` 文件描述"面板长什么样"，
> 用 PSScript 的 `ui.*` 函数驱动"面板怎么动"。数据面 JSON 造出物品与机器，
> PSScript 写出逻辑，PSUI 把两者呈现在玩家眼前——赠品面板、工作台槽位、
> 机器操作界面，都从这一个目录开始：`packs/<你的包>/ui/*.psui`。

## 本章导航

| 篇 | 主题 | 你能学到 |
|---|---|---|
| [02 面板文件格式](02-panel-file.md) | .psui 语法 | 缩进树、行格式、值类型、解析容错规则 |
| [03 元素与属性](03-elements.md) | 控件总表 | 15 种元素、各自属性与默认值、window 全属性 |
| [04 布局](04-layout.md) | 排版 | row/column/grid/scroll、spacing、尺寸语义 |
| [05 回调与生命周期](05-callbacks.md) | 交互 | on_click/on_change/on_build/on_open/on_window_closed |
| [06 动态构建](06-dynamic.md) | on_build 深入 | build_* 函数族、列表类面板的标准模式 |
| [07 图元树后端与槽位](07-slots.md) | slot 面板 | slot/grid_slot、whitelist、能放/不能放什么 |
| [08 机器绑定面板](08-machine-panels.md) | ui="psui" | 双击开窗、machine_* API、结构恒定铁律 |
| [09 实战](09-practice.md) | 综合演练 | 从零写三张面板：仪表盘 / 槽位工作台 / 机器界面 |

函数侧的完整手册（`ui.open`/`ui.set_text`/`ui.machine_*` …）在
[05 · 09 ui：界面函数](../05-api-reference/09-ui.md)——本章讲**怎么把面板做出来**，
那篇讲**脚本怎么操作面板**。

## 一张面板的一生

```text
启动游戏
  │
  ▼
PSApi.Events 扫描所有包的 ui/*.psui            ← 只扫该目录顶层, 子目录不扫
  │  解析成面板树 (纯数据, 不建任何游戏对象)
  │  ✗ 结构错误(缺 window 根等): 该面板不注册, 启动日志可见
  │  ⚠ 未知元素/属性: 警告并跳过, 不致命 (向前兼容)
  ▼
面板注册表: "包id:面板名"  (面板名 = window 显式 id, 缺省 = 文件名)
  │
  ├─ 脚本调用 ui.open("面板名")      → 打开独立窗口 (玩家可拖动/关闭)
  └─ 机器 machines/*.json ui="psui"  → 双击机器物品开窗, 槽内容随存档
  │
  ▼
惰性实例化: 此刻才构建窗口与元素, 回调绑定到本包 events/*.pss 的函数
```

两个关键机制先记住，后面各篇展开：

1. **惰性实例化**：解析只做校验，`ui.open` 时才真正建窗口——改了 `.psui` 重开面板即生效
   （不用重启游戏，`ui.rebuild` 也走这条路）。
2. **回调绑定本包函数**：`on_click: 函数名` 引用的是**本包** `events/*.pss` 里的全局函数，
   启动时校验存在性，点击时异常隔离（一个按钮炸不影响其他回调）。

## 双后端：一张表决定你的面板走哪条路

PSUI 内部有**两套渲染后端**，由面板内容自动分派，你不用选——但必须知道区别，
因为两套后端支持的元素集合不同：

| | CustomUI 后端 | 图元树后端 (v1.7.0+) |
|---|---|---|
| **触发条件** | 面板**不含** slot / grid_slot | 面板**任意位置含** slot 或 grid_slot（整面板切换） |
| **底层** | 官方 `CustomUIManager`（自定义窗口系统） | 原版图元树（`PixelWindow`，与游戏原生机器界面同源） |
| **交互控件** | 全量：label/button/progress/image/toggle/slider/input/dropdown | 仅 label/button/image/slot/grid_slot/spacer；其余（含 scroll）跳过并警告 |
| **槽位（放真物品）** | 不支持 | 支持，带白名单过滤 |
| **典型用途** | 仪表盘、设置页、列表浏览 | 合成台、打印机等"放进物品→产出"的工作台 |

```text
                    ┌─ 含 slot/grid_slot? ──是──→ 图元树后端 (07 篇)
 .psui 面板定义 ────┤
                    └──────否──────────────→ CustomUI 后端 (03/04 篇)
```

**最常见的坑**：往一张控件面板里加了一个 `slot`，整张面板切到图元树后端，
原来好端端的 progress/toggle/slider 全部消失（警告跳过）。两套后端的选择在
[07 图元树后端与槽位](07-slots.md) 详解。

## 来自真实包的例子

本章频繁引用工作区里三个官方包的真实面板（都是可直接运行的完整实现）：

| 包 | 面板 | 演示内容 |
|---|---|---|
| `example_hello` | `ui/e6_panel.psui` | CustomUI 全控件：progress/toggle/slider/dropdown/input + on_click/on_change |
| `example_hello` | `ui/u4_printer.psui` | 图元树后端：grid_slot 槽位 + 白名单 + 槽位 on_change |
| `psapi_manager` | `ui/manager.psui` | on_build 动态构建：三个 scroll 动态区 + 分页列表 + ui.rebuild |
| `gunworks` | `ui/gun_bench.psui` 等 | 机器绑定面板：结构恒定布局 + machine_* API + on_open |

## 版本速查

PSUI 能力随 PSApi.Events 版本演进（当前 v1.13.1）：

| 版本 | 能力 |
|---|---|
| v1.2.1 | on_build 构建期解析修复（构建期可 ui.set_text 本面板元素） |
| v1.4.0 | 机器绑定面板、window `persistent`、`on_open` |
| v1.4.1 | 布局三件套：button `size` 定宽高、label `size` 定宽、window `spacing`、`spacer` 占位 |
| v1.5.0 | 美观三件套：button `icon`（图标按钮）、图元 image、label/button `font_size` 字号 |
| v1.5.2 | font_size 重放幂等修复（此前每次 set_text 字号会叠乘缩小） |
| v1.6.0 | grid_slot 多物品遍历（`ui.get_slot_items` / `machine_slot_items`） |
| v1.7.0 | 图元树后端（slot/grid_slot 面板整体走 PixelWindow） |

---

下一篇：[02 · 面板文件格式](02-panel-file.md)
