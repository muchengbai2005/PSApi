# 03 · 数据面（III）：机器声明 machines/*.json

> 机器 = 一个物品 + 一份机器声明。声明决定它的**窗口**（双击开什么界面）和
> **隔夜行为**（怎么加工）。对应引擎源码：`RecipeService.cs`（机器编译与批次
> 加工）、`CustomMachineFactory.cs`（自装配窗口）、`ProgressRecipeService.cs`
> （多夜进度）、`ModulePrinterService.cs`（模组打印机）。

## 核心规则：声明了才是机器

v0.5.3 起的硬规则：**一个物品是不是机器，只看它在不在 `machines/*.json` 里
声明**。不再按模板名猜。没声明 → 纯装饰物品（即使模板是熔炉，日志会提示
`plain item: ... built as decoration`）。

机器声明与物品定义通过 id 关联：

```text
items/gun_bench.json            machines/gun_bench.json
"items": [{                       "machines": [{
  "id": "my_pack:gun_bench",  ◀──▶  "id": "my_pack:gun_bench",
  ...                                "ui": "psui", ...
}]                                }]
```

`machines[].id` 可以写短名（自动加包前缀），但**必须与某个 `items[].id` 对上**，
否则警告 `has no items/*.json definition → never built`。

## ui 字段：三条窗口路线

`ui` 是机器声明的灵魂，决定双击机器时打开什么窗口：

| 路线 | 写法 | 窗口从哪来 | 适合 |
|---|---|---|---|
| **原版工厂桥接** | `furnace` 等 13 种（下表） | 调原版机器工厂，保留真实窗口/槽位/电池/模组闭包 | 想要原版机器的全部原生行为 |
| **自装配窗口** | `custom` | `CustomMachineFactory` 自己拼窗口（输入/输出两栏） | 全新布局，纯 JSON 可控 |
| **PSUI 面板** | `psui` | PSApi 界面面按同包 `ui/<panel>.psui` 装配，逻辑全在脚本 | 深度定制界面+玩法（[ex29_psui_machine](../../examples/ex29_psui_machine/README.md) 路线） |
| （不声明） | 缺省或 `none` | 无窗口 | 纯装饰 |

**原版工厂 13 种**（大小写/`-`/空格不敏感，括号内为等价别名）：

| ui | 原版机器 | 原生槽位布局 |
|---|---|---|
| `furnace` | 熔炉 | 0=电池 1=模组 2=输入 3=输出 |
| `desequencer` | 芯片读写器 | 0=电池 1=模组 2=芯片槽 3=卡槽 4=输出 |
| `purifier` (`water_purifier`/`waterpurifier`) | 净水器 | 2=输入=输出（原位） |
| `moisturefarm` (`moisture_farm`) | 集水器 | 原生布局 |
| `recharger` | 充电器 | 原生布局 |
| `projector` | 投影仪 | 原生布局 |
| `alarm` (`alarme`) | 警报器 | 原生布局 |
| `bottleprinter` (`bottle_printer`) | 瓶装打印机 | 原生布局 |
| `cassetteplayer` (`cassette_player`) | 磁带机 | 原生布局 |
| `boxdispenser` (`box_dispenser`) | 盒子分配器 | 原生布局 |
| `feeddispenser` (`feed_dispenser`) | 饲料分配器 | 原生布局 |
| `winerack` (`agewell`/`wine_rack`) | 酒架 | 原生布局 |

> 槽位布局很重要：`inputSlot`/`outputSlot` 必须贴着原生布局，否则材料进不去/
> 产出出不来（audit 会警告 `deviates from native layout`）。已知约定：
> furnace = in 2 / out 3；desequencer = in 2或3 / out 4；purifier = 原位 2/2。

## 全字段表

