# ex20_api_shop_events · 示例 20：商店观测与商店事件

> **演示知识点**（对应文档 [04-shop.md](../../api_docs/05-api-reference/04-shop.md) ·
> [05-store-event.md](../../api_docs/05-api-reference/05-store-event.md)）：
> `shop.status()/attract()/current_client()/queue()` 四个只读观测函数 ·
> 查询类函数的对局兜底（`null`/空表，先判空再用） ·
> `store_event.register` 注册自定义"今日运势"事件（池/权重/文案/纸条） ·
> `store_event.queue` 定点必出 + `state` 防重记账 ·
> `store_event_started/ended` 桥接事件感知原版+自定义事件起止。

## 文件清单

```text
ex20_api_shop_events/
├── pack.json              ← 包清单
└── events/
    └── shop.pss          ← 1 个脚本: 顶层事件注册 + shop_opened 观测 + 起止桥接
```

## 安装

把整个 `ex20_api_shop_events` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex20_api_shop_events/
```

## 前置条件（重要）

本包所有事件都**只在开店存档（进 Emporium 开店）后触发**。主菜单阶段：
`shop` 查询拿到的全是空值，`store_event.register` 顶层注册是安全的
（事件池未就绪时框架自动挂起，下个 `day_wake` 补注入，日志可见），但
`store_event.queue` 必须等商店场景。所以脚本把"观测"放进 `shop_opened`、
"排队"也放进 `shop_opened`（此时必在商店场景）——**订阅到事件再调 API**
是对局受限 API 的标准写法。

## 验证（5 分钟）

1. 进入开店存档（Emporium），拉开卷帘门开店，控制台应出现四行观测 +
   一行排队日志（数值以你当日客流为准）：

```text
[ex20_api_shop_events] 状态: in_game=true, 距交租 3 天, 队列 4 人, 当前 ()
[ex20_api_shop_events] 吸引力: 基础 10, 随机生成率 50, 常客 0 人
[ex20_api_shop_events] 当前客户: (店内暂无客户)
[ex20_api_shop_events] 今日队列 4 人, 首位 老李(出售), 现金 250
[ex20_api_shop_events] 首次开店: 自定义事件「街区集市」已排队, 今天必出(新闻可见)
```

（数值以你当日客流为准；开店瞬间还没有当前客户，`status` 的客户名是空串
`当前 ()`，`current_client()` 此时返回 `null` 走"(店内暂无客户)"分支。）
2. 游戏内确认：当天新闻/事件 UI 出现「街区集市」，且会得到一张实体纸条
   （`slip: true` 的效果）。
3. 睡觉进入第二天：`store_event_started` 与 `store_event_ended` 桥接会陆续
   打出当天激活/结束的商店事件（**原版事件也会触发**，`event.event_id` 帮你
   区分是不是自家的）：

```text
[ex20_api_shop_events] 商店事件开始: ex20_api_shop_events:street_festival
[ex20_api_shop_events] 商店事件结束: ex20_api_shop_events:street_festival
```

## 逐文件讲解

### events/shop.pss —— 注册一行，观测四行，桥接两行

- **顶层 `store_event.register`**：加载期一次性注册。`id` 带包前缀防撞名；
  `pool="normal"` 进常规池参与每天抽选；`odd=30` 是权重（命中率取决于同池
  权重和，想必出就走 `queue`）；`duration` 天数、`slip` 纸条、`news/description`
  文案全由你定义。未知配置键只警告不报错。
- **`on shop_opened()`**：四个观测函数各打一行。写法要点：
  - `status()` 任何时候可调，先看 `in_game` 再解读其余字段；
  - `attract()/current_client()` 不在对局返回 `null`、`queue()` 返回空表——
    **查询类不报错只给空**，所以判空后再读字段；
  - `queue()` 条目的 `ptr` 字段是当日有效的客户指针，可传给
    `inject.force_client`（本包不展开，见 ex22）。
- **首次开店 `queue`**：`state.has` 防重保证整个存档只排一次（`shop_opened`
  重进店会再触发）。想改成"每天必出一次"就用
  `state.has("festival_day" + str(time.day()))` 的日期键写法。
- **`on store_event_started/ended`**：框架每天 `day_wake` 对激活事件做差集后
  发布，`event.event_id` 对原版事件是原版 id、对自定义事件是你注册的 id——
  `if event.event_id == "ex20_api_shop_events:street_festival": ...` 即可做
  自家事件的专属钩子（配合 `state` 记标记实现"事件期间热卖"类玩法）。

## 动手练习

1. 把 `odd = 30` 改成 `5`，删掉 `state.has("festival_queued")` 防重块，改成
   每天睡觉掷硬币 `if rand(0, 9) < 3:` 才 `queue`，观察抽选感。
2. 把 `slip = true` 删掉，对比事件激活后还会不会得到纸条。
3. 在 `on store_event_started():` 里加
   `if event.event_id == "ex20_api_shop_events:street_festival": player.add_cash(50, true)`，
   让集市当天给你发 50 块（带收钱音效）。

## 下一个示例

- [ex21_api_items](../ex21_api_items/README.md) —— items 命名空间全家桶与物品句柄
