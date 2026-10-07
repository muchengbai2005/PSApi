# ex25_psui_basic · 示例 25：PSUI 静态面板 · 全控件一览

> **演示知识点**（对应文档 [06-psui/02-panel-file.md](../../api_docs/06-psui/02-panel-file.md) ·
> [03-elements.md](../../api_docs/06-psui/03-elements.md) ·
> [04-layout.md](../../api_docs/06-psui/04-layout.md)）：
> .psui 缩进树语法 · window 属性（title/size/draggable/close_on_escape）·
> 全部 CustomUI 控件：label / image / progress / toggle / slider / dropdown / input / button ·
> 四种布局容器 row / column / grid / scroll ·
> `ui.bind_key` 按键开关面板 · 每控件接 `on_click`/`on_change` 回调打日志。

## 文件清单

```text
ex25_psui_basic/
├── pack.json              ← 包清单
├── ui/
│   └── showcase.psui      ← 1 张面板: 全控件演示 (window id = showcase)
├── events/
│   └── showcase.pss       ← 1 个脚本: bind_key + 8 个控件回调
└── icons/
    └── test_image.png     ← image 控件图源 (教学素材, 自备: 无则该元素警告跳过)
```

## 安装

把整个 `ex25_psui_basic` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex25_psui_basic/
```

## 验证（3 分钟）

1. 启动游戏，控制台（或 `MelonLoader/Latest.log`）应出现：

```text
[PSApi] [psui] bind_key: F8 → ex25_psui_basic:showcase
[PSApi] [pss ex25_psui_basic] [ex25_psui_basic] 全控件演示包就绪: 按 F8 开/关 showcase 面板
```

2. 进任意存档后按 **F8**：弹出「PSUI 全控件演示」窗口——从上到下依次是
   标题/反馈行、图片（`icons/test_image.png` 缺失则此格空，其余照常）、
   进度条、开关、滑条、下拉框、输入框、三个按钮、2×3 网格、8 行滚动区。
3. 逐个操作，观察每步都有一行 `[控件名] 触发` 日志 + 面板"反馈"行同步变化：

```text
[PSApi] [pss ex25_psui_basic] [ex25_psui_basic] [按钮 on_click] 触发: 按钮A
[PSApi] [pss ex25_psui_basic] [ex25_psui_basic] [开关 on_change] 触发: 开
[PSApi] [pss ex25_psui_basic] [ex25_psui_basic] [滑条 on_change] 触发: 68
[PSApi] [pss ex25_psui_basic] [ex25_psui_basic] [下拉框 on_change] 触发: 序号 2
[PSApi] [pss ex25_psui_basic] [ex25_psui_basic] [输入框 on_change] 触发 (每次键入): 你好
```

   拖动滑条还能看到进度条被回调里的 `ui.set_progress` 联动刷新。
   再按 F8（或 Esc / 右上角 ×）关窗——`ui.bind_key` 是开/关切换。

## 逐文件讲解

### ui/showcase.psui —— 一张窗口塞下全部控件

- **缩进即层级**：`window showcase:` 是唯一根；元素行 `type id:`，属性缩进其内。
  元素行带行内属性必须写 id（`button btn_a: text: "按钮A"` 合法；
  `button: text: "x"` 会解析成属性行——这是速查表里的头号语法陷阱）。
- **控件属性**：`progress` 的 `label` 渲染成进度条上方文字；`slider` 的
  `step: 1` 开整数吸附；`dropdown` 的 `options` 是数组、`index` 是初始序号；
  `input` 的 `placeholder` 是灰字占位提示；`button` 的 `tooltip` 悬停显示。
- **image 图源**：`src: "pack:test_image"` 读本包 `icons/test_image.png`
  （像素风渲染、进程内缓存）。解析器只认 `src`/`size` 两个属性——
  png 不存在 = 警告并跳过该元素，面板其余部分照常。
- **容器**：`row` 横排、`grid` 按 `columns`+`cell` 入格、`scroll` 固定
  `height` 内容超出滚动；window 顶层本身就是隐式 column（所以全部元素自动竖排）。
- **为什么没有 slot/spacer/rich_label**：面板里出现 `slot`/`grid_slot`
  整张面板会切到图元树后端，而 `progress`/`toggle`/`slider`/`dropdown`/`input`/`scroll`
  只在 CustomUI 后端存在——两套后端不混用。
  `spacer`/`rich_label` 是图元后端专属控件，去 [ex28_psui_slots](../ex28_psui_slots/README.md) 看。

### events/showcase.pss —— 回调接线

- `ui.bind_key("F8", "ex25_psui_basic:showcase")` 写在**顶层**：按键绑定
  面板注册完成后才可调，框架规定顶层调用时机刚好；按下 F8 = 开/关切换（幂等）。
- 每个回调第一行 `log.info("[{PACK}] [控件名] 触发…")`——教学包的"心跳"，
  排错时先看它有没有跳出来。
- `on_change` 统一收 `ev = {elem, value}`：toggle 是 bool、slider 是 float、
  dropdown 是 int 序号（0 起）、input 是 string（**每次键入即触发**）。
- `ui.set_text("fb_lbl", …)` 定位"本包当前面板"里的元素——
  所以 `fb_lbl` 必须在 .psui 里写 id。

## 动手练习

1. 给 `showcase.psui` 的 `dropdown` 加第四个选项 `"丁"`，重启游戏看列表；
   再把脚本里 `on_dropdown` 改成 `fb("选了第 {ev.value + 1} 项")`。
2. 把 `slider` 的 `step: 1` 删掉，重启后拖滑条——回调日志的值变成连续小数。
3. 把 `image` 的 `size` 改成 `32x32`；再故意把 `src` 改成 `pack:not_exist`，
   重启看启动日志的「包图标不存在, 跳过」警告（面板不炸）。
4. 复制整个包改 id 为 `my_ui`，把三个文件里的 `ex25_psui_basic` 全替换成
   `my_ui`，F8 改 F6——你就拥有了自己的第一张全控件面板。

## 下一个示例

- [ex26_psui_callbacks](../ex26_psui_callbacks/README.md) —— 五种回调全覆盖 + 反馈闭环
