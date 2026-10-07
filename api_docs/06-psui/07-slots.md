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

上面的例子来自模组打印机原型（[ex28_psui_slots](../../examples/ex28_psui_slots/README.md)
有完整的等价实现）——一张小面板 + 一段脚本就是一个完整可玩的工作台。

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

缺控件的应对手法（[ex28_psui_slots](../../examples/ex28_psui_slots/README.md) 实证）：

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
- **`size: WxH`** — 格数。`slot: 1x1` 是"单格小槽"（如数据卡槽），
  `grid_slot: 2x6` 是 12 格料仓（打印机熔化区）。
- **`on_change: 函数名`** — 槽内容变化回调，**逐帧轮询差分**触发
  （不是原生事件：PSUI 刻意不给槽位挂委托，规避 Il2Cpp 委托的 GC 风险）。
  拖入、取出、脚本消耗都算"变化"。
- **`shape: "01串"`**（仅 `grid_slot`,v1.40.0)— 行优先逐格格态串。
  `0`=可放（原生画底格）,`1`=永久洞（禁放且不渲染，视觉上就是洞）,
  `2`=锁定格（v1.41.0：禁放但**暗色渲染可见**，表达"这里以后解锁";
  框架用树外 uGUI overlay 画暗格，不进元素树；调原生 SetShape 前自动 2→1）。**必须带引号**
  （裸写会被解析成数字丢前导 0)，长度必须恰为 `size` 的 w×h，只含 0/1/2——
  违规解析期带行号硬报错。底层是原生 `GameGridInventory.SetShape`
  （与原版扩容套件 `MachineHelper.ExpandModule` 同 API)，放置判定三条路径
  逐格现读即时生效；运行时可用 `ui.machine_set_shape` 改（如升级解锁格，
  新串不含 2 时暗格 overlay 自动消失）。
  注意：SetShape 只管之后的放置判定，已在格内的物品不自动弹出。
- **`lock_interactions: true`**（v1.48.0)— 槽内物品锁死除「移动/装备/排序」外一切交互：
  双击全吞（脚本 `items.on_double_click` 与原生容器/机器开窗都拦）、不能成为
  `items.on_target` 的 use 目标端（高亮不亮、Target 跳过）。拿进拿出/快捷转移不受影响。
  v1.48.2 补齐双击外的开启路径：右键上下文菜单 Open/Use/Activate/Toggle/Unload
  按钮灰显（UpdateConditions 后置）+ 硬拦（TriggerButton 前置，Equip 装备与排序按钮放行——
  视为移动）、悬停热键「默认激活」（ItemDefaultActivateHandler.OnEventPress）硬拦。
  v1.48.3 实证修复：v1.48.2 用 Traverse.Field 读上述处理器的私有成员，但 Il2Cpp interop
  程序集里这些是【属性】不是字段，反射静默失败导致三条补丁空转（用户实测右键「打开」漏拦）——
  改为直接属性访问（编译期验证）；另在 `ItemMouseDoubleClickHandler.OpenContentAction`/
  `OpenExamineAction`（开箱/查看动作的公共出口）加路径无关兜底硬拦，上游任何 UI 路径
  漏拦都过不去；启动日志 `[lock-self]` 自检补丁挂载 + 成员形态。
  v1.48.4 补第 4 条开启路径「拖放存放」（拖 A 到槽内容器/机器物品 B 上提示「将 A 存放入 B」
  且松手真存入）：`GeneralHelper.MayPlayerInsertInto(GameItem)` 谓词压 false（提示不显示+
  存放不执行），`GraphUtils.CanInsertToActiveContainer`/`InsertToActiveContainer` 窗体侧兜底
  （目标内容窗 parentItems 含锁槽物品即拒，覆盖"先开窗后入锁槽"边角）。
  判定按「位于该槽」状态（物品父系含该槽），不标记物品实例、不持久化——
  面板重建自动重挂，读档/切场景天然恢复。用途：大物件装备槽防套容器藏东西
  （如旅行物品箱的大件槽 g_big）。另注：v0.9.19 起注册白名单槽位的强制放行先过
  几何守卫（grid_slot 越界/shape 洞/压已占格一律拒，"红=禁放"严格成立）;
  v0.9.20 起固定 slot 默认不验 footprint（一格任意大小），要验写 `strict_footprint: true`；
  v0.9.21 起 whitelist 支持 `"*"` 全放行条目（任何非空 id 命中，排除组"!"条目仍优先——
  `["*"]` = 显式"全物品可放"，g_big v0.45.2 起用此写法放开容器/机器过滤）。

