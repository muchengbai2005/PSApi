# ex33_state_deep · 示例 33：存档状态深入

> **演示知识点**（对应文档 [07-advanced/02-state-deep.md](../../api_docs/07-advanced/02-state-deep.md)）：
> `state` 三层结构（脚本编码 → 内存 owner → 磁盘 JSON）· 槽位隔离生命周期 ·
> 三种典型记账：开业天数（`day_wake` 累计）、累计收入（`trade_completed` 逐笔累加）、
> 一次性剧情标志（`state.has` 防重）· 落盘时机与崩溃窗口 ·
> **存档一致性验证**：写入 → 存档退菜单 → 重进 → 值还在 · state 文件的手改 / 删库急救。

## 文件清单

```text
ex33_state_deep/
├── pack.json
└── events/
    └── ledger.pss      ← 4 个事件 handler 组成的记账本
```

## 安装

把整个 `ex33_state_deep` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex33_state_deep/
```

## 验证（进存档后的操作步骤）

1. 进存档，第一天醒来：

```text
[ex33_state_deep] 首次开业! 「开业纪念」标志已写入 (本存档仅此一次)
[ex33_state_deep] 开门营业, 累计开业 1 天 (time.rel_day()=1)
```

2. 卖任意一件东西给进店客户：

```text
[ex33_state_deep] 卖出 scrap_metal 给可疑的商人, 收入 +12, 累计营业收入 12
```

3. 睡到第二天，确认天数累加、**纪念标志不再触发**（`has` 防重生效）：

```text
[ex33_state_deep] 开门营业, 累计开业 2 天 (time.rel_day()=2)
```

4. **存档一致性验证**（本示例的核心实验）：
   - 游戏内存档 → **退回主菜单** → **重新进入该存档**。加载完成瞬间出现：

```text
[ex33_state_deep] 读档完成, 快照: 开业 2 天 / 累计收入 12 / 纪念标志 true
```

   - 值全部还在 —— 退菜单时 `EnsureSlot` 触发 `FlushAll`（旧槽数据全部落盘），重进时重载同槽文件。
   - 再换一个**不同存档槽**进去：天数从 0 重新开始 —— 槽隔离是全自动的，每个存档各自记账。
5. 打开 `UserData/PSApi/state/<你的槽位>/ex33_state_deep.json`（关游戏后看）：

```json
{
  "first_day_badge": "true",
  "open_days": "2",
  "revenue_total": "12"
}
```

   ——这就是磁盘层：普通缩进 JSON，人可直接读改。

## 逐文件讲解

### events/ledger.pss —— 三个记账模式

- **跨天累计**（`open_days`）：`state.get("k", 0) + 1` 再 `set` 回去。数字、bool、list、dict
  存取往返无损（`ScriptJson` 编解码），文件里看到的是字符串、读回来还是数字。
- **逐笔累加**（`revenue_total`）：`trade_completed` 只记 `direction=="sell"`（玩家卖出），
  `amount` 为本笔成交金额。原版客户也发这个事件，所以任何一单都会进账。
- **一次性标志**（`first_day_badge`）：`not state.has(...) → set`。
  注意 `has` 只看键在不在、不看值真假 —— `set("flag", false)` 之后 `has` 仍是 `true`，
  要"存在且真"得写 `state.has("flag") and state.get("flag")`。

### 为什么没有 `game.save()` —— 落盘时机表

`state.set` 只改内存，框架在四个时机 `Flush`（单 owner）/ `FlushAll`（全部）：

| 时机 | 说明 |
|---|---|
| 每日 NPC 调度评估后 | 对局内**最可靠的定期落盘** —— 每天开店建队必落一次 |
| 存档槽切换时 | 退菜单/换存档 → 旧槽 FlushAll 保住再换 |
| 退出游戏时 | `OnDeinitializeMelon`，正常退出不丢 |
| 写盘失败 | 只告警不崩 |

**没有手动 flush API** —— 这不是遗漏而是设计：崩溃窗口最长 = 一天。
实战纪律由此推出：**贵重记账写进 `day_wake` / `day_sleep`**（此时机距落盘点最近），
别把关键数据只存在 `tick` 这类高频事件里裸奔一整天。

### 边界：state 与游戏本档的关系

| 行为 | 结果 |
|---|---|
| 游戏内读档回退（当天） | state **不回滚** —— 窗口期写入保留 |
| 删除游戏存档 | 对应槽的 state 目录**不会自动删**，重建同名槽会读到旧数据 |
| 云同步/拷存档 | `UserData/PSApi/state/` 不在游戏存档里，要自己带走 |

## 动手练习

1. **修数据**：关游戏，把 `ex33_state_deep.json` 里的 `"open_days": "2"` 改成 `"99"`，
   重启进同一存档 —— 开业天数从 99 续记（文件是追加友好的普通 JSON）。
2. **删库急救**：删掉整个 `ex33_state_deep.json` 再进档 —— 本包全部 state 归零，
   「开业纪念」会重新触发（这就是"删 owner 文件 = 该包 state 归零"）。
3. **体验崩溃窗口**：进档卖一笔货后**直接杀进程**（不退菜单不存档），再进档看那笔收入是否丢失
   —— 丢了就是窗口期写入；再用"退菜单"路径重做一遍对比，体会 FlushAll 的差别。
4. 给 `ledger.pss` 加一个 `state.set("ledger", {day = time.rel_day(), cash = player.cash()})` 的
   dict 记账（02 篇"资产负债表"模式），读档后打出来看 dict 无损往返。

## 下一个示例

- [ex34_mini_mod](../ex34_mini_mod/README.md) —— 综合迷你模组「迷你温室」：物品+机器+配方+NPC+psui 全系统串联
