# 05 · NPC 配置深度

> 本篇逐块讲透 `npc.register()` 配置字典的每一个键：经济、价格体系、收购清单、出售货物、九通道对话、choices、排班。生成管线、模板总表、register/pools 挂载机制见上一篇 [04 · NPC 生成管线与模板](04-npc-pipeline.md)。
>
> 事实来源：`_psapi/PSApi/Events/NpcService.cs` 的 `ParseConfig` / `ApplyDef` / `ApplyDialogues` / `BuildChoices` / `RollBuyingList` / `RollStaticSellPlan` / `EvalSchedule`（PSApi v2.0.0），游戏程序集 `StoreClient.ClientIntent` 枚举。

---

## 一、配置全景

`npc.register(cfg)` 接受一个 dict，已知键共 27 个（未知键警告并跳过，不报错）：

| 分组 | 键 |
|---|---|
| 身份 | `id`（必填）、`name`、`base_template`、`faction`、`sprite`、`sprites` |
| 意图与经济 | `intent`、`cash`、`budget` |
| 价格 | `price`（子键 `sell_single` / `sell_bulk` / `buy`）、`buy_price_mod`†、`sell_price_mod`† |
| 收购 | `buying_ids`、`buying_tags`、`buying_features`、`black_ids`、`black_tags`、`clear_buying`、`buy_pool`、`buy_count` |
| 出售 | `sell_items`（清单或函数）、`sell_pool`、`sell_count` |
| 对话 | `dialogues`、`auto_leave` |
| 行为开关 | `multi_buy_disabled`、`no_contraband` |
| 生成控制 | `schedule`、`can_spawn`、`register`、`pools` |

† v1.12.0 起废弃，写了仅触发迁移警告。

**重复注册语义**：同 id 的 `npc.register` 后者覆盖前者（日志警告"重复注册"）。

---

## 二、意图与经济

### intent：17 个枚举值

`intent` 经 `Enum.TryParse<ClientIntent>(..., ignoreCase: true)` 解析，全部合法值：

```
UNDEFINED · BUY · SELL · SELLNBUY · INSPECTION · DIALOGUE · BARTER · RENT ·
LOAN_SHARK · SPECIAL · INFORMATION_DEALER · PROCUREMENT_OFFER ·
PROCUREMENT_COLLECT · WHOLESALE · APPRAISAL_SERVICE · GUNSMITH · EXPEDITION
```

日常最常用的是四个：`BUY`（来买货）、`SELL`（来卖货）、`SELLNBUY`（既卖也买）、`DIALOGUE`（纯聊天）。其余值是游戏原版特殊系统的意图（枪匠、远征、鉴定服务等），脚本侧没有配套流程，配了只有原版模板自身逻辑才会响应。

**记住一个方向约定：`BUY`/`SELL` 站在客户视角**——`BUY` 客户花自己的钱买你的货，`SELL` 客户带货来卖给你。

### cash 与 budget

| 配置 | 写入 | 效果 |
|---|---|---|
| `budget = [amount, range]` | `SetClientBudget(amount, range)` + `useClientBudget = true` | 预算制：预算内随机，花完即走（原版交易闭环） |
| `cash = N` | `clientCash = N`；无 budget 时 `useClientBudget = false` | 现金制：带 N 现金，行为依赖模板 |
| 两者同配 | budget 优先写入，cash 照写但 `useClientBudget` 保持 true | 预算制生效 |

**auto_leave 的 BUY 盲区（框架已兜底）**：只配 `cash` 的 `BUY` 客户 `useClientBudget=false`，原版"预算花光→离开"的判定永不命中，会永远站着不走。v1.13.1 起，`auto_leave = true` + `BUY` + 只配 cash 时框架自动 `SetClientBudget(cash, 0)`——现金顶格当预算（花光即走），上限语义不变。你自己无须处理，但要知道为什么"现金 BUY 客户也会走"。

### 行为开关

- `multi_buy_disabled = true`：每次只买/收一件（登记销毁式 NPC 用）
- `no_contraband = true`：不收违禁品（安检/执法系人设用）

---

## 三、价格体系（v1.12/v1.13）

价格是重灾区——历史上换过三套语义，本节只讲**当前有效**的一套。三个系数全部站在**玩家视角**定义，缺省一律 `1.0`（中立，v1.13.0 起框架不内置任何折扣）：

