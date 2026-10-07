# 04 · NPC 生成管线与模板

> 本篇回答三个问题：**游戏每天怎么把客户排进队列**（生成管线）、**`base_template` 到底能填哪些值**（247 个有效模板总表）、**register / pools 把你的 NPC 挂进原版系统后会发生什么**。
>
> 分工：本篇讲**机制与管线**；NPC 配置字段的逐项语义（对话、价格、摆货、排班）见下一篇 [05 · NPC 配置深度](05-npc-config.md)；`npc.*` 函数签名见 [05-api-reference/08-npc](../05-api-reference/08-npc.md)。
>
> 事实来源：`_psapi/PSApi/Events/NpcService.cs`、`GameHooks.cs`（PSApi v2.0.0 单工程）、游戏程序集 `ModHook.cs` / `StoreClientListDict`（cpp2il ISIL 反编译，模板清单提取自其 `.cctor`）。

---

## 一、原版的每日建队与五相位钩子

游戏每天开店前由 `StoreClientManager` 建立当日客户队列（`clientStack`）：从若干**加权工厂池**里抽客户、调用工厂委托构建 `StoreClient` 实例、依次入队。构建过程分布在五个官方 ModHook 事件之间：

```
OnGenerateCustomerVeryEarly ──┐
OnGenerateCustomerEarly ──────┤
OnGenerateCustomerNormal ─────┼── 原版建队主体（抽池 → 工厂构建 → 入队）
OnGenerateCustomerLate ───────┤
OnGenerateCustomerVeryLate ───┘
```

五个事件签名均为 `Action<StoreClientManager>`，是游戏官方暴露的 modding 接口（零补丁、零风险）。PSApi 只挂其中两个：

| 相位 | PSApi 行为 | 目的 |
|---|---|---|
| **VeryEarly** | `SnapshotQueue()`：把 `clientStack` 里所有客户指针存进 `HashSet<long>` | 给末相位做**差集基准** |
| **VeryLate** | `OnGenerationDone()`：重新快照，**新出现的客户**逐个发 `customer_generated` 事件；随后执行脚本 NPC 的调度评估 | 识别"今天新来了谁" |

中间三个相位（Early/Normal/Late）PSApi 当前未使用——游戏在那里做主体建队，插进去反而会干扰原版逻辑。

---

## 二、customer_generated 事件

### 差集算法

VeryLate 时遍历当前 `clientStack`，凡是指针**不在** VeryEarly 快照里的，就是本次建队新生成的客户。用指针（`sc.Pointer.ToInt64()`）而非 id 判重，因为同一天队列里可能出现同 id 的多个实例。

### payload

```json
{
  "client": "<PsClientHandle>",
  "id": "shadyMerchant",
  "name": "可疑的商人",
  "source": "vanilla"
}
```

### source 三个值的来源

| source | 含义 | 判定路径 |
|---|---|---|
| `vanilla` | 原版自己生成的客户 | 差集发现，且 id 不在任何脚本 NPC 注册表里 |
| `pool` | 挂进加权池的**脚本 NPC 被原版抽中** | 差集发现，且 id 在注册表里（此时打一行日志 `npc '<id>'(...) 进入今日队列(source=pool)`） |
| `schedule` / `schedule+` | 脚本 NPC 走调度评估生成 | `Spawn()` 直接发布，不走差集；`schedule+` 表示是 `npc.schedule()` 强制生成 |

注意：**`vanilla` 客户也发这个事件**——这是监听"今天原版来了谁"的官方入口，不限于自己的 NPC。

---

## 三、脚本 NPC 的调度生成（EvaluateSchedules）

`OnGenerationDone` 的第二件事是 `EvaluateSchedules(mgr)`——每天一次，把所有注册的脚本 NPC 过一遍排班表。

### 执行条件与去重

```
mgr == null               → skip（无管理器）
PlayerStore.instance == null → skip（不在对局）
GameDay.TryRead 失败       → skip（读不到天数）
last_eval_day == 今天      → skip（今天已评估过）
```

`last_eval_day` 存在 `SaveStates.For("psapi.events.npc")`（即 `UserData/PSApi/state/<槽>/psapi.events.npc.json`），以**绝对天数** `day`（`StoreStation.GetDayCounter()`）记——换槽、重开游戏都不会重复评估同一天。

