# ex08_machines · 示例 08：机器三条路线

> **演示知识点**（对应文档 [03-items/03-machines.md](../../api_docs/03-items/03-machines.md)）：
> 机器 = 一个物品 + 一份机器声明 · `ui` 字段三条窗口路线（原版工厂桥接 / `custom`
> 自装配 / `psui` 面板）· 隔夜运行三种模式（**批次机**一晚出货 / **进度机**多夜累积 /
> 打印机）· 白名单三层 · **machine audit** 启动自检行 · 睡觉过夜验证产出。
> 实战参照：官方包 `example_hello` 的三台验证机。

## 机器 = 物品 + 声明

```text
items/machines_tour.json            machines/demo_furnace.json
"items": [{                          "machines": [{
  "id": "ex08_machines:demo_furnace",   "id": "ex08_machines:demo_furnace",
  ...                                   "ui": "furnace", ...
}]                                   }]
```

硬规则：**物品是不是机器，只看它在不在 `machines/*.json` 里声明**。
没声明 → 纯装饰（audit：`has no ui → built as plain item`）。
机器 id 必须与某个物品 id 对上，否则 `never built`。

## 文件清单

```text
ex08_machines/
├── pack.json
├── items/
│   └── machines_tour.json   ← 3 个机器物品（全部 directory=StationMachinery）
├── machines/
│   ├── demo_furnace.json    ← 路线① furnace 批次机
│   ├── demo_reader.json     ← 路线② desequencer 进度机
│   └── demo_press.json      ← 路线③ custom 自装配
└── recipes/
    ├── furnace_recipes.json ← 2 废金属 → 1 报纸（当晚出货）
    ├── reader_recipes.json  ← 空白卡(槽3) → 安全卡（第 3 夜出货）
    └── press_recipes.json   ← 2 废金属(网格) → 1 报纸（免电）
```

## 安装

把整个 `ex08_machines` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex08_machines/
```

## 验证（10 分钟）

1. 启动游戏，日志搜 `machine audit`，三台体检行齐全（**机器排障第一现场**）：

```text
machine audit: ex08_machines:demo_furnace ui=furnace in=2 out=3 power=8 battery=True speed=1 sort=priority recipes=1
machine audit: ex08_machines:demo_reader ui=desequencer progress-recipe(in=3 out=4 progress=34/100) power=10 battery=True recipes=1 inWL=2 outWL=1
machine audit: ex08_machines:demo_press ui=custom in=0 out=1 power=native battery=False speed=1 sort=priority recipes=1
```

   （`inWL/outWL` 是白名单条目数；press 的 `slotWhitelists` 不计入这两项。）
2. 按 **F12**：三台机器物品全部进后仓。
3. **睡觉过夜验证产出**（核心步骤）：

| 机器 | 摆放 | 过夜结果 |
|---|---|---|
| ① 示例批次炉 | 放店里，槽 2 放 2 废金属、槽 0 放电池（≥8 电） | **当晚**槽 3 出 1 报纸 |
| ② 示例读卡机 | 槽 3 放 1 空白门卡、槽 0 放电池（≥10 电） | 第 1 夜 tooltip `加工进度: 34/100`，第 2 夜 `68/100`，**第 3 夜**槽 4 出安全卡（34→68→102） |
| ③ 示例压块机 | 左侧网格放 2 废金属（**免电**） | **当晚**右侧出 1 报纸 |

4. 每晚日志可搜 `cycle fired`（批次机）/ `progress-recipe cycle fired`（进度机），
   是配方实际运行的现场记录。

## 逐文件讲解

### 路线① demo_furnace —— furnace 工厂桥接 + 批次机

`ui: "furnace"` 调**原版熔炉工厂**，保留真实窗口/槽位/电池/模组闭包。
槽位必须贴原生布局：`0=电池 1=模组 2=输入 3=输出`（写错 audit 警告
`deviates from native layout`，材料进不去/产出出不来）。
批次机**一晚完成**：

```text
读输入槽 → 按 recipeSort 排序选第一条 CanCraft 的配方（跳过玩家锁定的）
        → 探测输出槽空位（满则保料跳过）→ 扣电（不够保料停止）
        → 消耗材料、产出、应用品质规则
```

`progressSpeed: 1` 是**每晚最多加工批次数**（不是速度）；`recipeSort: "priority"`
按配方 priority 降序选线。

### 路线② demo_reader —— desequencer 工厂桥接 + 进度机

加 `progress: {progressPerNight: 34, progressMax: 100}` 块即切换为进度机：
每晚匹配到配方 → 进度 +34（电不够不涨），攒满 100 → 消耗材料、产出、归零。
**三夜出货**。要点：

- 加工中材料被**锁定**（LockInv，取不出来）；材料被移走 → 进度清零。
- 进度随存档保留；tooltip 自动显示"加工进度: 68/100"与"正在生产: X"。
- desequencer 有两个可选输入槽（2 芯片/3 卡），输出恒为 4；
  `inputWhitelist` / `slotWhitelists` 放宽原生过滤器让门卡能进槽。
- 配方 `inputs[].slot: 3` 是**进度机专属**约束：材料必须在槽 3（批次机忽略 slot）。

### 路线③ demo_press —— custom 自装配（免电）

`ui: "custom"` 不调原版工厂，由 `CustomMachineFactory` 自己拼窗口
（左输入栏/右输出栏）。四条 custom 专属铁律：

| 规则 | 原因 |
|---|---|
| 槽位固定 `inputSlot: 0` / `outputSlot: 1` | 自装配布局没有"原生槽位表" |
| `batteryPowered: false` **必填** | 无电池槽，缺省 true 会导致永不停机地找电池 |
| `inputWhitelist`/`outputWhitelist` **必写** | custom 槽位零原生委托，白名单是唯一过滤来源 |
| `inputKind: "grid"` + 配方 count>1 | grid 是严格网格可放多件；缺省 slot=单物品槽，count>1 的配方永远凑不齐 |

`inputSize: "3x4"` 输入格尺寸（格数）、`outputSize: "2x2"` 输出格尺寸。

### 机器声明的白名单三层（排障必背）

| 层 | 字段 | 作用范围 |
|---|---|---|
| ① | `inputWhitelist` / `outputWhitelist` | 主输入/输出槽 |
| ② | `slotWhitelists: [{slot, whitelist}]` | 任意指定槽位（多输入槽各配各的） |
| ③ | 配方输入写 `tag`（与原生过滤器同语言） | 让材料天然可进槽 |

## 动手练习

1. 把 demo_furnace 的 `inputSlot` 改成 `3`、`outputSlot` 改成 `4` 重启：
   audit 出现 `slot layout ... deviates from native furnace layout`——
   原生布局不能瞎写。
2. 把 demo_reader 的 `progressPerNight` 改成 `50`：两夜出货（50→100）。
3. 把 demo_press 的 `inputWhitelist` 里加 `"game:common_ore"`，再给
   press_recipes 加一条 2 普通矿石 → 1 电池的配方（记得 battery 不在
   inputWhitelist 时进不了网格）。

## 下一个示例

- [ex09_recipes](../ex09_recipes/README.md) —— 配方专题：多输入/display/priority 抢占
