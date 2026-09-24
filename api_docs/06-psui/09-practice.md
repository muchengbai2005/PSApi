# 06 · 09 实战：三张面板

> 本章收官：从零写三张面板，覆盖三条主路径——CustomUI 控件面板、
> 图元槽位独立面板、机器绑定面板。每例都是**完整可运行**的最小实现
> （面板 + 脚本全量贴出），物品全部用原版 id（`game:scrap_metal` 废金属、
> `game:newspaper` 报纸——example_hello 包验证过存在）。
> 建一个测试包跟着做：`packs/my_ui_lab/`，`pack.json` 写 `{"id": "my_ui_lab", ...}`。

## 示例一：开店仪表盘（CustomUI 后端）

**目标**：开店时自动弹出仪表盘，显示天数/队列/距交租，进度条可视化队列压力，
勾选"自动补货"后每天开店自动发 1 份报纸，按钮手动发报。

`ui/shop_dash.psui`：

```text
window shop_dash:
  title: "商店仪表盘"
  size: 420x260
  draggable: true
  close_on_escape: true

  label title_lbl:
    text: "加载中…"
  progress queue_bar:
    value: 0
    label: "今日队列"
  toggle auto_toggle:
    label: "开店自动发报"
    checked: false
    on_change: dash_auto_changed
  row:
    spacing: 8
    button give_btn:
      text: "发一份报纸"
      on_click: dash_give
    button close_btn:
      text: "关闭"
      on_click: dash_close
```

`events/s6_dash.pss`：

```pss
var dash_opened = false
var dash_auto = false

func dash_refresh():
    var s = shop.status()
    ui.set_text("title_lbl", "第 {time.rel_day()} 天 · 距交租 {s.day_until_rent} 天 · 当前客户: {s.current_name}")
    ui.set_progress("queue_bar", s.queue / 10.0, "今日队列 {s.queue} 位")

func dash_auto_changed(ev):
    dash_auto = ev.value          # toggle → bool

func dash_give():
    var ok = items.give("game:newspaper", 1)
    log.info("[dash] 发报: {ok}")

func dash_close():
    ui.close()

func on_window_closed(panel_id):
    if panel_id == "my_ui_lab:shop_dash":
        dash_opened = false       # 下次开店才会重新 open

on shop_opened():
    if not dash_opened:
        ui.open("shop_dash")      # 省本包前缀
        dash_opened = true
    dash_refresh()                # 开面板和已开着都要刷新
    if dash_auto:
        items.give("game:newspaper", 1)
```

**验证**：进对局等开店（或控制台触发）→ 面板弹出、进度条有值 →
勾 toggle 看日志 → 点发报按钮 → X 关窗再开店确认会重开。

学到：面板开关幂等（`is_open`/标志位）、构建后 `set_text`/`set_progress` 刷新、
`on_window_closed` 清状态。

## 示例二：废纸回收台（图元独立面板）

**目标**：放入废金属，按一次按钮消耗 1 个，产 1 份报纸进背包。
含 slot 的面板——整面板切图元树后端，注意 progress 之类控件不可用。

`ui/recycler.psui`：

```text
window recycler:
  title: "废纸回收台"
  size: 360x200
  draggable: true

  column:
    label status_lbl:
      text: "放入废金属, 按按钮再造报纸"
    row:
      spacing: 10
      slot feed:
        whitelist: ["game:scrap_metal"]
        size: 2x2
        on_change: rec_feed_changed
      button recycle_btn:
        text: "再造一份"
        on_click: rec_recycle
```

`events/s6_recycle.pss`：

```pss
func rec_feed_changed(ev):
    # slot 的 on_change: value = 首物品句柄 (空槽 null); 这里直接重读槽更稳
    var it = ui.get_slot_item("feed")
    if it == null:
        ui.set_text("status_lbl", "槽空: 放入废金属")
    else:
        ui.set_text("status_lbl", "材料: {it.id} x{it.count}")

func rec_recycle():
    var it = ui.get_slot_item("feed")     # 单格槽读首件
    if it == null or it.id != "scrap_metal":
        ui.set_text("status_lbl", "材料不足: 需要 1 个废金属")
        return
    if not items.consume(it):
        ui.set_text("status_lbl", "消耗失败")
        return
    items.give("game:newspaper", 1)
    ui.set_text("status_lbl", "已再造 1 份报纸")
    # consume 触发的槽位 on_change 会再跑 rec_feed_changed, 状态自动刷新

on shop_opened():
    if not ui.is_open("recycler"):
        ui.open("recycler")
```

