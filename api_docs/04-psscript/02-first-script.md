# 04-02 · 第一个脚本与运行模型

> 目标：写一个最短的 `.pss`，让它在游戏启动时留下日志，并理解
> **文件放在哪、什么时候执行、如何确认生效、出错去哪看**。
> 本篇不追求语法完备（后续各篇展开），只建立正确的运行模型。

## 最小可运行脚本

在你自己的包（本章以 `my_pack` 为例，参见[内容包系统](../02-pack/README.md)创建）里建：

```text
packs/my_pack/
├── pack.json
└── events/
    └── hello.pss
```

`hello.pss` 只需一行：

```pss
log.info("hello from my_pack!")
```

启动游戏（需 MelonLoader 正常加载，参见[环境搭建](../01-getting-started/02-setup.md)），
在 `UserData/MelonLoader/Latest.log` 里应能看到：

```text
[pss my_pack] hello from my_pack!
```

这就是 PSScript 的 "Hello World"。`log.info` 会把输出写进 MelonLoader 控制台与日志，
前缀固定为 `[pss <包id>]`——**看到这个前缀，就说明你的脚本被引擎编译并执行了**。

## 文件放哪、怎么被扫到

- **位置**：`UserData/PSApi/packs/<你的包>/events/` 下、扩展名 `.pss` 的文件。
- **子目录递归**：`events/bench/craft.pss` 这样的嵌套路径也会被扫到
  （引擎按 `events/` 内相对路径的字母序加载，报错信息会带子目录前缀，
  如 `my_pack/bench/craft.pss`）。
- **文件名随意**：文件名与内容没有对应关系，纯靠你自己组织。一个包只写一个
  大文件、或按功能拆十个文件，都合法。
- **加载顺序**：包按 pack.json 的依赖序，包内按文件路径字母序。

## 两阶段加载：为什么你的 on 块"提前"生效

引擎加载所有包的脚本分两步走，这是 PSScript 最重要的运行模型：

```text
阶段 2a  先把所有包所有文件的 on 块登记到事件总线
阶段 2b  再统一执行所有文件的顶层语句（从第一个文件第一行开始）
```

带来的效果：

1. **顶层代码执行时，全部事件订阅已就位**。即使 A 包顶层 `bus.emit` 了自定义事件，
   B 包（按加载序在 A 之后）的 `on "该事件":` 也已经订阅好了，不会错过。
2. **顶层语句按"包序 × 文件字母序"逐文件执行**，前面的文件先跑完才轮到下一个文件。

> **"顶层"是什么？** 缩进为 0 的语句就是顶层语句。`on ...:` 块虽然写在顶层，
> 但它的**体**不参与顶层执行——引擎装载期已把它登记为事件回调，跑顶层时会跳过它。

## 顶层代码什么时候跑一次？

**每次游戏启动、PSApi.Events 完成加载时，每个 .pss 的顶层语句跑一次**——不是每次进对局。
（进对局、切场景由事件通知你，见 [09 事件与 on 块](09-events.md)。）

想验证"顶层只跑一次、事件每次都触发"，可以放这个脚本：

```pss
# counter.pss — 顶层跑一次, scene_loaded 每次进场景触发
var boot_count = 0
var scene_count = 0

boot_count += 1
log.info("顶层执行: 第 {boot_count} 次(仅启动时)")

on scene_loaded():
    scene_count += 1
    log.info("场景 #{scene_count}: {event.value}")
```

启动 → 日志出现一次"顶层执行"；每次切换场景（主菜单 ↔ 商店 ↔ 野外）
追加一行"场景 #N"。这段代码同时演示了：**on 块里读写的变量就是包全局变量**，
和顶层共享（作用域规则详见 [03 值与变量](03-values.md)）。

## 同包多文件 = 一个全局环境

同包的多个 `.pss` 共享**同一个全局环境**：变量、函数互通，仿佛拼成一个大文件（但保持
加载顺序——文件按字母序，后加载的能用先加载的定义的东西）：

```text
events/
├── a_utils.pss     # 先加载(字母序在前)
└── b_main.pss      # 后加载, 可以调 a_utils.pss 里的函数
```

```pss
# a_utils.pss
func greet(name):
    return "你好, {name}!"

const TAX = 0.1
```

```pss
# b_main.pss
log.info(greet("店主"))          # 调 a_utils.pss 的函数
log.info("税率 {TAX}")            # 读 a_utils.pss 的常量
```

反过来不行：`a_utils.pss` 在顶层**调用** `b_main.pss` 里定义的函数会在加载时报
`未定义变量`（那时 b_main 还没执行）。但**在 on 块或函数体里**引用则没问题——
真正执行时整个包都已加载完。这是组织多文件包的核心技巧：

> **顶层只做"定义"，跨文件调用放进函数/事件里。**

跨包则完全隔离：A 包看不见 B 包的任何变量与函数；跨包协作走 `bus.emit`
自定义事件（见 [09 事件与 on 块](09-events.md)）。

## 修改脚本后如何生效

PSScript **没有热重载**：脚本在启动时编译一次。改了 `.pss` 之后：

1. 完全退出游戏；
2. 重新启动（MelonLoader 控制台里重新出现 `[pss my_pack] ...` 即新代码生效）。

开发期的完整循环（改 → 起游戏 → 看日志 → 退出）见
[开发工作流](../01-getting-started/04-workflow.md)。

## 怎么确认脚本真的被加载了

三个现场，从轻到重：

| 现象 | 去哪看 | 说明 |
|---|---|---|
| `log.*` 输出 | `UserData/MelonLoader/Latest.log`，搜 `[pss my_pack]` | 最直接：脚本活了 |
| 加载统计 | PSApi.Events 启动日志（`selftest` / 加载统计行） | 引擎报告扫到几个文件、几个 handler |
| 编译错误 | 控制台 `[pss] compile error: ...` + `UserData/PSApi/logs/pack_errors_*.log` | 文件没加载时先查这里 |

如果你的 `log.info` 一条都没出现，按顺序排查：

1. 包目录在 `packs/` 下吗？`pack.json` 合法吗？（见[内容包系统](../02-pack/README.md)）
2. 文件扩展名是 `.pss` 且在 `events/`（或其子目录）下吗？
3. 日志里有没有 `compile error`？（语法错会让整个文件不加载，见 [10 错误与调试](10-errors.md)）

## 真实例子：example_hello 的 hello.pss

官方示例包的冒烟脚本（节选，完整文件在
`UserData/PSApi/packs/example_hello/events/hello.pss`）：

```pss
const PACK = "example_hello"
var boot_count = 0

func describe_stock(name, tags, discount = 0):
    var price = 120
    if discount > 0:
        price = price * (100 - discount) / 100
    var label = "{name}({len(tags)} 个标签)"
    for t in tags:
        if t == "hot":
            label += " [烫手]"
            continue
        label += " [" + t + "]"
    return "{label} 估价 {price}"

var stock = ["contraband", "hot", "clean"]
var picked = []
var i = 0
while true:
    if i >= len(stock):
        break
    if stock[i] != "clean":
        push(picked, stock[i])
    i += 1

log.info("hello.pss 已加载(第 {boot_count} 次): {describe_stock('赃物包', picked, 25)}")

on scene_loaded():
    log.info("进入场景: {event.value} (scene_loaded handler 生效)")
```

它一口气演示了常量、函数默认参数、if/for/while/break/continue、数组字典、
字符串插值、事件订阅——本章后续各篇就是把这里每个语法点逐一讲透。

---

下一篇：[03 · 值与变量](03-values.md)
