# 05 · 03 世界查询与数值：time / player / rep / crime / power

> 五个命名空间管的是**世界状态**：今天几号、玩家多少钱、各势力怎么看玩家、
> 治安档案记了什么、五大势力的力量值。它们是事件脚本里最常用的一批只读+微调 API。

## time：游戏时间

三个函数同源（与事件上下文里的 `event.day / event.rel_day / event.weekday` 一致），
都**需要存档场景**——主菜单/加载期调用报
`time.* 当前不可用(不在存档场景中)`。

| 函数 | 返回 | 含义 |
|---|---|---|
| `time.day()` | `int` | 绝对游戏日（游戏内部计数） |
| `time.rel_day()` | `int` | 相对日——本存档**开业日 = 第 1 天** |
| `time.weekday()` | `int` | 星期几，**0=周一 … 4=周五 6=周日**（`rel_day - 1` 对 7 取余） |

```pss
on day_wake():
    log.info("第 {time.rel_day()} 天(周{time.weekday() + 1}), 绝对日 {time.day()})")
```

选型建议：**日常逻辑用 `rel_day()`**（"开业第 N 天"符合玩家直觉，第一天就是 1）；
`day()` 适合全局节奏（如 `time.day() % 3 == 0` 每三天一次，跨存档也稳定）。

## player：现金

需要存档场景，否则报 `player.* 当前不可用(不在存档场景中)`。

| 函数 | 签名 | 返回 |
|---|---|---|
| 查 | `player.cash()` | `int` 当前现金 |
| 加 | `player.add_cash(amount[, sound = false])` | `null` |

- `amount` 为负即扣钱（`(int)` 截断后走游戏 `ModCash`）。
- `sound = true` 时播放游戏内收钱音效（发奖励时建议开）。

```pss
player.add_cash(100, true)        # +100，带音效
player.add_cash(-rent_cost)       # 扣房租
```

## rep：派系声望

| 函数 | 签名 | 返回 |
|---|---|---|
| 查 | `rep.get(faction)` | `int` 当前声望 |
| 加 | `rep.add(faction, amount)` | `null`（负数即减） |

`faction` 接受**六势力简写**或**完整 factionId**（表外名字会先经游戏校验，
无效则报 `未知派系 'xxx'(可用: sec/rev/bm/cartel/ul/ll 或有效 factionId)`）：

| 简写 | 完整 factionId | 势力 |
|---|---|---|
| `sec` | `FACTION_SECURITY` | 治安署 |
| `rev` | `FACTION_REVOLUTION` | 革命军 |
| `bm` | `FACTION_BLACK_MARKET` | 黑市 |
| `cartel` | `FACTION_CARTEL` | 卡特尔 |
| `ul` | `FACTION_UPPER_LEVEL` | 上层区 |
| `ll` | `FACTION_LOWER_LEVEL` | 下层区 |

```pss
on night_services():
    rep.add("ll", 20)
    log.info("下层区声望 +20 → {rep.get('ll')}")
```

注意：`rep.get` 在场景刚加载、声望表还没为该势力建档时会报
`派系 'xxx' 当前无声望记录(场景未就绪?)`——建议在 `day_wake` 之后的时机调用。

## crime：治安档案

| 函数 | 签名 | 返回 | 自 |
|---|---|---|---|
| 记罪 | `crime.commit(crime_id, amount[, 显示名])` | `null` | — |
| 卖枪豁免 | `crime.exempt_guns(bool)` | `bool`（设置后的当前状态） | v1.7.0 |
| 档案快照 | `crime.list()` | `dict`（见下） | v2.0.2 |
| 清指定罪 | `crime.clear(crime_id)` | `int` 清除条数 | v2.0.2 |
| 清全部 | `crime.clear_all()` | `null` | v2.0.2 |
| 证据等级 | `crime.evidence([set 0..100])` | `int` 当前证据等级 | v2.0.2 |

**`crime.commit(crime_id, amount[, 显示名])`**

往玩家的治安档案写一条罪记录（`secData.CommitCrime`）。`crime_id` 是游戏内的
罪类型 id；`amount` 是严重度数值。第三个参数可选——给这条罪配一个**自定义显示名**
（不传则用游戏默认文案；同名 id 的显示名后注册覆盖先注册）。
需要存档场景且 `secData` 就绪。

```pss
# 剧情: 玩家在后台拆枪(crime_id 用游戏内罪类型 id, 此处为示意)
crime.commit("weapon_trafficking", 15, "涉嫌拆解枪械")
```

**`crime.exempt_guns(bool)`**

