# 03 · 数据面（II）：物品定义 items/*.json

> 物品是数据面的原子单位。本篇逐字段讲解物品定义，并说清"模板克隆"这一核心
> 机制——理解它，你就知道哪些字段不写会得到什么。对应引擎源码：
> `ItemModels.cs`（DTO）+ `ItemStore.cs`（解析与构建）。

## 核心机制：模板克隆

PS-API 不从零造物品，而是**站在原版物品的肩膀上**：

```text
"template": "furnace"    ←    克隆这个原版物品的一切
     + 你的 JSON 覆盖项        （图标切片、形状、类型、价值…）
     = 新物品
```

不写的字段 = 保持模板的值。比如不写 `shape`，新物品的占格形状与模板一致；
不写 `icon`，图标用模板的图集切片。显式声明的字段按"覆盖规则"生效（见文末
叠加规则表）。

模板缺省值：不写 `template` 时，取**目录里第一个原版物品**当模板
（自动跳过其他 mod 物品，防止链式污染）。

## 渐进式示例

**最小物品**（只有 id 是必填的）：

```json
{
  "items": [
    { "id": "my_pack:thing" }
  ]
}
```

→ 能注册、能发放，但名字是 id、图标是兜底图、无类型。**仅适合冒烟测试**。

**一个普通物品**（完整字段写法，对照教学包
[ex05_item_basic](../../examples/ex05_item_basic/README.md)）：

```json
{
  "items": [
    {
      "id": "my_pack:rifle_h1",
      "name": "解放轻步枪 H1",
      "desc": "SPP 定制的轻武器",
      "value": 300,
      "tags": ["LUXURY_ITEM", "WEAPON"],
      "shape": {
        "w": 6, "h": 3,
        "cells": [
          [0,0],[0,1],
          [1,0],[1,1],[1,2],
          [2,0],[2,1],[2,2],
          [3,0],[3,1],
          [4,0],[4,1],
          [5,0],[5,1]
        ]
      },
      "icon": { "file": "rifle_h1.png" }
    }
  ]
}
```

注意它**没写** `directory`（靠 `tags` 里的 `WEAPON` 映射进枪械目录）和
`template`（用目录第一个原版物品当基底）。

**一个机器物品**（要配合 `machines/` 声明，见[下一篇](03-machines.md)）：

```json
{
  "items": [
    {
      "id": "my_pack:example_smelter",
      "directory": "StationMachinery",
      "template": "furnace",
      "name": "示例熔炉",
      "desc": "PS-API 注册的测试熔炉, 由原版 Furnace 工厂桥接创建。",
      "value": 400
    }
  ]
}
```

## 全字段表

| 字段 | 类型 | 必填 | 默认/缺省行为 |
|---|---|---|---|
| `id` | string | **是** | —（**必须写全 `包id:名字`**，原样注册） |
| `directory` | string | 否 | 空 → 按 `tags`/`types` 映射（见下表）→ 再空 = `MiscItemDirectory` |
| `template` | string | 否 | 空 → 目录内第一个**原版**物品 |
| `name` / `desc` / `flavor` | string | 否 | 缺省走模板；进本地化表 |
| `value` | int | 否 | 售价（0 = 免费赠品既视感） |
| `stack` | int | 否 | 堆叠上限；≤1 = 游戏默认 |
| `icon` | string 或 object | 否 | 三种写法，见[图标篇](06-icons.md) |
| `shape` | object | 否 | `{w,h,cells}`；缺省克隆模板形状 |
| `types` | string[] | 否 | 类型标签；`tags` 的正式写法 |
| `typesMode` | string | 否 | `merge`（默认，模板类型+你的）/ `replace`（只要你的） |
| `tags` | string[] | 否 | `types` 的别名，**额外**参与目录映射 |
| `qualities` | string[] | 否 | 可用品质层 id（写全），详见[品质篇](05-qualities.md) |
| `defaultQuality` | string | 否 | 出厂品质；空 = `qualities[0]` |
| `contraband` | string | 否 | 违禁品等级 `none`(默认)/`low`/`mid`/`high`/`critical` |
| `test` | bool | 否 | `true` → F6 物品浏览器"测试"分类 |
| `machine` | string | 否 | 兼容字段：非空时无 JSON 配法则克隆模板配方管理器 |
| `useCount` | int | 否 | 最大使用次数；0 = 不启用 |
| `useBaseValue` | int | 否 | 次数归零时的残值底价 |
| `useValuePerUse` | int | 否 | >0 启用按剩余次数定价：价值 = 底价 + 次价×剩余次数 |

