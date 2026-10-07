# 02 · 示例包解剖：目录、清单与启动链路

> 本篇先不进任何具体文件，回答三个问题：**示例包体系怎么组织**、
> **一个包的目录到什么程度算"全"**、**启动后怎么从日志确认包活了**。
>
> 事实来源：仓库 `examples/` 下 34 个教学包的磁盘实况、
> `_psapi/PSApi/Shared/PackScanner.cs`、`_psapi/PSApi/Plugin.cs` 与两模块
> `Items/Plugin.cs`、`Events/Plugin.cs`（PSApi v2.0.0 单工程）。

## 一、目录对比：从最小包到完整包

34 个教学包按"教学递进"组织——先看五个代表：

```text
examples/
├── ex01_hello/             最小包: 4 个文件跑通数据面+逻辑面
│   ├── pack.json           包清单
│   ├── README.md           演示点/安装/预期日志/练习 (每个包必备)
│   ├── items/
│   │   └── greeting_card.json    1 个物品 (template 兜底图标, 无 png)
│   └── events/
│       └── hello.pss             1 个脚本 (启动日志 + 3 事件订阅)
│
├── ex02_dirs/              目录全景: 9 个子目录各放一个最小文件
│   ├── pack.json + README.md
│   ├── items/ machines/ recipes/ qualities/ icons/ ui/ events/ scenes/
│   └── (唯一一个用到全部子目录的教学包)
│
├── ex08_machines/          机器三路线: furnace/custom/进度机 各一台
│   ├── items/machines_tour.json   3 台机器的物品定义
│   ├── machines/                   3 份机器声明 (三路线各一)
│   └── recipes/                    3 份配方 (按机器拆文件)
│
├── ex30_npc_full/          NPC 全配置: 卖货/收购/剧情三型 NPC
│   ├── pack.json + README.md
│   ├── items/ex30_goods.json      NPC 交易用的货物
│   └── events/                    一个 NPC 一个文件 + monitor 总控
│
└── ex34_mini_mod/          迷你 mod: 温室 = 全书综合
    ├── pack.json + README.md
    ├── items/ machines/ recipes/  数据面 (温室+种子+产出)
    ├── ui/greenhouse.psui         机器面板 (PSUI)
    └── events/                    收获逻辑 + 收购买家
```

三个观察：

1. **没有"标准目录数"**——包只需要它用到的东西。ex01 只有 2 个内容文件也是
   完整的包；ex08 没有 ui/ 因为机器走原版窗口；ex25 没有 items/ 因为它只演示面板。
2. **README.md 是每个示例包的"微型教材"**。固定结构：演示知识点（链回文档）→
   文件清单 → 安装 → 验证（预期日志原文）→ 逐文件讲解 → 动手练习 → 下一示例链接。
3. **events/ 支持子目录与专题拆分**。加载器递归扫描 `events/**/*.pss`，
   **文件名即加载顺序**（`01_` 前缀先于 `02_`，同包共享全局环境）。
   ex30 用"一个 NPC 一个文件"演示了专题拆分。

## 二、pack.json 全景

两个教学包的清单并排看（字段规范详见 [02 内容包系统](../02-pack/README.md)）：

```json
// ex01_hello/pack.json（教学最小写法）
{
  "id": "ex01_hello",
  "name": "示例 01 · 第一个包",
  "version": "1.0.0",
  "authors": ["psapi"],
  "gameVersions": ["playtest"],
  "//": "教学示例包 ex01: PSApi 最小可运行包。演示: ..."
}

// ex04_deps_user/pack.json（依赖声明写法，节选）
{
  "id": "ex04_deps_user",
  "name": "示例 04 · 依赖声明（依赖方）",
  "version": "1.0.0",
  "//": "...",
  "requires_after": ["ex03_deps_base"]     // ← 声明前置包, 见 02-pack/03-distribution.md
}
```

实践要点：