### 单个 NPC 的评估流水

对每个注册的 `Entry`：

```
读档:  count = state["npc.<id>.count"]     （历史生成次数）
       onceFired = state["npc.<id>.once"] == "1"
评估:  EvalSchedule(排班表, relDay0, count, onceFired) → want + 诊断
       （relDay0 = rel_day - 1，0-based，对齐原版 NpcManager/RevDeal）
过滤:  can_spawn() 脚本函数（如配置）→ 异常/非真值 = 本次不生成
强制:  npc.schedule() 挂起的 _pendingSpawn → want = true
生成:  Spawn(entry, "schedule"/"schedule+", put_first)
```

生成成功后立刻记账并落盘：

- `npc.<id>.count` +1（存在**该 NPC 所属包**的 SaveStates 里，不是 psapi.events.npc）
- 排班模式为 `once` 时写 `npc.<id>.once = "1"`
- 整轮评估结束统一 `SaveStates.FlushAll()`

### 诊断行：npc eval 日志怎么读

每个评估日、每个注册 NPC 都会往 `MelonLoader/Latest.log` 写**恰好一行**（常驻可见性设计——排班问题不需要 debug 开关就能查）：

```
npc eval: gunsmith_daily day=12 eligible=true roll=-/1.00 can_spawn=true → SPAWN
npc eval: rare_visitor day=12 eligible=false roll=0.37/0.50 can_spawn=true → skip(chance)
npc eval: one_time_quest day=12 eligible=false roll=-/1.00 can_spawn=true → skip(already fired)
```

| 字段 | 含义 |
|---|---|
| `eligible` | 排班模式判定（日期/间隔/once 等硬条件）是否满足 |
| `roll/chance` | `random` 模式的掷骰值/概率（非 random 模式 roll 显示 `-`）；eligible=false 时若原因不是 chance，roll 仍可能显示数值 |
| `can_spawn` | `can_spawn()` 脚本过滤是否通过 |
| `→ SPAWN` / `→ skip(原因)` | 最终判定；原因可能是 `day`（未到）/`interval`（间隔未满）/`chance`（掷骰失败）/`max_times`（次数用尽）/`already fired`（once 已触发）/`can_spawn=false` |

**排障口诀：NPC 不来，先 grep `npc eval: <id>`，答案就在那一行。**（详见 [06 · 调试排障](06-debugging.md)）

### Spawn：入队与插队

```csharp
bool putFirst = putFirstOverride ?? (schedule.put_first);
if (putFirst) mgr.AddNextClient(sc);   // 插到队首（开门第一位）
else          mgr.AddClient(sc);       // 排到队尾
```

`put_first` 的典型用途：剧情关键 NPC（`npc.schedule()` 的第二参、排班表的 `put_first` 键）保证玩家一开门就见到。

---

## 四、BuildClient：从模板到成品

`Spawn` 的第一步是 `BuildClient(entry)`，完整流程：

```
base_template 有值?
 ├─ 是 → StoreClientListDict.CreateStoreClient(模板id)
 │       ├─ 正常 → 得到模板克隆
 │       ├─ 键不存在 → ★游戏侧静默回退拾荒者（见下节，PSApi 不知情）
 │       └─ 抛异常/返回 null → Warn "模板克隆异常/无效，回退奸商模板"
 └─ 否/上一步失败 → StoreClientList.CreateShadyMerchant()（奸商兜底）
        └─ 再失败 → Warn "无法创建任何模板客户"，生成失败

 → ApplyFaction(sc, faction)          阵营覆写（可选）
 → sc.CompleteClientCreation(true)    原版收尾（构建对话骨架等）
 → ApplyDef(sc, entry)                应用你的全部配置
       └─ 中途抛异常 → Err "应用定义出错, 退回未修改模板客户"
                      （同原版 NpcManager 语义：绝不上半残客户）
```

### ★ 无效模板名的真实行为（重要修正）

`CreateStoreClient` 对**不存在的键**既不抛异常也不返回 null。游戏侧实际逻辑（ISIL 反编译实证）：

> 键查不到 → `LogError("StoreClient with identifier not found In ListDict")` → **返回 `CreateScavGeneral()`**。

