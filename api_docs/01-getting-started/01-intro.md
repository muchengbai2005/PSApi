# 01 · PS-API 是什么

> 阅读时间约 10 分钟。读完你会知道：PS-API 的定位、三层架构、启动时发生了什么、
> 一个内容包由哪几部分组成。

## 一句话定位

**PS-API 是《Probably Stolen》的内容包加载器。** 它本体是一个 MelonLoader 模组
（`PSApi.dll`，v2.0.0 起由原 `PSApi.Items.dll` + `PSApi.Events.dll` 双组件合并而来），
装好后常驻游戏；而你——模组作者——
只需要在 `UserData/PSApi/packs/` 下放一个**内容包文件夹**，游戏启动时 PS-API 会自动
发现它、校验它、把里面的内容注册进游戏。

这和 Minecraft 玩家把 mod 扔进 `mods/` 文件夹是同一种体验：

```text
Minecraft:  把 .jar 放进 mods/     → Forge/Fabric 加载
PS-API:     把文件夹放进 packs/    → PSApi 加载
```

## 三面一体架构

PS-API 把模组内容拆成三个"面"，各用一种专用格式，互不干扰：

```text
┌────────────────────────────────────────────────────────────────┐
│                        游戏本体 (IL2CPP Unity)                    │
│              Probably Stolen · Questing Goose Studio             │
└───────────────────────────────┬────────────────────────────────┘
                                │ MelonLoader (模组加载器)
┌───────────────────────────────▼────────────────────────────────┐
│                     PSApi.dll (单宿主, v2.0.0)                   │
│  ┌─────────────────────────┐     ┌────────────────────────────┐ │
│  │ 数据面：JSON             │     │ 逻辑面：PSScript (.pss)     │ │
│  │  items/    物品          │     │  events/ 脚本，写玩法逻辑    │ │
│  │  machines/ 机器          │     │  scenes/ 自定义外出场景      │ │
│  │  recipes/  配方          │     │                            │ │
│  │  qualities/品质          │     │ 界面面：PSUI (.psui)        │ │
│  │  icons/    图标          │     │  ui/ 声明式界面文件          │ │
│  └─────────────────────────┘     │                            │ │
│   (命名空间 PSApi.Items.*)        │ + NPC / 商店 / 注入 / 掠夺池 │ │
│                                  │   事件总线 / 存档状态         │ │
│                                  │  (命名空间 PSApi.Events.*)   │ │
│                                  └────────────────────────────┘ │
└───────────────────────────────┬────────────────────────────────┘
                                │
┌───────────────────────────────▼────────────────────────────────┐
│            UserData/PSApi/packs/<你的包>/                       │
│   一个文件夹 = 一个模组 = 数据 + 逻辑 + 界面 三件套按需混搭        │
└────────────────────────────────────────────────────────────────┘
```

三个面的分工：

| 面 | 格式 | 适合做什么 |
|---|---|---|
| **数据面** | JSON 文件 | 物品、机器、配方、品质、图标——"游戏里**有什么**" |
| **逻辑面** | `.pss` 脚本（PSScript 语言） | 玩法规则、事件响应、NPC 对话、自定义场景——"游戏里**发生什么**" |
| **界面面** | `.psui` 文件（PSUI 声明式 UI） | 自定义窗口、按钮、槽位网格——"玩家**看到什么**" |

> **为什么拆开？** 数据用 JSON 声明，改数值不用碰代码；逻辑用专用小语言 PSScript，
> 语法比 C# 轻量得多（接近 Python/GDScript 的手感），且错误只会落在日志里、不会崩溃游戏；
> 界面用声明式文件描述，脚本通过 id 与之绑定。三层各司其职，AI 辅助编写时也易于分块生成。

## 一个内容包长什么样

仓库 [`examples/`](../../examples/) 目录下有 34 个教学示例包（导航见
[08 实战讲解](../08-examples/README.md)）：最小的 [ex01_hello](../../examples/ex01_hello/README.md)
只有 pack.json + 1 个物品 + 1 个脚本，[ex02_dirs](../../examples/ex02_dirs/README.md)
则 9 个子目录各放一个最小文件。一个内容包的标准目录长这样：

```text
UserData/PSApi/packs/<你的包>/
├── pack.json            ← 包清单：id、名字、版本（必备，唯一必备）
├── items/               ← 数据面：物品定义
├── machines/            ← 数据面：机器声明
├── recipes/             ← 数据面：配方
├── qualities/           ← 数据面：品质层
├── icons/               ← 数据面：图标 png
├── events/              ← 逻辑面：PSScript 脚本
├── scenes/              ← 逻辑面：自定义外出场景
└── ui/                  ← 界面面：PSUI 面板
```

三个面各来一段真实代码感受一下。

**数据面**——声明一个"示例熔炉"物品（`items/example_smelter.json`）：

```json
{
  "items": [
    {
      "id": "my_pack:example_smelter",
      "directory": "StationMachinery",
      "template": "furnace",
      "name": "示例熔炉",
      "desc": "PS-API 注册的测试熔炉, 由原版 Furnace 工厂桥接创建。",
      "value": 400
    }
  ]
}
```

**逻辑面**——`events/hello.pss` 的开头（顶层代码在启动时立即执行，`on` 块订阅事件）：

