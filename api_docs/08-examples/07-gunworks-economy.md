# 07 · gunworks 经济闭环：商品怎么流进市场

> 一个包做了 31 个物品，怎么让玩家**搞得到、卖得掉、还想再搞**？
> gunworks 的答案是一条三通道流通链 + 一张违法风险牌。本篇带读
> distribution.pss 与 permit.pss 两个脚本，看 inject 系统与治安系统的合围。
>
> 事实来源：`UserData/PSApi/packs/gunworks/events/{distribution,permit}.pss`
> （v0.13.0 磁盘实况）；机制细节见 [07 进阶 · 原版注入](../07-advanced/03-inject-deep.md)。

## 一、全景：模组物品的五个来路

gunworks 的机器体系（组装台/打印机/分析仪）是**自产线**，但玩家第一次
接触这些物品永远靠买。distribution.pss + cells.pss + guide.pss 一起，
给每类商品安排了来路：

| 商品 | 主渠道 | 副渠道 | 野路子 |
|---|---|---|---|
| 组装台/打印机/分析仪 | 老枪 sell_pool（权重 6） | 博士夜间商店 | 朱利安见面礼（组装台×1） |
| 枪械零件（现金主材） | 老枪 sell_pool（高权重） | 老鬼备售步枪/狙击 | 原版 NPC 货架 3-8%、掷骰池 |
| 枪械部件（打印材料） | 老枪 sell_pool | 老猫·影 sell_pool | 原版 NPC 货架 8-10% |
| 空白数据卡 | 老枪/老鬼/老猫 | — | 货架 5%、material 池 2% |
| 成品数据卡 | **分析仪自产（唯一）** | — | 货架 2-3%（全场最低） |
| 仿制证书 | 老鬼 sell_pool（7 天款） | 费边（3-30 天款） | 朱利安见面礼（10 天款） |
| 组装枪 | **组装台自产** | — | 货架 2-3%（gw_pistol/smg） |
| 解放步枪 H1 | 诺曼/拜伦 sell_items | 老枪/老鬼 sell_pool | makeshiftWeapon 掷骰池 3% |

**设计语言**：两台机器"只能自产"守住了玩法的唯一性；散件全渠道流通
保证入门无门槛；每类商品都有一条"错过主渠道还有救"的副线。

## 二、distribution.pss：inject 三通道实战

### 通道一：博士夜间商店（阈值 + 概率开门）

```pss
inject.doctor("gunworks:gw_bench", 1)
inject.doctor("gunworks:part_printer", 1)
inject.doctor("gunworks:item_analyzer", 1)

var DOCTOR_PLAN = {
    "gunworks:gw_bench"      = {day = 4,  pct = 0.40},
    "gunworks:part_printer"  = {day = 8,  pct = 0.35},
    "gunworks:item_analyzer" = {day = 12, pct = 0.35},
}

on day_wake():
    var today = time.rel_day()
    for row in inject.list():
        if row.channel != "doctor":
            continue
        var plan = DOCTOR_PLAN[row.id]
        if plan == null:
            continue
        var up = today >= plan.day and randf() < plan.pct
        inject.tune(row.idx, {enabled = up})
    inject.reset_tracking()
```

三个可抄的决策：

1. **注册在顶层、开关在 day_wake**——`inject.tune` 的覆盖层只活本次会话，
   每天重算等于幂等（设计注释：越贵的机器越晚开放，博士是错过老枪后的备胎）。
2. **idx 现查现用，绝不写死**——`inject.list()` 的 idx 受**所有包**的注册
   顺序影响，按 `channel + id` 匹配才能对顺序零假设。
3. **`inject.reset_tracking()` 每天调**——重置"今天是否已注入"记录，
   让明天重新掷骰。

### 通道二：原版 NPC 货架（概率档位学）

```pss
inject.sell_shelf("gunworks:part_barrel_s", "1-3", 0.10)   # 部件小件, 数量随机
inject.sell_shelf("gunworks:datacard_barrel", 1, 0.03, {uses = 5})  # 满次数卡
inject.sell_shelf("gunworks:gw_pistol", 1, 0.03)           # 模组枪
inject.sell_shelf("handmade_pistol", 1, 0.04)              # 原版枪(裸 id)
```

26 条注册的概率是**三轮调参**的结果（注释记录了完整过程）：
v0.9.0 的 0.10-0.15 太高（货架泛滥）→ v0.9.1 的 0.04-0.06 太低（等于没有）→
v0.10.0 落在中间档，并且**按商品层级分档**：

| 层级 | 概率 | 逻辑 |
|---|---|---|
| 部件小件（barrel_s/grip_s） | 0.10 | 入门材料，多见 |
| 零件散货 | 0.03-0.08 | 现金耗材，中档 |
| 模组枪 | 0.02-0.03 | 违禁稀有好货 |
| 成品数据卡 | 0.02-0.03 | **全场最低**——只能分析仪自产的底线不能被货架冲垮 |

