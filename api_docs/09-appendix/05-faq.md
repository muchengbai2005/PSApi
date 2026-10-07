# 05 · FAQ：十个高频坑

> 按症状排的"先翻这里"清单。每条给：症状 → 原因 → 解法 → 出处。

## 1. NPC 长得不对/行为像拾荒者，日志零警告

**原因**：`base_template` 填了 247 个有效键以外的值。游戏侧
`StoreClientListDict.CreateStoreClient` 对不存在的键**既不抛错也不返回 null**，
静默回退 `scavGeneral`（拾荒者），PSApi 完全不知情。

**解法**：对照 [07 · 模板总表](../07-advanced/04-npc-pipeline.md)（247 个
有效 id，★ 为实测验证）；怀疑中招看客户外观/行为是否拾荒者。

## 2. NPC 永远不收我的模组物品 / 收购物品清单里混进了 `game:`

**原因**：npc.* 侧物品 id **不做 `game:` 前缀归一化**——
`"game:scrap_metal"` 被当作不存在的 id，跳过 + WARNING。

**解法**：原版物品写**裸 id**，模组物品写 `包id:名`。全书前缀规则一张表：
[09 · 速查表 §一](03-cheatsheet.md)。

## 3. 对话选项结算串了：选了"只要货"却触发"全套"

**原因**：`choices[].cond` 隐藏选项会使**运行时下标前移**。现金 150-299 时
玩家看到的选项 0 其实是列表里的选项 1；`event.choice` 是运行时下标。

**解法**：结算按**运行时索引 + 业务条件双重判定**（forger.pss 范式）：

```pss
if event.choice == 0 and cash >= 300:      # 全套
    ...
if (event.choice == 0 and cash >= 150) or (event.choice == 1 and cash >= 300):
    ...                                     # 只要货
```

## 4. 机器面板槽位物品"串槽"了（读档后东西在错误的槽里）

**原因**：persistent 面板的槽内物品按**元素树 BFS 索引**记入存档。
上线后面板结构改过（增删元素）→ 存档索引对不上。

**解法**：**结构恒定铁律**——运行时只改文本/白名单/锁，不增删元素。
已经发生：让玩家取回物品、重买机器（通行解法）；面板数值
收进 psui 头部"调参区"注释，挤/空改参数不改结构。

## 5. SELL 型自定义 NPC 不摆货（v1.9.x 时代的回归坑）

**原因**：历史 bug——摆货时机挪到"对话末行播完"时曾要求链尾带 endAction，
而 auto_leave 同批收窄到 DIALOGUE → SELL 型 NPC 链尾无 endAction → 永不摆货。

**解法**：Events ≥ v1.10.0 已修（链尾判定去掉 endAction 要求）。旧包遇此
症状先查版本；全局 sell_shelf 仍要求 endAction 以对齐原版摆货帧。

## 6. NPC 排班不来 / 来得太勤

**排查**：grep `npc eval: <id>`——每天每 NPC 恰好一行：

```text
npc eval: xxx day=12 eligible=false roll=0.37/0.50 can_spawn=true → skip(chance)
```

`skip(原因)` 直接给答案：`day`（未到）/`interval`/`chance`（掷骰失败）/
`max_times`/`already fired`（once 已触发）/`can_spawn=false`。
排班记账按**存档槽**存（读档回退不回滚已耗次数，属预期）。
详见 [07 · 管线](../07-advanced/04-npc-pipeline.md)。

## 7. 卖违禁品没有任何提示，但治安档案悄悄涨了

**机制**：柜台议价与展示柜自动售货都走 `SecData.CheckSoldContraband` 写档，
**没有弹窗**；档案涨→顾客变少/治安官事件。

**解法**：剧情规避用"证书模式"（`crime.exempt_guns` 扫展示柜，见
[05 · 03 world](../05-api-reference/03-world.md)）；
或压根不给物品挂 contraband。

## 8. 克隆奸商模板后 NPC 收购价是 0 / 摆货行为诡异

**原因**：模板克隆会连带**私有回调与收购特性**（如 shadyMerchant 的
"违禁品按等级折扣价"特性把 critical 压到 0 元购）。

**解法**：v1.13.0 起 ApplyDef 前自动**白板化**（清回调/收购特性清单/
sellPriceModifier）。低于 v1.13.0 的环境别用带私货的模板，或显式配
`price = {buy = 1.05}` 等三系数。

## 9. state 里的数据读档后变了类型 / 丢了

**规则**：state 值只支持 string/int/float/bool/dict/list；**句柄不能进 state**
（client/machine/item 运行时对象序列化即失效）。机器要存就存 `uid`，
用 `ui.machine_find_uid(uid)` 找回。

**另外**：state 按**存档槽**隔离（`UserData/PSApi/state/<槽>/<包>.json`），
换槽=另一份；落盘时机有崩溃窗口（详见 [07 · 存档状态](../07-advanced/02-state-deep.md)）。

## 10. 游戏更新后包坏了（物品失效/价格错乱）

**流程**（对应 2026-09-18 实际事件）：

1. 启动一遍，grep 日志 `WARNING`——Events ≥ v1.11.0 对注入/摆货的无效 id
   会逐条点名（旧版静默，需 F6 逐个试）；
2. `buy_price_mod`/`sell_price_mod` 若还在用：**语义已翻转**（玩家买单加价%），
   改 `price` 三系数；
3. base_template/皮肤键对照新 dump（模板表见 07 章；皮肤键看 `UserData/probe/`）；
4. 把修复日期记进 pack.json `//`——通行做法，下次更新时省一半排查。

---

## 快速分流表

| 症状 | 先看 |
|---|---|
| 任何"没生效" | 日志 `MelonLoader/Latest.log` + F6 注入区/浏览器 |
| NPC 相关 | FAQ 1/2/6/8 |
| 对话/选项 | FAQ 3 |
| 机器/面板 | FAQ 4/9 |
| 经济/价格 | FAQ 7/8/10 |
| 完整症状→排查对照 | [07 · 调试排障](../07-advanced/06-debugging.md) |

---

下一篇：[06 · 待确认清单](06-unresolved.md)
