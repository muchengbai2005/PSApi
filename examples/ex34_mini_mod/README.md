# ex34_mini_mod · 示例 34：综合迷你模组「迷你温室」

> **演示知识点**（对应文档 [03-items/03-machines.md](../../api_docs/03-items/03-machines.md)、
> [03-items/04-recipes.md](../../api_docs/03-items/04-recipes.md)、
> [06-psui/06-dynamic.md](../../api_docs/06-psui/06-dynamic.md)、
> [07-advanced/02-state-deep.md](../../api_docs/07-advanced/02-state-deep.md) 与
> [07-advanced/04-npc-pipeline.md](../../api_docs/07-advanced/04-npc-pipeline.md)）：
> 前面 33 个示例的知识点在这里汇成一条完整数据流：物品（种子/作物/机器）→ 机器（`custom` 批次机，过夜产出）→
> 配方（1 种子 → 2 瓜）→ NPC（收购商 `buying_ids` 只收作物）→ 现金（`trade_completed.amount`）→
> `state`（累计笔数/金额）→ PSUI 面板（`on_build` 读 state 出晨报）。
> 这是全套示例的收官之作 —— 一个"小而完整"的模组长什么样。

## 数据流全图

```text
[日光瓜种子袋 seed_pouch]        ← 起点: F12 领取 (test 物品)
   │  放进温室左格 (3x4 网格, inputWhitelist 只放它)
   ▼
[迷你温室 mini_greenhouse]        ← custom 批次机, 免电
   │  每晚 1 批 (progressSpeed): 配方 r_grow — 1 种子 → 2 瓜 (日志搜 cycle fired)
   ▼
[日光瓜 sun_melon]                ← 右格 (2x2) 取货, 摆上柜台
   │
   ▼
[果贩阿禾 melon_buyer_ahe]        ← intent:buy, buying_ids 只收日光瓜
   │  每隔 2 天保底 + buy 池 6% 补抽; budget 500±150, price.buy=1.1
   ▼
[现金 + state]                    ← trade_completed {direction="sell", amount=成交金额}
   │                                melon_sold(笔数) / melon_coins(金额)
   ▼
[晨报面板 greenboard]            ← day_wake 自动弹出 / F7 开关
                                   on_build 构建期读 state 填 scroll body
```

## 文件清单

```text
ex34_mini_mod/
├── pack.json                 ← 包清单
├── items/
│   └── greenhouse.json       ← 3 个物品: 种子袋 / 日光瓜 / 温室机器
├── machines/
│   └── greenhouse.json       ← custom 批次机声明 (免电, 左格进右格出)
├── recipes/
│   └── greenhouse.json       ← 配方 r_grow: 1 种子 → 2 瓜
├── events/
│   ├── buyer.pss             ← 收购 NPC 果贩阿禾 (buy 池)
│   └── harvest.pss           ← state 记账 + 晨报构建 + F7 绑定
└── ui/
    └── greenboard.psui       ← 温室状态牌 (on_build 动态流水)
```

## 安装