第 4 参 `{uses = 5}` 让注入的数据卡带满次数（与 JSON useCount 呼应）；
count 支持 `"1-3"` 区间（上限 5，每客户实例独立掷）。

### 通道三：原版掷骰池（世界观的暗线）

```pss
inject.loot_pool("makeshiftWeapon", "gunworks:mcb_weapon_gun1", 0.03)
inject.loot_pool("material", "gunworks:pistol_parts", 0.04)
inject.loot_pool("material", "gunworks:datacard_blank", 0.02)
```

makeshiftWeapon/material 是拾荒客/小贼等 SELL 客户"带货上门"与远征的底层池。
**什么不进池**的注释比进池的更值得读：成品数据卡（会扰动定价体系）、
机器（高价值设备走博士+老枪渠道）、证书（剧情伪证不该野摊流通）——
每一条"不做"都有经济理由。

## 三、permit.pss：一张证书改变卖枪规则

仿制证书是全书**改动游戏规则最深**的物品：开张前放展示柜 → 当天卖任何
WEAPON 类型物品不写治安档案（WEAPON_TRAFFICKING 与 FENCING 同免）。
实现全在 33 行的 permit.pss：

```pss
var PERMIT_ID = "gunworks:permit_forged"

shop.block_sale(PERMIT_ID)              # 顶层: 任何客户都不买证书(防展示柜被买走)

on shop_opened():
    var found = false
    for it in shop.showcase_items():    # 开卷帘门时扫展示柜
        if it.id == PERMIT_ID:
            found = true
    crime.exempt_guns(found)            # 有证 → 当天豁免
    if found:
        log.info("[gunworks] 仿制证书生效: 本日售卖枪械不留治安记录")

on day_wake():
    crime.exempt_guns(false)            # 复位: 重新等开张扫描
    for c in items.find_all(PERMIT_ID): # 全场证书 -1 次(背包/后仓)
        items.use(c, 1)
    for it in shop.showcase_items():    # 展示柜里的也要扣
        if it.id == PERMIT_ID:
            items.use(it, 1)
```

四个机制决策，每个都有出处：

| 决策 | 理由 |
|---|---|
| **开张前**放才生效 | 与原版许可证 HandlePermit 开卷帘门扫描同构——防"被查了再补证"的对策 |
| 只免**当天新记录** | 历史治安档案不动，豁免是"向前"的 |
| 衰减按 id 扫描 `items.use` | 与 useCount 上限无关 → 费边的 3-30 天款、见面礼 10 天款**共用这一段逻辑**，零特判 |
| `shop.block_sale` 顶层注册 | 证书放展示柜是"摆给系统看的"，不是商品——不禁售会被路人买走 |

这就是 v0.7.0 新脚本 API（`crime.exempt_guns` / `shop.showcase_items` /
`shop.block_sale` / `items.find_all`）的**组合样板**：单看每个函数都简单，
合起来就是一条玩法规则。

## 四、回收环：卖什么、卖给谁

经济闭环的最后一环是**出口**。cells.pss 的收购表（[05 · NPC 层](05-gunworks-npc.md)）
+ 声望表构成"按阶层回收"：

```text
部件(打印材料) ──卖──→ 裘德(ll+2~4)          组装枪低档 ──→ 马洛夫翻新(ll)
零件(现金主材) ──卖──→ 邓肯(ll)              组装枪高档 ──→ 格雷洗货(bm)
成品数据卡     ──卖──→ 马洛夫(ll+2, 按剩余次数折价收)
证书/解放步枪  ──卖──→ 瑞特(bm)              枪械零件   ──→ 切尼(sec+5, 销毁登记)
```

配合 [04 · 数据层](04-gunworks-data.md) 的定价账：部件合计+零件 ≈ 枪售价
55%-78%，玩家每把枪赚 65-225；组装枪有收购方保底（马洛夫/格雷 buy_pool），
但**收购价 = 原版折算不动**——想赚得多就得等 NPC 顾客上门零售，
风险（违禁等级/治安档案）与收益对齐。

## 五、抄什么

| 你想要 | 抄这个 |
|---|---|
| 机器/商品分阶段解锁 | DOCTOR_PLAN 表 + day_wake 重算（idx 现查） |
| 让模组物品出现在原版 NPC 货架 | sell_shelf 概率分档 + `{uses}` |
| 让模组物品被原版客户"带上门" | loot_pool（想想什么**不该**进池） |
| 改变一条游戏规则 | permit.pss 的"扫描→豁免→复位→衰减"四段 |
| 每类商品多条来路 | 第一节的渠道矩阵表 |

---

**本篇完。** 下一篇：[08 · psapi_manager 带读](08-psapi-manager.md)
