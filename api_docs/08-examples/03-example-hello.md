# 03 · example_hello 带读：数据面的标准写法

> example_hello 是 PSApi 自测/示范包：**每种能力给一个最小完整例子**。
> 本篇带读它的数据面四件套（物品/机器/配方/品质）与冒烟脚本；
> 学完你能回答"一个只加内容的包，最少要写什么"。
>
> 事实来源：`UserData/PSApi/packs/example_hello/`（v0.4.1）磁盘实况；
> 字段规范见 [03 数据面 JSON](../03-items/README.md)。

## 一、物品：从"克隆"到"品质层"

example_hello 的 6 个物品文件里，两头的写法最有教学价值。

**最简物品**（`items/mcb_weapon_gun1.json`）——不写 template，全靠显式字段：

```json
{
  "id": "example_hello:mcb_weapon_gun1",
  "name": "解放轻步枪 H1",
  "desc": "SPP 定制的轻武器",
  "value": 300,
  "tags": ["LUXURY_ITEM", "WEAPON"],
  "shape": { "w": 6, "h": 3, "cells": [[0,0],[0,1],[1,0],[1,1],[1,2],
              [2,0],[2,1],[2,2],[3,0],[3,1],[4,0],[4,1],[5,0],[5,1]] },
  "icon": { "file": "mcb_weapon_gun1.png" }
}
```

`id` / `name` / `value` / `shape` / `icon` 五件套就是一个能进背包、能卖钱、
背包占位是异形格子的物品。`tags` 决定它被哪些收购标签匹配。

**最繁物品**（`items/_placeholder.json`）——克隆原版水 + 挂四层品质：

```json
{
  "id": "example_hello:example_water",
  "directory": "FoodItemDirectory",
  "template": "bottled_water",
  "name": "示例净水瓶",
  "value": 12,
  "qualities": ["example_hello:q_rusty", "example_hello:q_pure",
                 "example_hello:q_lab_pure", "example_hello:q_star_infused"],
  "defaultQuality": "example_hello:q_star_infused"
}
```

要点：

- `template: "bottled_water"` 克隆原版瓶装水（继承类别/可堆叠等行为），
  `directory` 显式指目录（不写则按 tags 推断，见 [03 · 物品](../03-items/02-items.md)）。
- `qualities` 挂的四个 id 定义在 `qualities/example_qualities.json`；
  `defaultQuality` 让它生下来就是最高档——方便一眼看出品质层生效。
- **注意区分**：物品侧的品质引用叫 `qualities` + `defaultQuality`；
  品质层自身的定义文件是另一个 JSON（下面）。

## 二、品质层：两种模式一锅端

`qualities/example_qualities.json` 把品质层的两种模式全演示了：

```json
{ "qualities": [
  { "id": "example_hello:q_rusty", "tag": "quality_example_rusty",
    "display": "铁锈", "priceMul": 0.5, "tier": 0 },
  { "id": "example_hello:q_lab_pure", "mode": "feature",
    "category": "CATEGORY_CHEMICAL_PURITY", "display": "星尘纯品",
    "priceMul": 4.0, "tier": 4 },
  { "id": "example_hello:q_star_infused", "mode": "feature",
    "category": "CATEGORY_STAR_QUALITY", "categoryDisplay": "星辉品级",
    "display": "星辉灌注", "priceMul": 5.0, "tier": 5 }
] }
```

| 模式 | 识别特征 | 效果 |
|---|---|---|
| **tag 模式**（缺省） | 只有 `tag` 键 | 物品名前缀变化 + `priceMul` 改价 |
| **feature 模式** | `mode: "feature"` + `category` | 走原版 ItemFeature 管线，**标签打印机的下拉里出现这一层**，可后天改标 |

`CATEGORY_STAR_QUALITY` 是**自建 category**：原版没有这个键，注册后打印机
下拉多出一张独立的"星辉品级"表——这是"给原版系统加自定义维度"的样板。

## 三、机器三型：ui 字段决定窗口从哪来

example_hello 的三台机器各代表一种 `ui` 取值，是机器篇的活教材：

| 机器 | ui | 窗口 | 特征 |
|---|---|---|---|
| `example_smelter` | `furnace` | 原版熔炉窗 | 桥接原版工厂，槽 2 输入/槽 3 输出 |
| `example_desequencer` | `desequencer` | 原版读卡器窗 | 双输入槽 + **多夜进度** |
| `example_processor` | `custom` | 自装配窗口 | inputKind=grid 网格输入，免电 |

读卡器那条（`machines/example_desequencer.json`）信息最密：

```json
{ "machines": [
  {
    "id": "example_hello:example_desequencer",
    "itemRef": "example_hello:example_desequencer",
    "ui": "desequencer",
    "inputSlot": 3,
    "inputSlots": [2, 3],
    "outputSlot": 4,
    "drawnPower": 10,
    "batteryPowered": true,
    "inputWhitelist": ["blank_keycard", "ser_keycard", "sup_keycard",
                        "scrap_metal", "common_ore"],
    "slotWhitelists": [
      { "slot": 2, "whitelist": ["blank_keycard", "ser_keycard", "sup_keycard",
                                   "scrap_metal", "common_ore"] }
    ],
    "progress": { "progressPerNight": 34, "progressMax": 100 }
  }
] }
```

