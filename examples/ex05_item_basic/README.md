# ex05_item_basic · 示例 05：物品字段全家桶

> **演示知识点**（对应文档 [03-items/02-items.md](../../api_docs/03-items/02-items.md)）：
> 一个物品文件里 6 个物品各演示一组字段：`value`/`stack`/图标省略走模板 ·
> `shape` 占格形状 · `types`/`typesMode`（tags 是别名）· `contraband` 违禁品 ·
> `directory` 显式目录 · `qualities` + `defaultQuality` 出厂品质。

## 文件清单

```text
ex05_item_basic/
├── pack.json
├── items/
│   └── field_tour.json      ← 6 个物品，每个 desc 写明自己演示的字段
└── qualities/
    └── field_tour.json      ← 1 条品质（精铸锭的 defaultQuality 引用它）
```

## 安装

把整个 `ex05_item_basic` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex05_item_basic/
```

## 验证（5 分钟）

1. 启动游戏，日志搜 `rescan(init)` 计数 +1，无 `parse failed` / `not found` 字样。
2. 进任意存档按 **F12**，6 个物品全部出现在后仓，逐个核对：

| 物品 | 验证点 |
|---|---|
| 标准零件 | 单价 25；图标与原版废金属一致（icon 省略 → 模板切片）；能堆到 5 |
| L 形零件 | 背包里占 L 形 4 格（3x2 包围盒缺右上） |
| 奢侈品零件 | F6 物品浏览器里它的类型只有 LUXURY_ITEM + MATERIAL（replace 清掉了模板类型） |
| 加密黑卡 | tooltip 有违禁警示；卖出时走原版黑货安检链路 |
| 示例扳手 | F6 浏览器搜"示例扳手"→ 分类是**工具**而非材料（显式 directory 赢过 tags 映射） |
| 精铸锭 | tooltip 多一行 `品质: 新鲜出炉 ×1.5` |

3. 图标排障（可选）：日志搜 `icon: ex05_item_basic:`，6 行都应是
   `from=template`（本包没有 icons/，键为空走模板，这是特性不是 bug）。

## 逐文件讲解

### items/field_tour.json —— 6 组字段

**① value / stack / 图标省略（标准零件）**
`value` 单件售价；`stack` 堆叠上限（只有 >1 才显式设置）；`icon` 不写 →
继承模板的图集切片。省略即继承是 PSApi 物品系统的核心心智模型。

**② shape（L 形零件）**
`{w, h, cells}`：w/h 是包围盒宽高，cells 是占据格 `[x, y]` 列表——
**x=列、y=行、原点左上**（写成"行,列"是最常见的错误）。包围盒可以有洞。

**③ types / typesMode（奢侈品零件）**
`types` 与 `tags` 是同一个字段的两个名字（types 优先），自动转大写写进
`itemTypes`，原版任务需求/槽位过滤/商店分类都按它匹配。`tags` 的额外福利：
参与目录映射。`typesMode`：`merge`（默认，模板类型∪你的）/ `replace`（只要你的）。

**④ contraband（加密黑卡）**
字符串枚举：`none`(默认)/`low`/`mid`/`high`/`critical`（内部 0-4 级）。
非 none 时走**原版** `InitContrabandItem` 一次性完成违禁标签、安检识别、
tooltip 警示——治安系统全面接管，不需要任何额外声明。

**⑤ directory（示例扳手）**
物品落哪个游戏目录。不写则由 tags/types 按映射表推导（`MATERIAL` →
`MaterialDirectory`）；**显式声明优先于映射**——本物品 tags 说 MATERIAL，
directory 说 ToolDirectory，最终落工具目录。目录名拼错 →
`directory 'X' not found, skip <id>`，物品不注册。

**⑥ qualities / defaultQuality（精铸锭）**
声明可用品质层（写全 id），`defaultQuality` 是出厂品质，不写取 `qualities[0]`。
品质必须已在 `qualities/*.json` 注册——数据面按 `qualities → items → recipes`
顺序消费，顺序天然保证。

### qualities/field_tour.json

tag 模式最小品质：`tag` 是 TagSystem 标签名，`display` 是显示名，
`priceMul 1.5` → 收购报价 ×1.5。完整品质机制见
[03-items/05-qualities.md](../../api_docs/03-items/05-qualities.md)，
四种模式对比见 ex10。

## 显式声明 vs 模板克隆（速查）

| 属性 | 你显式声明时 | 你不写时 |
|---|---|---|
| 图标 | 自定义图标（键命中时） | 模板图集切片 → 兜底勋章图 |
| 形状 | 显式 `shape` | 克隆模板形状 |
| 类型 | merge 并集 / replace 只留你的 | 克隆模板类型 |
| 文本 | 你的 name/desc/flavor | 模板文本 |
| 价值/堆叠 | 你的值 | 模板值 |
| 品质 | 你的品质组 | 模板品质行为 |

## 动手练习

1. 把 L 形零件的 `cells` 改成 `[[0,0],[1,0],[2,0],[0,1],[1,1],[2,1]]`
   （满 6 格），F12 重发看占格变化——顺带体会 x/y 方向。
2. 把奢侈品零件的 `typesMode` 改成 `merge`，F6 里看它的类型多了什么
   （模板 scrap_metal 的类型会并回来）。
3. 把示例扳手的 `directory` 改成 `StationMachinery `（多个空格）重启，
   日志搜 `not found, skip` 看它被拒收。
4. 把加密黑卡的 `contraband` 改成 `"critical"`，对比 tooltip 警示变化。

## 下一个示例

- [ex06_item_template](../ex06_item_template/README.md) —— 模板克隆机制专题