```python
npc.register({
    "id": "my_pack:dealer",
    "price": {
        "sell_single": 0.9,   # 玩家单件买入价 = 基础价 × 0.9（九折）
        "sell_bulk":   0.8,   # 玩家整桌批发价 = 已折单价 × 0.8（再八折）
        "buy":         1.05,  # NPC 收购玩家的货 = 基础价 × 1.05（加价 5%）
    },
})
```

### sell_single → 玩家买单折扣/加价

换算 `buyPriceModifier = (系数 - 1) × 100`，写入客户。原版在货物摆上柜台/展示区时给每件挂「顾客折扣/加价」特性，玩家买入单价 = 基础价值 × (1 + modifier/100)。系数 < 1 为折扣（原版公式天然支持负数，tooltip 正常显示）。

### sell_bulk → 批发（整桌买下）

批发只在 `sell_bulk < sell_single` 时开启（大于等于 = 不开）。折扣百分比 = `(1 - bulk/single) × 100`，钳制到 `[-99, -1]`，写入 `isOfferWholesale = true` + `wholesaleDiscount`。批发总价 = Σ(已按 sell_single 折算的单价) × (1 + discount/100)，精确落到 sell_bulk 系数。

### buy → NPC 收购溢价/折价

换算 `pct = (buy - 1) × 100`，注入 `psapi_premium_buy_N`（正）或 `psapi_discount_buy_N`（负）特性到 `clientItemFeatureBuying`——克隆原版 `premiumBuy15` 同款机制（原版字典没有任意百分比档位，框架按需自建工厂）。玩家卖货给该 NPC 时每件挂此特性加/减价。

**v1.13.1 收购特性方向闸门**：实证发现 `clientItemFeatureBuying` 的特性会漏挂到客户自己在售的货上（导致玩家买客户的货也被加价）。框架在 `GameItem.AddClientItemBuyingFeature` 挂了 Prefix 补丁：脚本 NPC 的非玩家所有物品一律跳过收购特性。原版客户不受影响。

### 废弃键与恒定行为

- `buy_price_mod` / `sell_price_mod`（v1.12 前）：9-18 版本语义反转后废弃，配置仅触发警告
- `sellPriceModifier`：白板化时恒置 0（中和模板残留）
- `acceptRetailMarkup = true`：恒写——卖给自定义 NPC 享受与原版客户一致的零售加价

---

## 四、收购侧：客户收什么

### 静态清单与黑名单

| 键 | 写入字段 | 语义 |
|---|---|---|
| `buying_ids` | `clientBuyingIdList` | 必收物品 id 列表 |
| `buying_tags` | `clientBuyingTagList` | 按标签收（如 `"FOOD"`） |
| `buying_features` | `clientItemFeatureBuying` | 按特性收（原版特性 id） |
| `black_ids` | `clientBlackIdList` | 绝不收的物品 |
| `black_tags` | `clientBlackTagList` | 绝不收的标签 |
| `clear_buying`（默认 true） | — | true = 先清空模板克隆的收购清单再填；false = 在模板清单上追加 |

**黑名单永远不清模板**（追加语义）。`buying_features` 与价格体系的 `clientItemFeatureBuying` 写同一个字段——`clear_buying = false` 且模板带收购特性时，价格特性与你的清单混挂，一般保持默认 true。

### buy_pool + buy_count：当日随机加收

```python
"buy_pool": ["my_pack:ak47:2.0", "my_pack:mp5", {"id": "my_pack:glock", "weight": 0.5}],
"buy_count": "2-3",     # 每次来访掷 2~3 种（定数也行，1..20）
```

- 池条目格式：`"id:权重"` 字符串（权重缺省 1.0）或 `{id, weight}` dict；兼容带命名空间 id（从尾部识别权重段）
- 抽法：**加权不放回**掷 `buy_count` 种，先剔除 `buying_ids` 必收已有的
- `buy_pool` 必须搭配 `buy_count`（缺了报错）；`buy_count` 无池 = 警告忽略
- **当日收购 = buying_ids（必收）∪ buy_pool 掷中种**，在客户生成时掷定（当日多次对话不重掷）
- 每个 id 在写入前过 `DirectoryMaster.Has` 存在性校验，无效 id 跳过 + 告警一次（每来源×每 id 一次）

### {buy_list} 台词插值