```python
const PACK = "my_pack"
var boot_count = 0

func describe_stock(name, tags, discount = 0):
    var price = 120
    if discount > 0:
        price = price * (100 - discount) / 100
    return "{name} 估价 {price}"

boot_count += 1
log.info("hello.pss 已加载(第 {boot_count} 次)")

on scene_loaded():
    log.info("进入场景: {event.value}")
```

**界面面**——`ui/u4_printer.psui`（缩进块描述窗口与控件树）：

```text
window u4_printer:
  title: "模组打印机 (原型)"
  size: 360x220
  draggable: true
  close_on_escape: true

  column:
    label status_lbl:
      text: "放入 2 个废金属, 点按钮打印"
    grid_slot feed:
      whitelist: ["game:scrap_metal"]
      size: 3x4
      on_change: u4_feed_changed
    button print_btn:
      text: "打一份报纸"
      on_click: u4_print
```

> 三段代码里出现的具体语法（JSON 各字段含义、`{插值}`、`on scene_loaded():`、
> `grid_slot` 白名单……）都会在后续章节展开，这里只需建立直觉。

## 游戏启动时发生了什么

理解启动流程，排障时你就知道去哪找问题（流程取自 `PSApi` 模组
`OnInitializeMelon` 真实代码）：

```text
1. MelonLoader 按优先级加载 Mods/ 下的 DLL
   ├─ PSPack.<id>.dll   (优先级 5)  ← 编译成 DLL 的内容包，先登记内嵌资源
   └─ PSApi.dll         (优先级 10) ← 单宿主：数据面先就绪，脚本随后可安全引用物品

2. PSApi 启动（数据面）
   ├─ 扫描 UserData/PSApi/packs/* 解析 pack.json（与内嵌包合并去重）
   ├─ 加载顺序：qualities → items → recipes
   └─ 解析错误汇总写入 UserData/PSApi/logs/pack_errors_*.log

3. PSApi 启动（逻辑/界面面）
   ├─ 建 PSScript 引擎，编译加载全部 events/*.pss 与 scenes/**/*.pss
   ├─ on 块登记到事件总线；编译错误同样落 logs/
   └─ 控制台自检：event bus selftest hits=12

4. 进入对局后
   ├─ 游戏物品目录就绪（ModHook 信号或 3 秒轮询兜底）→ 注册全部自定义物品
   ├─ 每次进场景广播 psapi.scene.loaded；每秒广播 psapi.tick
   └─ F12 发放全部自定义物品 / F6 打开管理面板（调试快捷键）
```

两条对开发者最重要的推论：

- **内容在游戏启动时一次性加载**。改了文件要重启游戏才生效（当前没有热重载）。
- **物品注册要等"目录就绪"**。进对局前物品定义只是"解析完毕"，进对局后才会真正出现在
  背包/商店里，所以测试物品请先进入存档。

## 能力清单（你能用 PS-API 做什么）

以下均已在源码中落地，逐项文档见对应章节：

| 能力 | 章节 |
|---|---|
| 自定义物品（模板克隆 / 异形占位 / 图标 / 品质层） | [03 数据面](../03-items/README.md) |
| 自定义机器（复用原版熔炉/读卡器窗口，或自装配窗口） | [03 数据面 · 机器](../03-items/README.md) |
| 配方（含跨多夜的"进度配方"） | [03 数据面 · 配方](../03-items/README.md) |
| 品质层（改名 / 改价 / tooltip 行） | [03 数据面 · 品质](../03-items/README.md) |
| PSScript 脚本玩法（变量 / 函数 / 容器 / 事件订阅） | [04 PSScript](../04-psscript/README.md) |
| 内置 API：物品发放 / 机器操控 / 品质 / 时间 / 存档 | [05 API 参考](../05-api-reference/README.md) |
| 自定义 NPC（对话、买卖、剧情链、皮肤） | [07 进阶 · NPC](../07-advanced/README.md) |
| 原版内容注入（NPC 收购单 / 货架 / 掠夺池） | [07 进阶 · 注入](../07-advanced/README.md) |
| 自定义外出场景（搜打撤 / raid / 实时战斗） | [07 进阶 · 场景](../07-advanced/08-scenes-raid.md) |
| PSUI 自定义界面（窗口 / 按钮 / 槽位网格） | [06 PSUI](../06-psui/README.md) |
| 存档隔离状态（每个存档槽独立 KV） | [07 进阶 · 存档状态](../07-advanced/README.md) |
| 犯罪豁免 / 治安玩法定制 | [05 API 参考 · crime](../05-api-reference/README.md) |

## 设计边界（什么不归 PS-API 管）

- **不要求你写 C#**。C# 只在两种场景出现：PS-API 本体的开发，以及把内容包编译成
  分发用 DLL（`_tools/pack_compiler` 自动完成，你仍然只写 JSON/PSS/PSUI）。
- **不动 `Mods/` 目录**（开发期）。日常开发全程在 `UserData/PSApi/packs/` 里改文件；
  只有正式分发时才用 pack_compiler 产出 `PSPack.<id>.dll` 放进 `Mods/`。
- **不修改游戏本体文件**。所有注入通过运行时 hook 完成，卸载模组即恢复原样。

---

下一步：[02 · 环境准备与安装验证](02-setup.md)
