# 04 · gunworks 数据层：31 个物品的设计账

> gunworks 的物品层是一份"设计账"：每个物品为什么定这个价、为什么挂这个
> template、为什么不写 template。本篇带读 5 个物品 JSON + 2 个机器声明，
> 看一个中型包如何把物品体系当作经济系统来设计。
>
> 事实来源：`UserData/PSApi/packs/gunworks/items/`（v0.13.0 磁盘实况）；
> 叠加规则实证自 `_psapi/PSApi.Items/ItemStore.cs`（见 [03 · 物品](../03-items/02-items.md)）。

## 一、物品总表：五个文件、三个用途族

| 文件 | 物品 | 用途族 |
|---|---|---|
| `gunworks_bench.json` | 组装台 + 9 部件 + 6 组装枪（16 个） | **机器玩法族**：台子是机器，部件/枪是耗材与产出 |
| `gunworks_machines.json` | 部件打印机 + 物品分析仪 | 机器玩法族的上游设备 |
| `gunworks_datacards.json` | 空白数据卡 + 5 种成品卡 | **次数消耗族**：useCount 体系 |
| `gunworks_parts.json` | 4 种枪械零件 | 纯耗材（组装台现金主材） |
| `gunworks_permit.json` | 仿制证书 | **剧情道具族**：豁免治安档案 |
| `mcb_weapon_gun1.json` | 解放轻步枪 H1 | 剧情武器（自 example_hello 复刻入命名空间） |

全部物品带 `"test": true`（除 mcb_weapon_gun1）→ 归入 F6 浏览器第 29 类
"测试"分类，方便验收拿取（除了不想让人白拿的剧情枪）。

## 二、template 与显式字段的叠加账

`gunworks_bench.json` 头注写着它的素材策略（v0.8.0 物品编辑器时代的产物）：

> template 保留作基底 — 显式 shape/icon 覆盖克隆结果（ItemStore: shape 显式
> 优先、icon 自定义键优先），tags 与模板类型**合并**（merge 模式），contraband
> 显式等级走官方 InitContrabandItem。

看一条完整的部件定义（小机匣）：

```json
{
  "id": "gunworks:part_receiver_s",
  "name": "小机匣",
  "desc": "手枪/冲锋枪用小型机匣",
  "template": "gun_part",
  "value": 60,
  "test": true,
  "shape": { "w": 3, "h": 2, "cells": [[0,0],[0,1],[1,0],[1,1],[2,0],[2,1]] },
  "icon": { "file": "part_receiver_s.png" },
  "tags": ["GUN_MOD", "MATERIAL"]
}
```

**为什么保留 template**：克隆 `gun_part` 继承原版"枪械配件"的类别行为
（可被枪匠类收购标签匹配），显式 shape/icon 只换视觉。这是"行为抄原版、
外观全自定义"的标准姿势。

**为什么零件没有 template**（`gunworks_parts.json`）：头注写明
"无 template — tags MATERIAL → MaterialDirectory，模板取目录首个原版 id 兜底，
显式 shape/icon 全覆盖视觉"。四件零件只是组装台的现金耗材，不需要任何原版
行为，于是用 tags 路由进材料目录即可。

## 三、违禁等级：与证书豁免配套

六把组装枪显式声明了 `contraband`，等级随威力走：

| 枪 | value | contraband |
|---|---|---|
| 组装手枪 | 260 | `low` |
| 自动手枪 / 冲锋枪 | 340 / 460 | `mid` |
| 步枪 | 560 | `high` |
| 两种狙击枪 | 620 / 720 | `critical` |

这不是装饰：**违禁等级决定卖枪写不写治安档案**（WEAPON_TRAFFICKING），
而"免档案"正是仿制证书的全部价值（见 [07 · 经济闭环](07-gunworks-economy.md)）。
没有证书卖 critical 狙击枪 = 治安档案快速拉满 = 顾客变少、治安官上门。
物品数据层的 `contraband` 一个字段，就是后面剧情系统的扣子。

## 四、useCount 次数体系：数据卡与证书共用

五种成品数据卡和仿制证书都用了原生 UseCountHelper（[03 · 物品](../03-items/02-items.md)）：

