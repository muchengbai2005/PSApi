# ex24_api_ui_handles · 示例 24：界面与句柄回调

> **演示知识点**（对应文档 [09-ui.md](../../api_docs/05-api-reference/09-ui.md) ·
> [10-handles.md](../../api_docs/05-api-reference/10-handles.md) · PSUI 语法
> [02-panel-file.md](../../api_docs/06-psui/02-panel-file.md) ·
> [05-callbacks.md](../../api_docs/06-psui/05-callbacks.md)）：
> `ui.bind_key` 按键开关面板（KeyCode 名） · PSUI 面板四类基础元素
> （window/label/button/input）与缩进树语法 · `on_click`（无参）/`on_submit`
> （回车，收 `{elem, value}`）回调 · `ui.set_text` 动态改文本 ·
> `items.on_double_click` 双击句柄回调 · `items.on_target` 拖拽句柄回调 ·
> 物品句柄函数 `get_data/set_data/set_name`。

## 文件清单

```text
ex24_api_ui_handles/
├── pack.json              ← 包清单
├── items/
│   ├── mystery_box.json   ← 神秘礼盒: 双击/拖拽回调的目标
│   └── gift_tag.json      ← 礼签: 拖拽回调的源
├── ui/
│   └── demo_panel.psui    ← 1 张面板: window + label x2 + row(button x2) + input
└── events/
    └── panel.pss          ← 1 个脚本: F8 绑定 + 4 个 UI 回调 + 2 个句柄回调注册
```

## 安装

把整个 `ex24_api_ui_handles` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex24_api_ui_handles/
```

## 验证（5 分钟）

1. 进任意存档（**对局内** `ui.open` 才有效），按 **F8** 开关面板：
   再按一次关，再按又开（toggle）。
2. 面板上点「打个招呼」→ 顶部状态行变为（重启游戏后计数仍在，走 `state`）：

```text
你好! 这是第 1 次点击(state 计数, 重启也在)
```

   点「恢复默认」回到初始文本；在输入框打字回车 → 状态行变成
   `收到输入: <你输入的内容>`（输入框被框架自动清空）。
3. **F12** 领取测试物品：背包里有「神秘礼盒」+「礼签」x2（`game_loaded` 也会
   自动发一套）。在背包里**双击**神秘礼盒，控制台出现：

```text
[ex24_api_ui_handles] 双击礼盒: set_data(opened=1) + set_name 改名 →「开过的礼盒」, 祝福语 未贴签
```

   礼盒实例名变成「开过的礼盒」；再双击一次走"已经开过了"分支。
4. **按住礼签拖到神秘礼盒上**松手（`on_target`）：

```text
[ex24_api_ui_handles] 拖拽: 礼签贴上礼盒 → 祝福语已写入(岁岁平安), 礼签消耗 1 张
```

   礼盒改名「贴了签的礼盒」，礼签少 1 张。

## 逐文件讲解

### ui/demo_panel.psui —— 缩进树 + 四类元素

- 一个 `.psui` = 一张面板，唯一根必须是 `window`；面板注册名 = `包id:window的id`
  （`ex24_api_ui_handles:demo_panel`）。元素行 `type [id]:`，属性行缩进其内。
- `window`：`title/size/draggable/close_on_escape` 是最常用四属性；
  位置被玩家拖动后自动记忆。
- `row`：横排容器，两个按钮并排放；`label` 既能静态显示也能被
  `ui.set_text` 改写（元素带 id 才能被脚本定位）。
- 回调写**本包顶层函数名**（裸串）：`button.on_click` 无参调用；
  `input.on_submit` 回车触发、收 `{elem, value}`。启动期校验函数是否存在，
  拼错名字启动日志直接警告。

### events/panel.pss —— 两条主线

- **面板线**：`ui.bind_key("F8", "ex24_api_ui_handles:demo_panel")` 顶层绑定
  （KeyCode 名如 `F6`/`Slash`）。回调里 `ui.set_text("status_lbl", ...)`——
  脚本内引用**本包**元素可省包前缀；`ui.set_text` 只在面板打开后可调，
  但按钮点击时面板必然开着，天然安全。
- **句柄线**：两个注册都在**顶层**执行一次（回调表不进存档，每次加载重建）：
  - `items.on_double_click(id, fn)`：`fn` 收 1 个物品句柄。本包用
    `h.get_data("opened", 0)`（PSD_ 数据，**缺键给默认值**——F12 领的礼盒
    没有 data 也能安全走"未开"分支）、`h.set_data`、`h.set_name`
    （句柄函数全集：`get_data/set_data/set_name/set_desc/set_value/set_uses`，
    只读成员 `id/count/value/uid/name/desc/tags`）。
  - `items.on_target(src, dst, fn)`：把 A 拖到 B 上触发，`fn(h_source, h_target)`；
    `return true` 跳过原生 Target（`return false` = 放行原生）。消耗礼签用
    `items.consume(h, 1)` 部分扣减。

## 动手练习

1. 给面板加一个第二个 `input`，`on_submit` 里用 `items.give("ex24_api_ui_handles:gift_tag", 1)`
   实现"回车领一张礼签"的调试小面板。
2. 把 `ui.bind_key("F8", ...)` 改成 `"F6"`，并在 `on_msg_submit` 里把
   `a.value` 存进 `state`（`state.set("last_msg", a.value)`），重启后从面板
   显示上次输入（`on_hello_click` 里读回来拼文本）。
3. 把 `on_box_double_click` 的"已开过"分支扩成计数器：
   `h.set_data("opened", h.get_data("opened", 0) + 1)` 并在 `== 3` 时
   `h.set_value(999)` 改实例单价——观察句柄 `set_value` 的效果（范围 0..99999999）。

## 下一个示例

- [ex25_psui_basic](../ex25_psui_basic/README.md) —— API 演示系列到此收束，
  PSUI 专题系列开始：面板元素/布局/槽位的系统教学。
