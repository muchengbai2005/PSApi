# 06 · 07 图元树后端与槽位

> 前面的面板都是"控件窗"——看得到、点得了，但放不进**真物品**。
> 要做"放进 2 个废金属 → 打印 1 份报纸"的工作台，需要 `slot`/`grid_slot`
> 物品槽，而它们会**把整张面板切到图元树后端**——与游戏原生机器界面同源的
> 渲染系统。本篇讲清这条后端的规则、支持边界和槽位工作流。

## 后端分派：一个 slot 换一套底层

判定规则只有一条：**面板元素树里任意位置含 `slot` 或 `grid_slot`
→ 整面板走图元树后端**（v1.7.0 起）。没有"混合后端"——
CustomUI 窗里嵌图元槽，或图元窗里嵌 CustomUI 控件，都做不到。

```text
window:
  label status_lbl:          # 图元后端: label ✔ (TagElement)
    text: "放入 2 个废金属"
  grid_slot feed:            # ← 槽位出现, 整面板切图元树
    whitelist: ["game:scrap_metal"]
    size: 3x4
    on_change: u4_feed_changed
  button print_btn:          # 图元后端: button ✔ (ButtonElement)
    text: "打一份报纸"
```

上面的例子就是 `example_hello/ui/u4_printer.psui` 原文——模组打印机原型，
18 行面板 + 33 行脚本就是一个完整可玩的工作台。

## 支持边界（硬规则）

图元树后端只认这些元素，其余**警告跳过（含子树）**：

| 支持 | 元素 | 图元映射 |
|---|---|---|
| ✔ | `label` | TagElement（+ `size` 定宽、`font_size` 字号倍率） |
| ✔ | `button` | ButtonElement（+ `size` 定宽高、`icon` 图标按钮、`font_size`）；**tooltip 不支持** |
| ✔ | `slot` / `grid_slot` | GameSlotInventory / GameGridInventory（真物品槽） |
| ✔ | `row` / `column` / `grid` / `spacer` | 网格布局（见 [04 布局](04-layout.md)） |
| ✔ | `image` | **仅 `src: "pack:名"`**（走包图集；`game:`/`mod:` 源不可用） |
| ✖ | `progress` / `toggle` / `slider` / `input` / `dropdown` / `scroll` | 警告跳过 |

缺控件的应对手法（gunworks 实证）：

- **"常开熔炼"开关** → 按钮切文本：`printer_melt_toggle` 在"常开熔炼: 关/开"之间切换。
- **进度显示** → label 文本（"金属液: 12 / 30 mB"）。
- **输入/选择** → 槽位本身（插卡选部件）或多个按钮选一。

## slot 与 grid_slot

| | `slot` | `grid_slot` |
|---|---|---|
| 语义 | 单格物品槽（放一组堆叠） | 多格物品仓（可放多组） |
| `size` 默认 | `2x2` | `3x4` |
| `size` 语义 | **格数**（宽 x 高各几格），不是像素 | 同左 |
| 读单个 | `ui.get_slot_item(id) → 句柄\|null` | —（也能调，只拿第一件） |
| 读全部 | `ui.get_slot_items(id) → [句柄]` | ✔ 主战场（v1.6.0） |
| on_change 的 value | 首物品句柄（空槽 = null） | 句柄列表（空 = 空表） |

槽位三属性：

- **`whitelist: [物品id...]`** — 排他白名单：只接受列表内物品放入。
  不写会警告"槽位不受管, 放入判定不可靠"（能放但过滤不可靠，**强烈建议写**）。
  机器面板的槽位白名单可运行时用 `ui.machine_set_whitelist` 覆盖式重设。
- **`size: WxH`** — 格数。`slot: 1x1` 是"单格小槽"（gunworks 数据卡槽），
  `grid_slot: 2x6` 是 12 格料仓（打印机熔化区）。
