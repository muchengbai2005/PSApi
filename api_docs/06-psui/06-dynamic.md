# 06 · 06 动态构建（on_build 与 build_*）

> 静态 `.psui` 骨架写死了"面板长什么样"，但列表内容、每行按钮、分页页码
> 这些**运行时才知道**的东西怎么办？答案是 `on_build`：构建期（Show 之前）
> 跑一段脚本，用 `ui.build_*` 函数族往骨架里**命令式填充**。
> 这是 PSUI 里最接近"写代码画界面"的部分。

## 模型：静态骨架 + 动态区

`.psui` 里留**带 id 的容器**（row/column/grid/scroll），`window` 声明 `on_build`：

```text
window manager:
  title: "PSApi 管理面板 · F6"
  size: 780x960
  on_build: build_panel
  # ...
  scroll browse_list:        # 动态区 1: 物品列表
    height: 300
  scroll inject_list:        # 动态区 2: 注入注册
    height: 140
  scroll queue_list:         # 动态区 3: 客户队列
    height: 180
```

`events/*.pss` 里实现 `build_panel`——它在你每次 `ui.open`/`ui.rebuild` 时
于**构建期**执行（面板还没显示，builder 还可用）：

```pss
func build_panel():
    refresh_texts()      # 静态区元素 ui.set_text (构建期也可以!)
    fill_browser()       # 动态区填充 (build_*)
    fill_inject()
    fill_client()
    fill_queue()
```

## build_* 函数族

| 函数 | 签名 | 说明 |
|---|---|---|
| 进入容器 | `ui.build_begin(container_id)` | 推进**静态树**里带 id 的容器（row/column/grid/scroll），后续填充落入其中；**必须**配 `ui.build_end()` |
| 开横排 | `ui.build_row([spacing=4])` | 开动态 row；配 `build_end` |
| 开竖排 | `ui.build_column([spacing=4])` | 开动态 column；配 `build_end` |
| 开滚动 | `ui.build_scroll(height)` | 开动态 scroll；配 `build_end` |
| 闭合 | `ui.build_end()` | 闭合最近一个未闭合的 build_begin/row/column/scroll |
| 加标签 | `ui.build_label(text[, {id, tooltip}]) → id` | 返回元素 id（没给 id 自动生成 `__dyn1`、`__dyn2`…） |
| 加按钮 | `ui.build_button(text, fn名[, {id, arg, tooltip}]) → id` | **动态按钮可带 arg**，点击时 fn 收 `{elem, arg}` |

**构建期红线**：`build_*` 只能在 `on_build` 回调里调。
在别处（事件回调、tick）调用直接报错
`仅可在面板的 on_build 构建期调用(动态内容变化请 ui.rebuild 重建)`。
已经建好的面板**不能事后加元素**——想改内容：文本用 `ui.set_text`，
结构变了就 `ui.rebuild()`（关闭重开，`on_build` 重跑）。

## 实战模式：psapi_manager 的物品列表

这是管理面板 `fill_browser` 的真实逻辑（节选）——列表 + 分页 + 每行"拿取"按钮：

```pss
PAGE_SIZE = 14

func fill_browser():
    var r = items.browse(g_cat, g_applied, g_page, PAGE_SIZE)   # 查询一页物品
    ui.build_begin("browse_list")          # 进入静态骨架的滚动区
    if len(r.items) == 0:
        ui.build_label("(无匹配物品) 换个分类或搜索词试试")
    else:
        for it in r.items:
            ui.build_row()                 # 每行: 横排
            ui.build_label("{it.name} [{cat_label(it.cat)}]{val} · {it.id}")
            ui.build_button("拿取", "on_take",
                {arg = it.id, tooltip = "发 1 件到背包(后仓)"})
            ui.build_end()                 # 闭合 row
    ui.build_end()                         # 闭合 browse_list

func on_take(ev):                          # 动态按钮回调: ev = {elem, arg}
    var ok = items.give(ev.arg, 1)
    log.info("[manager] 拿取 {ev.arg}: {ok}")
```

三个值得吸收的细节：

1. **`arg` 解决"哪个按钮"**：静态 `on_click` 无参，动态按钮把上下文塞进 `arg`
   ——物品 id、下标、整个 dict 都行（`{arg = {id = e.id, count = e.count}}` 也在用）。
2. **空态也要填**：列表空时 `build_label("(空)")`，否则滚动区高度塌陷、面板变形。
3. **`build_begin` 与 `build_row` 的配对**是缩进无关的**显式闭合**——
   for 循环里 row 开/闭要对称，漏了 `build_end` 由运行时防御性闭合兜底
   （能跑但布局乱，别依赖）。

## 刷新：ui.rebuild 的工作流

数据变了怎么刷新列表？**改数据变量 → ui.rebuild()**：

```pss
func on_next():                   # "下一页"按钮 (静态 on_click, 无参)
    g_page += 1
    ui.rebuild()                  # 关闭 → 记忆位置 → on_build 重跑 → 原位重开

func on_search_input(ev):         # 搜索框 on_change: 只存草稿
    g_search = ev.value

func on_search():                 # "搜索"按钮: 才应用过滤
    g_applied = g_search
    g_page = 0
    ui.rebuild()
```

`ui.rebuild()` 作用于本包最近打开的面板（也可传面板 id）。
玩家拖过的位置自动记忆恢复，体感是"原地刷新"。

psapi_manager 还有一个进阶手法——**tick 签名式自动刷新**：每秒比对一次
数据签名（拼个字符串比较），变了才 `ui.rebuild()`，避免无脑重建。
适合"被动展示"面板；交互频繁的面板用按钮显式触发更可控。

## 构建期还能做什么

`on_build` 期间（`_build` 上下文非空），普通 `ui.set_text`/`ui.set_progress`
也走**构建期解析**——直接作用于构建中的面板，不受"须先打开"限制
（v1.2.1 修复，之前版本此处会误报"没有打开的面板"）：

```pss
func build_panel():
    # 静态骨架上的元素: 构建期 set_text
    ui.set_text("status_lbl", "距交租: {st.day_until_rent} 天")
    ui.set_pref_size("attract_lbl", -1, 38)   # 撑开多行文本高度
    # 动态区填充
    fill_browser()
    ...
```

推荐分工：**恒定位置的元素放静态骨架**（.psui 里声明，构建期 set_text 填值），
**数量不定的一次性元素动态构建**（build_* 循环生成）——
这样 `.psui` 文件保持"面板结构说明书"的可读性。

## 图元面板的 on_build 限制

面板含 slot/grid_slot（图元树后端）时，`on_build` **被忽略**（启动期警告：
"走图元树后端, on_build 构建期填充暂不支持"）。槽位面板的结构恒定是铁律
（见 [08 机器面板](08-machine-panels.md) 的存档串槽问题），动态填充本来也不该用。

---

下一篇：[07 · 图元树后端与槽位](07-slots.md)
