# 04 · PSScript 语言：总览

> PSScript（`.pss`）是 PS-API 的**逻辑面语言**：数据面 JSON 回答"游戏里有什么"，
> PSScript 回答"游戏运行时**做什么**"——发物品、改声望、订事件、发自定义事件、驱动界面。
> 它是一门为 modding 设计的小型脚本语言，语法手感刻意贴近 Python / GDScript：
> 缩进块、`var`/`func`/`if`/`for`、字符串插值、字典数组字面量，有 Python 基础五分钟上手。

## 本章导航

| 篇 | 主题 | 你能学到 |
|---|---|---|
| [02 第一个脚本](02-first-script.md) | 运行模型 | 文件放哪、何时执行、怎么验证生效 |
| [03 值与变量](03-values.md) | 类型系统 | 七种值、var/const、作用域与查找规则 |
| [04 运算符与表达式](04-expressions.md) | 表达式 | 算术/比较/逻辑/`in`/索引/成员访问 |
| [05 字符串与插值](05-strings.md) | 字符串 | `"{expr}"` 插值、转义、format、join/split |
| [06 控制流与缩进](06-control-flow.md) | 流程 | if/while/for/break/continue/pass、缩进规则 |
| [07 函数](07-functions.md) | 函数 | func、默认参数、闭包、函数是值 |
| [08 数组与字典](08-containers.md) | 容器 | 字面量、增删改查、引用语义 |
| [09 事件与 on 块](09-events.md) | 事件 | 15 个桥接事件全表、event 变量、bus.emit |
| [10 错误与调试](10-errors.md) | 排障 | 编译/运行错误对照、熔断限制、日志现场 |

## 三十秒语言速览

```pss
# my_pack/events/hello.pss — 放进包里启动游戏即运行
const PACK = "my_pack"
var boot_count = 0

func price_with_tax(price, rate = 0.1):
    return floor(price * (1 + rate))

var stock = ["rifle", "medkit", "scrap"]
var deal = {"buyer": "老猫", "price": 500, "done": false}

for name in stock:
    if name == "scrap":
        continue
    log.info("在售: {name}")

boot_count += 1
log.info("{PACK} 第 {boot_count} 次启动, 含税价 {price_with_tax(500)}")

on shop_opened():
    log.info("开店了, 今天是第 {event.rel_day} 天")
```

如果这段代码你能猜个八九不离十，本章你只需要精读 [09 事件与 on 块](09-events.md)
（事件是 PSScript 与其他脚本语言差异最大的部分）；否则建议按导航顺序读。

## 语言设计一览

| 方面 | 设计 | 类比 |
|---|---|---|
| 缩进 | 4 空格或 1 tab 一级，同文件禁止混用 | Python |
| 声明 | `var x = 1` / `const X = 2`（const 必须初始化） | GDScript |
| 类型 | 动态类型，七种值：null/bool/int/float/string/array/dict（+函数/命名空间/句柄） | Python |
| 除法 | `int / int` 截断取整；任一方是 float 则得 float | GDScript 2.x |
| 字符串 | 单双引号皆可，`"{expr}"` 插值 | GDScript / f-string |
| 字典 | `{"key": v}`，键也接受标识符与 `=`：`{key = v}` | GDScript |
| 作用域 | 到函数为止；if/for 块**不**开新作用域 | GDScript |
| 事件 | `on event_name():` 顶层块订阅，数据经 `event` 变量注入 | （自创） |
| 逻辑 | `and` / `or` / `not`，短路求值 | Python |
| 结尾 | 无分号，一行一条语句 | Python |

**没有的东西**（写惯 C/JS 的注意）：没有 `;`、没有 `++`/`--`、没有三元 `?:`、
没有位运算、没有 switch/match、没有关键字传参（调用只按位置）、没有类（class）——
组织代码的单位是**函数 + 包全局环境**。

## 运行模型（一张图）

