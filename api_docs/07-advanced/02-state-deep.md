# 07 · 02 存档状态深入

> [05 · 02 state](../05-api-reference/02-state.md) 讲了四个函数怎么调。
> 本篇讲它们的**全部底层**：值在内存里长什么样、何时写盘、写到哪个文件、
> 换存档槽/退出游戏/中途崩溃各发生什么——以及据此推导出的实战纪律。

## 一、三层结构

`state.*` 背后是一个纯托管类 `SaveStates`（`_psapi/PSApi/Shared/SaveStates.cs`，
v2.0.0 合并后单 dll 内一份；合并前 Items / Events 两模组曾各自编译一份）：

```text
state.set("k", [1,2,3])                    脚本层
      │  ScriptJson.Encode → "[1,2,3]"
      ▼
SaveStates 实例   Dictionary<string,string>   内存层(按 owner 一份)
      │  Flush → JsonSerializer.Serialize
      ▼
UserData/PSApi/state/<槽>/<owner>.json      磁盘层
```

三层各记一件事：

1. **脚本层编码**：`state.set` 的值先 JSON 编码成字符串再存（`ScriptJson.Encode`）；
   `state.get` 读回时先尝试 JSON 解码（`ScriptJson.TryDecode`），解析不了就当
   普通字符串原样返回。所以**数字、bool、list、dict 存取往返无损**，字符串
   也不会被意外二次解析（编码后的字符串带引号，解码即还原）。
2. **内存层按 owner 分实例**：`SaveStates.For(owner)` 维护一张
   `owner → 实例` 表；脚本侧的 owner 固定是**包 id**（`BuiltinsGame.RegisterState`，
   每次调用现取实例——不缓存，槽位切换后自动拿到重载后的新数据）。
3. **磁盘层一 owner 一文件**：整个文件就是一个 `键→字符串` 的 JSON 字典，
   缩进格式（`WriteIndented = true`），人可直接读改。

## 二、磁盘布局与 owner

```text
UserData/PSApi/state/
├─ 8/                        ← 存档槽 8
│  ├─ ex01_hello.json
│  ├─ ex33_state_deep.json
│  └─ psapi.events.npc.json
├─ 9/ ... 10/ ... 14/        ← 其余槽位(实际存在哪些取决于玩家建过的存档)
└─ global/                   ← "无存档"槽(主菜单期写入落这里)
```

三个事实：

- **槽目录名 = `PlayerStore.saveSlotId`**，读不到时归 `global`
  （`SaveStates.CurrentSlot()`）。
- **owner 除了包 id，还有框架自用的 `psapi.events.npc`**——NPC 服务的
  排班记账（`npc.<id>.count` / `npc.<id>.once` / `last_eval_day`）就存在这里。
  你在 [05 · 08 npc](../05-api-reference/08-npc.md) 读到的"排班记账与你的
  state 同库不同文件"，指的就是这个文件。
- 文件是**追加友好**的普通 JSON：手动改值、合并备份都可行（游戏关着改）。

## 三、槽位隔离的生命周期

`SaveStates` 用一个静态字段 `_loadedSlot` 记录"当前内存里是哪个槽"。
关键动作都在 `EnsureSlot()`：

```text
启动 → 主菜单                _loadedSlot = "global"
进入存档 8(场景加载)          槽变 → FlushAll 全部落盘 → 清空实例表 → 重载槽 8 的各 owner 文件
存档 8 玩了一天               读写都在内存
退主菜单 → 进存档 9           槽变 → FlushAll(槽 8 落盘) → 重载槽 9
退出游戏(OnDeinitializeMelon) FlushAll 落盘
```

触发点在两个模组的 `OnSceneWasLoaded`（`sceneName != "EmporiumMenu"` 时调
`EnsureSlot`——主菜单场景不触发切槽）与 `OnDeinitializeMelon`。

**推论（实战纪律）**：

- 主菜单期顶层脚本写入的 state（比如首次初始化标志）落在 `global/`，
  **任何存档都看不到它**。想"每个存档各自初始化一次"，判断要放在
  对局内事件里（`on day_wake(): if not state.has("inited"): ...`）。
- 槽隔离是全自动的，脚本不需要也不存在"指定槽"的 API。

## 四、落盘时机与崩溃窗口

`Flush`（单 owner）与 `FlushAll`（全部）的调用点全部列出来：

