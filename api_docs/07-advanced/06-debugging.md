# 06 · 调试排障

> 本篇是"手册"：日志去哪看、启动时正常该看到什么、出症状从哪查起。机制细节在各专题篇（[02 存档状态](02-state-deep.md) / [03 注入](03-inject-deep.md) / [04-05 NPC](04-npc-pipeline.md)），本篇只给**入口和读法**。

---

## 一、日志地图

| 位置 | 内容 | 何时看 |
|---|---|---|
| `MelonLoader/Latest.log` | **主战场**：MelonLoader 加载序列 + PSApi 全部日志（`[psapi]` 前缀）+ 游戏自身 LogError | 几乎所有排查 |
| `UserData/PSApi/logs/pack_errors_*.log` | 内容包解析错误**汇总落盘**（`pack_errors_yyyyMMdd_HHmmss.log`，**有错误才写**） | 启动报错后懒得翻大海捞针的 Latest.log 时 |
| 游戏控制台窗口 | Latest.log 的实时流 | 边玩边观察 |

PSApi 的日志分级前缀统一为 `[psapi]`（Info/Warning/Error 同前缀），grep 这个词就能过滤出全部框架日志。

## 二、启动链路（健康基线）

两个模组的加载顺序由 MelonPriority 决定：**PSApi.Items（10）先于 PSApi.Events（20）**。一切正常时，Latest.log 里应按序出现：

```
PSApi.Items v0.9.3 loaded. packs=N items=M qualities=K      ← Items 扫包完成
item directory ready via ModHook.OnModItemDirectoryInit...   ← 进对局后目录注入成功(或 via poll)
rescan(init): N pack(s), M pss file(s), K handler(s), 0 compile error(s)   ← Events 扫包+脚本编译
event bus selftest hits=12 ...                               ← 事件总线自检
PSApi.Events v1.13.1 loaded. packs=N
```

对照要点：

- `packs` 数量不对 → 看启动早段的 `pack conflict [...]` 警告与主菜单弹出的冲突窗（同 id 文件夹包 vs 内嵌包冲突）
- `0 compile error(s)` 不为零 → 编译错误明细在启动段与 pack_errors 落盘文件里
- `item directory ready` 一直不出现 → 没进对局或 ModHook 订阅失败（会有 "will poll instead" 警告，3 秒轮询兜底中）
- 每日建队、每次生成 NPC、每次摆货都有常驻日志（不需要开任何 debug 开关）——它们就是主要诊断信号

## 三、随身工具

| 快捷键 | 作用 | 可配置 |
|---|---|---|
| **F6** | 管理面板（浏览物品目录、注入清单、NPC 注册表等） | MelonPreferences `PSApi.Events` → `ManagerHotkey`（KeyCode 名） |
| **F12** | 把全部已注册的 PSApi 物品发到背包（调试物品用） | MelonPreferences `PSApi.Items` → `GrantHotkey` |

F12 在物品没进目录时打 `nothing granted (not in store yet?)`——它同时是"目录是否就绪"的快速探针。

## 四、症状 → 排查对照表

### 1. 包整个没加载

```
查: 启动段 pack conflict 警告 → 主菜单冲突弹窗
因: 文件夹包与内嵌包同 id 冲突 / pack.json 无效 / 目录层级错(packs/<包id>/pack.json)
```

### 2. pss 编译错

```
查: "rescan" 行的 compile error 数 → 错误明细(行号+原因)在紧邻行与 pack_errors_*.log
因: 语法错 / 缩进错 / 顶层不允许的语句(顶层规则: 04-psscript/02-first-script)
果: 整个文件的 handler 全部不注册
```

### 3. 编译过了但顶层代码没效果

```
查: 顶层运行错误也是运行期警告(psapi warn 前缀), 编译期只查语法
因: 顶层抛异常 → 该文件后续顶层语句中断(handler 已注册, 不受影响)
```

### 4. on handler 不触发

```
查: ① 事件名拼写(全表: 04-psscript/09-events.md) —— 订阅不存在的事件名启动时会有警告
    ② 事件是否真的发生(如 game_loaded 只在读档进对局时, psapi.tick 每秒一发可当心跳)
    ③ handler 是否编译成功(rescan 行的 handler 数)
```

### 5. 物品 id 不存在

统一告警格式（每来源×每 id 只警告一次）：

```
[psapi] 物品 id 'xxx' 在当前版本不存在, 已跳过 (来源: npc xxx buying_ids / buy_pool / sell_items / inject.buy_list ...)
```