```text
游戏启动
  │
  ▼
PSApi 逻辑面扫描所有包的 events/**/*.pss 与 scenes/**/*.pss（子目录递归，按路径排序；`_` 开头库目录先装载）
  │
  ├─ 阶段1 编译: 词法 → 语法 → AST   ✗ 报错: 文件:行:列, 该文件整体不加载
  │
  ├─ 阶段2a 登记: 全部 on 块先订阅到 EventBus（先于任何顶层代码执行）
  │
  ├─ 阶段2b 执行: 逐文件运行顶层语句  ✗ 报错: 只中断当前文件
  │
  ▼
进入对局后, 事件触发 → 执行对应 on 块（event 变量注入）
```

三条铁律（每条都有专篇展开）：

1. **每包一个全局环境**：同包多文件共享变量与函数（后加载的文件能调先加载的函数），
   跨包完全隔离。→ [02 第一个脚本](02-first-script.md)
2. **on 块先登记、后执行**：所有包的所有 on 块都订阅完毕后，顶层语句才开始跑，
   所以顶层发出的 `bus.emit` 一定能被同批加载的订阅者收到。→ [09 事件与 on 块](09-events.md)
3. **错误不连坐**：一个文件编译失败不影响其他文件；一段代码运行出错只中断自己，
   不会卡死游戏。→ [10 错误与调试](10-errors.md)

## 内置能力分层

PSScript 的内置函数分两层，本章只讲**语言层**，游戏层 API 在下一章展开：

| 层 | 内容 | 本文档位置 |
|---|---|---|
| 语言层（本章） | `len/str/int/float/range/push/pop/keys/join/split/format/rand/...` 标准库，`log.*` 日志，`bus.emit` 自定义事件 | 各语法篇 + [05 字符串](05-strings.md) + [08 容器](08-containers.md) |
| 游戏层（下一章） | `state` 存档、`time/player/rep/power/crime` 查询与修改、`items/quality/machine` 物品操作、`store_event/npc/shop` 玩法注入、`ui` 界面、`inject` 物品池 | [05 内置 API 参考](../05-api-reference/README.md) |

语言层的完整函数表见 [10 错误与调试](10-errors.md) 文末附录，或随各语法篇边学边记。

## 来自真实包的例子

本章所有语法点都配有可直接运行的示例；此外对照仓库 `examples/` 目录的
语言教学包边学边跑（ex12～ex18，逐一对应本章各篇）：

| 包 | 演示内容 |
|---|---|
| [ex12_pss_basics](../../examples/ex12_pss_basics/README.md) | 类型/var/const/运算/类型转换（[03 值与变量](03-values.md)） |
| [ex13_pss_strings](../../examples/ex13_pss_strings/README.md) | 插值/转义/format/join/split（[05 字符串](05-strings.md)） |
| [ex14_pss_flow](../../examples/ex14_pss_flow/README.md) | if/elif/while/for-in/range/break/continue（[06 控制流](06-control-flow.md)） |
| [ex15_pss_funcs](../../examples/ex15_pss_funcs/README.md) | func 定义/默认参数/返回值/递归（[07 函数](07-functions.md)） |
| [ex16_pss_containers](../../examples/ex16_pss_containers/README.md) | 数组/字典/map 字面量/嵌套（[08 容器](08-containers.md)） |
| [ex17_pss_events](../../examples/ex17_pss_events/README.md) | 事件订阅：19 个别名/event 对象（[09 事件](09-events.md)） |
| [ex18_pss_errors](../../examples/ex18_pss_errors/README.md) | 错误与防御：判空/越界/调试三问（[10 错误](10-errors.md)） |

游戏层 API（state/items/npc/ui...）对应的示例包（ex19～ex24）见
[05 内置 API 参考](../05-api-reference/README.md)与
[08 实战示例包](../08-examples/README.md)总导航。

---

下一篇：[02 · 第一个脚本与运行模型](02-first-script.md)
