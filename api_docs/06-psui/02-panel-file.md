# 06 · 02 面板文件格式（.psui）

> 一个 `.psui` 文件 = 一张面板。语法是一棵**缩进树**：每行一个元素或一条属性，
> 子内容缩进一级。没有括号、没有闭合标签——像 Python 一样靠缩进表达层级。
> 本篇讲透语法规则与解析器的容错行为，元素本身的含义见下一篇。

## 文件放哪、面板叫什么

```
packs/<你的包>/ui/xxx.psui
```

- 扫描**只扫 `ui/` 目录顶层**，子目录里的 `.psui` 会被忽略。
- 面板注册 id = `"包id:面板名"`。面板名取 `window` 的显式 id；没写就**默认用文件名**
  （`ui/bench.psui` 无显式 id → `my_pack:bench`）。
- 同名冲突（同包两文件抢一个面板名，或 window id 撞名）：**后者跳过**，启动日志警告。

脚本里开自己的面板可省包前缀：`ui.open("bench")` = `ui.open("my_pack:bench")`。

## 最小面板与行格式

```text
window:                       # 元素行: type [id]: [行内属性]
  title: "我的面板"            # 属性行: key: value

  label hello:                # 元素行: 带元素的 id
    text: "你好, PSUI!"       # 属性行: 缩进在 label 之内 → 是它的属性
  button ok_btn: text: "确定" # 元素行 + 行内属性 (单行写法)
```

三种行：

| 行 | 格式 | 说明 |
|---|---|---|
| 元素行 | `type [id]: [k: v, k: v, ...]` | 冒号后为空或跟行内属性 |
| 属性行 | `key: value` | 缩进在某个元素之内，归属它 |
| 注释 | `# ...` | 引号外的 `#` 到行尾；字符串里的 `#` 不算 |

**行内属性的语法陷阱（必读）**：元素行**带行内属性时必须写 id**。
解析器靠"冒号后有没有值"区分两种行——`label: "你好"` 会被解析成
"给当前元素设属性 `label`"，而不是"建一个 label 元素"。
想不写 id，冒号后就必须为空：

```text
label:
  text: "合法: 匿名元素, 属性另起一行"
label titled: text: "合法: 有 id, 行内属性"
label: text: "非法! 这是属性行, 不是元素"
```

## 缩进规则

- 空格计 1，**tab 计 4**；可以混用但换算后必须构成一致的层级。
- 子元素/属性相对父级**缩进一级**（多少都行，只要大于父级且同级一致）。
- 同级元素缩进相同；缩进回到父级层级 = 当前元素结束（没有显式闭合）。

```text
window:            # 缩进 0
  column:          # 缩进 2 → window 的子元素
    label a:       # 缩进 4 → column 的子元素
      text: "1"
    label b:       # 缩进 4 → 与 a 同级
      text: "2"
  row:             # 缩进 2 → 回到 column 的层级 → column 结束, row 是 window 的子元素
```

## 值类型

属性值共六种，全部**不写类型声明**，按字面解析：

| 写法 | 类型 | 例 |
|---|---|---|
| `"..."` | 字符串（支持 `\"` `\n` `\\` 转义） | `text: "第 {n} 行"` |
| `[a, b, c]` | 数组（元素递归解析，可嵌套） | `options: ["A", "B"]`、`whitelist: ["game:scrap_metal"]` |
| `true` / `false` | 布尔 | `draggable: false` |
| `42` / `-3` | 整数 | `columns: 3` |
| `0.75` | 小数 | `value: 0.5`、`font_size: 0.7` |
| 裸串 | 原样字符串 | `size: 420x300`、`on_click: my_func` |

裸串兜底一切：尺寸 `420x300`、函数名、图标键都不是引号串也不是数字，按原样字符串处理。
数组内逗号切分、行内属性逗号切分都会跳过引号内和方括号内的逗号。

字符串里要输出 `#` 没问题（在引号内），要输出换行用 `\n`。

## window：唯一根元素

每个文件必须有且只有一个 `window` 根，其他全部元素都在它之内。
window 前出现任何元素 = 编译错误（面板不注册）；属性不缩进在元素内同样是错误。

window 的完整属性表（解析器只认这些，其余警告跳过）：