**验证**：开店面板自动弹出 → 拖一块废金属进槽（白名单外的物品放不进）→
按钮 → 报纸入背包、状态行刷新 → X 关窗，**槽里的废金属自动退回背包**（非 persistent）。

学到：slot 白名单、`get_slot_item` 读槽、consume 后 on_change 的连锁刷新、
关窗退物品的默认行为。

## 示例三：夜间孵化器（机器绑定面板）

**目标**：可购买的机器物品，双击开面板；放入电池 + 报纸，按"开始孵化"锁槽，
过一夜（`day_wake`）产出 3 份报纸进输出槽，取走成品解锁回到待机。
这是 gunworks 组装台状态机的最小化版本。

需要三个文件 + 一张图标。

`items/incubator.json`（机器物品本体，借用原版机器模板）：

```json
{
  "items": [
    {
      "id": "my_ui_lab:incubator",
      "name": "夜间孵化器",
      "desc": "双击打开: 放报纸和电池, 过夜孵化出 3 份",
      "template": "desequencer",
      "value": 300,
      "shape": { "file": "incubator.png" }
    }
  ]
}
```

`machines/incubator.json`（声明 ui 走 psui）：

```json
{
  "machines": [
    {
      "id": "my_ui_lab:incubator",
      "ui": "psui",
      "panel": "incubator"
    }
  ]
}
```

`ui/incubator.psui`（元素树从此恒定——锁/文本变化，不增删元素）：

```text
window incubator:
  title: "夜间孵化器"
  size: 340x200
  draggable: true
  persistent: true
  on_open: inc_on_open
  spacing: 4

  row:
    spacing: 8
    column:
      label:
        text: "饲料 (报纸)"
        font_size: 0.65
      slot s_feed: whitelist: ["game:newspaper"], size: 1x1, on_change: inc_changed
    column:
      label:
        text: "电池"
        font_size: 0.65
      slot s_batt: whitelist: ["energy_credit"], size: 1x1, on_change: inc_changed
    column:
      label:
        text: "产物"
        font_size: 0.65
      slot s_out: whitelist: ["my_ui_lab:__reserved"], size: 2x2, on_change: inc_changed
  row:
    spacing: 8
    button btn_go: text: "开始孵化", on_click: inc_start
    label lbl_status:
      text: "放入报纸与电池"
      font_size: 0.7
```

`events/s6_incubator.pss`（状态机：idle → working → done）：

