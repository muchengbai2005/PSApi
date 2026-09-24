# 03 · 速查表

> 写包时摊在手边的浓缩表。每张表都是"够用的一页"，细节链回对应章节。

## 一、物品 id 前缀规则速记（全书最重要的一张表）

"原版物品什么时候写 `game:` 前缀、什么时候写裸 id"——各系统**不一致**，
实证汇总（[03 · 物品](../03-items/02-items.md)、[07 · NPC 管线](../07-advanced/04-npc-pipeline.md)）：

| 系统 | 写法 | `game:` 前缀 | 建议 |
|---|---|---|---|
| 物品/配方 JSON（output/inputs） | `"game:newspaper"` | ✔ 归一化剥掉 | 两写法都行 |
| custom 机器白名单 | `"game:scrap_metal"` | ✔ 注册时剥掉 | 两写法都行 |
| **npc.* 配置**（buying_ids/sell_items/buy_pool/sell_pool/black_*） | 裸 id `"scrap_metal"` | ✘ **不归一化**，带前缀=查无此物+WARNING | **一律裸 id** |
| **inject.***（sell_shelf/doctor/…） | 裸 id 优先 | 会剥（NormalizeId） | 一律裸 id 最稳 |
| items.* / shop.* 脚本函数 | 裸 id `"scrap_metal"` | ✔ 统一归一化剥掉（[05 章 §①](../05-api-reference/README.md)） | 裸 id 最稳 |
| 模组物品（任何地方） | `包id:名字` | — | 全书统一 `gunworks:gw_pistol` 风格 |

**一句话**：模组物品永远带 `包id:` 前缀；原版物品**建议一律裸 id**——
npc 侧写了 `game:` 必错，别处写了只是多余。

## 二、物品 JSON 字段速查

```json
{ "items": [ {
  "id": "包id:名",            // 必填, 命名空间前缀
  "name": "显示名",            // 必填
  "desc": "描述",              // tooltip
  "flavor": "风味文本",        // 可选
  "value": 100,               // 基准价
  "template": "原版id",         // 可选: 克隆基底(行为继承)
  "directory": "XxxDirectory", // 可选: 无 template 时按 tags 路由
  "tags": ["WEAPON"],          // 收购标签匹配
  "shape": { "w": 2, "h": 2, "cells": [[0,0],[1,0],[0,1],[1,1]] },
  "icon": { "file": "xx.png" }, // icons/ 下同名文件
  "contraband": "low",          // low/mid/high/critical
  "useCount": 5, "useBaseValue": 10, "useValuePerUse": 18,  // 次数体系
  "qualities": ["包id:q_x"], "defaultQuality": "包id:q_x",  // 品质层
  "test": true                  // F6 "测试"分类
} ] }
```

叠加规则：显式 shape/icon **覆盖** template；tags 与 template **合并**；
contraband 显式走 InitContrabandItem。

## 三、npc.register 28 键速查（按功能分组；另有 buy_price_mod/sell_price_mod 两废弃键）

```text
身份    id* name* base_template*(247总表) faction sprite/sprites
交易    intent cash budget=[额,浮动]
        price={sell_single, sell_bulk, buy}(缺省全1.0, v1.13.0)
        multi_buy_disabled no_contraband
收购    buying_ids buying_tags buying_features black_ids black_tags
        clear_buying(默认true) buy_pool+buy_count(v1.11.0)
出售    sell_items(id:数量:概率|{id,count,p}) sell_pool+sell_count(v1.10.0)
对话    dialogues{main/accept/all_done/repeat/wrong_item/right_item/
        interogation/glasse/on_arrest} auto_leave(默认true, 仅DIALOGUE生效)
出现    schedule{manual/daily/every_ndays/specific_day/random/once
        +max_times+put_first} can_spawn register pools[{name,weight_pct}]
```

*斜体=必填；buy/sell 同 id 不允许重叠；已废弃：`buy_price_mod`/`sell_price_mod`。*

## 四、15 个桥接事件总表

| on 写法 | 时机 | event 关键字段 |
|---|---|---|
| `scene_loaded()` | 进/切场景 | `value`(场景名) |
| `game_loaded()` | 读档/新开局 | `save_slot` `run_id` |
| `day_wake()` | 每天早晨 | `day` `rel_day` `weekday`(0=周一) |
| `day_sleep()` | 每晚睡觉 | 同上 |
| `shop_opened()` / `shop_closed()` | 开店/打烊 | 同上 |
| `store_leaving()` / `store_returning()` | 出门/回店 | `day` |
| `night_services()` | 夜间结算 | `day` `rel_day` `weekday` |
| `store_event_started()` / `store_event_ended()` | 商店事件起/止 | `event_id` |
| `customer_generated()` | 客户生成 | `client`(句柄) `id` `name` `source` |
| `dialogue_choice()` | 对话选项 | `dialogue_id` `choice` `npc_id` `npc_name` `title` |
| `trade_completed()` | 成交 | `direction`(buy/sell) `item_id` `amount` `npc_id` `npc_name` |
| `tick()` | 每秒(对局内) | `day` `rel_day` `weekday` |

