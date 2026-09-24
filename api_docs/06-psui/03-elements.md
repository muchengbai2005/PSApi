# 06 · 03 元素与属性

> PSUI 共 15 种元素：4 种布局容器 + 11 种内容件。本篇按 CustomUI 后端
> （面板不含槽位时的默认后端）逐个讲属性与行为；图元树后端的差异
> （部分元素不支持、`slot`/`grid_slot` 只在图元后端存在）集中在
> [07 图元树后端与槽位](07-slots.md) 一张对照表里。

## 元素总表

| 元素 | 用途 | 关键属性 | 详见 |
|---|---|---|---|
| `row` | 横向容器 | `spacing` | [04 布局](04-layout.md) |
| `column` | 纵向容器 | `spacing` | [04 布局](04-layout.md) |
| `grid` | 网格容器 | `columns` `cell` `spacing` | [04 布局](04-layout.md) |
| `scroll` | 滚动区 | `height` | [04 布局](04-layout.md) |
| `label` | 文本 | `text` `size` `font_size` | 本篇 |
| `button` | 按钮 | `text` `on_click` `tooltip` `size` `icon` `font_size` | 本篇 |
| `progress` | 进度条 | `value` `label` | 本篇 |
| `image` | 图片 | `src` `size` | 本篇 |
| `toggle` | 开关 | `label` `checked` `on_change` | 本篇 |
| `slider` | 滑条 | `label` `min` `max` `step` `value` `on_change` | 本篇 |
| `input` | 文本输入 | `label` `placeholder` `text` `on_change` | 本篇 |
| `dropdown` | 下拉选择 | `label` `options` `index` `on_change` | 本篇 |
| `slot` | 物品槽（单格） | `whitelist` `size` `on_change` | [07 槽位](07-slots.md) |
| `grid_slot` | 物品槽（多格） | `whitelist` `size` `on_change` | [07 槽位](07-slots.md) |
| `spacer` | 空位占位 | `size` | [07 槽位](07-slots.md)（仅图元后端） |

通用规则：

- 所有元素都有可选 **`id`**（元素行 `type id:` 写法）。有 id 的元素才能被脚本
  `ui.set_text`/`ui.get_value` 等定位，以及作为 `on_change` 回调的 `elem` 报告值。
- 除 `slot`/`grid_slot`/`spacer` 外，全部元素在 CustomUI 后端可用。
- 属性表之外的属性一律**警告跳过**（不报错）——拼写错了不会炸，但也不生效。

## label — 文本

| 属性 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `text` | string | `""` | 文本内容；支持 `\n` 换行 |
| `size` | WxH | — | **仅图元后端**：定宽（只用 W），防折行/撑列宽 |
| `font_size` | number | `1.0` | **仅图元后端**：字号倍率 0.2..3，如 `0.7` = 70% 小字 |

脚本可随时改文本：`ui.set_text("status_lbl", "完成!")`。
多行长文本在 CustomUI 后端可配 `ui.set_pref_size`（见 [05 函数手册](../05-api-reference/09-ui.md)）撑开高度。

## button — 按钮

| 属性 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `text` | string | 元素 id | 按钮文字 |
| `on_click` | 函数名 | — | 点击回调（本包函数，0 参调用） |
| `tooltip` | string | — | 悬停提示（仅 CustomUI 后端；图元按钮忽略） |
| `size` | WxH | — | **仅图元后端**：定宽定高（长文本防折行），如 `100x18` |
| `icon` | "pack:名" | — | **仅图元后端**：图标按钮（AdvButton，图标面+文字） |
| `font_size` | number | `1.0` | **仅图元后端**：字号倍率 |

```text
button btn_take: text: "拿取", on_click: on_take, tooltip: "发 1 件到背包"
```

静态按钮的 `on_click` 回调**不带参数**——要区分"是哪个按钮"，给按钮不同函数名，
或改用动态按钮 `ui.build_button`（可传 `arg`，见 [06 动态构建](06-dynamic.md)）。

## progress — 进度条

| 属性 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `value` | number | `0` | 初始值，0..1（越界自动钳制） |
| `label` | string | — | 进度条**上方**的文字标签（构建时自动加一个前置 label） |