| 时机 | 触发者 | 说明 |
|---|---|---|
| 每日 NPC 调度评估后 | `NpcService.EvaluateSchedules` 尾部 | 对局内**最可靠的定期落盘**（每天开店建队时） |
| 存档槽切换时 | `EnsureSlot` → `FlushAll` | 旧槽数据保住再换 |
| 退出游戏时 | 两个模组 `OnDeinitializeMelon` | 正常退出不丢 |
| 失败防护 | `Flush` 自身 try/catch | 写盘失败只告警不崩 |

**崩溃窗口**：`state.set` 只改内存。如果玩家在"上一次 FlushAll"之后、
下一次落盘之前崩溃/杀进程，这段窗口内的写入会丢。窗口最长 = 一天
（因为每天调度评估必 FlushAll）。重要交易记录想更稳，可以在关键节点后
主动……没有手动 flush API？对——那就**把关键记账与每日评估对齐**：
贵重数据在 `day_wake`/`day_sleep` 事件里写，此时机距落盘点最近
（wake 时评估已跑完，sleep 后次日 wake 必落盘）。

> **修复玩家数据**：state 文件是纯 JSON，关游戏后可直接编辑。改坏了？
  删掉那个 owner 文件 = 该包全部 state 归零（NPC 记账同理，删
  `psapi.events.npc.json` 会让 once/max_times 重新计数）。

## 五、值的编码细节

| 你存的 | 文件里的样子 | 读回类型 |
|---|---|---|
| `state.set("n", 42)` | `"n": "42"` | `int` 42 |
| `state.set("ok", true)` | `"ok": "true"` | `bool` true |
| `state.set("l", [1, "a"])` | `"l": "[1,\"a\"]"` | `list` |
| `state.set("d", {x = 1})` | `"d": "{\"x\":1}"` | `dict` |
| `state.set("s", "hello")` | `"s": "\"hello\""` | `string` |
| `state.set("k", null)` | （键被移除） | — |

注意两点：

1. **del 就是 set null**：`state.del(key)` 内部 `Set(key, null)`，字典直接
   移除键——文件里不留痕迹。
2. **has 只看键在不在**，不看值真假。`state.set("flag", false)` 之后
   `state.has("flag")` 仍是 `true`。要"存在且真"写
   `if state.has("flag") and state.get("flag"):`。

## 六、典型模式（对照 05 章，补机制注解）

### 防重（一次性剧情）

```pss
on shop_opened():
    if not state.has("gift_given"):
        items.give("my_pack:starter_kit")
        state.set("gift_given", true)     # 落盘等每日评估; 当天崩溃会重发——可接受再想想
```

> 机制注解：想要"崩溃也不重发"，把标志写进 `day_sleep` 之后的次日
> `day_wake` 并接受"当天内重复"的窗口，或把发放物设计成幂等（给计数器
> 而非给物品）。

### 跨天记账（资产负债表）

```pss
on day_wake():
    st = state.get("ledger", {})           # dict 直存直取, 无损往返
    st["day_" + str(time.rel_day())] = player.cash()
    state.set("ledger", st)
```

### 按槽隔离的"新手引导"

```pss
on game_loaded():
    # 每次读档触发; 此处在对局内, state 已切到该存档的槽
    if not state.has("tutorial_done"):
        ui.open("my_pack:tutorial")        # 首次进档弹引导面板
        state.set("tutorial_done", true)
```

> `game_loaded` 的 payload 里带 `save_slot` 与 `run_id`（`GameHooks.OnGameLoaded`），
> 需要区分周目的逻辑可直接用 `event.save_slot`。

## 七、与游戏本档的关系（重要边界）

PSApi 的 state 文件**独立于游戏存档系统**：

| 行为 | 结果 |
|---|---|
| 游戏内读档回退（当天） | state **不回滚**（窗口期写入保留） |
| 删除游戏存档 | 对应槽的 state 目录**不会自动删**，重建同名槽位会读到旧数据 |
| 云同步/拷贝存档 | 只同步游戏目录，`UserData/PSApi/state/` 要自己带走 |

这也是 NPC 排班"读档回退不回滚已耗次数"的根源（[05 · 08](../05-api-reference/08-npc.md)）。

---

**本篇完。** 下一篇：[03 · 原版注入深入](03-inject-deep.md)
