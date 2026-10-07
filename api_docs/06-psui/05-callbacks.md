# 06 · 05 回调与生命周期

> 面板是静态的，回调让它活起来。PSUI 的回调全部**绑定本包函数**：
> `.psui` 里写函数名，实现写在 `events/*.pss`——启动期校验、运行期异常隔离。
> 本篇讲五种回调的触发时机与参数，以及面板从打开到关闭的完整生命周期。

## 五种回调一览

| 回调 | 写在哪 | 触发时机 | 回调参数 | 篇章 |
|---|---|---|---|---|
| `on_click` | button | 玩家点击 | 无（0 参调用） | 本篇 |
| `on_change` | toggle/slider/input/dropdown | 玩家改值 | `{elem, value}` | 本篇 |
| `on_change` | slot/grid_slot | 槽内容变化（轮询差分） | `{elem, value}` | [07 槽位](07-slots.md) |
| `on_build` | window | 面板构建期（Show 之前） | 无 | [06 动态构建](06-dynamic.md) |
| `on_open` | window | 机器面板每次开窗 | `{machine}` | [08 机器面板](08-machine-panels.md) |
| `on_window_closed` | 全局函数约定 | 窗口任何路径关闭 | 面板全名（string） | 本篇 |

回调函数签名规则（与 PSScript 的 `ArgsFor` 约定一致）：

- **0 参函数**：直接调用，什么都不传。
- **1 参函数**：收一个 dict（上表第三列）。
- 2 参及以上 = 不匹配，按 1 参传（多余参数吃默认值或报错）。

## on_click：按钮点击

```text
button close_btn:
  text: "关闭"
  on_click: e6_close_panel
```

```pss
func e6_close_panel():
    ui.close()
```

静态按钮回调**无参数**。列表里几十个按钮都要调同一个函数怎么办？
两个选择：动态按钮（`ui.build_button` 的 `arg` 选项，见 [06 动态构建](06-dynamic.md)），
或像 [ex26_psui_callbacks](../../examples/ex26_psui_callbacks/README.md) 那样"每按钮一函数 + 公共辅助函数"。

## on_change：玩家改值

```pss
# toggle → bool / slider → float / input → string / dropdown → int 序号
func e6_auto_changed(ev):
    e6_auto = ev.value          # ev = {elem = "auto_toggle", value = ...}
    log.info("[e6] 自动赠报: {e6_auto} (elem={ev.elem})")
```

两条红线：

1. **脚本回写不触发**：`ui.set_value("auto_toggle", true)` 不会触发 `on_change`
   ——防"回写→回调→再回写"的自激循环。只有**玩家操作**才触发。
2. **input 每次键入即触发**：不是失焦才触发，每次字符变化都调一次回调。
   搜索框要"点按钮才搜"的话，回调里只存草稿，按钮回调里再应用
   （psapi_manager 的 `g_search`/`g_applied` 双变量就是这个模式）。

## on_window_closed：关闭收尾（全局函数约定）

不是 `.psui` 属性，而是**包级全局函数约定**：包里定义了名为
`on_window_closed` 的全局函数，**任何**本包面板以任何路径关闭
（玩家 X / Esc / 脚本 `ui.close` / `ui.rebuild` 重建）都会被调用，参数 = 面板全名。

```pss
# 关面板 = 停自动赠报, 避免面板已关后 shop_opened 再触发 ui.* 报"没有打开的面板"
func on_window_closed(panel_id):
    e6_opened = false
    e6_auto = false
    log.info("[e6] on_window_closed: {panel_id}")
```

典型用途：清开关状态、停定时逻辑、记录"面板已关"。不定义就什么都不发生（可选约定）。

## 面板生命周期总图

```text
启动期 (一次性)
  │  扫描 ui/*.psui → 解析注册 (纯数据)
  │  BindAndValidate: on_click/on_change/on_open/on_build 引用的函数
  │  在本包 events/*.pss 查不到 → 启动日志警告 (点击/触发时也不生效)
  ▼
ui.open("panel") ────── 幂等: 已打开只置顶
  │
  ├─ 构建静态骨架 (按 .psui 元素树)
  ├─ on_build 回调 (构建期, Show 前) ← ui.build_* 只在此期间合法
  ├─ Show 显示
  ▼
运行期 (窗口打开中)
  │  玩家点击 button ────────→ on_click 函数
  │  玩家改 toggle/slider/… ──→ on_change {elem, value}
  │  脚本 set_text/set_value ─→ 直接生效, 不触发 on_change
  │  玩家拖动窗口 ────────────→ 位置持续记忆 (进程内)
  ▼
关闭 (任何路径: X / Esc / ui.close / ui.rebuild)
  │  图元面板: 槽内物品退回玩家后仓 (persistent: true 除外)
  │  on_window_closed(面板全名) 回调
  │  记录最终位置 → 下次 open/rebuild 恢复
  ▼
(可再 open — 从头构建, on_build 重跑)
```

要点：

- **构建在 open 时**（惰性实例化）——`ui.rebuild` = 关闭再 open，
  `on_build` 会重跑，这是动态面板刷新数据的标准做法。
- **回调异常隔离**：一个 `on_click` 抛错只记日志，不影响窗口、其他回调、游戏本体。
- **委托生命周期**：所有原生委托 pin 在面板记录上，窗口关闭随记录释放——
  你不需要管，但要知道"关了再开"是干净重建，不会累积泄漏。

## 机器面板的差异（预告）

机器绑定面板（machines/*.json `ui="psui"`）的生命周期略有不同：
开关由**原生双击**管理（不是 ui.open/close），每次开窗触发 `on_open {machine}`，
按钮回调带 `{elem, machine}`，槽位变化带 `{elem, value, machine}`——
细节见 [08 机器绑定面板](08-machine-panels.md)。

## 调试回调三招

1. **启动日志查绑定**：`on_click 函数 'xxx' 在包 'yyy' 的 events/*.pss 里未定义`
   ——启动期校验直接告诉你哪个面板哪一行。
2. **回调第一行打日志**：`log.info("ev={ev}")` 看 dict 结构（PSScript 的 dict 直接可插值）。
3. **面板开着才能操作**：`ui.set_text` 等在面板未打开时报
   `包 'xxx' 没有打开的面板 (先 ui.open)`——回调里先 `ui.is_open` 判断，
   或用 `on_window_closed` 清状态。

---

下一篇：[06 · 动态构建](06-dynamic.md)
