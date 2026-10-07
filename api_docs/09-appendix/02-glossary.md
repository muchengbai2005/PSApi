# 02 · 术语表

> 收录全书出现的 PS-API 与游戏双层术语。按主题分组；
> 斜体标注的词条是**游戏原生概念**（PS-API 文档常引用）。

## 加载器与架构

| 术语 | 含义 |
|---|---|
| **PS-API / PSApi** | 本书主角：基于 MelonLoader 的内容包式 modding 加载器，双模块（Events + Items） |
| **PSApi** | 统一宿主（v2.0.0，`Mods/PSApi.dll`，MelonPriority 10）：数据面（物品/机器/配方/品质/图标）+ 逻辑面（PSScript/场景/注入/NPC）+ 界面面（PSUI） |
| **PSApi.Items / PSApi.Events** | 合并前双组件（v0.9.x / v1.x 时代），v2.0.0 起合并为 PSApi 单 dll；名字作为**命名空间与内部模块名**保留（`PSApi.Items.*` / `PSApi.Events.*`），老文档中的"需 Events vX"指合并前能力版本 |
| *MelonLoader* | 游戏通用 mod 加载器，PS-API 的运行底座 |
| **IL2CPP** | 本游戏的代码编译形态（运行时只有原生码+元数据）——PS-API 用 cpp2il dump 做参考 |
| **三面一体** | PS-API 的结构：数据面（JSON）+ 逻辑面（PSScript）+ 界面面（PSUI） |

## 内容包

| 术语 | 含义 |
|---|---|
| **内容包 / pack** | `UserData/PSApi/packs/` 下一个带 pack.json 的目录 |
| **pack.json** | 包清单：id/name/version/authors/gameVersions（+惯用 `//` 说明键） |
| **`//` 键** | JSON 里的注释惯用写法；包作者常用来当 changelog 与设计账 |
| **目录包 / DLL 包** | 分发形态：zip 目录 vs pack_compiler 编译的 `PSPack.<id>.dll` |
| **命名空间前缀** | 模组内容 id 一律 `包id:名字`；跨包引用靠它避免撞车 |
| **`game:` 前缀** | 引用原版物品的显式写法；**仅部分系统归一化**（见速查表/FAQ） |

## 数据面

