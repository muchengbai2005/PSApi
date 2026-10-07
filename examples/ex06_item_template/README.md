# ex06_item_template · 示例 06：模板克隆机制

> **演示知识点**（对应文档 [03-items/02-items.md](../../api_docs/03-items/02-items.md)）：
> **template 是底子，显式字段覆盖**。同一模板 `common_ore` 派生 3 个物品
> （零覆盖 / 覆盖 name+value / 覆盖 shape）+ 1 个完全不用 template 的裸定义物品
> 对比"模板缺省值"机制。

## 核心机制：模板克隆

PSApi 不从零造物品，而是**站在原版物品的肩膀上**：

```text
"template": "common_ore"   ←    克隆这个原版物品的一切
     + 你的 JSON 覆盖项        （名字、价值、形状、图标…）
     = 新物品
```

**不写的字段 = 保持模板的值。**不写 `shape` → 占格与模板一致；不写 `icon` →
图标用模板的图集切片。

**模板缺省值**：连 `template` 都不写时，取**目录里第一个原版物品**当模板
（自动跳过其他 mod 物品，防止链式污染）。

## 文件清单

```text
ex06_item_template/
├── pack.json
└── items/
    └── ore_family.json   ← 4 个物品：3 个克隆 common_ore + 1 个裸定义
```

## 安装

把整个 `ex06_item_template` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex06_item_template/
```

## 验证（4 分钟）

1. 启动游戏，日志搜 `rescan(init)` 计数 +1。
2. 进任意存档按 **F12**，对照原版「普通矿石」（也在 F12 里能翻到）逐个看：

| 物品 | 验证点 |
|---|---|
| 矿克隆甲 | 图标/占格/**未显示 value 时与普通矿石一个价**——数据面字段零覆盖，全盘继承 |
| 富集矿 | 名字与价格变了（value 120），**图标和占格没变** |
| 矿片 | 占格变 2x1 条形；**图标没变**（icon 省略 → 模板切片） |
| 野生矿晶 | 裸定义：图标/形状来自 MaterialDirectory 里第一个原版物品（隐藏模板） |

3. 图标排障（可选）：日志搜 `icon: ex06_item_template:`，4 行都应 `from=template`。

## 逐物品讲解

**① 矿克隆甲 —— 零覆盖**
只有 id/name/desc/test。这就是"克隆"的纯态：注册一个与普通矿石几乎一模一样、
但 id 归你管的物品。适合做"我先占个位，之后再慢慢改"的原型。

**② 富集矿 —— 覆盖 name + value**
显式写的字段按覆盖规则生效；没写的继续继承。注意 `desc` 我们也写了——
name/desc/flavor 三段文本缺省都会走模板，想换文案就得显式写。

**③ 矿片 —— 覆盖 shape、icon 省略**
`shape` 覆盖成 2x1 条形；`icon` 故意省略，证明"覆盖是逐字段的"——
改形状不牵连图标。

**④ 野生矿晶 —— 裸定义（模板缺省值）**
不写 `template` → 取 `directory` 里**第一个原版物品**当隐藏模板。
显式写 `directory: "MaterialDirectory"` 让"第一个"可预期（不写则由
tags 映射推导目录，第一个原版物品是谁就不那么直观了）。
裸定义适合"我只要一个普通东西，不在乎长得像谁"的快活儿；
想要可预期行为，**永远显式写 template**。

## 叠加规则总表（背下来）

| 属性 | 你显式声明时 | 你不写时 |
|---|---|---|
| 图标 | 自定义图标（键命中时） | 模板图集切片 → 都没有则兜底图 `scav_medal` |
| 形状 | 显式 `shape` | 克隆模板形状 |
| 类型 | merge: 并集 / replace: 只留你的 | 克隆模板类型 |
| 文本 | 你的三段（name/desc/flavor） | 模板文本（本地化键接管） |
| 价值/堆叠 | 你的值 | 模板值 |
| 品质 | 你的品质组 | 模板品质行为 |
| 机器行为 | `machines/*.json` 声明优先 | 模板原生行为（`machine` 字段或克隆） |

> 机器物品的 `template` 同时是原生工厂的第二落点，但窗口/槽位行为由
> `machines/*.json` 的 `ui` 决定——template 只是底子（见 ex08）。

## 动手练习

1. 给矿克隆甲加 `"value": 1`——现在它和普通矿石还剩什么区别？（答：id 与名字）
2. 把野生矿晶的 `directory` 改成 `FoodItemDirectory` 重启，图标/形状换成谁了？
   （第一个原版食品）
3. 把富集矿的 template 改成 `metal_ingot`，观察图标/占格/价格基线一起换底子。

## 下一个示例

- [ex07_item_uses](../ex07_item_uses/README.md) —— useCount 使用次数体系