后果：**`base_template` 打错字不会在 PSApi 日志里报任何错**——你会无声地得到一个拾荒者模板的客户（外观/行为全是拾荒者），那行 LogError 落在游戏自己的日志流里。NpcService 源码注释里的"回退奸商模板"只覆盖异常路径，日常写错模板名走的是拾荒者路径。

**防御手段：对照下一节的总表填模板名**；怀疑中招时看客户是不是长着拾荒者的样子/行为。

### ApplyFaction 支持的阵营值

`faction` 配置键在 `CompleteClientCreation` 之前覆写阵营（不配 = 保留模板阵营）：

| faction 值 | 游戏侧调用 |
|---|---|
| `scav` | `InitScav` |
| `lower` / `lower_level` | `InitLowerLevelCitizen` |
| `security` | `InitSecurity` |
| `upper` / `upper_level` | `InitUpperLevelCitizen` |
| `tourist` | `InitTourist` |
| `rev` | `InitRev` |
| `black_market` / `blackmarket` | `InitBlackMarket` |
| `cartel` | `InitCartel` |

未知阵营值 → 警告一次，保留模板阵营。

### ApplyDef 的白板化（v1.13.0 起）

模板克隆会**连带私有回调与收购特性**（实证案例：克隆 `shadyMerchant` 会继承一个对每件上架货按违禁等级挂"折扣价"特性的摆货回调，组装狙击枪 critical 档会被压到 0 元购）。因此 ApplyDef 在应用你的配置**之前**先把克隆体抹成白板：

- `onItemAddedToClient` / `onItemAddedToWeighted` / `OnItemAddedToShowcase` = null（清摆货回调）
- `clientItemFeatureBuying.Clear()`（清模板收购特性清单）
- `sellPriceModifier = 0`（中和模板残留折价）

其余（价格、对话、摆货、收购清单……）见 [05 · NPC 配置深度](05-npc-config.md)。

### 立绘固定

自定义 NPC 的立绘按 id 做 **FNV-1a 稳定哈希**取模固定（`net6` 的 `GetHashCode` 按进程随机化，重启就换脸，故不可用）。未配 `sprite`/`sprites` 时，以工厂掷好的 `spriteName` 剥尾部数字得前缀（如 `maleScav8` → `maleScav`），从 SpriteDict 筛同前缀皮肤再固定一张——同 id 重启不换脸。

---

## 五、base_template 有效模板总表（247 个）

### 数据来源与可靠性

模板 id 全集 = 游戏程序集 `StoreClientListDict.storeClientDict` 字典的键，在其 `.cctor`（静态构造）里逐个注册。本表从 cpp2il ISIL 反编译的 `.cctor` 方法体中完整提取，**共 247 个**，与当前游戏版本对应。填总表以外的任何值都会触发上一节的"拾荒者静默回退"。

标注说明：

- ★ = 经实际 mod 包使用并验证可正常克隆的模板（16 个）
- **分组按命名推断**，仅为查阅方便，不代表游戏的内部分类
- `CreateCartelIntro` 带 `Create` 前缀是游戏源码的命名笔误，但它确实是注册键，有效
- 带 `wanted` / `georgio` / `mentor` / `retired_` 等前缀的是**剧情/任务系模板**，克隆后可能携带剧情对话状态，做常规商人请优先选经济系模板

### ① 拾荒与回收（21）

`junker`、`junker2`★、`scavGeneral`（无效模板的兜底者）、`scavCrate`、`scavInspectable`、`scavBlood`、`scavStamp`、`scavAppraisal`、`scavAppraisalRandom`、`scavBarter`、`scavHaul`、`scavWaterMinor`、`scavenger`、`scavengerHousehold`、`machine_scrapper`★、`salvagePilot`★、`scrap_byer`、`scavGenericFoodSeller`、`scavGenericHouseholdSeller`、`scavGenericMaterialSeller`、`scavGenericMedSeller`

### ② 商贩零售（5）

`merchant`、`shadyMerchant`★、`shadyPharma`★、`lower_level_rare_merchant`★、`secSurplusMerchant`★（安保盈余物资商人）

### ③ 枪械系（8）