| 属性 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `title` | string | 面板全名 | 窗口标题栏文字 |
| `size` | WxH | 自适应 | 窗口尺寸，如 `420x300` |
| `position` | "x,y" | 居中 | 初始位置；玩家拖动后有**位置记忆**（进程内），重开/重建自动恢复 |
| `draggable` | bool | `true` | 可否拖动标题栏 |
| `close_on_escape` | bool | `true` | Esc 关窗（CustomUI 后端） |
| `layer` | string | `"overlay"` | 窗口层级（CustomUI 后端） |
| `on_build` | 函数名 | — | 构建期动态填充回调，见 [06 动态构建](06-dynamic.md) |
| `on_open` | 函数名 | — | 机器绑定面板每次开窗回调，见 [08 机器面板](08-machine-panels.md) |
| `persistent` | bool | `false` | 图元面板关窗时**不**把槽内物品退回玩家（槽内容归属由你负责），见 [07 槽位](07-slots.md) |
| `spacing` | number | `0` | 图元后端顶层多元素竖排间距，见 [04 布局](04-layout.md) |

`position` 格式不对（非 `"x,y"` 两个数字）会警告并忽略，退回居中/记忆位置。

## 解析器的容错行为

设计取向是**向前兼容**——PSApi 升级加了新元素/新属性，旧版本加载新包不该炸：

| 问题 | 后果 |
|---|---|
| 未知元素 | ⚠ 警告，**该元素及其整个子树跳过** |
| 未知属性 | ⚠ 警告，该属性跳过 |
| 无法解析的行 | ⚠ 警告，该行跳过 |
| id 不是合法标识符 | ⚠ 警告，元素按匿名处理（window 则改用文件名） |
| 缺 `window` 根 / window 不唯一 / window 前有元素 / 属性悬空 | ✗ 编译错误，**整张面板不注册**（启动日志 `[psui] compile error`） |
| `on_click`/`on_change`/`on_open` 值不是合法函数名 | ⚠ 警告，触发时不生效 |

id 必须是标识符：字母或 `_` 开头，只含字母/数字/`_`。`my_btn` 合法，`my-btn`/`2b` 非法。

回调和函数的绑定校验在启动期完成：`on_click: foo` 但包里没定义 `foo` →
启动日志直接警告（不用等到点击才发现）。函数从**本包** `events/*.pss`
的全局环境查找——面板与脚本靠包 id 关联，不需要任何注册语句。

## 一个真实的面板（[examples/ex25_psui_basic](../../examples/ex25_psui_basic/README.md) 节选）

```text
window e6_panel:
  title: "E6 演示 · PSUI 面板"
  size: 420x420
  draggable: true
  close_on_escape: true

  column:
    spacing: 6
    label title_lbl:
      text: "PSUI U3 已就位: 交互控件 + on_change"
    progress progress_bar:
      value: 0.5
      label: "备货中"
    toggle auto_toggle:
      label: "开店自动赠报"
      checked: false
      on_change: e6_auto_changed
    row:
      spacing: 8
      button news_btn:
        text: "来一份"
        on_click: e6_give_news
      button close_btn:
        text: "关闭"
        on_click: e6_close_panel
```

注意三处：`window e6_panel:` 用显式 id 命名面板（也可以省略用文件名）；
`progress` 的 `label` 会渲染成进度条上方的文字标签；`on_click` 的值是**裸串函数名**
（不加引号——加了引号也是字符串，但约定俗成裸写，两种都能解析）。

## 排错速查

| 症状 | 检查 |
|---|---|
| 面板没注册，`ui.open` 报"未知面板" | 启动日志找 `[psui] compile error`；是否缺 `window` 根 |
| 面板开了但某元素不显示 | 日志找 `未知元素`/`未知属性` 警告；拼写是否对 |
| 某元素整个子树消失 | 未知元素的**子树整棵跳过**——先修父元素 |
| 按钮/回调没反应 | `on_click` 函数是否在本包 events/*.pss 定义且拼对 |
| 改了 .psui 没生效 | 面板注册在启动期；`ui.rebuild()` 或重开面板可刷新构建，新增/删除面板文件须重启 |

---

下一篇：[03 · 元素与属性](03-elements.md)
