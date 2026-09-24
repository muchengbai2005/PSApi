# 06 · gunworks 机器层：三台机器的状态机

> 组装台、打印机、分析仪三台机器是**机器绑定 PSUI 面板**的官方模板
> （v1.4.0 机器系统）：双击开面板、材料存机器里随存档序列化、隔夜结算。
> 本篇以组装台为主线全链路带读，打印机/分析仪讲差异。
>
> 事实来源：`UserData/PSApi/packs/gunworks/` 的 `machines/gun_bench.json`、
> `ui/gun_bench.psui`、`events/bench/{01_data,02_bench}.pss`、
> `events/printer/*`、`events/analyzer.pss`（v0.13.0 磁盘实况）；
> 机制细节见 [06 PSUI · 机器绑定面板](../06-psui/README.md)。

## 一、全链路：四份文件各管一段

```text
items/gunworks_bench.json   物品: 组装台本体(4x6 塔形格子, value 500)
        ↓
machines/gun_bench.json     声明: {id, ui: "psui", panel: "gun_bench"}
        ↓                          双击 → PSApi.Events 装配 ui/gun_bench.psui
ui/gun_bench.psui           骨架: 6 武器按钮 + 5 部件槽 + 1 零件槽 + 1 输出槽
        ↓                          回调名写在元素上(on_click/on_change/on_open)
events/bench/01_data.pss    数据: 配方表/槽位表/显示名 (纯 var, 先加载)
events/bench/02_bench.pss   逻辑: 状态机 + 面板回调 + on day_wake 隔夜完成
```

文件名即加载顺序：`01_data` 的全局表先就位，`02_bench` 的函数才能引用。
**数据与逻辑拆开**的收益：改配方只动 01_data.pss（改表即改配方），
状态机代码不动。

## 二、状态机：idle → assembling → done

每台机器一个 state 键，**键名带机器 uid** 实现多台隔离：

```pss
func bench_key(m):
    return "bench:{m.uid}"

func bench_st(m):
    var k = bench_key(m)
    if not state.has(k):
        state.set(k, {weapon = "pistol", status = "idle", day = 0})
    return state.get(k)
```

三态流转：

```text
idle          可放部件/可切换武器; 部件放齐 → 输出槽出预览
  │ 点[开始组装]              st.status = "assembling"; st.day = time.day()
  ▼                          全部槽上锁(插入锁+取出锁)
assembling   过一夜; 点[取消组装]回 idle(部件保留)
  │ day_wake: time.day() > st.day
  ▼                          bench_finish: 消耗部件+零件, 成品留输出槽
done         成品可取; 输出槽取空 → 回 idle
```

**两个"存"的分工**：

- **状态**（当前武器/阶段/开始日）→ 存 `state`（按存档槽隔离）；
- **物品**（槽里的部件/预览/成品）→ 留在面板槽里，**随机器子物品树原生序列化**。
  脚本从不把物品句柄写进 state——句柄不能序列化，uid 可以。

## 三、隔夜结算：uid 清单模式

"句柄不能进 state, uid 可以"是机器脚本的**第一铁律**。day_wake 里用
清单 + `ui.machine_find_uid(uid)` 找回机器：

```pss
on day_wake():
    var lst = state.get("bench:list", [])
    if len(lst) == 0:
        return
    var keep = []
    for uid in lst:
        var k = "bench:{uid}"
        if not state.has(k):
            continue
        var st = state.get(k)
        var alive = true
        if st.status == "assembling" and time.day() > st.day:
            var m = ui.machine_find_uid(uid)
            if m == null:
                alive = false           # 机器没了(卖掉/丢失) → 从清单剔除
            else:
                bench_finish(m, st)
        if alive:
            push(keep, uid)
    state.set("bench:list", keep)
```

`bench_save` 时把 uid 压进 `bench:list`；day_wake 顺手清死条目（机器被卖掉
后 find_uid 返回 null）。**keep 数组模式** = 遍历中安全删除清单的 PSScript
惯用法（语言没有 filter/map 高阶函数时，for + push 就是标准写法）。

`bench_finish` 的消耗次序有讲究——**先解锁再销毁**：

```pss
# 与 ProgressRecipeService 锁料消耗同次序
ui.machine_lock(m, BENCH_SLOTS[i], false)
var it = ui.machine_slot_item(m, BENCH_SLOTS[i])
if it != null:
    items.consume(it)
```

锁着的槽脚本自己也没法操作（双锁对脚本同样生效），所以消耗前必须先解锁。

## 四、面板骨架与"结构恒定铁律"

`ui/gun_bench.psui` 是纯静态骨架（约 50 行），全部回调名声明在元素上：

```text
window gun_bench:  persistent: true, on_open: bench_on_open
 ├ 左列  6 个 button btn_w_* (on_click: bench_pick)
 ├ 中部  grid 2 列: label lbl_s0..s5 + slot s0..s5 (on_change: bench_parts_changed)
 └ 右列  "→" + slot sout (on_change: bench_output_changed)
 底部   btn_assemble (on_click: bench_assemble) + lbl_desc + lbl_status
```

**结构恒定铁律**（psui 头注原话）：面板的元素树**永远不变**——切武器只改
槽名/白名单/锁/按钮文本（`bench_apply`），不增删元素。原因写在头注里：

> 存档按图节点 **BFS 索引**记录槽内物品位置，结构变了读档会串槽。

gunworks 五次布局重排的代价也记录在案："此前存档里已放出的组装台面板结构
对不上——旧台子读档后槽内物品可能串位；把旧台子物品取回、卖掉重买一台即可"。
**给所有做机器面板的人：上线后再想改布局，就要替玩家付重买成本。**