## 格子类型全分类（v1.48.4 / Items v0.9.21）

| 类型 | 视觉 | 尺寸语义 | 过滤方式 | 放置判定 | 典型用例 |
|---|---|---|---|---|---|
| `slot`（固定单槽） | 一格底格，物品图标悬在槽上，**不管 footprint 多大** | `size` 只定底格视觉大小（格数），**不限制物品尺寸**；放"一件/一组堆叠" | whitelist 排他（id/#TAG/#DATA/"!"排除/"*"全放） | 白名单判定；**默认不验 footprint**（slot 语义=一格任意大小）；`strict_footprint: true` 才验外接矩形≤槽格数 | 鉴定机 s_item（2x2 收 10x3 枪）、打印机罐槽 s_tank（1x1 收 1x2 罐）、数据卡槽、熔炉燃料槽、组装台部件槽/成品槽、旅行箱武器/头盔/胸式槽、**大件槽 g_big** |
| `grid_slot`（网格区域） | w×h 格区，物品按 footprint 占格自由摆放 | `size` = 可用格区，物品 footprint 必须落在格区内 | 同上 | **严格几何判定**：footprint 越界/shape 洞/压已占格一律拒（红=禁放，v0.9.19 起强执行） | 旅行箱口袋 g_pocket 2x3、熔炉输入区 s_in 6x6、模组区 s_mod 7x5（带 shape 洞） |
| `scroll_grid_slot`（滚动网格） | 可视区 w×h + 内容可滚动 | `size`=可视格数，`content_height`=内容总高（像素） | 同上 | 同 grid_slot | 暂无使用 |
| `grid_slot mirror:`（镜像伴侣窗） | 占位格，显示源槽内容器物品的子窗内容 | 借来的原生容器窗，大小随源容器 | 源容器自身规则 | **原生判定**（借的是原生窗，PSApi 不管） | 暂无使用 |
| 原生格子（非 psui） | 原版背包/柜子/机器面板/搜打撤地面·容器·口袋网格 | 原生定义 | 原生规则 | **原生判定**（未注册白名单，PSApi 守卫不介入） | 原版背包/储物柜；搜打撤 ScriptGridService 地面/物资容器/口袋 |

选型口诀：**"一件东西不管多大"→ `slot`；"多格自由摆、要算格子"→ `grid_slot`**；
要洞/锁定格用 grid_slot `shape`；要锁交互（只取放）加 `lock_interactions: true`。

```pss
# 5x7 模组仓, 只开中央 2x2 (r3-4/c2-3), 其余全洞 (行优先 5 列×7 行=35 字符)
grid_slot s_mod: whitelist: ["system_module_quality"], size: 5x7, shape: "11111111111001110011111111111111111"
```

## 槽位工作流（打印机全流程）

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

图元后端专属的视觉调参，[ex29_psui_machine](../../examples/ex29_psui_machine/README.md)
的机器面板重度使用：

| 属性 | 作用 | 典型用法 |
|---|---|---|
| `font_size: 0.65..3` | TMP 字号**倍率**（1.0 = 原生默认，不写不缩放） | 槽标签 0.65 / 状态行 0.7 / 箭头 1.2 |
| `size: 100x18`（button） | 定宽定高像素（防长文本折行） | 武器按钮 100x18 |
| `icon: "pack:名"` | 图标面按钮（AdvButton，图标走包图集） | —（也可回退纯文字方案） |
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