`lower_level_gunsmith`★、`ll_gun_buyer`、`merc_gun_buyer`、`ul_gun_buyer`、`ll_gun_seller`、`merc_gun_seller`、`ul_gun_seller`、`retired_gunsmith`（剧情）

### ④ 水经济（11）

`waterSeller`★、`generic_water_buyer`、`waterScavenger`、`thirstySpacer`、`scavWaterShopper`、`waterSpace2`、`waterStockpiler`、`waterCompany`、`waterSellerScav`、`waterHishonestSellerScav`、`honestBadWaterSeller`

### ⑤ 食物与农业（13）

`spacerFood`、`spacerFoodVariety`、`spacerAmenityVariety`、`farmer_standard`、`farmerSeedBuyer`、`farmerMinor`、`foodBuyer`、`foodBankBuyer`、`spacerFoodI`、`hungrySpacer`、`spacerChef`、`privateChef`、`shadyFarmer`

### ⑥ 医疗与化学（12）

`lowerLevelChemist`★、`rareLowerLevelChemist`、`pharmacist`、`clinicVolunteer`、`nurseMinor`、`nurseMinor2`、`clinicNurse`、`lowLevelDoctor`、`hospitalAdmin`、`spacerMedical`、`sickChildCaretaker`、`sickLowers`

### ⑦ 安保与执法（13）

`patrolOfficer`、`secRequisitionOfficer`、`securityOfficerFine`、`securityOfficerArrest`、`securityOfficerRevokePermit`、`securityOfficerExecution`、`privateSecurityContractor`，Lun 警官剧情线：`officerLunVisit`、`officerLunShot`、`officerLunBackOnDuty`、`officerLunSurplus`、`officerLunInspection`、`lunCriminel`

### ⑧ 上层与游客（12）

`upperLowerVisitor`、`upperLowerVisitorLuxury`、`brokeUpperLevel`★、`upperLevelCharity`、`upperLevelVisitorMinor`、`upperLevelServantHoboParty`、`upperLevelHedonist`、`cluelessTouristBuyer`、`touristWeaponSouvenirBuyer`、`touristJunkSouvenirBuyer`、`foodieTourist`、`foodieTouristReturning`

### ⑨ 小偷与犯罪（28）

`thief`★、`pettyThief`、`foodThief`、`criminalBuyerThief`、`criminalBuyerMerc`、`street_dealer`、`thiefGenericFoodSeller`、`thiefGenericHouseholdSeller`、`thiefGenericMaterialSeller`、`thiefGenericMedSeller`，通缉线：`wanted1`、`wanted2`、`wanted3`、`wanted4`、`wanted4Normal`、`wanted6`、`wanted7`、`wanted8`、`wanted9`、`wantedBait`、`wanted_intro`、`wanted_intro2`，卡特尔线：`cartelIntro`、`cartelSec`、`cartelSecSnitched`、`CreateCartelIntro`、`cartelHandlerWeekly`、`cartelWoundedSoldier`

### ⑩ 金融与保险（6）

`loan_shark`、`loan_shark2`、`loan_shark_collect`、`lotteryRep`、`lotteryWinnerMinor`、`insuranceIntroduction`

### ⑪ 供给与快递（11）

`supplier`★、`supplierIntro`、`expeditionSupplier`、`courrierRedImp`、`courrierMining`、`courrierChem`、`courrierPharma`、`courrierRedImpFree`、`courrierMiningFree`、`courrierChemFree`★、`courrierPharmaFree`

### ⑫ 市民日常（10）

`lowerLevelCitizenPowerBuying`、`lowerLevelCitizenMaintenanceBuying`、`upperLevelCitizenPowerBuying`、`lowerLevelElder`、`spacerHouseholdItem`、`youngSpacerHousehold`、`familyMother`、`citizenHousehold`、`upperMaid`、`wornOutSpacer`

### ⑬ 工程维修与采矿（13）

`inventor`、`inventorStorage`、`lowerInventor`、`stationEngineer`、`stationEngineerMinor`、`annoyedEngineer`、`miner`、`minerMinor`、`iceMiner`、`foreman`、`peatMinorClient`、`lowerRepairman`、`maintenanceWorker`

### ⑭ 成瘾与以物易物（6）

