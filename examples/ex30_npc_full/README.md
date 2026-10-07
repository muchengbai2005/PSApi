# ex30_npc_full · 示例 30：多 NPC 网络

> **演示知识点**（对应文档 [07-advanced/04-npc-pipeline.md](../../api_docs/07-advanced/04-npc-pipeline.md)
> 与 [07-advanced/05-npc-config.md](../../api_docs/07-advanced/05-npc-config.md)）：
> 三种意图各一个 NPC 组成小网络 —— 卖货商（`intent:"sell"` + `sell_items` 静态货架 + `price` 三系数 +
> `every_ndays` 排班）、收购商（`intent:"buy"` + `buying_ids` 必收 + `buy_pool`/`buy_count` 当日随机加收 +
> `{{buy_list}}` 台词插值）、剧情 NPC（`intent:"dialogue"` + `texts` 多段对话链 + `choices` 选项 + `cond` 显示条件）。
> 三人各挂一个原版加权池（`npc.pool_add`：sell / buy / client），
> `customer_generated` 按 id 打日志、`dialogue_choice` 记录玩家选择。

## 文件清单

```text
ex30_npc_full/
├── pack.json                ← 包清单
├── items/
│   └── ex30_goods.json      ← 2 个物品: 前哨头灯 / 废料核心 (NPC 买卖清单都指向它们)
└── events/
    ├── npc_sell.pss         ← 卖货商老金 (sell_items + price + every_ndays + sell 池)
    ├── npc_buy.pss          ← 收购商齐姐 (buying_ids + buy_pool/buy_count + buy 池)
    ├── npc_story.pss        ← 剧情NPC陆婆 (dialogues.texts + choices + cond + client 池)
    └── monitor.pss          ← customer_generated 分 id 打日志
```

## 安装

把整个 `ex30_npc_full` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex30_npc_full/
```

## 验证（约 10 分钟）

1. 启动游戏，日志应出现三行注册（少一行 = 对应脚本在 register 前抛异常）：

```text
npc 'ex30_peddler' registered (pack=ex30_npc_full, ...)
npc 'ex30_recycler' registered (pack=ex30_npc_full, ...)
npc 'ex30_storyteller' registered (pack=ex30_npc_full, ...)
```

2. 进存档过一天。搜 `npc eval:`，三个 NPC 各有一行排班判定，例如：

```text
npc eval: ex30_storyteller day=1 eligible=true roll=-/1.00 can_spawn=true → SPAWN
npc eval: ex30_peddler day=1 eligible=true roll=0.86/0.80 can_spawn=true → skip(chance)
```

   `→ SPAWN` 的当天，开门后日志会出现（来源可能是 schedule 或 pool）：

```text
[ex30_npc_full] 陆婆到店 (source=schedule) — choices 已随对话构建
[ex30_npc_full] 老金到店 (source=pool) — sell_items 将在交易阶段上架
```

3. **老金**：开门和他对话，两段台词自动连播；柜台购买他的货按 95 折、整桌批发 8 折。
4. **齐姐**：F12 领一个 `废料核心` 放上柜台卖给她 —— 第二段台词的 `{buy_list}`
   已被替换成她当日随机加收的物品名；卖东西给她按 105% 加价结算。
5. **陆婆**：对话完出现三个选项；点「听她讲完」后日志出现：

```text
[ex30_npc_full] 陆婆对话选项被选: choice=0 (灯的来历: false), npc=ex30_storyteller
```

   开业满 3 天后她再来，选项会变成四个可见项（第三个有 `cond`），点最底下那个再观察日志。

## 逐文件讲解

### items/ex30_goods.json —— 网络的货

两位商人的清单只引用两个本包物品 + 一个原版裸 id（`common_ore`）。
注意对照：**同一件 `ex30_npc_full:scrap_core`，在老金的出售清单里也在齐姐的必收清单里** ——
跨 NPC 流通没有任何限制；被禁止的是**同一个 NPC** 的收购侧与出售侧重叠
（`ParseConfig` 四种组合任一重叠直接报错，注册失败）。

### events/npc_sell.pss —— 卖货商

- `intent` 站在**客户视角**：`"sell"` 是他来卖货给你。方向写反是新手第一大坑。
- `sell_items` 条目 `"id:数量:概率"`：概率是**每条独立判定**；数量支持 `"1-2"` 区间。
- `price` 三键全部站在**玩家视角**：`sell_single` 你买入的折扣、`sell_bulk` 整桌批发折扣、
  `buy` 他收你货的加价。纯卖货商用不到 `buy`，配着是为了三键齐观。
- 排班 `every_ndays(interval=2, chance=0.8)` 之外还 `npc.pool_add("sell", ..., 8.0)`：
  排班是"保底到场"，挂池是"随机抽中"，两条路共用同一份配置 —— 见 04 篇 §八。

### events/npc_buy.pss —— 收购商

- `buying_ids` 必收清单要在台词里**写死**（避免复读）；`buy_pool` + `buy_count`
  是"每次来访掷 N 种"的当日随机加收，**客户生成时掷定，当日多次对话不重掷**。
- 台词里写 `{{buy_list}}`：pss 字符串插值吃掉一层花括号后留下字面 `{buy_list}`，
  框架构建对话时替换成掷中种的名字。注意 gunworks 的活例都是**双层花括号**。
- `budget = [800, 200]` 预算制：花完即走；`price.buy = 1.05` 表示收你的货加价 5%。

### events/npc_story.pss —— 剧情 NPC

- `dialogues.main` 是 dict 而不是字符串时，可以带 `texts`（多段链）、`id`、`choices`。
- `main.id = "omen_talk"` → `dialogue_id = "ex30_npc_full:omen_talk"`，`dialogue_choice`
  事件靠这个 id 路由剧情；不配 `id` 时为 `包id:npcId:main`。
- `choices` 每项可带 `cond`（构建时求值的显示条件）—— 被隐藏的选项会让**后续选项下标前移**，
  文件尾 `dialogue_choice` 里 `time.rel_day() >= 3 and event.choice == 2` 的联动判定演示怎么防这个坑。
- 选项选中后框架只负责"对话完即离店"，剧情效果全靠你订阅 `dialogue_choice` 自己写。

### events/monitor.pss —— 到店监视器

`customer_generated` 对**原版客户也发**（`source="vanilla"`）。本包只对自己三个 id 打日志；
排障时先搜 `npc eval: <id>`，`eligible/roll/can_spawn/→ SPAWN` 四个字段一眼定位 NPC 不来的原因。

## 动手练习

1. 把老金的 `sell_bulk` 从 `0.8` 改成 `1.1`（≥ `sell_single`）—— 批发折扣会**直接关闭**，验证文档说的"大于等于不开"。
2. 给齐姐的 `buy_pool` 再加一条 `"scrap_metal:2"`，把 `buy_count` 改成 `"2-3"`，看她下一次来访的 `{buy_list}` 变化。
3. 给陆婆加第 4 个**不带 `key`** 的选项 —— 观察日志里选项身份键的轮换规律（默认池只有 3 件物品，第 4 个会循环复用）。
4. 把陆婆的排班改成 `{mode="once", put_first=true}`，进新存档验证：开业当天她必排开门第一位，且之后永不再来（`npc eval` 显示 `skip(already fired)`）。

## 下一个示例

- [ex31_scene_basic](../ex31_scene_basic/README.md) —— 自定义外出场景：scene.json + 搜索点/撤离点 + 场景事件