```pss
# 状态存 state (按存档槽隔离): "inc:<uid>" = {status, day}
# 槽内物品随机器存档原生序列化, 不进 state; uid 清单供隔夜遍历。
func inc_key(m):
    return "inc:{m.uid}"

func inc_st(m):
    var k = inc_key(m)
    if not state.has(k):
        state.set(k, {status = "idle", day = 0})
    return state.get(k)

func inc_register(m):
    var lst = state.get("inc:list", [])
    var uid = m.uid
    var found = false
    for u in lst:
        if u == uid:
            found = true
    if not found:
        push(lst, uid)
        state.set("inc:list", lst)

func inc_apply(m):
    # 结构恒定: 只改文本/锁, 不动元素树
    var st = inc_st(m)
    if st.status == "working":
        ui.machine_set_text(m, "btn_go", "孵化中…")
        ui.machine_set_text(m, "lbl_status", "孵化中, 明早取产物")
        ui.machine_lock(m, "s_feed", true)      # 双锁: 防取走饲料
        ui.machine_lock(m, "s_out", true)       # 防取走 (尚未产出的) 占位
    else:
        # idle / done: 可操作 (done 时输出槽解锁等玩家取)
        ui.machine_set_text(m, "btn_go", "开始孵化")
        ui.machine_set_text(m, "lbl_status", "放入报纸与电池")
        ui.machine_lock(m, "s_feed", false)
        ui.machine_lock(m, "s_out", false)

func inc_on_open(ev):
    inc_apply(ev.machine)         # 每次双击开窗刷新

func inc_changed(ev):
    var m = ev.machine
    var st = inc_st(m)
    if st.status == "done" and ev.elem == "s_out" and ui.machine_slot_item(m, "s_out") == null:
        state.set(inc_key(m), {status = "idle", day = 0})   # 成品取走 → 回待机
    inc_apply(m)                 # 槽位变化时同步锁态/文本

func inc_start(ev):
    var m = ev.machine
    var st = inc_st(m)
    if st.status == "working":
        return
    var feed = ui.machine_slot_item(m, "s_feed")
    var batt = ui.machine_slot_item(m, "s_batt")
    if feed == null or feed.id != "newspaper":
        ui.machine_set_text(m, "lbl_status", "缺饲料: 放 1 份报纸")
        return
    if batt == null:
        ui.machine_set_text(m, "lbl_status", "缺电池")
        return
    items.consume(feed)           # 消耗饲料与电池 (on_change 会再触发 inc_changed)
    items.consume(batt)
    state.set(inc_key(m), {status = "working", day = time.day()})
    inc_register(m)               # 进隔夜清单
    inc_apply(m)

on day_wake():                    # 隔夜结算: 按 uid 清单找回机器
    var lst = state.get("inc:list", [])
    for uid in lst:
        var m = ui.machine_find_uid(uid)
        if m == null:
            continue              # 机器被卖/毁, 跳过
        var k = inc_key(m)
        var st = state.get(k, {status = "idle", day = 0})
        if st.status != "working":
            continue
        if time.day() <= st.day:
            continue              # 还没过夜 (防重复触发)
        ui.machine_spawn(m, "s_out", "game:newspaper", 3)   # 产出直入输出槽 (不过白名单)
        state.set(k, {status = "done", day = st.day})
        inc_apply(m)              # done: 解锁输出槽, 等玩家取
```

注意 `inc_changed` 里那行 done 判定——**成品被取走**（`s_out` 变空）就是
"回合结束"的信号：状态回 `idle`、槽位解锁、按钮复原。这是输出槽模式的标准收口
（gunworks 组装台同款：`done` 解锁输出槽 → 取走 → `on_change` → 回 `idle`）。

**验证**：商店进货或 `items.give("my_ui_lab:incubator", 1)` →
后仓双击机器开窗 → 放报纸+电池 → 开始孵化（槽双锁、按钮变字）→
睡觉过夜 → 开窗见输出槽 3 份报纸 → 取走后解锁回待机。
重启游戏读档再开窗，槽内物品与状态都在（state + 机器存档双通道）。

学到：机器物品声明三件套（items/machines/ui）、`m.uid` 状态键模式、
`machine_lock`/`machine_spawn`/占位白名单输出槽、`day_wake` + `machine_find_uid`
隔夜结算、结构恒定（只改锁与文本）。

## 收尾自查清单

发布一个含 UI 的包之前过一遍：

- [ ] 面板 id 不与其他包撞名（`包id:面板名` 唯一性靠包 id 保证）
- [ ] 所有 `on_click`/`on_change`/`on_build`/`on_open` 函数存在（启动日志无警告）
- [ ] 图元面板没有误用 progress/toggle/slider/input/dropdown/scroll
- [ ] slot 都写了 whitelist 且都有 id
- [ ] 机器面板元素树与上一版**逐一对应**（结构恒定），有变化则在版本注释写迁移方案
- [ ] `ui.*` 调用前确认面板开着（`is_open` 或 `on_window_closed` 清状态）
- [ ] 输出槽用占位白名单 + `machine_spawn`，未取走时 `machine_lock` 防重复
- [ ] 隔夜逻辑用 uid + `machine_find_uid`，句柄不进 state

---

本章完。下一章：[07 · 进阶](../07-advanced/README.md)（存档状态深入 / 原版注入 / NPC 完整参考 / 调试排障 / item_editor）。
