# ex07_item_uses · 示例 07：使用次数体系（useCount）

> **演示知识点**（对应文档 [03-items/02-items.md](../../api_docs/03-items/02-items.md)
> 与 [05-api-reference/06-items.md](../../api_docs/05-api-reference/06-items.md)）：
> `useCount` / `useBaseValue` / `useValuePerUse` 三字段 · tooltip 自动显示
> "余量 N/上限" · 按剩余次数**折价** · **归零销毁** · 次数随存档保留 ·
> 脚本侧 `items.uses / uses_max / use` 三件套。

## 次数体系速览

```text
useCount: 20          ← 启用次数体系: 最多 20 次, tooltip 显示 余量 N/20
useBaseValue: 10      ← 次数归零时的残值底价
useValuePerUse: 5     ← >0 启用动态定价: 价值 = 底价 + 次价 × 剩余次数
                       满箱 = 10 + 5×20 = 110; 剩 19 次 = 105 … 归零销毁
```

三个字段**不启用就全部不写**（useCount 保持 0）。次数由原生 `UseCountHelper`
托管：随存档保留、`items.use` 每次扣 1、归零自动销毁。

## 文件清单

```text
ex07_item_uses/
├── pack.json
├── items/
│   └── tool_uses.json     ← 2 个物品：工具箱（三字段全开）/ 火柴包（只开 useCount）
└── events/
    └── uses_demo.pss      ← 发放满次数演示品 + 每天早晨用一次并打日志
```

## 安装

把整个 `ex07_item_uses` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex07_item_uses/
```

## 验证（4 分钟）

1. 新开一局（或读档），`on game_loaded` 自动发放满次数演示品，日志出现：

```text
[PSApi] [pss ex07_item_uses] [ex07_item_uses] 已发放满次数的演示品: 工具箱 20/20, 火柴包 10/10 (看背包 tooltip 的余量行)
```

2. 打开背包看两件物品的 tooltip：
   - 工具箱：`余量 20/20`，价值 110（满箱 = 10 + 5×20）；
   - 火柴包：`余量 10/10`，价值恒 6（没写折价字段）。
3. **睡觉过夜**验证折价与扣次：第二天早晨日志出现：

```text
[PSApi] [pss ex07_item_uses] [ex07_item_uses] 第 N 天早晨用掉 1 次: 剩余 19/20, 折价后价值 105 (10 底价 + 5x19)
```

   再看背包：工具箱 tooltip 变 `余量 19/20`——**每用一次折 5 块**；
   火柴包不动（脚本只消耗工具箱）。
4. 归零销毁：把 `tool_uses.json` 里工具箱的 `useCount` 改成 `2`（顺带把
   `useBaseValue`/`useValuePerUse` 留着），重启、新开一局 → 两个早晨后
   日志出现 `这次用完触发归零销毁`，背包里工具箱消失。

## 逐文件讲解

### items/tool_uses.json —— 两种姿势

**工具箱（三字段全开）**：`useValuePerUse > 0` 是"按剩余次数定价"的开关——
`价值 = useBaseValue + useValuePerUse × 剩余次数`。这天然实现了"二手折价"：
越用越便宜，归零前一刻只值底价。`value: 110` 只是目录里的满箱参考价。

**火柴包（只开 useCount）**：折价字段不写 → 价值不随次数变化。
tooltip 一样显示余量、一样归零销毁——**折价是可选的**。

### events/uses_demo.pss —— 三件套 API

| 函数 | 语义 |
|---|---|
| `items.give(id, count, uses)` | 第 3 参显式发 N 次的实例 |
| `items.uses(h)` / `items.uses_max(h)` | 剩余 / 上限（无次数机制 = 0） |
| `items.use(h[, n])` | 用 n 次（缺 1）；不足返回 `false`；**归零自动销毁** |

脚本里有一个易踩的坑被刻意演示了：**归零销毁后句柄失效**——所以流程是
"先 `items.uses` 读剩余 → 剩 ≤1 时把话说完再 `items.use` → 否则用完再读"。
直接对刚归零的句柄调 `items.uses` 会报友好错误。

> 给**已有物品实例**临时挂次数（比如"这把原版刀只能再用 3 次"）用
> `items.use_init(handle, max[, base_value[, value_per_use]])`，语义同数据面三字段。

## 动手练习

1. 把工具箱的 `useValuePerUse` 改成 `0`（或删掉），重启看价值是否还随次数变。
2. 给火柴包补上 `"useBaseValue": 2, "useValuePerUse": 1`，让它也折价
   （满包 12 → 用一根少一块）。
3. 把 `uses_demo.pss` 里 `on day_wake` 的 `items.use(box, 1)` 改成 `items.use(box, 3)`，
   一夜扣 3 次，观察日志与 tooltip。

## 下一个示例

- [ex08_machines](../ex08_machines/README.md) —— 机器三条路线（批次/进度/自装配）