对话台词里的 `{buy_list}` 占位符在**构建对话时**替换为 buy_pool 当日掷中种的显示名（顿号分隔）。**只含掷中种，不含必收的 buying_ids**——设计意图：必收项在台词里写死，避免复读；无池或当日没掷中 → 替换为"暂无特别加收"。掷中子集按客户指针缓存（防 Il2Cpp 对象销毁后指针复用串台）。

---

## 五、出售侧：客户带什么货

### sell_items：静态清单 / 动态函数

条目格式 `"id:数量:概率"`（兼容命名空间 id）或 `{id, count, p}` dict：

```python
"sell_items": [
    "my_pack:ak47:1:0.3",          # 30% 概率带 1 把
    "my_pack:glock:1-3:0.8",       # 80% 概率带 1~3 把（每次生成独立掷）
    {"id": "my_pack:ammo_box", "count": 2, "p": 1.0},
]
```

- 数量支持 `"min-max"` 区间（1..99）；`p` 为**每条目独立判定概率**（与 sell_pool 的权重抽选是两种语义）
- 传**函数**则每次生成该客户时调用（须返回同格式数组）——动态定价、按剧情进度配货都靠它。函数出错 = 本次无货 + 警告

### sell_pool + sell_count：凑种数

与 buy_pool 同构（加权不放回），语义是"**补齐**到 sell_count 种"：先摆 sell_items 掷中的，再从池里（剔除已中的 id）抽齐差额，池条目数量固定 1~2 随机。sell_items 与 sell_pool 可混用。

### 摆货时机（StockClient）

货物**不在生成时上架**，而是拖到交易阶段。每个客户实例**恰好一次**（指针记账，拖走不补）：

```
主时机: main 对话链末行文本播完（OnCurrentTextDisplayed 链尾判定）
兜底:   交易 UI 打开（NegociationUIManager.OpenUI Postfix）
条件:   intent 为 SELL 或 SELLNBUY（BUY 客户不摆货）
动作:   逐件 DirectoryMaster.Item 创建 → AddDirectSellingItemToTable 上架
        （无效 id 跳过 + 告警；上架件数记日志 "原生摆货 +N 件"）
```

配套补丁：`StoreClient.IsClientSelling` Postfix 给有已掷货物的 SELL/SELLNBUY 脚本客户强制可卖资格（否则交易入口死锁——原版资格判定不认外部摆的货）。

---

## 六、对话九通道

### 通道表与原版字段映射

`dialogues` 是 `{通道名: 台词/候选}` dict。通道与游戏 `StoreClient` 对话字段的对应（顺序即内部通道号）：

| # | 通道 | 原版字段 | 播出时机 |
|---|---|---|---|
| 0 | `main` | `mainDialogue` | 客户进店首次对话（**链尾可挂 choices**） |
| 1 | `accept` | `acceptDealDialogue` | 玩家接受交易（框架不挂离开动作——交割流程负责离场） |
| 2 | `all_done` | `allDoneDialogue` | 交易全部完成（覆盖模板自带的 → auto_leave 时必须补挂离开，否则永不离开） |
| 3 | `repeat` | `repeatDialogue` | 重复交谈 |
| 4 | `wrong_item` | `placedWrongItemWhenSellingToDialogue` | 卖给他不收的东西 |
| 5 | `right_item` | `placeRightItemWhenSellingToDialogue` | 卖给他要的东西 |
| 6 | `interogation` | `interogationDialogue` | 审问（安保系） |
| 7 | `glasse` | `glasseDialogue` | 戴墨镜检查[待确认：原版具体触发场景] |
| 8 | `on_arrest` | `customOnArrestDialogue` | 被逮捕时 |

### 候选与多段链

每个通道的值有三种写法：

```python
"dialogues": {
    "main": "一句话",                       # 单候选单段
    "accept": ["候选一", "候选二", {"text": "候选三"}],   # 多候选，每次生成随机挑一个
    "all_done": {"texts": ["第一段", "第二段", "第三段"]},  # 单候选多段
}
```

- **多候选**：每次**生成客户**时随机选一个（当日不变，不是每次对话刷新）
- **多段链**（`texts`）：映射到原版 `NextDialogue` 链顺序播放，播完一段自动下一段
- dict 候选可用键：`text`（单段）/ `texts`（多段链）二选一，同现报错；`id` / `choices` **仅 main 通道**
- `main.id` 在多个候选里重复定义 = 后者覆盖前者 + 警告；`choices` 在多个候选里定义 = 直接报错（只能定义在一处）