"今日卖枪不记档案"开关（仿制证书玩法的机制出口）。设为 `true` 后：
当天卖出 `WEAPON` 类型物品不写治安档案，`WEAPON_TRAFFICKING` / `FENCING`
两类记录同免；**只管设置之后的新记录**，已记的不追溯。
惯例用法——`shop_opened` 时扫描展示柜决定开关，`day_wake` 时复位：

```pss
on shop_opened():
    var has_cert = false
    for it in shop.showcase_items():
        if it.id == "my_pack:fake_cert":
            has_cert = true
    crime.exempt_guns(has_cert)

on day_wake():
    crime.exempt_guns(false)   # 新的一天重新判定
```

**`crime.list()`**（v2.0.2 新增）

治安档案**全量快照**。原版 `secData` 里罪名、数量、累计值是三张**平行表**
（`crimeTypes / crimeAmount / crimeAmountTotal` 同索引成组），`list` 把它们
逐罪分组导出，其余字段直出原版显示/计算方法：

| 返回键 | 含义 |
|---|---|
| `crimes` | `[{id, amount, total} ...]`——逐罪的罪类型 id、当前数量、累计总量 |
| `evidence` / `evidence_display` | 证据等级数值 / 原版显示文案 |
| `total_value` | 犯罪总值 |
| `biggest` | 最大罪名 |
| `sentence` / `fine` | 预计刑期 / 罚金 |

需要存档场景且 `secData` 就绪（同 `crime.commit`）。psconsole 的
`crime list` 指令即基于它。

**`crime.clear(crime_id)`**（v2.0.2 新增）

按罪名清除该罪**全部记录**：三张平行表按索引同删，罪 id **大小写不敏感**；
返回清除的条数（`int`，没匹配到 = 0）。

**`crime.clear_all()`**（v2.0.2 新增）

一笔勾销——直调原版 `secData.ResetCrime` 清空整个治安档案，返回 `null`。

**`crime.evidence([set])`**（v2.0.2 新增）

调查/证据进度：**无参 = 读**当前证据等级；**带参 = 设**（0..100，
超出两端钳制到边界），返回当前等级（`int`）。

```pss
var rec = crime.list()
log.info("证据: {rec.evidence_display}, 预计刑期 {rec.sentence}, 罚金 {rec.fine}")
for c in rec.crimes:
    log.info("罪 {c.id}: 当前 {c.amount} / 累计 {c.total}")

crime.clear("fencing")     # 洗掉全部销赃记录 → 返回清除条数
crime.clear_all()          # 整份档案一笔勾销
crime.evidence(0)          # 证据清零
```

## power：五势力数值

与 `rep`（声望，玩家关系）不同，`power` 是**世界格局数值**——革命军/治安署/黑市
的力量强弱、下层动荡度、上层好感度。读它需要**商店场景**
（`StoreOperationManager` 挂在商店站点上），否则报
`power.* 当前不可用(不在商店场景中)`。

| 函数 | 签名 | 返回 |
|---|---|---|
| 查 | `power.get(which)` | `int` |
| 加 | `power.add(which, delta)` | `null`（负数即减） |

`which` 只认五个键：

| 键 | 含义 |
|---|---|
| `rev` | 革命军力量 |
| `sec` | 治安署力量 |
| `bm` | 黑市力量 |
| `lower_unrest` | 下层区动荡度 |
| `upper_friend` | 上层区好感度 |

```pss
on game_loaded():
    log.info("势力快照: rev={power.get('rev')} sec={power.get('sec')} bm={power.get('bm')} "
           + "下层动荡={power.get('lower_unrest')} 上层友好={power.get('upper_friend')}")
```

（可运行的对照示例见
[ex19_api_state_world](../../examples/ex19_api_state_world/README.md)。）

写错键名报 `未知势力 'xxx'(可用: rev/sec/bm/lower_unrest/upper_friend)`。

## 本篇函数速查

```pss
time.day()                        # 绝对日
time.rel_day()                    # 本存档第几天(0起)
time.weekday()                    # 0=周一

player.cash()                     # 现金
player.add_cash(n[, sound])       # 加/扣钱

rep.get("ll")                     # 声望
rep.add("ll", 20)                 # 声望加减

crime.commit(id, amount[, name])  # 记罪
crime.exempt_guns(bool)           # 今日卖枪豁免
crime.list()                      # 档案快照 (v2.0.2)
crime.clear(id)                   # 清指定罪, 返回条数 (v2.0.2)
crime.clear_all()                 # 清空档案 (v2.0.2)
crime.evidence([n])               # 读/设证据等级 0..100 (v2.0.2)

power.get("rev")                  # 势力数值
power.add("sec", -5)              # 势力数值加减
```

---

**本篇完。** 下一篇：[04 · shop：商店查询](04-shop.md)。