- **`on_change: 函数名`** — 槽内容变化回调，**逐帧轮询差分**触发
  （不是原生事件：PSUI 刻意不给槽位挂委托，规避 Il2Cpp 委托的 GC 风险）。
  拖入、取出、脚本消耗都算"变化"。

## 槽位工作流（u4_printer 全流程）

```pss
# 槽内容变化: 拖入/取出/消耗都触发; ev = {elem, value}
func u4_feed_changed(ev):
    var n = len(ui.get_slot_items("feed"))       # grid_slot 永远用 get_slot_items
    ui.set_text("status_lbl", "材料: {n}/2 废金属")

func u4_print():
    var list = ui.get_slot_items("feed")
    var targets = []
    for it in list:
        if it.id == "scrap_metal":               # 物品句柄读 id (剥掉 game: 前缀)
            push(targets, it)
    if len(targets) < 2:
        ui.set_text("status_lbl", "材料不足: 需要 2 个废金属")
        return
    var ok1 = items.consume(targets[0])          # 消耗也是"变化", 会再触发 on_change
    var ok2 = items.consume(targets[1])
    if ok1 and ok2:
        items.give("game:newspaper", 1)          # 产出直接发背包
        ui.set_text("status_lbl", "已打印 1 份报纸")
```

注意 `items.consume` 消耗槽内物品后，`on_change` 会**再触发一次**
（value 变了）——回调里若更新计数/文案要防重复（u4 用"重新读槽计数"天然幂等）。

## 美观三件套（v1.5.0+）

图元后端专属的视觉调参，gunworks 三个面板重度使用：

| 属性 | 作用 | gunworks 实例 |
|---|---|---|
| `font_size: 0.65..3` | TMP 字号**倍率**（1.0 = 原生默认，不写不缩放） | 槽标签 0.65 / 状态行 0.7 / 箭头 1.2 |
| `size: 100x18`（button） | 定宽定高像素（防长文本折行） | 武器按钮 100x18 |
| `icon: "pack:名"` | 图标面按钮（AdvButton，图标走包图集） | —（gunworks 回退纯文字方案） |
| `spacer: size 6x40` | 空单元占位预留像素（对角线/错落布局） | 组装台右列下推成品槽 |

`font_size` 的重放机制：脚本 `ui.set_text` 会重建文本节点丢掉缩放，
PSUI 记住倍率自动重放（v1.5.2 起经基线幂等，不叠乘）——你只管设一次。

## 关窗行为与 persistent

图元面板关闭（X / Esc / 脚本 `ui.close` / `ui.rebuild`）时，
**槽内物品自动退回玩家后仓**（`ItemsFacade.ReturnToPlayer`）——
防物品随窗口对象丢失，玩家体验是"关窗不掉东西"。

`window persistent: true` 跳过退回——槽内容随**面板使用者**负责，
配合脚本 state 存取实现"面板即容器"。机器绑定面板天然 persistent
（槽内容随**机器**存档，见下一篇）。

## 独立图元面板 vs 机器绑定面板

同一个图元树后端，两种宿主形态：

| | 独立面板（本篇） | 机器绑定面板（[08 篇](08-machine-panels.md)） |
|---|---|---|
| 打开方式 | `ui.open("panel")` 脚本开 | 玩家**双击机器物品**原生开窗 |
| 槽内容存档 | 关窗退回玩家（或 persistent 自管） | **随机器物品存档**（隔夜不丢） |
| 生命周期 | ui.open/ui.close 管理 | 原生双击管理，`on_open` 每次开窗回调 |
| 脚本 API | `ui.get_slot_item(s)` | `ui.machine_slot_item(s)` + `machine_*` 全家桶 |
| 典型场景 | 通用工作台、临时操作窗 | 组装台/打印机/分析仪等**放置型机器** |

选择建议：面板逻辑挂在"某个具体机器物品"上 → 机器绑定；
面板是全局工具或商店配套服务 → 独立面板。

---

下一篇：[08 · 机器绑定面板](08-machine-panels.md)
