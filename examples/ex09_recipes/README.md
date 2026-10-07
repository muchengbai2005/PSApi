# ex09_recipes · 示例 09：配方专题

> **演示知识点**（对应文档 [03-items/04-recipes.md](../../api_docs/03-items/04-recipes.md)）：
> 配方 = 纯数据（不管机器是批次还是进度模式，写法完全一样）· 单输入/多输入
> （AND 语义）· `display` UI 显示名 · `priority` + 机器 `recipeSort` 的
> **抢占机制** · `qualityRule` 产物品质规则 · id 归一化（`game:` 前缀）·
> 睡觉过夜验证产出。

## 核心心智：每晚选第一条可 craft 的

机器声明 `recipeSort` 决定配方遍历顺序（本包 `priority` 策略）：

```text
每晚: 按 priority 降序遍历 → 取第一条 CanCraft 命中的 → 消耗材料、产出
```

**顺序即优先**。本包 r_basic 和 r_priority 同吃"2 废金属"，但 r_priority 的
`priority: 10` 让它永远排在 r_basic（默认 0）前面——放 2 废金属**只会出瓶装水**。
这不是 bug，是本示例刻意安排的抢占演示。

## 文件清单

```text
ex09_recipes/
├── pack.json
├── items/
│   └── demo_furnace.json    ← 1 个机器物品
├── machines/
│   └── demo_furnace.json    ← 1 台 furnace 批次机（recipeSort=priority）
└── recipes/
    └── tour.json            ← 4 条配方（下表）
```

| 配方 | 输入 → 输出 | 演示点 |
|---|---|---|
| `r_basic` | 2 废金属 → 1 报纸 | 单输入基线 + 显式 `qualityRule:"none"` + `display` |
| `r_hybrid` | 1 废金属 + 1 普通矿石 → 1 电池 | 多输入 **AND** + 两个 `display` |
| `r_priority` | 2 废金属 → 1 瓶装水 | `priority: 10` 抢占 r_basic |
| `r_ore` | 1 普通矿石 → 1 废金属 | 最简配方（可选字段全省） |

## 安装

把整个 `ex09_recipes` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex09_recipes/
```

## 验证（8 分钟）

1. 启动游戏，日志应有机器体检行（`recipes=4`）：

```text
machine audit: ex09_recipes:demo_furnace ui=furnace in=2 out=3 power=8 battery=True speed=1 sort=priority recipes=4
```

2. 按 **F12** 拿到 `示例配方炉`，放店里，槽 0 放电池（≥8 电）。
3. **睡觉过夜验证产出**——往槽 2 放不同材料组合，每次睡一觉：

| 槽 2 放什么 | 过夜产出 | 走的哪条配方 |
|---|---|---|
| 2 废金属 | 1 **瓶装水** | r_priority（priority 10 抢占；r_basic 被压制） |
| 1 废金属 + 1 普通矿石 | 1 电池 | r_hybrid（缺一不可的 AND） |
| 1 普通矿石 | 1 废金属 | r_ore（最低门槛） |

   每晚日志搜 `cycle fired` 可看到配方运行现场。
4. 想看到 r_basic 出报纸 → 做"动手练习 1"（救活被压制的配方）。

## 逐文件讲解

### 配方层字段（recipes/tour.json 全用到）

| 字段 | 说明 |
|---|---|
| `id` | 配方 id（可短名自动加包前缀）；同 id 后加载者覆盖 |
| `machine` | 归属机器，必须是你 `machines/*.json` 声明的（写错 audit 警告 `recipe never runs`） |
| `output` | `{id, count}`，count 缺省 1（下限 1） |
| `inputs` | 材料列表（消耗品）；**所有条目都要满足（AND）** |
| `qualityRule` | `none`(默认)/`inherit_frame`/`inherit_max_input`，见下表 |
| `customName` / `description` | UI 显示名/描述（缺省用 id） |
| `priority` | 越大越优先（默认 0），配合机器 `recipeSort` 生效 |

**Filter（inputs 元素）**：`id`（`game:裸id` 或短名/全名）或 `tag`（按类型标签
宽匹配，如 `MATERIAL`）二选一；`count` 缺省 1；`display` UI 显示名；
`slot` 限定材料槽位（**仅进度机消费**，批次机忽略，见 ex08）。

### qualityRule 三个值

| 值 | 行为 |
|---|---|
| `none`（本包用的） | 产物无品质 |
| `inherit_frame` | 继承**第一个**带品质输入的品质（"画框"整进整出） |
| `inherit_max_input` | 继承**最高 tier** 输入的品质（好料出好货） |

品质继承的完整链条见 [03-items/05-qualities.md](../../api_docs/03-items/05-qualities.md)
与 ex10；继承类规则需要输入物品真的带品质层才有可看的效果。

### id 归一化（引用写法速查）

| 字段 | 规则 |
|---|---|
| `recipes[].id` / `machine` | 无冒号 → 加包前缀 |
| `output.id` / `inputs[].id` / `tools[].id` | `game:` → 剥成裸 id；无冒号 → 加包前缀 |

所以引用原版物品**推荐写 `game:scrap_metal`**（与裸 id 等价，但意图明确、
不会被误加包前缀）；引用自己包的物品写全 id。

### recipeSort 三策略

| recipeSort | 排序 |
|---|---|
| `priority`（默认/本包） | 按 `priority` 降序；同分按声明序 |
| `value` | 按产物 `value` 降序，同价按 priority 降序 |
| `firstMatch` | 纯声明序（文件名字典序 + 文件内顺序） |

## 动手练习

1. **救活 r_basic**：把 r_priority 的 `priority` 从 10 改成 0（与 r_basic 同分 →
   按声明序 r_basic 在前）重启，再放 2 废金属过夜 → 出**报纸**。
2. 给 r_ore 加 `"priority": 20`，然后放"1 普通矿石 + 1 废金属"过夜——
   注意 r_ore 只吃 1 矿石，r_hybrid 需要两种都在：谁会赢？
3. 把 r_hybrid 里 `common_ore` 那条改成 `"tag": "MATERIAL"`（任意材料都行），
   放 1 废金属 + 1 报纸过夜试试宽匹配。

## 下一个示例

- [ex10_qualities](../ex10_qualities/README.md) —— 品质层：tag 与 feature 双模式