```json
// 数据卡（gunworks_datacards.json）
{
  "id": "gunworks:datacard_barrel",
  "value": 100,
  "useCount": 5,
  "useBaseValue": 10,
  "useValuePerUse": 18
}

// 证书（gunworks_permit.json）
{
  "id": "gunworks:permit_forged",
  "value": 300,
  "useCount": 7,
  "useBaseValue": 90,
  "useValuePerUse": 30
}
```

三个字段一个公式：**满值 = useBaseValue + useValuePerUse × useCount**

- 数据卡：10 + 18×5 = **100**（满卡）。打印机每打印一次脚本调 `items.use(card, 1)`，
  剩 4 次时价值 82、tooltip 显示"余 4/5"，归零**原生自动销毁**。
- 证书：90 + 30×7 = **300**。每晚脚本全场扣 1 次，等效"7 天有效期"，
  剩余天数照常折价。

**这是"用次数系统当保质期/耐久度"的通用范式**：JSON 定容量和价签，
脚本只负责"什么时候扣"，销毁与折价全部原生托管。伪造师卖 3-30 天随机款
证书时甚至不用新物品 id——`items.give(id, 1, days)` 直接发一个指定次数的实例。

## 五、定价账：成本≈售价 55%~78%

`gunworks_parts.json` 头注把整本账算给你看（部件合计+零件 vs 枪售价）：

| 枪 | 成本 | 售价 | 成本率 | 每晚毛利 |
|---|---|---|---|---|
| 手枪 | 195 | 260 | 75% | 65 |
| 冲锋枪 | 330 | 460 | 72% | 130 |
| 步枪 | 420 | 560 | 75% | 140 |
| 栓动狙击 | 395 | 620 | 64% | 225 |
| 半自动狙击 | 495 | 720 | 69% | 225 |

设计意图写在账本边上：**入门枪薄利走量、狙击厚利**；组装台一晚一把，
零件是唯一现金主材（部件可走打印体系自产）。同时打印机 1200 / 分析仪 900 /
空白卡 45 的定价堵住了"空白卡+废部件→分析成卡光倒卖"的套利——
45+45=90 → 满卡 100 只剩薄利，卡的真价值在 5 次打印。

**给做经济模组的人**：把定价公式和套利检查写进 JSON 头注 `//` 是 gunworks
的好习惯——数值平衡的推理过程跟着数据走，比单独的设计文档可信。

## 六、机器声明：两行 JSON 接管一台机器

三台机器的声明极短（`machines/gun_bench.json`、`machines/printer_analyzer.json`）：

```json
{ "machines": [
  { "id": "gunworks:gw_bench", "ui": "psui", "panel": "gun_bench" },
  { "id": "gunworks:part_printer", "ui": "psui", "panel": "part_printer" },
  { "id": "gunworks:item_analyzer", "ui": "psui", "panel": "item_analyzer" }
] }
```

`ui: "psui"` = 窗口由 PSApi.Events 按 `ui/<panel>.psui` 装配（双击机器原生开窗，
不是 `ui.open`）；槽内物品随机器子物品树序列化进存档。
与 example_hello 的三种 `ui` 对比：**psui 型把交互逻辑全交给脚本**，
机器 JSON 只做"这台物品是机器 + 面板叫什么"两件事。配套的面板与状态机
在 [06 · 机器层](06-gunworks-machines.md)。

## 七、抄什么

| 你想要 | 抄这个 |
|---|---|
| 行为抄原版、外观自定义 | `part_receiver_s`（template + 显式 shape/icon/tags） |
| 纯耗材物品 | `pistol_parts`（无 template，tags 路由目录） |
| 耐久/保质期/次数 | 数据卡三件套 `useCount/useBaseValue/useValuePerUse` + 脚本 `items.use` |
| 违禁品经济 | 枪的 `contraband` 等级阶梯（与治安档案联动） |
| 机器绑定 PSUI 面板 | machines/ 两行声明 + 同名 psui |

---

**本篇完。** 下一篇：[05 · gunworks NPC 层](05-gunworks-npc.md)
