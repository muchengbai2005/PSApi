# ex27_psui_dynamic · 示例 27：动态构建（on_build）与自动刷新

> **演示知识点**（对应文档 [06-psui/06-dynamic.md](../../api_docs/06-psui/06-dynamic.md) ·
> [04-layout.md](../../api_docs/06-psui/04-layout.md)）：
> 静态骨架 + 带 id 动态区的标准骨架 · `on_build` 构建期命令式填充 ·
> `ui.build_begin / build_row / build_label / build_button / build_end` 函数族与显式配对 ·
> 动态按钮 `arg`（区分"是哪颗按钮"）· 数据变化 → `ui.rebuild()` 整体重建 ·
> `on tick` 定时改数据触发重建 · 空态填充防塌陷。写法参照官方面板
> `psapi_manager`（manager.psui + manager.pss）。

## 文件清单

```text
ex27_psui_dynamic/
├── pack.json              ← 包清单
├── ui/
│   └── ticker.psui        ← 1 张面板: 静态骨架 + scroll row_list 动态区
└── events/
    └── ticker.pss        ← 1 个脚本: build_panel + 4 个交互回调 + tick 自动刷新
```

## 安装

把整个 `ex27_psui_dynamic` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex27_psui_dynamic/
```

## 验证（3 分钟）

1. 启动游戏，控制台应出现：

```text
[PSApi] [psui] bind_key: F9 → ex27_psui_dynamic:ticker
[PSApi] [pss ex27_psui_dynamic] [ex27_psui_dynamic] 动态面板包就绪: 按 F9 开/关 ticker 面板 (on_build + tick 重建)
```

2. 进任意存档后按 **F9**：弹出「动态面板」窗口——滚动区里已经有
   **2 行初始数据**（它们不是写在 .psui 里的，是脚本 `build_panel` 循环生成的），
   顶部构建状态行显示「第 1 次构建 · 当前行数 2」。
3. 点 **添加一行** → 日志 `[按钮 on_click] 添加行 #3 → ui.rebuild 重建列表`，
   列表多一行且构建状态变「第 2 次构建」——每次 rebuild 都重跑 on_build。
4. 点任意行的 **删除** → 日志 `[动态按钮] 删除行 #3 (arg 区分是哪颗按钮) → rebuild`。
5. 什么也不做等 5/10/15 秒 → 每到 5 秒整自动多一行「自动行 · tick 第 N 秒生成…」：

```text
[PSApi] [pss ex27_psui_dynamic] [ex27_psui_dynamic] [tick] 数据变化 (每 5 秒) → ui.rebuild 刷新列表 (当前 5 行)
```

   而底部的 `tick: N 秒` 文本是每秒 `ui.set_text` 直改的——**不重建**。
   （列表超过 40 行时最老的行被挤出，防止无限增长。）
6. 点 **清空全部** → 列表只剩一行灰字「(空列表)」——空态也是 build 出来的。

## 逐文件讲解

### ui/ticker.psui —— "面板结构说明书"

- 动态面板的分工：**恒定元素写死**（按钮、标题、状态行），**数量不定的一次性
  元素留动态区**——`scroll row_list` 只声明 `height: 230`，内容全由脚本填。
- `window on_build: build_panel`：每次构建（open/rebuild）在 Show 之前执行。
  注意 on_build 是 **window 属性**，不是某个控件的回调。
- 带 id 的容器才能被 `ui.build_begin("row_list")` 定位——动态区必须写 id。

### events/ticker.pss —— 命令式填充与刷新工作流

- **build_panel 的三段式**：`build_begin` 进容器 → 循环 `build_row` +
  `build_label` + `build_button` + `build_end` → 最后 `build_end` 闭合容器。
  begin/row 的开闭是**显式配对**（与缩进无关），漏闭合能跑但布局乱，别依赖兜底。
- **`arg` 解决"哪个按钮"**：静态按钮 on_click 无参，循环生成几十个删除按钮必须
  靠 `ui.build_button("删除", "on_del_row", {arg = r.id})` 把行 id 塞进去，
  回调收 `{elem, arg}`。
- **刷新 = 改数据变量 → ui.rebuild()**：rebuild 会记忆窗口位置 → 关闭 →
  重开 → on_build 重跑，玩家体感是"原地刷新"。
- **tick 双轨刷新**：每秒的文本走 `ui.set_text`（便宜、不闪）；
  结构/数量变化才 `ui.rebuild()`（贵、整树重建）。这是 psapi_manager 的
  "文本直改 + 结构变化才重建"工作流的最小化复刻。
- **面板没开不碰 ui.\***：tick 里先 `ui.is_open("ticker")` 挡一道，
  否则未开面板时 rebuild/set_text 会报"没有打开的面板"。

## 动手练习

1. 把 `build_panel` 里 `ui.build_button("删除", …)` 的文字改成"移除"，观察
   rebuild 后按钮文字变化——纯包改动，不用重启游戏。
2. 把 tick 的 `% 5` 改成 `% 3`，等 3 秒看自动行节奏变化。
3. 在 `build_panel` 的循环里加一句 `ui.build_label("  ↳ 附属行")`——体会
   build_row/build_end 配对错乱后布局会怎么乱（改回来即可）。
4. 进阶：把 rows 的每项再加一个 `hp = rand(1, 100)` 字段，行文本里显示它，
   tick 里把所有 hp 都 ±5 再 rebuild——你就做出了一个"实时刷新的监视面板"。

## 下一个示例

- [ex28_psui_slots](../ex28_psui_slots/README.md) —— 物品槽 slot/grid_slot（图元树后端）
