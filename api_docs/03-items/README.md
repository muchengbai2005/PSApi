# 03 · 数据面 JSON：总览

> 数据面回答"游戏里**有什么**"：物品、机器、配方、品质、图标，全部用 JSON 声明，
> 由 `PSApi.Items` 在启动时解析、进对局后注册。本篇讲贯穿数据面的通用规则；
> 五类文件的逐字段详解见后续各篇。

## 本章导航

| 篇 | 主题 | 你能学到 |
|---|---|---|
| [02 物品](02-items.md) | `items/*.json` | 定义一个新物品的全部字段 |
| [03 机器](03-machines.md) | `machines/*.json` | 让物品成为能加工的机器 |
| [04 配方](04-recipes.md) | `recipes/*.json` | 输入材料 → 输出产物 |
| [05 品质](05-qualities.md) | `qualities/*.json` | 品质层：改名、改价、打印标签 |
| [06 图标](06-icons.md) | `icons/*.png` | 自定义物品图标 |

## 五个子目录

```text
packs/<你的包>/
├── items/       物品定义      {"items": [ ... ]}
├── machines/    机器声明      {"machines": [ ... ]}
├── recipes/     配方          {"recipes": [ ... ]}
├── qualities/   品质层定义    {"qualities": [ ... ]}
└── icons/       图标 PNG（二进制，非 JSON）
```

| 子目录 | 消费者 | 一个文件能放几条 |
|---|---|---|
| `items/` | ItemStore | 多条（`items` 数组） |
| `machines/` | RecipeService | 多条（`machines` 数组） |
| `recipes/` | RecipeService | 多条（`recipes` 数组） |
| `qualities/` | QualityService | 多条（`qualities` 数组） |
| `icons/` | IconService | 一文件一图标 |

## 通用文件规则

- **顶层包装**：每类文件都是 `{"<类型复数>": [ <条目>, ... ]}`。一个文件放一条还是
  一百条都行——文件名与内容无任何关联，纯粹是你自己的组织方式。
- **排序**：文件按路径名字典序加载，文件内按数组顺序；同 id 条目**后者覆盖前者**
  （跨文件、跨包都会覆盖，覆盖时警告 `overwritten by pack <id>`）。
- **嵌套子目录合法**：`items/weapons/rifle.json` 会被递归扫到。
- **JSON 宽容语法**：`//` 注释、尾逗号、字段名大小写不敏感，全数据面统一。
- **未知字段忽略**：写错字段名不报错、直接被丢弃——**这是新手最大坑**：拼错字段
  等于没写，务必对照本篇字段表。

## 加载顺序

启动时 `PSApi.Items` 按固定顺序解析（`Plugin.Rescan`）：

```text
① qualities/   全部包的品质定义          （先加载，物品品质组才能校验）
② items/       全部包的物品定义 + 图标
③ machines/ + recipes/  逐包交替加载（每包先 machines 后 recipes）
④ audit 自检   逐台机器体检（见 03-machines.md）
```

## id 书写规则（重要，三处不一样）

数据面各处的 id 归一化规则**不统一**，写错会静默失效。牢记下表：

| 写在哪 | 规则 | 例子（当前包 id = `my_pack`） |
|---|---|---|
| `items[].id` | **原样使用，不做任何加工**——必须写全 `包id:名字` | `"my_pack:rifle"`（写 `"rifle"` 会以裸 id 注册，坏） |
| `machines[].id` / `itemRef`、`recipes[].id` / `machine` | 无冒号 → 自动加当前包前缀 | 写 `"rifle"` = `"my_pack:rifle"`；写全也行 |
| `recipes` 的 `output.id` / `inputs[].id` / `tools[].id` | `game:` 前缀 → 剥成原版裸 id；无冒号 → 加包前缀 | `"game:scrap_metal"` → `scrap_metal`；`"rifle"` → `my_pack:rifle` |
| 白名单 `inputWhitelist` 等 | 支持 `game:` 前缀（注册时剥成裸 id） | `"game:scrap_metal"` |
| 品质引用（`qualities` 数组、`defaultQuality`） | 原样查询，必须写全 | `"my_pack:q_rusty"` |

> 判断标准就一句话：**凡是指向"已存在的定义"的引用（物品、品质），写全 id；
> 定义本身的 id（机器、配方）可以偷懒写短名。** 最省心的做法：全部写全
> `包id:名字`，引用原版统一 `game:裸id`，永远不出错。

大小写：物品查询按不区分大小写比较；types/tags 匹配时自动转大写。

## 从 JSON 到游戏内的生命周期

```text
启动: 解析 items/*.json → ItemDef 列表（纯数据，校验品质引用）
           │  错误 → logs/pack_errors_*.log
           ▼
进对局: 游戏物品目录就绪 → 逐个 dir.Add(id, 惰性工厂)
           │  （注册极廉价；真正构建在第一次被访问时）
           ▼
被访问: 工厂执行 BuildItem
           ├─ 机器类: 原生工厂创建（保留真实窗口/槽位/电池闭包）
           └─ 普通类: CreateEmptyItem + 克隆模板属性
           ├─ 图标: 自定义 > 模板切片 > 兜底
           ├─ 形状: 显式 shape > 克隆模板
           ├─ 文本/数值/品质/违禁品/使用次数 …
           ▼
      GameItem 实例（F12 发放 / 商店 / 配方产出时才真正实例化）
```

**排障三现场**（详见[开发工作流](../01-getting-started/04-workflow.md)）：

1. `pack_errors_*.log`——解析期错误（JSON 坏、缺 id）。
2. 控制台 `machine audit:` 行——机器体检。
3. 控制台 `build begin/done`、`icon:` 行——单个物品构建现场。

---

下一篇：[02 · 物品定义](02-items.md)