`addictBarter`、`addictGeneralBarter`、`addictBarterBlood`、`lowerLevelChemistBarter`、`normalFoodBarter`、`desperateAddict`

### ⑮ 剧情、导师与演出（51）

退休系列：`retired_farmer`、`retired_rancher`、`retired_water_merchant`、`retired_junker`、`retired_junker_intro`、`retired_winemaker`、`retired_chemist`；导师系列：`mentor`、`mentor_event_tip`、`mentor_visual_inspection`、`mentor_outro`、`mentor_display_case`、`mentor_display_case_reminder`、`mentorAfterhours`、`mentor_bargain`、`mentor_sec_record`；门卫：`janitorIntroduction`、`janitorUEIntroduction`、`janitorUEIntroductionFence`；wild 线：`wild2`、`wild_upgrade_reminder`、`wild_network_reminder`、`wild_bailout`、`wildMarketplaceIntro`；拾荒剧情：`food_scav_story`、`food_scav_story2`、`food_scav_story3`、`luckyScavStory`；故事商贩：`story_scrap_seller`、`story_cigarette_vendor`、`story_cigarette_vendor2`、`storyWaterVendor`、`story_water_buyer`、`story_water_buyer2`、`spacerFoodStory`、`spacerFoodStory2`、`storyFoodBarter`；鉴定板：`appraisalBoardIntro`、`appraisalBoardIntro2`；其他演出：`offDutyOfficerStoryTreat`、`inventorIntro`、`inventorModuleIntro`、`brokenMachineIntro`；georgio 线：`georgio1`、`georgio1_1`、`georgio2`、`georgio3`、`georgio4`、`georgioLoan`、`georgioLoanPayback`、`georgio_nuclear_waste`

### ⑯ 过剩/短缺经济客户（10）

`food_surplus_client`、`material_surplus_client`、`medical_surplus_client`、`substance_surplus_client`、`weapon_armor_surplus_client`、`food_shortage_client`、`material_shortage_client`、`medical_shortage_client`、`substance_shortage_client`、`weapon_armor_shortage_client`

### ⑰ 特殊买主与角色（17）

`poisonBuyer`、`radioactiveWasteCustomer`★、`evidenceSeller`★、`landlord`、`landlord_wholesale`、`mercenaryBuyer`、`panicBuyer`、`corporateBuyer`、`panickedShopkeeperWeaponBuyer`、`shadyCustomerInformation`、`selfDefenseEnthusiast`、`woundedMan`、`minorMerc`、`conspiracyMinor`、`lynching_citizen`、`lover`、`OldScav1`

### 选模板的建议

1. **常规买主/卖主**：从 ①–⑥、⑪、⑯ 里选经济系模板，行为干净（白板化后残留最少）。
2. **需要"收特定货"的行为底座**：先看模板的收购语义是否接近你要的（如 `evidenceSeller` 收证据），再在 05 篇的 `buying_ids`/`buying_tags` 里覆写清单。
3. **剧情系模板（⑨ 通缉/卡特尔、⑮ 全部）**：除非你在做剧情复刻，否则别用——它们可能带阶段状态与专属对话。
4. 拿不准时，先只配 `base_template` + `name` 生成一次，F6 面板观察外观与默认行为再加配置。

---

## 六、register=true：注册进原版模板字典

`npc.register()` 配置里写 `register = true`（**或配了任何 pools——写 pools 隐含 register**）的 NPC，会在每日评估时（`EnsureRegistered`）被塞进游戏全局的 `StoreClientListDict.storeClientDict`：

```csharp
// NpcService.EnsureRegistered 的核心语义
bool want = entry.Def.Register || entry.Def.Pools.Count > 0;  // pools 隐含 register
if (dict.ContainsKey(entry.Def.Id)) continue;                 // ★原版键占用 → 不覆盖
dict.Add(entry.Def.Id, FactoryOf(entry));                     // 挂工厂委托
```

两个关键语义：

1. **注册的是工厂委托，不是实例**。之后任何系统（包括游戏原版逻辑、其他模组、`npc.spawn_by_id` 类调用）执行 `CreateStoreClient("你的id")` 都会实时走一遍 `BuildClient`——你的配置（当日掷好的收购清单、价格、对话）当场生效。
2. **原版键占用不覆盖**。如果你的 NPC id 撞了 247 个原版键之一，注册被跳过——所以**永远给你的 NPC id 加包名前缀**
（`my_pack:xxx` 形态或至少 `xxx_mp` 形态），这与 [物品 id 规范](../03-items/02-items.md) 是同一要求。