多见于：id 忘写包前缀（模组物品必须 `包id:名`）、物品 JSON 没被加载（回到症状 1/2）、游戏版本更新删了原版 id。**启动期不做这个校验**（目录未就绪全是假阳性），全部延迟到执行点，所以这个警告出现在对局中段的日志里是正常的。

### 6. NPC 不来

一行定案——grep `npc eval: <id>`（每个评估日每个注册 NPC 恰好一行）：

```
skip(manual)          → 模式 manual, 只能 npc.schedule() 强制
skip(day_not_matched) → 日期/间隔没到(every_ndays/specific_day)
skip(once_fired)      → once 模式已触发过(读档不回滚)
skip(max_times(3/3))  → 次数用尽
skip(chance)          → roll=x.xx/y.yy 概率没掷中
skip(can_spawn=false) → can_spawn 函数返回 falsy 或抛异常(异常另有警告行)
没有任何 eval 行       → EvaluateSchedules 没跑: 无注册 NPC / PlayerStore 未就绪 / 读不到天数
                      → 或根本没注册上(rescan 行往后 grep "npc '<id>' registered")
```

### 7. NPC 长得/表现得像拾荒者

```
因: base_template 拼错 → 游戏侧静默回退 scavGeneral(PSApi 日志无警告!)
     (见 04 篇: CreateStoreClient 对无效键返回拾荒者, LogError 落在游戏日志流)
查: 对照 04 篇的 247 模板总表逐字核对
```

### 8. NPC 带货但不摆上桌 / 交易入口死锁

```
查: "原生摆货 +N 件(对话结束/OpenUI兜底, 每客户一次)" 有没有出现
因: intent 不是 SELL/SELLNBUY(BUY 客户不摆货) / 摆货已执行过一次(指针记账, 拖走不补)
   / sell_items 概率全部没掷中(plan 为空 → 不算有货)
死锁兜底: IsClientSelling Postfix 强制可卖 —— 若连交易按钮都没有, 先确认 intent
```

### 9. PSUI 面板不出现

```
查: ① psui 文件解析警告(启动段) ② F6 能否 Toggle(热键是否被改)
   ③ 机器绑定面板: ui="psui" 的机器 JSON 是否被 Items 加载(症状 1)
   ④ 面板注册日志 "panel registered" 类行
```

### 10. 句柄失效 / 数据串台

```
因: Il2Cpp 对象随场景/客户离开销毁, 指针可能被复用
律: 句柄(client/item)只在做局内短期使用; 跨天/跨场景的数据一律走 state.*(02 篇)
   框架内部注册表(选项/掷中缓存/摆货记账)都在场景切换时主动清空, 脚本侧同理
```

### 11. state 丢了 / 变了

```
查: ① 是不是换了存档槽(state 按槽隔离, 02 篇目录布局)
   ② 崩溃窗口: state 只在特定时机落盘(每日 NPC 评估后/退出/槽切换), 中途崩溃丢最后一截
   ③ key 笔误: has 只看键存在, get 取不到返回默认
```

### 12. inject 注入不生效

```
查: 03 篇"调节工作流": F6 管理面板看 inject.list 条目是否在/enabled
   → uses 用尽 / 物品不存在(症状5格式) / 非玩家所有(uses=isOwned 纪律) / NPC 是脚本注册(跳过)
   loot_pool: 表每局重建, 换局自动重注, 看管理面板 chance 列
```

## 五、通用排查流程

```
症状出现
  │
  ├─ 启动就不对? ──→ Latest.log 启动段 + pack_errors_*.log(症状 1/2/9)
  │
  ├─ 对局中不对? ──→ grep "[psapi]" 定位相关警告行
  │                   ├─ 有警告 → 按警告文案对号入座(本篇第四节)
  │                   └─ 无警告 → 机制性静默(症状 7 拾荒者回退 / 语义误解)
  │                                → 回对应专题篇核对配置语义
  └─ 时好时坏?  ──→ 概率类: chance/p 掷骰(npc eval 的 roll 列)
                    时序类: 目录就绪竞态(F12 探针) / 槽隔离(症状 11)
```

## 六、给模组作者的自查清单

发布前建议过一遍：

1. 干净 Latest.log：无 `pack conflict`、`compile error`、启动期 warn
2. 新开档跑一个完整日：`npc eval` 行全部符合预期、无"物品 id 不存在"警告
3. 读档再跑一天：once/count 记账符合预期（读档不回滚）
4. 换一个存档槽：state 隔离正确
5. F12 发放 + F6 面板浏览：物品全量可建
6. 故意写错一个物品 id：确认告警格式正常、游戏不崩（健壮性）

---

**本篇完。** 下一篇：[07 · item_editor 物品编辑器](07-item-editor.md)——可视化涂格子做物品的工作区工具。
