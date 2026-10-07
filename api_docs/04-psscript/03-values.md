# 04-03 · 值与变量

> PSScript 是动态类型语言。本篇讲全部**值类型**、`var`/`const` 声明、
> 变量查找与作用域规则——最后一节"作用域陷阱"是新手最容易踩的坑，务必读完。

## 七种基础值

| 类型 | 字面量例子 | `type()` 返回 | 说明 |
|---|---|---|---|
| null | `null` | `"null"` | 空值；缺省返回值、缺键读值都是它 |
| bool | `true` / `false` | `"bool"` | 逻辑值 |
| int | `42`、`-7` | `"int"` | 64 位整数 |
| float | `3.14`、`-0.5` | `"float"` | 双精度浮点；**必须带小数点** |
| string | `"hi"`、`'hi'` | `"string"` | 不可变文本；见 [05 字符串](05-strings.md) |
| array | `[1, "two", [3]]` | `"array"` | 有序列表，可增删改；见 [08 容器](08-containers.md) |
| dict | `{"a": 1, "b": 2}` | `"dict"` | 字符串键 → 任意值；见 [08 容器](08-containers.md) |

另有三种**由引擎提供**的值，脚本不能创建只能接收：

| 值 | 来源 | 例子 |
|---|---|---|
| function | `func` 定义，或内置函数 | `type(len)` → `"function"` |
| namespace | 内置命名空间对象 | `log`、`state`、`time` 等（见 [05 API 参考](../05-api-reference/README.md)） |
| handle | 包装游戏对象的只读句柄 | `event.client`（客户）、`items.find()` 的返回（物品） |

数字只有 int/float 两种，没有单独的"数字字符串"之类；int 与 float 的转换规则
见 [04 运算符](04-expressions.md)。

## 声明：var 与 const

```pss
var count = 0              # 变量：可重新赋值
var items = ["sword"]      # 声明时可初始化为任意表达式
const TAX = 0.1            # 常量：必须立即初始化，之后不可赋值
const OWNER = "老猫"
```

规则：

- `var`/`const` 都是**声明语句**，在**当前环境**新建一个名字（区别于赋值，见下节）。
- `const` 必须带初始值（`const X` 单独写是编译错误）。
- 给 const 再次赋值是**运行错误**：`不能给常量 'X' 赋值`。
- 声明不是表达式：`var a = var b = 1` 不合法；一行一条语句。
- 变量名：字母/下划线开头，后跟字母/数字/下划线（`_score`、`boot_count2` 合法）。
- 大小写敏感：`TAX` 与 `tax` 是两个名字。

> 命名习惯：常量全大写（`TAX`、`PACK`），变量/函数小写下划线（`boot_count`、`describe_stock`）。
> 这是 [ex12_pss_basics](../../examples/ex12_pss_basics/README.md) 等教学示例包的统一风格。

## 未初始化与 null

PSScript **没有"声明但不赋值"**：`var x` 单独一行是编译错误（应为 `'='`）。
一个变量要么带着值出生，要么不存在。访问不存在的变量是运行错误：

```pss
log.info("{nosuchvar}")     # 运行错误: 未定义变量 'nosuchvar'
```

null 本身是一个合法值，用于表达"没有"：

```pss
var target = null           # 先占位
on customer_generated():
    target = event.client   # 事件里再填
```

## 赋值 vs 声明（关键机制）

```pss
var x = 1      # 声明: 在当前环境新建 x
x = 2          # 赋值: 沿作用域链查找名为 x 的变量, 找到就更新
```

赋值的查找规则（源码 `PsEnv.Set`）：

1. 从当前环境向外层（函数局部 → 包全局）找**已存在**的同名变量，找到即更新；
2. 一路找不到，才在**当前环境**新建。

后果：**赋值"穿透"块作用域**。`if`/`for`/`while` 块根本不开新环境（见下节），
所以不存在"块里赋值只影响块内"这回事——所有赋值要么改函数局部、要么改包全局。

## 作用域：到函数为止

PSScript 只有两层环境：

```text
包全局环境（每个包一个, 同包所有 .pss 共享）
   ▲
   └── 函数局部环境（每次调用新建一层）
```

**if / for / while / on 块都不产生新作用域**（与 GDScript 一致）。演示：

```pss
# scope_demo.pss
var total = 0

for i in range(3):        # i 直接落在包全局
    var step = i * 10     # var 也落在包全局(块不开新环境)
    total += step

log.info("i={i} step={step} total={total}")   # i=2 step=20 total=30, 全部可见
```