```text
progress brew_bar:
  value: 0.5
  label: "备货中"
```

脚本侧 `ui.set_progress("brew_bar", 0.8, "备货 80%")` 同步改数值与标签（第三参可省）。

## image — 图片

| 属性 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `src` | string | — | 图源，三种写法见下 |
| `size` | WxH | 自适应 | 显示尺寸 |

`src` 三种写法（CustomUI 后端全支持；图元后端仅支持 `pack:`）：

| 写法 | 来源 | 例 |
|---|---|---|
| `game:键` | 游戏内置 sprite（`CustomUIManager.GetSprite`） | `src: "game:coin"` |
| `mod:模组id/键` | 官方 Mod 资源 sprite | `src: "mod:core/gear"` |
| `pack:名` 或裸名 | **本包** `icons/<名>.png` 自加载 | `src: "pack:logo"` |

包图标是给物品图标用的同一套 `icons/` 目录（见
[03 · 06 图标](../03-items/06-icons.md)），像素风渲染（Point 过滤）、进程内缓存。
图源加载失败 = 警告并跳过该元素，不炸面板。

## 交互四件套 — toggle / slider / input / dropdown

这四个是"玩家可操作、值会变"的控件，统一用 `on_change` 回调（CustomUI 后端专属；
图元后端不支持，工作台面板想要"开关"得用按钮切文本模拟——gunworks 打印机正是这么做的）。

| 元素 | 特有属性 | `on_change` 收到的 value |
|---|---|---|
| `toggle` | `label` `checked`（初始开关态） | `bool` |
| `slider` | `label` `min`(0) `max`(100) `value`(0) `step` | `float` |
| `input` | `label` `placeholder` `text`（初始文本） | `string`（每次键入即触发） |
| `dropdown` | `label` `options`(数组) `index`(0) | `int`（选中项下标，从 0 起） |

`slider` 的 `step: 1`（≥1）开启整数吸附；不写或 <1 为连续滑条。

```text
toggle auto_toggle:
  label: "开店自动赠报"
  checked: false
  on_change: e6_auto_changed
slider count_slider:
  label: "每次份数"
  min: 1
  max: 5
  step: 1
  value: 1
  on_change: e6_count_changed
dropdown item_dropdown:
  label: "赠品选择"
  options: ["game:newspaper", "game:scrap_metal"]
  index: 0
  on_change: e6_item_changed
input slogan_input:
  placeholder: "自定义标语…"
  on_change: e6_slogan_changed
```

对应回调（ev = `{elem, value}`）：

```pss
func e6_count_changed(ev):
    e6_count = int(ev.value)      # slider → float
func e6_item_changed(ev):
    if ev.value == 0:             # dropdown → int 序号
        e6_item = "game:newspaper"
```

脚本回写（`ui.set_value`）**不会触发** `on_change`——防自激循环；只有玩家操作才触发。
完整读写语义见 [05 · 09 ui](../05-api-reference/09-ui.md) 的 set_value/get_value 分派表。

## slot / grid_slot / spacer（预告）

物品槽三件套只在图元树后端存在——面板里出现任何一个 `slot`，整张面板切换后端。
`slot` 的 `size: 2x2` 语义是**格数**不是像素，`whitelist` 决定能放什么——
这些差异大到值得单独一篇：[07 图元树后端与槽位](07-slots.md)。

## 双后端支持对照表（速查）

| 元素 | CustomUI 后端 | 图元树后端 |
|---|---|---|
| row / column / grid / spacer | row/column/grid ✔（spacer ✖） | 全 ✔ |
| scroll | ✔ | ✖（警告跳过） |
| label | ✔ | ✔（+size 定宽、font_size） |
| button | ✔（tooltip ✔） | ✔（+size/icon/font_size，tooltip ✖） |
| progress / toggle / slider / input / dropdown | ✔ | ✖（警告跳过，子树同跳） |
| image | ✔（game:/mod:/pack:） | 仅 `pack:` 源 |
| slot / grid_slot | ✖（不存在） | ✔（面板因此整体切换到图元后端） |

---

下一篇：[04 · 布局](04-layout.md)
