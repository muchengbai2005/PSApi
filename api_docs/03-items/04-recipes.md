# 03 · 数据面（IV）：配方 recipes/*.json

> 配方描述"什么机器、放什么、出什么"。配方是纯数据——不管机器走批次/进度
> 哪种模式，配方的写法完全一样（运行模式由机器声明的块决定）。
> 对应引擎源码：`RecipeService.cs`（加载/编译/匹配）。

## 最小配方

```json
{
  "recipes": [
    {
      "id": "my_pack:smelt_newspaper",
      "machine": "my_pack:smelter",
      "output": { "id": "game:newspaper", "count": 1 },
      "inputs": [ { "id": "game:scrap_metal", "count": 2, "display": "废金属" } ]
    }
  ]
}
```

→ 熔炉输入槽放 2 个废金属，过夜产出 1 份报纸。这就是基线配方
（教学包 [ex09_recipes](../../examples/ex09_recipes/README.md)）。

## 全字段表

**配方层**：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | **是** | 配方 id（可短名，自动加包前缀）；同 id 后加载者覆盖前者 |
| `machine` | string | **是** | 归属机器（可短名 / `game:` 不适用——必须是你声明的机器） |
| `output` | object | **是** | 产物 `{id, count}`；`id` 必填，`count` 缺省 1（下限 1） |
| `inputs` | Filter[] | 否 | 材料列表（消耗品） |
| `tools` | Filter[] | 否 | 工具列表（不消耗，如需要"在场"的工具） |
| `qualityRule` | string | 否 | 产物品质规则：`none`(默认)/`inherit_frame`/`inherit_max_input` |
| `customName` | string | 否 | 显示名（锁定窗口等 UI 处显示；缺省用 id） |
| `description` | string | 否 | 描述文本 |
| `uses` | int | 否 | 剩余可用次数（机器锁定窗口按次数卖配方时用；缺省无限） |
| `priority` | int | 否 | 优先级，**越大越优先**（默认 0）；配合机器 `recipeSort` 生效 |

**Filter（inputs/tools 数组元素）**：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | 二选一 | 按物品 id 匹配（`game:裸id` 或短名/全名） |
| `tag` | string | 二选一 | 按类型标签匹配（大写，如 `MATERIAL`；与物品的 types/tags 对） |
| `count` | int | 否 | 需要数量，缺省 1（≤0 修为 1） |
| `display` | string | 否 | UI 显示名（缺省用 id/tag） |
| `slot` | int | 否 | 限定材料必须在指定槽位（**仅进度机消费**，批次机忽略） |

## 输入匹配语义

配方运行时把机器输入槽的物品收拢成列表逐条匹配：

- **id 匹配**：`game:前缀` 剥掉、短名剥包前缀后与物品 `identifier` 不区分
  大小写比较。`"id": "game:scrap_metal"` 与 `"id": "scrap_metal"` 等价；
  `"my_gun"` 与 `"my_pack:my_gun"` 等价。
- **tag 匹配**：自动转大写后与物品 `itemTypes` 逐一比较（`"material"` =
  `"MATERIAL"`）。适合"任意材料都行"的宽匹配。
- `inputs` 里**所有**条目都要满足（AND）；`tools` 不消耗、只需在场。

## slot 约束（进度机专属）

多输入槽机器（如 desequencer 槽 2+3）上，`slot` 限定材料位置：

```json
"inputs": [
  { "id": "game:blank_keycard", "count": 1, "slot": 2 },
  { "id": "game:ser_keycard",   "count": 1, "slot": 3 }
]
```

进度机选配方前先按槽预检：带 `slot` 的过滤器必须在**对应槽位**凑够 count。
批次机不消费 `slot`（合并所有输入槽一起验）。

## qualityRule —— 产物品质

| 值 | 行为 |
|---|---|
| `none`（默认） | 产物无品质 |
| `inherit_frame` | 继承**第一个**带品质输入的品质（如"画框"整进整出） |
| `inherit_max_input` | 继承**最高 tier** 输入的品质（好料出好货） |

品质在材料被消耗**前**选定、产物落地后应用——链条见[品质篇](05-qualities.md)。

## priority 与 recipeSort

机器声明 `recipeSort` 决定配方遍历顺序（`priority` 为默认策略）：

| recipeSort | 排序 |
|---|---|
| `priority` | 按 `priority` 降序（默认；不加 priority 的配方全 0 = 声明序） |
| `value` | 按产物 `value` 降序，同价按 priority 降序 |
| `firstMatch` | 纯声明序（文件名字典序 + 文件内顺序） |

每晚选配方时取**第一个 CanCraft 命中**的——顺序即优先。想让"高级配方抢在
低级前面"就加 `"priority": 10`。

## id 归一化（再强调一遍）

| 字段 | 规则 |
|---|---|
| `recipes[].id` / `machine` | 无冒号 → 加包前缀 |
| `output.id` / `inputs[].id` / `tools[].id` | `game:` → 剥成裸 id；无冒号 → 加包前缀 |

## 实战带读：读卡器链式配方

下面这组读卡器配方演示了配方系统的常用姿势（配合进度机；可运行的
对照包见 [ex09_recipes](../../examples/ex09_recipes/README.md)）：

```json
// ① 链式升级：产物回投再加工
{ "id": "my_pack:deseq_blank_to_ser",
  "machine": "my_pack:desequencer",
  "output": { "id": "game:ser_keycard", "count": 1 },
  "inputs": [ { "id": "game:blank_keycard", "count": 1, "slot": 3 } ] }

// ② 双输入槽分工（slot 约束）
{ "id": "my_pack:deseq_blank_ser_to_sup",
  "output": { "id": "game:sup_keycard", "count": 1 },
  "inputs": [
    { "id": "game:blank_keycard", "count": 1, "slot": 2 },
    { "id": "game:ser_keycard",   "count": 1, "slot": 3 } ] }

// ③ 同机器多配方：进度机每夜按当前槽内材料选
//    （blank+ser → sup 与 blank+sup → ser 同时声明，放什么料走什么线）
```

排障口诀：

- 配方不跑 → audit 警告 `recipe X machine 'Y' has no machines/*.json declaration`
  （machine 写错）或材料进不了槽（tag 与原生过滤器冲突，见机器篇白名单三层）。
- 产出不见了 → 批次机输出槽满会**保料跳过**（日志 `output slot/grid full`），
  腾出输出槽即可。
- 想看每晚现场 → 日志搜 `cycle fired` / `progress-recipe cycle fired`。

---

下一篇：[05 · 品质](05-qualities.md)