## 逐字段详解

### id —— 物品身份证（必须写全）

```json
{ "id": "my_pack:rifle_h1" }
```

**原样注册进游戏目录，不做任何加工**。写短名 `"rifle_h1"` 会以裸 id 注册：
F12 找得到，但跨包引用、脚本 API 全部对不上号。省心铁律：永远写 `包id:名字`。

### directory —— 物品目录

物品分类进哪个游戏目录（影响商店货架、任务判定等原生行为）。可直接写
目录类名，不写则由 `tags`/`types` 按下表映射（`ItemStore.TagDirMap`，先命中
先赢）：

| tag | 目录类名 | 语义 |
|---|---|---|
| `FOOD` / `PROCESSED_FOOD` / `BREVAGE` | `FoodItemDirectory` | 食品饮料 |
| `FIREARM` / `WEAPON` | `GunsItemDirectory` | 枪械 |
| `MELEE_WEAPON` | `MeleeWeaponItemDirectory` | 近战武器 |
| `ARMOR` | `ArmorItemDirectory` | 护甲 |
| `MACHINE` | `StationMachinery` | 机器（**机器物品必须落到这**） |
| `MODULE` / `ALARM_MODULE` | `ModuleDirectory` | 功能模组 |
| `GUN_MOD` | `GunModDirectory` | 枪械配件 |
| `TOOL` / `MEDICAL_TOOL` | `ToolDirectory` | 工具 |
| `MEDICAL` / `NARCOTIC` / `SUBSTANCE` | `MedsItemDirectory` | 药品 |
| `SEED` | `HydroponicDirectory` | 种子 |
| `MATERIAL` / `BASIC_MATERIAL` | `MaterialDirectory` | 材料 |
| `STORAGE` | `ContainerItemDirectory` | 容器 |
| `DOCUMENT` / `ACCESS_CARD` | `KeyItemDirectory` | 文件/门禁卡 |
| （都不命中） | `MiscItemDirectory` | 杂物 |

目录名写错（如 `StationMachinery ` 多个空格、拼错）→ 注册时警告
`directory 'X' not found, skip <id>`，物品不注册。

### template —— 克隆模板

原版物品的裸 id（如 `furnace`、`desequencer`、`blank_keycard`、`bottled_water`）。
模板决定你**没写**的一切：图标切片、占格形状、原生行为。

- 显式 `directory` 与 `template` 可以不对应（如 `my_pack:processor` 落
  `StationMachinery` 但用 `furnace` 当模板基底）。
- 对**机器物品**，`template` 同时是原生工厂的第二落点——但窗口/槽位行为由
  `machines/*.json` 的 `ui` 决定（见[机器篇](03-machines.md)），template 只是基底。

### name / desc / flavor —— 三段文本

`name` 物品名、`desc` 短描述（hover 第二行）、`flavor` 风味文本（斜体小字）。
三者都注入原版本地化管线（`item_<id>_name` 等键），脚本侧
`items.name(...)` 也从这里读。

### value / stack —— 数值

`value` 单件售价（进原版定价基数）；`stack` 堆叠上限，只有 `>1` 才显式设置
（写 0/1 = 保持模板默认）。

### shape —— 占格形状

背包是网格放置（类 Resident Evil），`shape` 声明物品占哪些格：

```json
"shape": { "w": 2, "h": 2, "cells": [[0,0],[0,1],[1,0],[1,1]] }
```

- `w`/`h`：包围盒宽高（格数）。
- `cells`：占据的格子坐标 `[x, y]` 列表，**x=列 y=行，原点左上**。
- 可以有洞（L 形、条形都行），`w*h` 可以大于 `cells` 数。
- 缺省 = 克隆模板形状。

### types / typesMode / tags —— 类型标签

`types` 与 `tags` 是**同一个字段**的两个名字（`types` 优先）：都会被
`SetGameItemType` 写进物品的 `itemTypes`（自动转大写），原版的任务需求、
槽位过滤、商店分类都按它匹配。

- `typesMode: "merge"`（默认）：模板的类型 + 你的类型并集。
- `typesMode: "replace"`：只要你声明的，模板类型清空。
- `tags` 的额外福利：参与上面的目录映射。上面的"解放轻步枪"就是只写 `tags`
  不写 `directory` 的活例子。