| 术语 | 含义 |
|---|---|
| **template（模板克隆）** | 物品 JSON 里克隆一个原版物品作基底；显式字段按规则覆盖/合并 |
| **directory（物品目录）** | 游戏物品分类注册表（如 FoodItemDirectory）；无 template 时按 tags 路由 |
| **shape（占位形状）** | 背包网格占位：`{w, h, cells}` 格子清单，支持异形 |
| **tag（标签）** | 物品大写标签（WEAPON/MATERIAL…），收购标签匹配用 |
| **quality / 品质层** | tag 模式（名前缀+priceMul）或 feature 模式（标签打印机可改） |
| **useCount 体系** | 原生 UseCountHelper：`useCount/useBaseValue/useValuePerUse` → 满值=base+per×count，`items.use` 扣次、归零自动销毁 |
| **`test: true`** | 物品归入 F6 浏览器第 29 类"测试"，开发拿取用 |
| **machines 声明** | machines/*.json：`ui` 字段决定窗口来源（furnace/desequencer/custom/psui 四型） |
| **progress（多夜进度）** | 机器/配方级 `progressPerNight/progressMax`，一夜涨一段 |
| **grid 输入** | custom 机器 `inputKind: "grid"`：严格网格输入区，配方 count>1 必须用 |

## PSScript

| 术语 | 含义 |
|---|---|
| **PSScript / .pss** | PS-API 的脚本语言（Python 风格语法，Godot GDScript 式函数/事件） |
| **顶层（加载期）** | .pss 文件缩进为 0 的代码：包加载完立即执行；注册类调用必须在这 |
| **handler** | `on <事件名>():` 订阅的事件处理函数 |
| **event** | handler 内的事件负载对象；不同事件字段不同 |
| **falsy / 真值** | PSScript 判定：`false/null/0/空串/空容器` 为假，其余为真 |
| **插值** | `"{expr}"` 字符串内嵌表达式；`{{` 转义字面大括号 |
| **句柄** | 运行时对象引用（client/machine/item）；**可读写不可序列化**——不能进 state |
| **state** | 存档状态：`state.get/set/has`，按存档槽隔离、自动落盘 |
| **uid** | 机器唯一数字 id（uniqueId 入库分配）；可序列化，隔夜找回机器靠它 |

## PSUI

| 术语 | 含义 |
|---|---|
| **PSUI / .psui** | 声明式界面文件：`window: 元素树 + 属性 + 回调名` |
| **元素 / elem** | 面板控件（label/button/slot/scroll/dropdown/input…共 15 型） |
| **persistent: true** | 面板实例不随关闭销毁（机器面板必开——槽内容挂元素树上） |
| **结构恒定铁律** | 机器面板元素树永不增删：存档按 **BFS 索引**记槽内物品，改结构=串槽 |
| **占位白名单** | 输出槽填不存在的 id → 排他注册全拒 → "只出不进"（脚本 spawn 直入不受限） |
| **双锁** | `ui.machine_lock`：插入锁+取出锁同时上；对玩家与脚本都生效 |
| **on_build** | 面板构建期回调：命令式填充动态区（build_begin/build_* /build_end） |
| **ui.rebuild()** | 整面板重建（位置自动记忆）；配"签名比对"防闪动 |
| **动态面板** | 静态骨架 psui + on_build 填充的模式（样板：psapi_manager） |
| **grid_slot** | 多物品网格槽元素；触发图元树后端（PixelWindow） |

## NPC 系统

| 术语 | 含义 |
|---|---|
| **npc.register** | 脚本 NPC 注册（config 28 键，另有 2 废弃键）；加载期顶层调用 |
| **base_template** | 基底客户模板 id；**必须取自 247 个有效键**（07-advanced/04 总表） |
| **拾荒者静默回退** | 无效 base_template 的真实行为：游戏侧回退 scavGeneral，PSApi 无警告 |
| **白板化** | v1.13.0 起 ApplyDef 前清空模板私有回调/收购特性/折价——克隆体不带原版私货 |
| **intent** | 客户意图：BUY/SELL/SELLNBUY/DIALOGUE/WHOLESALE/PROCUREMENT_OFFER… |
| **faction（阵营）** | scav/lower/security/upper/tourist/rev/black_market/cartel |
| **排班 / schedule** | 出现规律：manual/daily/every_ndays/specific_day/random/once（+max_times/put_first） |
| **npc eval 日志** | 每天每 NPC 一行排班判定：`eligible/roll/can_spawn → SPAWN 或 skip(原因)` |
| **customer_generated** | 每日建队新生成客户的事件；`event.source` = vanilla/pool/schedule |
| **差集识别** | PSApi 识别"今天新来了谁"的算法：末相位快照 − 首相位快照（按指针） |
| **对话通道** | main/accept/all_done/repeat/wrong_item/right_item/interogation/glasse/on_arrest 九通道 |
| **texts 多段链** | 对话候选的 `texts = [段1, 段2, ...]`：顺序播放，choices 挂链尾 |
| **choices[].cond** | 选项谓词（v1.11.0）：falsy 不显示；**隐藏会使下标前移**（FAQ#3） |
| **sell_items** | 静态售卖清单：`"id"` / `"id:数量:概率"` / dict `{id, count, p}` |
| **sell_pool / sell_count** | 加权不放回抽 N 种上架；权重=抽选概率（与 sell_items 独立掷骰语义不同） |
| **buy_pool / buy_count** | 收购侧同构；与 buying_ids 必收合并；buy/sell 同 id 不允许重叠 |
| **pools / npc.pool_add** | 挂进原版五池（client/buy/sell/upper/bm）加权抽选 |
| **npc.pool_scale** | 按 id 或 `"前缀*"` 批量缩放池权重；幂等 |
| **npc.schedule()** | 挂起强制生成（下次建队必出）；`put_first` 排队首 |
| **can_spawn** | 生成前谓词；异常/假值=今天不出 |
| **price 三系数** | `price = {sell_single, sell_bulk, buy}`，缺省全 1.0（v1.13.0） |
| **buy_price_mod / sell_price_mod** | 旧价格键，v1.12.0 起废弃（仍解析，仅告警迁移） |
| **{buy_list} 插值** | 对话里点名 buy_pool 当日掷中种显示名（v1.11.0） |
| **auto_leave** | 说完话自动离开；v1.9.1 起仅 DIALOGUE intent 生效 |

## 注入（inject）

| 术语 | 含义 |
|---|---|
| **inject** | 把模组内容挂进**原版**流通路径的命名空间（不新造客户） |
| **buy_list** | 任意收购意图客户的收购池（清单或函数） |
| **sell_shelf** | 原版 SELL 客户货架摆货（对话末行播完时概率掷） |
| **doctor** | 夜间博士商店 |
| **barter** | 野外以物易物商人 |
| **loot_pool** | 原版掷骰池（makeshiftWeapon/material…）——拾荒客带货与远征的底层池 |
| **inject.tune** | 会话级条目覆盖（enabled/count）；重启即失，每日重算=幂等 |
| **inject.list** | 运行时注册表；**idx 受全部包注册顺序影响，永远现查现用** |
| **会话守卫** | 每客户一会话只注入一次的防双份机制（v1.8.0 起并集防双份） |

## 游戏原生概念（被引用）

| 术语 | 含义 |
|---|---|
| *StoreClient* | 游戏客户实体类 |
| *StoreClientManager / clientStack* | 每日建队器与客户队列 |
| *五相位* | 游戏官方 ModHook：OnGenerateCustomer{VeryEarly,Early,Normal,Late,VeryLate} |
| *SaveData / 存档槽* | 玩家存档；PSApi state 按槽隔离 |
| *SecData / 治安档案* | 卖违禁品写档（WEAPON_TRAFFICKING/FENCING…） |
| *展示柜 / showcase* | 商品展示区；`shop.showcase_items()` 枚举 |
| *标签打印机* | 原版改品质的机器；feature 模式品质层出现在它的下拉里 |
| *UseCountHelper* | 原生次数系统（PS-API 的 useCount 体系映射到它） |
| *吸引力 / 常客档位* | 商店吸引力值 → t1/t2/t3 阈值档位，影响常客生成率 |
| *F6 / F12* | 游戏内工具键：F6 = PSApi 管理面板；F12 =（游戏原生）日志/调试 |

## 工具与文件

| 术语 | 含义 |
|---|---|
| **pack_compiler** | 把目录包编译成 DLL 的工具（`UserData/PSApi/` 下） |
| **item_editor** | 可视化物品编辑器：涂格子→绑图→导出 JSON（07-advanced/07） |
| **Latest.log** | `MelonLoader/Latest.log`：排障第一现场 |
| **probe/** | `UserData/probe/`：实测 dump（皮肤键、loot 表等） |
| **cpp2il / ISIL dump** | IL2CPP 反编译产物（`UserData/cpp2il_isil/`）；247 模板总表的数据来源 |

---

下一篇：[03 · 速查表](03-cheatsheet.md)