把整个 `ex34_mini_mod` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex34_mini_mod/
```

## 验证（约 15 分钟）

1. 启动游戏，日志搜 `machine audit`，温室体检行（custom 机器格式，对照 ex08 的 demo_press）：

```text
machine audit: ex34_mini_mod:mini_greenhouse ui=custom in=0 out=1 power=native battery=False speed=1 sort=priority recipes=1
```

   进存档后出现就绪行：

```text
[ex34_mini_mod] 迷你温室就绪: F12 领种子和温室, F7 看状态牌, 每天早晨自动弹晨报
```

2. **F12**：后仓出现「迷你温室」和「日光瓜种子袋」（「日光瓜」也会一并入仓 —— 收官前先忍住别直接卖它）。
3. 温室摆进店里，打开机器窗口，左格放 1–2 袋种子（**免电**），**睡觉过夜**：
   每袋种子当晚出 2 个日光瓜，落在右格；日志搜 `cycle fired` 是配方实际运行的现场记录。
   早晨自动弹出晨报面板 + 日志（若某版本早晨没自动弹出，F7 手动开关照常可用）：

```text
[ex34_mini_mod] 温室晨报: 累计卖出 0 笔 / 累计进账 0
```

4. **等阿禾**：`every_ndays` 每 2 天保底 + buy 池 6% 补抽（搜 `npc eval: melon_buyer_ahe` 看排班判定）。
   把日光瓜摆上柜台卖给她：

```text
[ex34_mini_mod] 果贩阿禾 收走日光瓜, 本笔 +66 → 累计 1 笔 / 66
```

   （金额 = 实际成交价，随她的 `buy=1.1` 加价与品质浮动，以你打出来为准。）
5. **F7** 随时开关状态牌 —— 每次开关 `on_build` 重跑，累计数从 state 读出；
   面板开着时再卖一单，`ui.rebuild` 会原地刷新数字。
6. **state 收尾验证**（复习 ex33）：存档 → 退回主菜单 → 重进 —— 晨报数字还在；
   关游戏后打开 `UserData/PSApi/state/<你的槽位>/ex34_mini_mod.json`：

```json
{
  "melon_coins": "66",
  "melon_sold": "1"
}
```

## 逐文件讲解

### items/greenhouse.json —— 三个环节各一件物品

- 种子/作物是普通物品：`template` 借原版植物系物品（`nutrifruit_seed` / `nutrifruit`，
  同一家族 —— 种什么长什么），图标省略走模板切片（icons/ 不建）。
- 温室是**机器物品**：铁律"是不是机器只看 machines/ 里有没有声明"，但物品定义也必须给
  （否则 audit 警告 `never built`）；`directory: "StationMachinery"` + `tags: MACHINE` + 3x3 `shape` 三件套齐观。

### machines/greenhouse.json —— custom 批次机（复习 ex08 路线③）

四条 custom 铁律照抄 demo_press：槽位固定 `inputSlot:0`/`outputSlot:1`、`batteryPowered:false` 必填
（无电池槽）、双白名单必写（custom 槽位零原生委托，白名单是唯一过滤来源）、`inputKind:"grid"`
严格网格可放多件 —— 左格囤一排种子，每晚吃 1 袋。`progressSpeed:1` = 每晚 1 个批次（缺省值，显式写出便于教学）。

### recipes/greenhouse.json —— 配方是纯数据

运行模式（一晚出货的批次机 / 多夜累积的进度机）由机器声明的块决定，配方写法通吃。
本包物品写全 id；原版物品才写 `game:` 前缀（加载时剥成裸 id）。`qualityRule:"none"` 产物无品质层。

### events/buyer.pss —— 变现环节

收购三件套（`buying_ids` 必收 + `budget` 预算制 + `price.buy` 收购加价）+ 排班/池双通道（复习 ex30）。
两个刻意的"省略"：`faction` 不配 = 保留 foodBuyer 模板阵营（只有想覆写时才写）；
没有 `buy_pool`/`{{buy_list}}`（那是 ex30 齐姐的课，练习 3 会加回来）。

### events/harvest.pss + ui/greenboard.psui —— 记账与显示

- `trade_completed.amount` 是**金额不是件数**：进账直接累加，"笔数"按每笔成交 +1
  （事件不带件数字段，想按件记得自己另存口径）。
- `day_wake` 对局内 `ui.open`（幂等，已打开只置顶）弹晨报；F7 `bind_key` 手动开关 —— 两条打开路径同一面板。
- `on_build` 构建期用 `ui.build_begin("body")` / `build_label` / `build_end` 填滚动区
  （`ui.build_*` 只能在 on_build 里调）；面板开着时数据变了 → `ui.rebuild("greenboard")` 原地刷新。
- 面板是 state 的**纯显示**：显示层不存数据，换存档槽数字自动跟着走。

## 动手练习

1. 把 `progressSpeed` 改成 2 —— 每晚吃 2 袋种子出 2 批（左格记得囤够种子）。
2. 加一条高产配方 `2 种子 → 5 瓜` 且 `priority: 10` —— 复习 ex09 的抢占：左格 ≥2 袋时每晚先走高产线。
3. 给阿禾加 `buy_pool = ["nutrifruit:1.0"]` + `buy_count = "1"`，`main` 台词改成两段数组并在第二段放
   `{{buy_list}}` —— 复习 ex30 的随机加收与台词插值（原版物品在 NPC 配置里写裸 id）。
4. 给 state 加 `melon_last_day`（最近成交日 `time.rel_day()`），晨报里多一行显示 ——
   记账与面板各改一处，体会"数据与显示分离"。
5. 照 ex30 老金的样子加一位**卖种子的货郎**（`intent:"sell"` + `sell_items`）——
   种子不再靠 F12，数据流真正闭环。

## 毕业

**全套 34 个示例到此毕业。**

从 ex01 的第一张物品卡，到品质/机器/配方，再到 PSS 脚本、PSUI 面板、API 句柄、外出场景与战斗，
最后到 NPC 网络、state 深应用与本篇的全系统串联 —— 你现在拥有从零写一个"小而完整"模组的全部零件。
想回头查任何一块，从 [ex01_hello](../ex01_hello/README.md) 重新走一遍，或直接翻 `api_docs/`。