### choices：main 对话选项

```python
"dialogues": {
    "main": {
        "texts": ["老板，有货吗？", "最近风声紧……"],
        "choices": [
            {"label": "卖给他", "next": "成交。下次还来找你。", "key": "my_pack:ak47"},
            {"label": "举报", "next": "你会后悔的。", "cond": "() -> security_trust() > 50"},
            {"label": "聊聊", "desc": "打听消息"},
        ],
    },
}
```

机制要点（对齐原版 RevDeal 的 AddChoiceSafe）：

1. **choices 挂在 main 链尾**，要求 `isDummyChoiceDialogue`（纯选择，非物品交割）
2. **选项身份键物品**：原版选项机制要求每个选项绑定一件物品做身份键。`key` 指定物品 id；不指定则按序轮换默认池 `newspaper` / `paper_towel` / `scrap_metal`——**超过 3 个无 key 选项会循环复用同一物品**
3. **选中动作只标记 `isBusinessComplete`**（对话完即离店）——剧情效果**全部**走 `dialogue_choice` 事件，在你的包脚本里订阅：

```python
on dialogue_choice(e):
    # e.dialogue_id / e.choice / e.npc_id ...
    if e.dialogue_id == "my_pack:dealer_offer" and e.choice == 0:
        state.set("dealer_deal_done", "1")
```

4. **dialogue_id 构造**：`包id:配置id`；未配 `dialogues.main.id` 时为 `包id:npcId:main`
5. `next` = 选中后续台词（单段）
6. **cond 显示条件**（v1.11.0）：构建对话时调用函数，返回 falsy = 该选项不显示（出错按不显示 + 警告）
7. ⚠️ **下标前移陷阱**：`event.choice` 是**运行时显示数组**的下标——被 cond 隐藏的选项会使后续选项下标前移。判定逻辑必须与 cond 条件联动（cond 隐藏第 0 项时，原第 1 项的 choice 变 0）

### auto_leave 的精确语义

`auto_leave`（默认 **true**）的"说完就走"只在一个场合挂离场动作：

| 场景 | 行为 |
|---|---|
| intent = `DIALOGUE`（纯聊天）且无 choices | main 链尾挂延迟 1 秒离场（与原版 OnDealAccepted 同款 `CurrentClientLeave(1f)`） |
| 交易客户（BUY/SELL/SELLNBUY） | main 链尾**不挂**——离开走成交/拒收 → allDone 链的原版流程，挂了会"话没说完就走" |
| all_done 通道 | 覆盖了模板自带的 allDone（其链尾本来就有原版离开动作）→ auto_leave 时必须补挂，否则永不离开 |
| 有 choices 的 main | 不挂（选项动作自己标 isBusinessComplete） |

`auto_leave = false` + 不配 all_done 的交易客户：走模板克隆的原版流程。

---

## 七、排班六模式

`schedule = {mode, interval, day, chance, max_times, put_first}`，判定在每日建队末相位执行（管线细节见 04 篇第三节）。**relDay0 是 0-based 开业日序号**（开业当天 = 0，与原版 NpcManager/RevDeal 对齐；脚本侧 `time.rel_day()` 返回的是 1-based，换算 -1）：

| mode | 判定 | 典型用途 |
|---|---|---|
| `manual`（默认） | 永不自动生成，只能 `npc.schedule()` 强制 | 完全由剧情驱动的 NPC |
| `daily` | 每天命中 | 常驻商人 |
| `every_ndays` | `relDay0 % interval == 0`（interval 默认 1；开业日 relDay0=0 必命中） | 每周来 = interval 7 |
| `specific_day` | `relDay0 == day`（day 默认 0） | 一次性剧情日 |
| `random` | 每天掷 `chance`（默认 1.0） | 惊喜访客 |
| `once` | 命中一次后永久跳过（`npc.<id>.once = "1"` 记账，读档不回滚） | 剧情首遇 |

公共修饰：

- `max_times`（默认 -1 不限）：历史生成次数（`npc.<id>.count`，按存档槽记账）达到上限后跳过
- `chance`（默认 1.0）：任何模式都可叠加概率掷骰
- `put_first`：命中时插队到当日队首（开门第一位）
- 判定顺序（skip 原因）：`manual` → `day_not_matched` → `once_fired` → `max_times(N/M)` → `chance`——即"日期先卡，次数再卡，最后掷骰"

