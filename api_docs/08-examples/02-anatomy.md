# 02 · 三个包解剖：目录、清单与启动链路

> 本篇先不进任何具体文件，回答三个问题：**三个包各自装了什么**、
> **一个包的目录到什么程度算"全"**、**启动后怎么从日志确认包活了**。
>
> 事实来源：`UserData/PSApi/packs/` 下三个包的磁盘实况、`_psapi/Shared/PackScanner.cs`、
> `_psapi/PSApi.Items/Plugin.cs`、`_psapi/PSApi.Events/Plugin.cs`（v1.13.1 / v0.9.3）。

## 一、目录对比

```text
packs/
├── example_hello/          入门示范：每种能力一个最小例子
│   ├── pack.json           包清单
│   ├── items/              6 个 JSON（含 _placeholder.json 品质示例）
│   ├── machines/           3 个 JSON（furnace / desequencer / custom 三型各一）
│   ├── recipes/            3 个 JSON（基线配方 + 钥匙卡链 + 多夜进度）
│   ├── qualities/          1 个 JSON（tag 模式 + feature 模式共 5 层）
│   ├── icons/              2 张 png
│   ├── ui/                 2 个 psui（u4_printer / e6_panel）
│   └── events/             8 个 pss（hello + m1/e2~e6/u4 各专题）
│
├── gunworks/               全特性实战：剧情 + 机器 + 经济
│   ├── pack.json
│   ├── items/              5 个 JSON，31 个物品（台/部件/零件/数据卡/证书/枪）
│   ├── machines/           2 个 JSON，3 台机器（组装台/打印机/分析仪）
│   ├── ui/                 3 个 psui + external/（SVG 素材库 + 光栅化脚本）
│   ├── icons/              27 张 png
│   └── events/             10 个 pss（guide/cells/forger/cat_shadow/
│   │                              distribution/permit/analyzer +
│   │                              bench/ 与 printer/ 两个子目录）
│
└── psapi_manager/          系统包：F6 管理面板本体
    ├── pack.json
    ├── ui/manager.psui     静态骨架（约 70 行）
    └── events/manager.pss  动态填充 + 交互回调（约 290 行）
```

三个观察：

1. **没有"标准目录数"**——包只需要它用到的东西。psapi_manager 只有 3 个文件也是完整的包；gunworks 没有 recipes/ 和 qualities/，因为它的机器走脚本状态机而不是配方系统（见 [06 · 机器层](06-gunworks-machines.md)）。
2. **icons/ 只放被引用的图**。物品 JSON 的 `icon.file` 按文件名找 `icons/` 下的同名 png；gunworks 的 `ui/external/` 是给设计工具用的 SVG 源，**不是**运行时资源。
3. **events/ 支持子目录**。gunworks 的 `bench/01_data.pss`、`bench/02_bench.pss` 演示了递归扫描与**文件名即加载顺序**（`01_` 数据先于 `02_` 状态机，同包共享全局环境）。

## 二、pack.json 全景

三个包的清单并排看（字段规范详见 [02 内容包系统](../02-pack/README.md)）：

```json
// example_hello/pack.json（当前 v0.4.1）
{
  "id": "example_hello",
  "name": "PS-API 示例包 + Custom Items",
  "version": "0.4.1",
  "authors": ["psapi"],
  "gameVersions": ["playtest"]
}

// gunworks/pack.json（当前 v0.13.0，name 截取）
{
  "id": "gunworks",
  "name": "Gunworks · 老祝剧情 + 革命军潜伏网络 + 枪械组装台 + ...",
  "version": "0.13.0",
  "authors": ["psapi"],
  "gameVersions": ["playtest"]
}
```

实践要点：

- **`//` 键是惯例的"开发日志"**。gunworks 的 `//` 字段累积了 v0.3.0→v0.11.0 每个版本的改动记录（超过 3000 字）。PS-API 的 JSON 解析允许未知键，包作者普遍用它当 changelog——比外部文档更不容易和代码失同步。
- **gameVersions 目前只有一个值 `playtest`**。游戏更新（如 9-18 版本）不会自动淘汰包，但可能让包里引用的**原版物品 id** 失效——这正是 gunworks 把"物品 id 核实记录"写进 `//` 的原因。

## 三、启动链路：包是怎么被发现的

MelonLoader 起来后的顺序（详细版见 [01 入门 · 架构](../01-getting-started/01-intro.md)与 [07 进阶 · 调试](../07-advanced/06-debugging.md)）：

```text
MelonLoader
  └─ PSApi.Items (MelonPriority 10, 先)
  │    └─ PackScanner 扫描 packs/*/pack.json → 逐包加载
  │       items/ machines/ recipes/ qualities/ icons/    ← 数据面
  └─ PSApi.Events (MelonPriority 20, 后)
       └─ 同一 PackScanner 结果 → 逐包加载
          ui/*.psui（面板注册） + events/**/*.pss（脚本编译执行顶层）
```

顶层代码立即执行的例子——permit.pss 第一段就是：

```pss
# gunworks/events/permit.pss（节选）
var PERMIT_ID = "gunworks:permit_forged"

shop.block_sale(PERMIT_ID)      # ← 顶层直接调用：包加载完，禁售就已生效
```

**"注册类调用放加载期顶层、玩法逻辑放事件 handler"**——这条铁律在三个包里无一例外：

| 顶层（加载即执行） | handler（事件触发） |
|---|---|
| `npc.register(...)`（cells.pss ×14、guide ×2、forger、cat_shadow） | `on day_wake()`（调度/隔夜结算） |
| `inject.sell_shelf / doctor / loot_pool`（distribution.pss） | `on dialogue_choice()`（选项结算） |
| `shop.block_sale`（permit.pss） | `on trade_completed()`（声望记账） |
| `var` 全局表（bench/01_data.pss 配方表） | `on shop_opened()`（证书扫描） |

## 四、启动验收：日志怎么读

包改完重启，先看日志确认"活了"，再进游戏验收。gunworks 的 README 里写了四条验收标准，对应日志行如下（`MelonLoader/Latest.log`）：

| 期待日志 | 出处 | 说明 |
|---|---|---|
| 18 行 `npc '...' registered (pack=gunworks, ...)` | NpcService | 14 细胞 + guide/guide_pay/forger/cat_shadow；**少一行 = 有脚本在 register 前抛异常** |
| `[gunworks] 博士商店今日上架已按天数/概率重算 (day=N)` | distribution.pss | day_wake 跑通 |
| `npc eval: <id> day=N eligible=... → ...` | NpcService | 每天每 NPC 一行排班判定（[07 进阶 · 管线](../07-advanced/04-npc-pipeline.md)） |
| `[gunworks] trade_completed: ...` | guide.pss | 任何柜台成交都会打（冒烟用） |

排查入口总表见 [07 进阶 · 调试排障](../07-advanced/06-debugging.md)；F6 面板（psapi_manager）可以现场看注入注册与客户队列，见 [08 · psapi_manager 带读](08-psapi-manager.md)。

## 五、给"抄结构"的人

做自己的包时，目录照哪个抄：

- **纯数据包**（加物品/配方/品质，不写逻辑）→ 抄 example_hello 的 items/machines/recipes/qualities 四件，events/ 可以不要。
- **玩法包**（NPC/剧情/机器玩法）→ 抄 gunworks：`events/` 按专题拆文件（一个 NPC 一个文件、机器的数据与状态机拆开），别把 600 行塞一个文件。
- **面板工具** → 抄 psapi_manager：静态骨架 psui + on_build 动态填充，两个文件就是全部。

---

**本篇完。** 下一篇：[03 · example_hello 带读](03-example-hello.md)