```pss
func counter_demo():
    var local_var = "只在函数内"
    local_var += "!"
    return local_var

log.info("{counter_demo()}")
log.info("{local_var}")    # 运行错误: 未定义变量 'local_var'
```

### 陷阱 1：函数里"忘记 var"会改掉全局

```pss
var price = 100

func bad_discount():
    price = 50            # 没写 var! 沿链找到全局 price, 直接改了

bad_discount()
log.info("{price}")       # 50 —— 全局被污染
```

想改全局这是特性；不想改这是 bug。**函数内所有临时变量都老实写 `var`。**

### 陷阱 2：循环变量是"设置"不是"声明"

`for x in ...` 对 x 做的是**赋值**（沿链查找）而非新建局部：

```pss
var name = "店主"
func show():
    for name in ["a", "b"]:    # 赋值沿链找到全局 name, 循环把它改写
        pass
    return name                # "b" —— 全局 name 已被循环覆盖

log.info(show())               # b
log.info("{name}")             # b —— 外面也变了
```

避免方式：循环变量不要与有意义的全局重名（`i`、`t` 这类"用完即弃"的名字最安全）。

### 陷阱 3：读未定义变量立刻报错，没有默认值

```pss
if false:
    never_defined = 1     # 块没执行, 变量从未创建
log.info("{never_defined}")   # 运行错误: 未定义变量 'never_defined'
```

条件分支里的声明只在**真的执行到**时才发生。

## 句柄与命名空间（概览）

脚本里还会遇到两种"像 dict 但只读"的值：

```pss
on customer_generated():
    var c = event.client          # client 句柄
    log.info("{c.name} 现金 {c.cash}")   # 属性读取
    c.set_cash(c.cash + 100)      # 方法调用(成员函数)
    # c.cash = 0                 # 运行错误: 句柄成员只读, 不能赋值
```

- **句柄（handle）**：包装真实游戏对象（客户、物品）。属性可读、方法可调、**不可赋值**，
  对象失效后访问会给友好错误。客户句柄的完整成员表见
  [05 API 参考](../05-api-reference/README.md)。
- **命名空间（namespace）**：`log`、`state`、`time` 这些内置对象，成员是函数/常量，
  同样只读（`log = 1` 报错）。

它们的共同点：**点号访问，只读**；区别于 dict（点号访问，可写，缺键返回 null）。

## 类型相关工具函数

| 函数 | 作用 | 例子 |
|---|---|---|
| `type(x)` | 返回类型名字符串 | `type(1)` → `"int"`；`type(null)` → `"null"` |
| `str(x)` | 转打印文本（与插值一致） | `str([1,2])` → `"[1, 2]"` |
| `int(x)` | 转整数 | `int(3.9)` → `3`（截断）；`int("42")` → `42`；`int(true)` → `1` |
| `float(x)` | 转浮点 | `float(2)` → `2.0`；`float("3.5")` → `3.5` |
| `bool(x)` | 转 bool（truthy 规则） | `bool("")` → `false` |

`int()` 解析字符串时先按整数、再按浮点（截断）：`int("3.7")` → `3`；
解析失败是运行错误。

## truthy：什么算"真"

`if` / `while` / `and` / `or` / `not` / `bool()` 用同一套规则：

| 值 | 真假 |
|---|---|
| `null` | false |
| `false` / `true` | 本身 |
| `0` / `0.0` | false；非零数字 true |
| `""` | false；非空字符串 true |
| `[]` / `{}` | false；非空容器 true |
| 其他（函数/命名空间/句柄） | 恒 true |

> 一个实用惯例：**"可能为 null"的值直接当条件用**。
> `if event.client:` 等价于"客户句柄存在且非空"。`if scrap != null:`
> 是更明确的写法，两者效果一致（items API 的判空用法见
> [ex21_api_items](../../examples/ex21_api_items/README.md)）。

## 类型转换对照表

```text
int   ↔ float   运算时自动：1 + 0.5 → 1.5；显式：int(0.5)→0, float(1)→1.0
any   → string  插值 / str() / "+" 拼接（见 05 篇）
string → int/float  int("42") / float("3.5")，失败报运行错误
bool  → int     int(true) → 1（极少用）
```

---

下一篇：[04 · 运算符与表达式](04-expressions.md)