### qualities / defaultQuality —— 品质组

声明该物品可以拥有哪些品质层（互斥，同时至多一层）：

```json
"qualities": ["my_pack:q_rusty", "my_pack:q_pure"],
"defaultQuality": "my_pack:q_pure"
```

- 引用必须写全 id，且品质必须已在 `qualities/*.json` 注册（品质先于物品加载，
  所以顺序天然保证）。
- 引用了不存在的品质 → 警告 `references unknown quality`。
- `defaultQuality` 不写或无效 → 取 `qualities[0]`。
- 完整机制（定价、tooltip、标签打印机）见[品质篇](05-qualities.md)。

### contraband —— 违禁品等级

```json
"contraband": "high"
```

`none`(默认) / `low` / `mid` / `high` / `critical` → 内部 0-4 级。非零走**官方**
`InitContrabandItem` 一次性完成：违禁标签、安检识别、tooltip 警示——原版
治安系统（卖黑货被查）全面接管，不需要你做任何额外声明。

### test —— 测试分类标记

`"test": true` 让物品出现在 F6 管理面板物品浏览器的"测试"分类里，方便开发期
翻找。正式发布可留着（玩家也能看到）也可去掉。

### machine —— 配方克隆开关（兼容字段）

非空（任意字符串）时：若该物品**没有**任何 JSON 配方，就整份克隆模板的
配方管理器（原版配方照跑）。这是 JSON 配方落地前的过渡兼容行为；正常开发
请用 `recipes/*.json` 声明配方，把本字段留给"完全复刻原版机器"的场景。

### useCount / useBaseValue / useValuePerUse —— 使用次数

v0.8.1 起接入**原生** `UseCountHelper`（次数体系教学见
[ex07_item_uses](../../examples/ex07_item_uses/README.md)）：

```json
"useCount": 5,
"useBaseValue": 10,
"useValuePerUse": 18
```

- `useCount: 5` → 启用次数体系：tooltip 自动显示 `余量 3/5`，脚本
  `items.use(...)` 每次扣 1，**归零自动销毁**，次数随存档保留。
- `useValuePerUse > 0` 时按剩余次数动态定价：`价值 = useBaseValue + useValuePerUse × 剩余次数`。
  上例满卡 = 10 + 18×5 = 100。
- 不启用就全部不写（`useCount` 保持 0）。

## 显式声明 vs 模板克隆：叠加规则总表

| 属性 | 你显式声明时 | 你不写时 |
|---|---|---|
| 图标 | 自定义图标（键命中时） | 模板图集切片 → 都没有则兜底图 `scav_medal` |
| 形状 | 显式 `shape` | 克隆模板形状 |
| 类型 | merge: 并集 / replace: 只留你的 | 克隆模板类型 |
| 文本 | 你的三段 | 模板文本（本地化键接管） |
| 价值/堆叠 | 你的值 | 模板值 |
| 品质 | 你的品质组 | 模板品质行为 |
| 机器行为 | `machines/*.json` 声明优先 | 模板原生行为（`machine` 字段或克隆） |

## 诊断日志行

物品构建是惰性的（被访问才 build），排障时按顺序搜这些行：

| 日志行 | 含义 |
|---|---|
| `registering: <id> -> <目录> (tpl=...)` | 注册开始 |
| `item registered: <id> [<包>] -> <目录>` | 注册成功（目录就绪后） |
| `build begin/done: <id> null=False` | 真正构建 GameItem |
| `icon: <id> key=... from=custom/template/fallback atlas=... sprite=...` | **图标排障第一行**：`key` 非空但 `from=template` = 图标键没命中（png 没加载或名字对不上） |
| `directory 'X' not found, skip <id>` | 目录名写错 |
| `item missing id, skipped` | 条目没写 id |
| `[<包>] <文件> parse failed: ...` | JSON 语法错误 |

## 常见错误速查

**物品注册了但商店/配方找不到** → `id` 写了短名没写全（对照上文 id 规则）。

**图标是别人的** → 不写 `icon` 就继承模板切片——这是特性不是 bug；要换图标
就放 png 进 `icons/` 并声明。

**形状占格不对** → `cells` 是 `[x,y]`（列,行），不是 `[行,列]`。

**想要"独立物品"却被模板行为绑架** → 检查 `machine` 字段是否误填；机器行为
请走 `machines/*.json`。

---

下一篇：[03 · 机器声明](03-machines.md)