`can_spawn` 函数在排班命中**之后**执行（每次评估调用，异常/非真值 = 本次不生成 + 警告一次）。排班命中 + can_spawn 通过 → SPAWN，诊断行格式见 [04 篇](04-npc-pipeline.md#三脚本-npc-的调度生成evaluateschedules)。

---

## 八、设计约束：买卖清单不得重叠

`ParseConfig` 在注册时强制检查四种组合，**任一物品同时出现在收购侧与出售侧 = 直接报错**（注册失败）：

```
buying_ids × sell_items    → 报错
buying_ids × sell_pool     → 报错（全量池成员检查，与掷骰结果无关）
buy_pool   × sell_items    → 报错
buy_pool   × sell_pool     → 报错
sell_items 为动态函数       → 只能运行时留意（注册时警告提醒）
```

原因：同一件物品既在客户的收购清单又在出售计划里，原版交易逻辑会出现"客户卖给你又买回去"的循环。**selling_tags / black 清单不在检查范围**——标签重叠是允许的（但同样不推荐）。

---

## 九、完整示例：剧情武器贩子

综合运用本篇全部机制——两段开场链 + 三个选项（一个带 cond）+ buy_pool 当日加收 + {buy_list} 插值 + 批发折扣 + once 排班：

```python
# my_pack 包 · story_dealer.pss

var met = state.get("story_dealer_met", "")    # 首遇标记(按存档槽)

npc.register({
    "id": "my_pack:story_dealer",
    "name": "独眼老兵",
    "base_template": "retired_gunsmith",        # 剧情系模板(本 NPC 本身就是剧情角色)
    "intent": "SELLNBUY",                       # 既卖枪也收枪
    "budget": [1500, 300],                      # 预算制: 1500±300
    "price": {"sell_single": 0.95, "sell_bulk": 0.85, "buy": 1.10},
    "schedule": {"mode": "once", "put_first": true},   # 只来一次, 开门第一位
    "buying_ids": ["my_pack:glock"],           # 必收
    "buy_pool": [                               # 当日随机加收 1~2 种
        "my_pack:ak47:2.0",
        "my_pack:mp5:1.0",
        {"id": "my_pack:ammo_box", "weight": 3.0},
    ],
    "buy_count": "1-2",
    "sell_items": [
        "my_pack:ak47:1:0.5",                  # 50% 概率带一把 AK
        "my_pack:ammo_box:2-4:1.0",            # 必带 2~4 盒弹药
    ],
    "dialogues": {
        "main": {
            "id": "veteran_offer",              # dialogue_id = my_pack:veteran_offer
            "texts": [
                "……你是新来的店主？",
                "今天加收 {buy_list}，价好。",
            ],
            "choices": [
                {"label": "生意兴隆", "next": "少打听，多做买卖。",
                 "cond": "() -> met != \"\""},       # 只有二次见面才显示(首遇 met 为空 → 隐藏)
                {"label": "初次见面？", "next": "咱们没打过交道。记住规矩：货真，价实。",
                 "cond": "() -> met == \"\""},       # 首遇专属选项
                {"label": "看看货", "desc": "进入交易"},
            ],
        },
        "all_done": "下次……也许没有下次了。",
        "wrong_item": "这破玩意儿留给收废品的。",
        "right_item": "好货。价钱不会亏你。",
    },
})

on dialogue_choice(e):
    if e.dialogue_id == "my_pack:veteran_offer":
        if e.choice == 0:
            state.set("story_dealer_met", "1")   # 记住见过面
        if e.choice == 1:
            state.set("story_dealer_met", "1")
            time.notify("独眼老兵记住了你")
```

行为推演：开业某日命中 `once` → 插队队首 → 两段开场链（第二段插入当日加收清单名）→ 三选项（首遇只见 1、2 两个；cond 隐藏第 0 项时 `e.choice` 的 0 对应"初次见面？"——**注意下标前移**）→ 交易界面：他带的枪按 95 折单买 / 85 折批发，你卖给他的 Glock 和掷中的池货按 110% 加价 → allDone 台词 → 离场。存档重读：`once` 已记账 + `story_dealer_met` 已写，他永不再来，但记忆还在。

---

**本篇完。** 下一篇：[06 · 调试排障](06-debugging.md)——日志地图、启动链路、症状对照表与 F6/F12 工具。