| 字段 | 类型 | 适用路线 | 说明 |
|---|---|---|---|
| `id` | string | 全部 | 机器 id（可短名）；必须与物品 id 对上 |
| `itemRef` | string | 全部 | 关联的物品 id，缺省 = `id`（一般不用写） |
| `ui` | string | 全部 | 窗口路线（上表） |
| `panel` | string | `psui` | 绑定的 PSUI 面板短名（同包 `ui/<panel>.psui`，可写全 `pack:panel`） |
| `inputSlot` | int | 原版/custom | 主输入槽位号；缺省 2 |
| `inputSlots` | int[] | 原版 | 多输入槽（如 desequencer `[2,3]`） |
| `outputSlot` | int | 原版/custom | 输出槽位号；缺省 = inputSlot+1 |
| `inputSize` | string | custom | 输入格尺寸 `"WxH"`（如 `"3x4"`），缺省 `3x4` |
| `outputSize` | string | custom | 输出格尺寸，缺省 `2x2` |
| `inputKind` | string | custom | `"slot"`(默认，单物品槽) / `"grid"`(严格网格，可放多物品) |
| `outputKind` | string | custom | 同上 |
| `drawnPower` | int | 全部 | 每夜耗电；缺省走原生扣电逻辑 |
| `batteryPowered` | bool | 全部 | 是否需要电池；**缺省 true**；custom 无电池槽必须写 false |
| `progressSpeed` | int | 批次机 | **每晚最多加工批次数**（⚠ 不是速度），缺省 1 |
| `recipeSort` | string | 批次机 | 配方选择策略：`priority`(默认)/`value`/`firstMatch` |
| `inputWhitelist` | string[] | 原版/custom | 放宽输入槽过滤器（额外放行的物品 id，支持 `game:`） |
| `outputWhitelist` | string[] | 原版/custom | 放宽输出槽过滤器 |
| `slotWhitelists` | [{slot,whitelist}] | 原版 | 额外槽位的白名单（多输入槽各自放宽） |
| `progress` | object | 进度机 | `{progressPerNight, progressMax}`，多夜加工，见下 |
| `printer` | object | 打印机 | `{templateSlot, blankSlot, outputSlot, progressPerNight, progressMax, randomEffects}`，见下 |
| `toggleable` | bool | — | ⚠ **死字段**：只解析不生效（audit 会警告） |
| `ignoreOutputFilter` | bool | — | ⚠ **死字段**：v0.5.7 起废弃，输出预检改探测法天然放行 |

## 隔夜运行模式（四选一，由声明的块决定）

### 模式 A：批次机（什么块都不加）

```json
{
  "machines": [
    {
      "id": "my_pack:smelter",
      "ui": "furnace",
      "inputSlot": 2, "outputSlot": 3,
      "drawnPower": 8, "batteryPowered": true,
      "progressSpeed": 1, "recipeSort": "priority"
    }
  ]
}
```

每晚（睡觉过夜）执行 `progressSpeed` 个批次；每批次：

```text
读输入槽 → 按排序选第一个 CanCraft 的配方（跳过玩家锁定的）
        → 探测输出槽是否有空位（满则保料跳过）
        → 扣电（不够则保料停止）
        → 消耗材料、产出到输出槽、应用品质规则
```

一晚即可完成——"熔炉当晚出货"就是这个模式。

### 模式 B：进度机（`progress` 块）

```json
{
  "machines": [
    {
      "id": "my_pack:desequencer",
      "ui": "desequencer",
      "inputSlot": 3, "inputSlots": [2, 3], "outputSlot": 4,
      "inputWhitelist": ["blank_keycard", "scrap_metal"],
      "outputWhitelist": ["newspaper"],
      "progress": { "progressPerNight": 34, "progressMax": 100 }
    }
  ]
}
```

像原版读写器一样**跨多夜累积**：

- 每晚匹配到配方 → 进度 `+progressPerNight`（电不够则不涨）。
- 进度 ≥ `progressMax` → 消耗材料、产出、进度归零。
- 上例 34/100 → 第 3 夜出货（34→68→102≥100）。
- **加工中材料锁定**（LockInv，取不出来）；材料被移走 → 进度清零。
- 进度随存档保留（机器唯一 id 托管表 + 存档标签双载体）。
- tooltip 自动显示"加工进度: 68/100"与"正在生产: X"。

### 模式 C：打印机（`printer` 块）