- **`//` 键是惯例的"开发日志"**。PSApi 的 JSON 解析允许未知键，包作者普遍用
  `//` 记设计决策与版本变更——比外部文档更不容易和代码失同步。教学包用它
  说明"本包演示什么、对应哪篇文档"。
- **gameVersions 目前只有一个值 `playtest`**。游戏更新不会自动淘汰包，
  但可能让包里引用的**原版物品 id** 失效——引用原版 id 的地方建议在 `//` 里
  留核实记录。

## 三、启动链路：包是怎么被发现的

MelonLoader 起来后的顺序（详细版见 [01 入门 · 架构](../01-getting-started/01-intro.md)
与 [07 进阶 · 调试](../07-advanced/06-debugging.md)）：

```text
MelonLoader
  └─ PSApi v2.0.0 (单 dll, 两段式初始化)
       ├─ 段1 Items: PackScanner 扫描 packs/*/pack.json → 逐包加载
       │            items/ machines/ recipes/ qualities/ icons/   ← 数据面
       └─ 段2 Events: 同一扫描结果 → 逐包加载
                    ui/*.psui（面板注册） + events/**/*.pss（脚本编译执行顶层）
```

顶层代码立即执行的例子——ex22_api_inject 的脚本开头就是：

```pss
# ex22_api_inject/events/inject_demo.pss（节选）
inject.sell_shelf({ ... })      # ← 顶层直接调用：包加载完, 货架注入就已生效
```

**"注册类调用放加载期顶层、玩法逻辑放事件 handler"**——这条铁律在全部
教学包里无一例外：

| 顶层（加载即执行） | handler（事件触发） |
|---|---|
| `npc.register(...)`（ex23 / ex30 各脚本） | `on day_wake()`（调度/隔夜结算） |
| `inject.sell_shelf / doctor / loot_pool`（ex22） | `on dialogue_choice()`（选项结算） |
| `shop.block_sale`（按需） | `on trade_completed()`（记账） |
| `var` 全局表（ex30 monitor 的配置表） | `on shop_opened()`（开店扫描） |

## 四、启动验收：日志怎么读

包改完重启，先看日志确认"活了"，再进游戏验收。以 ex01 为例，
`MelonLoader/Latest.log` 里逐条对：

| 期待日志 | 出处 | 说明 |
|---|---|---|
| `rescan(init): N pack(s), ...` | PackScanner | 包计数 +1 = 扫描到了 |
| `[pss ex01_hello] [ex01_hello] hello.pss 已加载 (第 1 次)` | PSScript | 顶层执行 = 脚本链路通 |
| `items loaded: N def(s), ...` | ItemsPlugin | 物品定义计数包含你的 |
| `rescan(init): ... 0 compile error(s)` | EventsPlugin | 脚本全部编译通过 |
| `[ex01_hello] 游戏加载完成, ...` | 事件 | game_loaded handler 在跑 |

排查入口总表见 [07 进阶 · 调试排障](../07-advanced/06-debugging.md)；
F6 面板（psapi_manager）可以现场看注入注册与客户队列，
见 [08 · psapi_manager 带读](08-psapi-manager.md)。

## 五、给"抄结构"的人

做自己的包时，目录照哪个抄：

- **纯数据包**（加物品/配方/品质，不写逻辑）→ 抄 ex05～ex11 里最接近你需求
  的那个，或直接抄 ex02_dirs 的目录全景再删不要的。
- **玩法包**（NPC/剧情/机器玩法）→ 抄 ex30（NPC）或 ex34（机器+面板综合）：
  `events/` 按专题拆文件（一个 NPC 一个文件、机器的数据与状态机拆开），
  别把 600 行塞一个文件。
- **面板工具** → 抄 ex25（静态）→ ex27（动态）→ psapi_manager（生产级样板）。

---

**本篇完。** 下一篇：[08 · psapi_manager 带读](08-psapi-manager.md)