- `inputSlots: [2, 3]`：读卡器原生有两个可选输入槽，都开；
  `slotWhitelists` 把槽 2 的过滤器**放宽**到与主输入一致（原版槽 2 只收芯片）。
- `progress: 34/100`：一夜涨 34 点，三夜出一次货——**多夜加工**就这么两行。
- 白名单里是**原版裸 id**（机器白名单侧 `game:` 前缀会在注册时剥掉，
  两种写法都有效——注意这与 NPC 侧**不同**，NPC 侧带前缀会查无此物；
  对照 processor 的写法见下）。

自装配那条（`example_processor.json`）则是**另一种白名单写法**：

```json
"inputWhitelist": ["game:scrap_metal"],
"outputWhitelist": ["game:newspaper"]
```

`game:` 前缀在这里**会**被剥成裸 id（machine 槽位过滤器注册时归一化）。
两种写法都有效，但混着读容易糊涂——**推荐统一写裸 id**，与本书附录 FAQ 的
"id 前缀规则速记"一致。

> ⚠️ custom 型的槽位是**零委托**的：`inputWhitelist`/`outputWhitelist` 必填，
> 不写就没有任何过滤兜底（v0.6.3 起）。`inputKind: "grid"` + `inputSize: "3x4"`
> 开严格网格——配方 `count > 1` 时必须用 grid，单物品槽堆叠不支持。

## 四、配方：链式与多材料

`recipes/example_desequencer_recipes.json` 的 7 条配方覆盖三种形态：

```json
{ "id": "example_hello:deseq_blank_to_ser",
  "machine": "example_hello:example_desequencer",
  "output": { "id": "game:newspaper", "count": 1 },
  "inputs": [ { "id": "game:scrap_metal", "count": 1, "display": "废金属", "slot": 3 } ],
  "qualityRule": "none",
  "customName": "粉碎：废金属 → 报纸",
  "description": "槽3放废金属 → 3夜后产出报纸" }
```

| 形态 | 例子 | 说明 |
|---|---|---|
| 链式升级 | 空白卡 → 服务卡 → 补给卡 → 空白卡 | 产物回投再加工，验证循环 |
| 多材料 | 槽2 空白卡 + 槽3 服务卡 → 补给卡 | `inputs` 数组逐条带 `slot` |
| 多格材料 | 3x2 废金属进 1x1 槽 | 原版 UncheckedAccept 放行，`count: 1` 指件数 |

配方侧的 `game:` 前缀（`"game:newspaper"`）会被归一化剥掉——这是全书
三处归一化不一致里的一处（[03 · 配方](../03-items/04-recipes.md)有总表）。

## 五、脚本：冒烟与四通道演示

**hello.pss** 是语言内核冒烟：顶层语句（while-break、字典、`+=`）+
函数默认参数 + `on scene_loaded()` 事件各来一遍，启动日志能看到即证明
PSScript 活了。它也是"日志即测试"的写法样板：

```pss
log.info("hello.pss 已加载(第 {boot_count} 次): {describe_stock('赃物包', picked, 25)}")
```

**m1_inject.pss** 演示 inject 四通道（buy_list/sell_shelf/doctor/barter），
注意它的现状：**演示注册全部注释掉了**（验收完毕后停用，免得污染正常游戏），
文件保留作语法参考。它还留了一个**函数形式收购池**的例子：

```pss
func m1_dynamic_buy(client):
    if client.cash > 500:
        return ["example_hello:example_water"]
    return []

# inject.buy_list(m1_dynamic_buy)   # 传函数而非 id 清单 → 每客户现算
```

**u4_demo.pss + ui/u4_printer.psui** 是图元槽位面板原型（每天开店自动弹出，
拖废金属打报纸），对应教程见 [06 PSUI · 槽位面板](../06-psui/README.md)；
**e2~e6** 系列是 API 专题演示，其中 e4 的演示 NPC"老猫·影"已在 v0.11.0
正式迁往 gunworks（见下一篇）。

## 六、抄什么

| 你想要 | 抄这个 |
|---|---|
| 加一个普通物品 | `mcb_weapon_gun1.json`（五件套） |
| 克隆原版物品改行为 | `_placeholder.json` + `template` |
| 给物品加品质/稀有度 | `example_qualities.json`（两种模式各抄一条） |
| 复用原版机器窗口 | `example_desequencer.json`（改白名单+进度） |
| 自定义窗口机器 | `example_processor.json`（零委托，白名单必填） |
| 循环加工链 | `example_desequencer_recipes.json`（链式+多材料） |

---

**本篇完。** 下一篇：[04 · gunworks 数据层](04-gunworks-data.md)
