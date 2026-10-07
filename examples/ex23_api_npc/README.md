# ex23_api_npc · 示例 23：自定义卖货 NPC

> **演示知识点**（对应文档 [08-npc.md](../../api_docs/05-api-reference/08-npc.md) ·
> [10-handles.md](../../api_docs/05-api-reference/10-handles.md)）：
> `npc.register` 的 config 最小子集（身份/交易/出售/对话/排班五组键） ·
> `base_template` 原版模板（抄参考包 cat_shadow.pss 的 `shadyMerchant`） ·
> `sell_items` 静态清单三种写法（`"id"` / `"id:数量"` / `"id:数量:概率"`） ·
> `price` 三向价格系数 · `dialogues` 四基础通道 ·
> `schedule: every_ndays` 排班 + `npc.pool_add("bm", ...)` 池加权并行 ·
> `on customer_generated` 里读客户句柄。

## 文件清单

```text
ex23_api_npc/
├── pack.json              ← 包清单
├── items/
│   ├── street_snack.json  ← 街头小吃: 老周必卖
│   └── lucky_charm.json   ← 平安符: 老周必卖 x2
└── events/
    └── peddler.pss        ← 1 个脚本: 顶层注册+挂池 + customer_generated 迎客日志
```

## 安装

把整个 `ex23_api_npc` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex23_api_npc/
```

## 到店验证（5 分钟）

1. 启动游戏，控制台应出现（顶层注册完成；主菜单阶段注册是安全的）：

```text
[ex23_api_npc] NPC「货郎老周」已注册: 每隔 2 天必来 + 黑市池 3% 加权
```

2. 进**开店存档**（进 Emporium 开店）等客户。老周的排班是
   `every_ndays interval=2, chance=1.0`——**最多隔 2 天必到一次**；同时他在
   黑市池里占 3% 权重（与排班并行，谁先中谁来）。到店时控制台出现：

```text
[ex23_api_npc] 货郎老周到店: 现金 600, 意图 SELL, 来源 schedule
```

（`event.source` 显示本次到店的渠道——排班触发/池抽中/剧情强制各有取值，以
实际日志为准。）

3. 游戏内确认：队列里出现「货郎老周」，开场白是"老周来啦, 看看今天的货?"，
   货架上摆着街头小吃 + 平安符 x2，运气好还有普通矿石（50%）；他的卖价是
   原价 ×1.2（`sell_single` 单买系数），成交台词"好眼光, 拿好。"
   （`event.source` 显示这次到店是排班还是池抽中）。
4. 隔天睡觉再开店：老周"每隔 2 天"来一次，可以对照日志观察节奏。

## 逐文件讲解

### items/*.json —— 老周的货

两个教学物品（`test: true`，F12 也能领），让 `sell_items` 里三种静态写法
各出现一种：裸 `"id"`（1 件必上）、`"id:2"`（2 件必上）、
`"id:1:0.5"`（原版普通矿石 50% 带 1 个）。

### events/peddler.pss —— 注册三段式

- **`npc.register({...})`**：加载期顶层调用一次。本包用了 27 个合法键里的
  最小子集，按功能分组：
  - 身份：`id`（脚本侧引用它，建议带包前缀）、`name`（显示名）、
    `base_template`（**必须取自 247 个有效原版模板键**——填错不报错，游戏
    静默回退成拾荒者外观！所以抄参考包 `cat_shadow.pss` 验证过的
    `shadyMerchant`）、`faction`。
  - 交易：`intent = "SELL"`（他来卖货）、`cash`（收你货的钱）、
    `price = {sell_single, sell_bulk, buy}` 三系数（单买/批发/收购，缺省 1.0）、
    `sell_items` 静态清单。
  - 对话：`main/accept/all_done/repeat` 四个基础通道（共九通道，字符串或
    字符串数组）。
  - 排班：`{mode = "every_ndays", interval = 2, chance = 1.0}` = 每 2 天必来。
- **`npc.pool_add("bm", id, 3.0)`**：五池（client/buy/sell/upper/bm）挂黑市池
  3% 权重，与排班并行生效——他想"既按固定节奏来、平时也可能随机刷到"。
- **`on customer_generated()`**：每位客户生成（进门）都触发；`event.id`
  匹配自家 NPC 后读 `event.client` 客户句柄（`name/faction/cash/intent`
  只读成员；`set_cash/say` 两个方法可在别的玩法里改现金/替换台词）。
  不做 id 过滤的话，**原版客户也会进这个事件**——`if event.id == ...`
  是过滤自家人的标准写法。

## 动手练习

1. 把 `schedule` 的 `chance = 1.0` 改成 `0.5`，连续开五天档，观察老周的
   出现节奏从"必来"变成"掷硬币"。
2. 加一个 `can_spawn` 条件函数：`func zhou_mood(): return rep.get("bm") >= 0`
   （照抄 cat_shadow 的"黑市声望非负才来"），然后在 config 里加
   `can_spawn = zhou_mood,`——声望为负的那几天他就不来了。
3. 在 `customer_generated` 里给老周换台词：
   `event.client.say("main", "第 {time.rel_day()} 天了, 老规矩。")`，注意
   `say` 是整段替换、每客户每天只该调一次。

## 下一个示例

- [ex24_api_ui_handles](../ex24_api_ui_handles/README.md) —— PSUI 面板 + 按键绑定 + 物品句柄回调