`bench_apply` 是铁律的执行者（节选）：

```pss
func bench_apply(m):
    var st = bench_st(m)
    var parts = BENCH_RECIPES[st.weapon]       # 当前武器配方: 每槽一个 id 或 null
    var busy = st.status != "idle"
    var i = 0
    while i < 5:
        var slot = BENCH_SLOTS[i]
        if parts[i] == null:
            ui.machine_set_text(m, "lbl_" + slot, "—")     # 此枪不用此槽
            ui.machine_lock(m, slot, true)
        else:
            ui.machine_set_text(m, "lbl_" + slot, bench_part_name(parts[i]))
            ui.machine_set_whitelist(m, slot, [parts[i]])  # 白名单收窄到精确 id
            ui.machine_lock(m, slot, busy)
        i = i + 1
    ...
```

注意 `ui.machine_set_whitelist(m, slot, [parts[i]])`：**psui 静态白名单是
六种武器的并集**（首帧前脚本还没跑），运行时收窄到当前武器的精确 id——
这就是 `bench_missing` 注释里"防大机匣混进手枪槽"的第二道闸。

## 五、输出槽：占位白名单 + 双锁

输出槽"只出不进"的机关是 psui 里一行：

```text
slot sout: whitelist: ["gunworks:__reserved_output"], ...
```

`__reserved_output` 是**不存在的物品 id**——排他注册后任何手动放入都被拒。
而脚本放预览/成品走 `ui.machine_spawn`，**不过白名单**：

```pss
# 部件齐 → 预览出现(取不下来: 输出槽锁定)
if bench_parts_complete(m, st):
    if ui.machine_slot_item(m, BENCH_OUTPUT_SLOT) == null:
        ui.machine_spawn(m, BENCH_OUTPUT_SLOT, BENCH_WEAPON_OUTPUT[st.weapon], 1)
    ui.machine_lock(m, BENCH_OUTPUT_SLOT, true)
```

`ui.machine_lock` 是**插入锁 + 取出锁双锁**；done 之前输出槽锁死
（预览取不走），取走成品那一刻 `bench_output_changed` 触发回 idle：

```pss
func bench_output_changed(a):
    var m = a.machine
    var st = bench_st(m)
    if st.status == "done":
        if ui.machine_slot_item(m, BENCH_OUTPUT_SLOT) == null:
            st.status = "idle"            # 取空了 → 下一轮
            bench_save(m, st)
            bench_apply(m)
```

**"防呆"的完整清单**（都在 02_bench.pss，可直接抄）：
切换武器前查三件事（组装中/成品未取/槽有料）；缺料时状态行点名缺什么；
按钮文本即状态（"开始组装"⇄"取消组装"）。

## 六、打印机与分析仪：同模式的三个变体

三台机器共享同一套骨架（状态键/uid 清单/day_wake/结构恒定），差异全部
在"隔夜结算怎么算"：

| | 组装台 | 部件打印机 | 物品分析仪 |
|---|---|---|---|
| 状态 | weapon/status/day | melt_on/liquid/printing/print_id/sel/day | analyzing/out_id/day |
| 触发 | 点开始组装 | 点开始打印 | 点开始分析 |
| 隔夜消耗 | 部件全吞+零件×1 | 扣液+扣电+**数据卡扣 1 次** | 部件+空白卡+15 电 |
| 产出 | 成品枪 | 选定部件 | 对应数据卡 |
| 失败语义 | —（必成） | 电不够任务保留明晚重试 | 同左 |

打印机最有意思的是**单电池供电优先级**（v0.6.1 用户裁定）：

```pss
# 1) 打印区优先: 10 电/件, 全有或全无
var drawn = false
var bp = ui.machine_slot_item(m, "s_batt")
if bp != null:
    drawn = items.power_draw(bp, PRINTER_PRINT_POWER)
if drawn:
    ...  # 扣液 → 卡扣次 items.use(card, 1) → spawn 成品
else:
    ui.machine_set_text(m, "lbl_status", "电量不足, 今晚未能打印 (明晚重试)")
# 2) 熔化区: 吃同一块电池里剩下的电, 2 电/锭
```

以及**熔化的容量整除判断**：

```pss
var capn = (PRINTER_LIQUID_MAX - st.liquid) / PRINTER_MB_PER_INGOT
if n <= capn:                      # 整堆能装下才熔, 否则跳过该堆
    if items.power_draw(bm2, PRINTER_MELT_POWER * n):
        items.consume(it)
        st.liquid = st.liquid + PRINTER_MB_PER_INGOT * n
```

"全有或全无"贯穿三台机器：电不够就不动材料，**宁可明晚重试也不做半份**——
隔夜机器的基本 UX 决策。

分析仪的 `analyzer_card_for(id)` 是一张纯函数映射表（9 部件 → 5 卡，
同类型部件析出同种卡），整个脚本里唯一"业务规则"，其余全是骨架。

## 七、抄什么

| 你想要 | 抄这个 |
|---|---|
| 任意"放材料→过夜→出成品"机器 | bench 四件套骨架（本篇第一节的文件链） |
| 多台机器数据隔离 | `bench:<uid>` 键 + `bench:list` 清单 |
| 只出不进的输出槽 | 占位 id 白名单 + `machine_spawn` 直入 |
| 防误操作 | 双锁 + 按钮文本即状态 + 三查切换 |
| 供电/资源不足 | "全有或全无"+ 任务保留明晚重试 |
| 改数值不改逻辑 | `01_data.pss` 独立数据文件模式 |

---

**本篇完。** 下一篇：[07 · gunworks 经济闭环](07-gunworks-economy.md)
