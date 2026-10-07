# ex26_psui_callbacks · 示例 26：PSUI 回调专题与反馈闭环

> **演示知识点**（对应文档 [06-psui/05-callbacks.md](../../api_docs/06-psui/05-callbacks.md)）：
> `on_click`（button，无参）· `on_change`（toggle→bool / slider→float / dropdown→序号）·
> `on_submit`（input 回车提交，仿 psconsole，回调后框架自动清空输入框）·
> `on_window_closed`（包级全局函数约定，任何路径关窗都触发）·
> 反馈闭环：回调里 `ui.set_text` / `ui.set_progress` 写回面板（点击 +1 计数、滑条联动进度条）·
> 两条红线：脚本回写不触发 on_change；input 每次键入即触发。

## 文件清单

```text
ex26_psui_callbacks/
├── pack.json              ← 包清单
├── ui/
│   └── counter.psui       ← 1 张面板: 每种控件各接一种回调
└── events/
    └── counter.pss        ← 1 个脚本: 8 个回调函数 + 状态复位
```

## 安装

把整个 `ex26_psui_callbacks` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex26_psui_callbacks/
```

## 验证（3 分钟）

1. 启动游戏，控制台应出现：

```text
[PSApi] [psui] bind_key: F7 → ex26_psui_callbacks:counter
[PSApi] [pss ex26_psui_callbacks] [ex26_psui_callbacks] 回调实验包就绪: 按 F7 开/关 counter 面板
```

2. 进任意存档后按 **F7**：弹出「回调实验室」窗口。按下面顺序操作，每步对照日志：
   - 连点 **点我 +1** 三次 → 面板计数 3，日志：

     ```text
     [PSApi] [pss ex26_psui_callbacks] [ex26_psui_callbacks] [按钮 on_click] 触发: +1 → 计数 3
     ```
   - 勾/取消 **自动模式** → `lbl_toggle` 跟着变 开/关；
   - 拖 **滑条** → 日志 `[滑条 on_change] 触发: 68`，且下方 progress 条同步走
     （回调里 `ui.set_progress` 形成闭环）；
   - 换 **下拉框** 选项 → 日志 `[下拉框 on_change] 触发: 香蕉 (序号 1)`；
   - 在输入框打字 → 每敲一个字符日志刷一行 `[输入框 on_change] 键入: 'xx'`；
     按**回车** → 日志 `[输入框 on_submit] 提交: 'xx' (回调后框架已自动清空输入框)`，
     输入框被清空、「已提交」行更新；
   - 输入 `reset` 回车 → 迷你指令生效，按钮计数清零；
   - 按 F7 / Esc / × 任意方式关窗 → 日志：

     ```text
     [PSApi] [pss ex26_psui_callbacks] [ex26_psui_callbacks] [on_window_closed] ex26_psui_callbacks:counter 已关闭 — 复位计数与草稿
     ```

     再按 F7 重开 → 计数从 0 开始（复位生效）。

## 逐文件讲解

### ui/counter.psui —— 回调写在控件属性上

- 每个控件的回调属性值是**裸写函数名**（`on_click: on_plus`），实现全在
  本包 `events/*.pss`——启动期校验：函数不存在直接警告（不用等到点击）。
- `input` 同一个控件可以同时接 `on_change`（每次键入）和 `on_submit`（回车）。
- window 上**没有** on_open——`on_open` 是机器绑定面板（`machines/*.json`
  声明 `ui: "psui"`）的每次开窗回调，普通面板不触发，见
  [ex29_psui_machine](../ex29_psui_machine/README.md)。

### events/counter.pss —— 回调实现与闭环写法

- **反馈闭环**：`on_plus` 里 `click_count += 1` 后立刻
  `ui.set_text("lbl_click", …)`；`on_slider` 里把 0-100 的滑条值除以 100 写进
  progress——"玩家操作 → 回调 → 写回面板"一圈转起来。
- **红线一**：`ui.set_progress`/`ui.set_text` 都是脚本回写，**不会**反过来触发
  任何 `on_change`——所以闭环不会自激成死循环。
- **红线二**：`on_input_draft` 每次键入都进——所以只更新草稿变量 `g_draft`；
  真正"执行"放在 `on_input_submit`（回车）里，这就是 psconsole 的
  草稿/提交双变量模式。
- **`on_window_closed(panel_id)`**：不是 psui 属性，是**包级全局函数约定**——
  只要脚本里定义了这个名字的函数，本包任何面板以任何路径关闭都会被调用，
  参数是面板全名。用它复位计数，避免"面板已关还在跑逻辑"的悬空状态。

## 动手练习

1. 给 `on_plus` 加一条 `ui.set_progress("pg_bind", click_count / 10.0, "点击 {click_count} 次")`
   ——点 10 次让进度条走满。
2. 把 `on_input_submit` 里的迷你指令再加一个 `help`：回显一段用法文本。
3. 把 `on_window_closed` 里的复位语句删掉，关窗再开——观察计数是否跨开关保留
   （变量在脚本环境里一直活着，面板静态文本每次重开才会回到初值）。
4. 故意把 .psui 里 `on_click: on_plus` 改成 `on_click: on_plus_typo`，重启游戏看
   启动日志的「函数未定义, 点击时不生效」警告——体验启动期校验。

## 下一个示例

- [ex27_psui_dynamic](../ex27_psui_dynamic/README.md) —— on_build 动态构建 + tick 定时重建