自定义：`bus.emit("包id:事件", {..})` + `on "包id:事件":`（字符串形式，
载荷必须 dict）。未知标识符事件名不报错、按 `psapi.<名>` 订阅打警告
（未来桥接后自动生效）。细节：[04 · 事件](../04-psscript/09-events.md)。

## 五、inject 通道表

| 通道 | 函数 | 挂进哪里 | 触发时机 |
|---|---|---|---|
| 买池 | `inject.buy_list(id 或 函数)` | 收购客户(BUY/SELLNBUY/WHOLESALE/PROCUREMENT_OFFER)的收购清单 | 客户生成时 |
| 货架 | `inject.sell_shelf(id, count, chance[, {uses}])` | SELL 客户货架原生摆货 | 对话末行播完时概率掷 |
| 博士 | `inject.doctor(id, count)` | 夜间博士商店 | VisitUpgradeMerchant 后 |
| 以物易物 | `inject.barter(id, count)` | 野外商人 | ShowBarter 时 |
| 掷骰池 | `inject.loot_pool(pool, id, chance)` | 原版随机池(makeshiftWeapon/material…) | 拾荒带货/远征 |

管理：`inject.list()`（idx 现查现用）/ `inject.tune(idx, {enabled, count})`
（仅本会话）/ `inject.reset_tracking()` / `inject.restock_current()` /
`inject.force_client(ptr)`。机制：[07 · 注入](../07-advanced/03-inject-deep.md)。

## 六、ui.machine_* 八函数（机器面板）

| 函数 | 用途 |
|---|---|
| `ui.machine_set_text(m, "elem", "文本")` | 改 label/按钮文本 |
| `ui.machine_slot_item(m, "slot")` | 读槽内物品句柄（空=null） |
| `ui.machine_slot_items(m, "grid_slot")` | 网格槽多物品遍历 |
| `ui.machine_set_whitelist(m, "slot", [ids])` | 运行时收窄白名单 |
| `ui.machine_spawn(m, "slot", id, count)` | 脚本直入物品（**不过白名单**） |
| `ui.machine_lock(m, "slot", bool)` | 双锁（插入+取出；对脚本也生效） |
| `ui.machine_clear(m, "slot")` | 清空槽 |
| `ui.machine_find_uid(uid)` / `ui.machine_is_open(m)` | 按 uid 找回机器 / 查窗口开否 |

回调参数：按钮 `{elem, machine}`，槽位 `{elem, value, machine}`，
on_open `{machine}`。

## 七、数值红线（框架硬限制）

| 红线 | 值 |
|---|---|
| `sell_shelf` count 区间上限 | 5 |
| sell_pool/buy_pool | 加权**不放回**；buy/sell 同 id 禁止重叠 |
| 非法对话通道键 | 警告跳过（九通道之外全忽略） |
| 未知 npc.register 键 | 警告跳过 |
| handler 熔断 | 步数/递归按每次触发独立计（[04 · 错误](../04-psscript/10-errors.md)） |
| 面板槽位存档 | 按 BFS 索引——persistent 面板结构变更 = 旧档串槽 |
| state 值类型 | string/int/float/bool/dict/list；**句柄不能进 state** |
| bus.emit 载荷 | 必须 dict（标量被拒） |
| 事件名字符串订阅 | 不支持插值 |

## 八、常用函数一行签

```text
time.day() / time.rel_day() / time.weekday()        # 总天数 / 开业第几天 / 0=周一
player.cash() / player.add_cash(n)                  # 现金 (n 可负)
rep.add(阵营, 分) / rep.get(阵营)                    # ll/ul/sec/bm/rev/cartel...
crime.commit(id, 金额, 描述) / crime.exempt_guns(bool)
items.give(id[, count[, uses]]) / items.give_counter(id[, count[, uses]])
items.find(id) / items.find_all(id) / items.name(id) / items.consume(句柄)
items.power(句柄) / items.power_draw(句柄, 量)        # 电量 / 扣电(全有或全无)
items.uses(句柄) / items.uses_max(句柄) / items.use(句柄, n)
shop.status() / shop.attract() / shop.current_client() / shop.queue()
shop.showcase_items() / shop.block_sale(id) / shop.unblock_sale(id)
store_event.register(id, config) / store_event.queue(id[, delay_days])
log.info/warn/error(...)                             # 日志三档
```

完整签名与返回值结构：[05 API 参考](../05-api-reference/README.md)。

---

下一篇：[04 · 版本纪要](04-versions.md)