```json
{
  "machines": [
    {
      "id": "my_pack:module_copier",
      "ui": "desequencer",
      "printer": {
        "templateSlot": 2, "blankSlot": 3, "outputSlot": 4,
        "progressPerNight": 25, "progressMax": 100,
        "randomEffects": 1
      }
    }
  ]
}
```

"复印机"模型：模板槽放功能模组、空白槽放打印材料，每夜 +progressPerNight，
攒满复制一份同型模组到输出槽。`randomEffects: N` 让成品附带 N 层随机已注册
品质。槽位缺省 2/3/4、进度缺省 25/100。

> ⚠ 已知限制：打印机不改槽位过滤器，desequencer 模板槽原生只收芯片/卡，
> 模组要靠脚本或存档放进槽。声明了 `printer` 就**忽略** recipes（audit 警告）。

### 模式 D：PSUI 机器（`ui: "psui"`）

```json
{
  "machines": [
    { "id": "my_pack:gun_bench", "ui": "psui", "panel": "gun_bench" }
  ]
}
```

窗口和逻辑完全交给脚本侧：`ui/gun_bench.psui` 描述界面，`.pss` 脚本订阅事件
写玩法。机器物品只负责"双击开窗 + 槽内物品随存档序列化"。这是最自由也最
费代码的路线，完整教学见 [06 PSUI](../06-psui/README.md)、教学包
[ex29_psui_machine](../../examples/ex29_psui_machine/README.md) 与
[08 实战示例包](../08-examples/README.md)。

## 槽位过滤器：白名单三层

原版槽位有原生过滤器（熔炉输入槽只收 `MATERIAL` 标签物品）。放行自定义/
不匹配物品的三层手段：

| 层 | 字段 | 作用范围 |
|---|---|---|
| ① | `inputWhitelist` / `outputWhitelist` | 主输入/输出槽 |
| ② | `slotWhitelists: [{slot, whitelist}]` | 任意指定槽位（多输入槽各配各的） |
| ③ | 配方输入写 `tag`（与原生过滤器同语言） | 让材料天然可进槽 |

白名单条目支持 `game:` 前缀（注册时剥成裸 id）。custom 路线的槽位零原生
委托，**白名单是唯一过滤来源**——所以 custom 机器的 whitelist 必写。

## audit：启动自检

每次加载完，控制台逐台打印体检行——**机器排障第一现场**：

```text
machine audit: my_pack:smelter ui=furnace in=2 out=3 power=8 battery=True speed=1 sort=priority recipes=1
machine audit: my_pack:desequencer ui=desequencer progress-recipe(in=3 out=4 progress=34/100) power=10 battery=True recipes=7 inWL=5 outWL=4
machine audit: my_pack:module_copier ui=desequencer printer(tpl=2 blank=3 out=4 progress=25/100) recipes=0
```

常见 audit 警告与修法：

| 警告 | 原因 / 修法 |
|---|---|
| `has no ui → built as plain item` | 缺 `ui` 字段——不是机器 |
| `has no items/*.json definition → never built` | 机器 id 与物品 id 对不上 |
| `has no recipes → native behaviour only` | 没配方也能跑原生行为；若非本意，检查 recipes 的 `machine` 是否写对 |
| `sets 'toggleable' — dead field` | 死字段，删掉 |
| `declares both printer and recipes` | 二选一 |
| `slot layout ... deviates from native X layout` | inputSlot/outputSlot 没按原生布局表填 |
| `recipe X input tag Y cannot enter Z slot` | 配方 tag 与槽位原生过滤器冲突，配 whitelist 或换 tag |
| `recipe X machine 'Y' has no machines/*.json declaration` | 配方的 machine 写错，配方永远不会跑 |

## 完整示例：机器三路线教学包

教学包 [ex08_machines](../../examples/ex08_machines/README.md) 用三台机器
覆盖了三条路线（furnace / custom / 进度机各一台），建议对着源文件读：

| 文件 | 内容 |
|---|---|
| `items/machines_tour.json` | 三台机器的物品定义 |
| `machines/` | 三份机器声明，三路线各一 |
| `recipes/` | 三份配方，按机器拆文件 |

---

下一篇：[04 · 配方](04-recipes.md)
