# 04-07 · 函数

> `func` 定义函数，默认参数、闭包、"函数是值"三个特性撑起 PSScript 的全部
> 逻辑组织。本篇还包括递归限制与跨文件调用的加载顺序规则。

## 定义与调用

```pss
func greet(name):
    return "你好, {name}!"

log.info(greet("店主"))            # 你好, 店主!

func add(a, b):
    a + b                          # 没写 return → 返回 null!
log.info("{add(1, 2)}")            # null —— 忘记 return 是新手第一大坑
```

规则：

- 定义语法 `func 名字(参数表):` + 缩进块；**空函数体不合法**，至少 `pass`。
- **没有写 `return` 或 `return` 不带值 → 返回 null**。函数不会"返回最后表达式的值"。
- 调用按**位置传参**，没有关键字传参（`greet(name="x")` 是编译错误）。
- 参数个数检查：多传报 `最多 N 个参数`，少传必填报 `缺少必填参数 'x'`。

## 默认参数

```pss
func describe_stock(name, tags, discount = 0):
    var price = 120
    if discount > 0:
        price = price * (100 - discount) / 100
    return "{name} 估价 {price}"

describe_stock("赃物包", ["hot"])              # discount 用默认 0
describe_stock("赃物包", ["hot"], 25)          # discount = 25
```

- 默认值是**任意表达式**，在**每次调用缺省时求值**（不是定义时固化）。
- **默认参数之后不能再有必填参数**：`func f(a, b = 1, c)` 是编译错误。

## 返回值与提前返回

`return` 立刻结束函数。惯用法：守卫子句（guard clause）层层前置。

```pss
func try_sell(item):
    if item == null:
        return false                # 守卫: 参数不合法早退
    if item.count <= 0:
        return false
    if not has(buyers, item.id):
        return false
    # ... 正常逻辑
    return true
```

`return` 在事件块里的效果见 [09 事件与 on 块](09-events.md)（提前结束本次 handler）。

## 作用域与闭包

函数体执行时开一层**局部环境**，其父环境是**函数定义处的环境**（闭包）：

```pss
var tax = 0.1                       # 包全局

func price_of(base):
    var with_tax = base * (1 + tax) # 读闭包变量(包全局)
    return with_tax

func bump_tax():
    var tax = 0.2                   # ✗ 陷阱: var 声明了局部 tax, 遮蔽全局
    return tax                      # 返回 0.2, 全局 tax 没变
```

闭包的经典用途——**工厂函数**（返回内层函数）：

```pss
func make_counter():
    var n = 0
    func inc():                     # 函数体内可以再定义函数
        n += 1                      # 修改闭包变量
        return n
    return inc                      # 函数是值, 可以被返回

var counter = make_counter()
log.info("{counter()}")             # 1 —— 每次调用共享同一个 n
log.info("{counter()}")             # 2
```

要点：

- 函数可以定义在任何块里（if 内、循环内、函数内），名字落在**当前环境**。
- 内层函数通过闭包读写外层变量（上例的 `n` 活在 `make_counter` 的环境里）。
- 同包全局环境下，"闭包变量"通常就是包全局——这也是 handler 改全局变量的原理。

## 函数是值（一等公民）

函数可以存进变量、放进数组/字典、当参数传递。但注意：**没有函数字面量/lambda**——
`func` 是语句，必须 `func 名字(...)` 带名字定义；把函数当值传递的姿势是
**先定义再引用**（不带括号的名字就是函数值）：

```pss
func double(x):
    return x * 2

func apply(fn, v):        # 函数当参数
    return fn(v)

log.info("{apply(double, 21)}")     # 42 —— double 不加括号 = 函数值

var handler = double                # 存进变量
log.info("{handler(5)}")            # 10
```

真实案例——npc 注册时传"动态货清单函数"（example_hello e4_demo.pss 注释里的原版演示）：

```pss
func fence_stock():                 # 每次客户生成时被引擎调用
    if time.weekday() == 4:
        return ["common_ore:3:1"]
    return ["common_ore:2:1"]

npc.register({
    id = "pss_demo_fence",
    sell_items = fence_stock,       # 注意: 不加括号! 传函数本身
    ...
})
```

写成 `sell_items = fence_stock()` 就变成"注册时调一次、传返回值"——完全不同的语义。
同理 PSUI 的 `on_click = "函数名"`（字符串引用，见 [06 PSUI](../06-psui/README.md)）。

## 递归

```pss
func fact(n):
    if n <= 1:
        return 1
    return n * fact(n - 1)

log.info("{fact(10)}")              # 3628800
```

调用深度上限 **64**：超过报 `调用深度超过上限(64), 疑似无限递归`。
深度 64 意味着普通分治/递归遍历够用，但别指望深递归处理大数组——
大循环改 while。（顺带：普通循环的步数上限见 [10 错误与调试](10-errors.md)。）

## 跨文件调用与加载顺序

同包多文件共享全局环境，但顶层语句按**文件字母序**执行（见
[02 第一个脚本](02-first-script.md)）：

```text
events/
├── a_utils.pss    先执行: 定义 greet()
└── b_main.pss     后执行: 顶层可直接调 greet()
```

- **后文件顶层**可以随意调用先文件的函数。
- **先文件顶层**调用后文件的函数 → 运行错误（还没定义）。
- **任何文件的函数体/on 块里**调用任何函数都没问题（执行时全包已加载完）。

> 实践守则：`a_` 前缀放工具函数，`z_` 前缀放主逻辑；或者干脆顶层只定义、
> 逻辑全放事件里。example_hello 的命名（hello, e2~e6, m1, u4）就是"顶层只做演示输出，
> 实际逻辑全在 on 块"的风格。

## 函数速查表

| 特性 | 支持 | 备注 |
|---|---|---|
| 位置参数 | ✓ | 唯一传参方式 |
| 默认参数 | ✓ | 默认值每次调用时求值 |
| 关键字传参 | ✗ | `f(a=1)` 非法 |
| 可变参数 | ✗ | 固定 arity（参数个数上限检查） |
| 匿名函数/lambda | ✗ | func 必须有名字 |
| 嵌套函数 | ✓ | 带闭包 |
| 递归 | ✓ | 深度 ≤ 64 |
| 函数当值 | ✓ | 名字不带括号即是函数对象 |
| 返回多值 | ✗ | 返回数组模拟：`return [a, b]` |

---

下一篇：[08 · 数组与字典](08-containers.md)