---

## 七、pools：挂进原版加权池

`npc.pool_add(pool, id, weight_pct)` 把 NPC 工厂挂进游戏原版的**加权客户池**。原版每天建队就是从这些池里抽工厂的——挂进去等于让你的 NPC 变成"原版随机客户"的一部分。

### 五池对照表

| pool 名 | 原版字段（StoreClientList） | 权重数组数 | 语义（命名推断） |
|---|---|---|---|
| `client` | `clientList` | 3（`clientListProba` / `II` / `III`） | 下层常规客户主池（三档权重） |
| `buy` | `buyClientList` | 1 | 买主池 |
| `sell` | `sellClientList` | 1 | 卖家池 |
| `upper` | `clientListUpper` | 1 | 上层客户池 |
| `bm` | `clientListBM` | 3（`clientListProbaBM` / `II` / `III`） | 黑市客户池（三档权重） |

带多档权重数组的池（client/bm），挂载时**每档都会加同一权重**——原版大概对应不同抽池档位/场景，脚本侧不区分。

### weight_pct 的换算（首次挂载缓存）

```
weight_pct >= 0:  resolved = max(1, round(池当前权重总和 × pct / 100))
weight_pct <  0:  resolved = 10（默认权重）
挂载权重 = max(1, round(resolved × scale))    （scale 见下）
```

`resolved` 在**首次挂载时缓存**（`PoolReq.ResolvedWeight`），之后跨天重挂都用缓存值——否则第二天池底数里已含你自己，越滚越大（漂移）。

### pool_scale：剧情式调权

`npc.pool_scale(pattern, factor)` 按 id（精确）或 `"前缀*"`（批量）把已挂载条目**撤旧重挂**，权重 = 首挂缓存 × factor。这是从原版 NpcManager 的 `RevPoolMultiplier` 平移的机制（原版案例：老祝"接受"后 `rev_cell_*` 全部池权重 ×3）。幂等：同 factor 重挂结果不变。不在对局时只记 Scale，下次挂载生效。

### 挂载时机与幂等

- 挂载发生在每日 `EvaluateSchedules` → `EnsurePools`（对每个 Entry 的每个 PoolReq 调 `AddToPool`）
- `AddToPool` 幂等：工厂委托已在池里就跳过
- 不在对局（`GetPoolLists` 拿不到原版静态列表）时静默跳过，次日评估再挂

### 池客户的一生

```
每日建队 → 原版按权重抽中你的工厂
        → 日志 "npc '<id>' 工厂被调用(池抽中/按 id 创建)"
        → BuildClient（同第四节全流程）
        → 入队 → VeryLate 差集 → customer_generated(source=pool)
```

---

## 八、设计模式速查

| 你想要的效果 | 配置路径 |
|---|---|
| 固定日程商人（每周三来） | `npc.register` + `schedule = {mode="specific_day", day=3}` |
| 一次性剧情 NPC | `npc.register` + `schedule = {mode="once"}`（触发后 `npc.<id>.once=1` 永久记账） |
| 随机常驻客户（融入原版生态） | `npc.register` + `npc.pool_add("client", id, 2.0)` |
| 剧情推进时强制上门 | 脚本里 `npc.schedule(id, put_first=true)` → 下次建队必来且排第一 |
| 每日概率刷新的黑市商 | `pools=["bm"]` + `schedule = {mode="daily", chance=0.3}` 组合 |
| 监听原版客户行为 | 订阅 `customer_generated`（source=vanilla 的也会发） |

**schedule 与 pools 可以共存**：走 schedule 的是"保底到场"，走 pools 的是"随机抽中"——两边共用 BuildClient，配置只有一份。注意共存时 count 只在 schedule 路径记账（池抽中不 +1）。

---

**本篇完。** 下一篇：[05 · NPC 配置深度](05-npc-config.md)——九通道对话、choices、价格体系、摆货计划、收购清单、排班六模式的逐字段语义。
