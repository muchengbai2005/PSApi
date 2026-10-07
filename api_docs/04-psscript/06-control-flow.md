# 04-06 · 控制流与缩进

> if / while / for / break / continue / pass——语法与 Python 几乎一致，
> 本篇重点讲**缩进规则**（PSScript 最严格的编译检查）和 for 的三种迭代对象。

## 缩进规则（先读这个）

PSScript 用缩进表达块结构，规则比 Python 更苛刻：

1. **4 个空格 = 1 级，或 1 个 tab = 1 级**。同一文件内**禁止混用**——
   既出现空格缩进又出现 tab 缩进，直接编译错误。
2. 空格缩进必须是 **4 的倍数**（2 空格、6 空格都报错）。
3. 回退缩进必须**与某个外层块精确对齐**（不能"看起来差不多"）。
4. **空行与纯注释行不参与缩进计算**（顶格写注释完全合法）。

```pss
# 这些都会编译失败:
if true:
    log.info("4 空格")
  log.info("2 空格")        # ✗ 空格缩进须为 4 的倍数

if true:
	log.info("tab")
        log.info("8 空格")   # ✗ 混用 tab 与空格
```

> 编辑器设置：把 `.pss` 当 Python 文件配置（Tab = 4 空格、自动转空格），
> VS Code 右下角选"空格: 4"即可。所有教学示例包统一用 4 空格。

## if / elif / else

```pss
var price = 120

if price > 100:
    log.info("高价")
elif price > 50:
    log.info("中价")
elif price > 10:
    log.info("低价")
else:
    log.info("白送")
```

- 条件用 truthy 规则（[03 值与变量](03-values.md)#truthy）。
- `elif` 可多个，`else` 最多一个且在最后；两者都可省。
- 条件**不强制加括号**，复杂条件可用括号分组：`if (a or b) and c:`。

### 单行块（便捷写法）

冒号后直接跟一条简单语句，不换行缩进：

```pss
if price <= 0:
    pass
# 等价便捷式:
if price <= 0: pass

for i in range(3):
    log.info("{i}")
# 等价便捷式:
for i in range(3): log.info("{i}")
```

**单行块只能放简单语句**——`if`/`while`/`for`/`func`/`on` 这些复合语句
不能写在冒号后（编译错误：复合语句不能写在冒号后同一行）：

```pss
if a: if b: pass        # ✗ 编译错误
if a: func f(): pass    # ✗ 编译错误
```

## while

```pss
var i = 0
while true:
    if i >= 3:
        break
    log.info("i = {i}")
    i += 1
# 输出 i=0,1,2 后 break 跳出
```

- 条件每轮求值，truthy 即继续。
- `while true:` + `break` 是标准循环模式——别怕写，引擎有步数熔断保护
  （单段执行超过 10 万步报"疑似死循环"，见 [10 错误与调试](10-errors.md)）。

## for：三种可迭代对象

```pss
# 1) 数组: 逐元素
for name in ["rifle", "medkit"]:
    log.info("物品: {name}")

# 2) 字典: 迭代键(不是值!)
var deal = {"buyer": "老猫", "price": 500}
for k in deal:
    log.info("{k} = {deal[k]}")      # buyer = 老猫 / price = 500

# 3) 字符串: 逐字符
for ch in "abc":
    log.info(ch)                     # a / b / c
```

迭代其他类型（数字、null、句柄）是运行错误：`类型 int 不可迭代(for 需要 array/dict/string)`。

### range()：生成整数序列

```pss
range(5)          # [0, 1, 2, 3, 4]        终点不含
range(1, 8, 2)    # [1, 3, 5, 7]           起点, 终点, 步长
range(5, 0, -1)   # [5, 4, 3, 2, 1]        负步长倒着数
range(0)          # []                     空序列, 循环体一次不执行

for i in range(3):
    log.info("第 {i} 轮")
```

range 返回的是**真实数组**（不是惰性序列），可以 `len(range(100))`、
可以重复迭代——它就是 `[0, 1, ..., 99]`。步长为 0 是运行错误。

> 想要"带索引遍历数组"：`for i in range(len(arr)):` 然后用 `arr[i]`。

### for 迭代数组时是快照

循环开始时对数组**拍照**，循环体内增删原数组不影响本轮遍历：

```pss
var list = [1, 2, 3]
for v in list:
    push(list, v * 10)      # 边遍历边追加, 不会死循环
log.info("{len(list)}")     # 6 —— 追加的 10/20/30 不在本轮遍历里
```

（如需遍历新增元素，用 while + 手动下标。）

## break / continue / pass

```pss
for t in tags:
    if t == "hot":
        label += " [烫手]"
        continue          # 跳过本轮剩余, 进入下一个元素
    if t == "contraband":
        break             # 整个循环立刻结束
    label += " [" + t + "]"

while true:
    pass                  # pass = 什么都不做(占位, 空块要求至少一条语句)
```

- `break`/`continue` 只作用于**最内层**循环（没有标签跳转）。
- 写在循环外**编译期不拦截**（语法合法），但执行到时表现为运行错误——别这么写。
- `pass` 的用途：空块占位（`if x: pass`）、预留函数体。

## 块与作用域（回顾）

if/for/while 块**不开新作用域**——块内 `var` 声明的变量在块外依然可见，
`for` 的循环变量也会落到外层。详见 [03 值与变量](03-values.md)。快速示例：

```pss
var found = null
for it in items_list:
    if it.id == "rifle":        # it 在块外仍可用
        found = it
        break
if found != null:               # found 在 if 块里赋值, 块外可读
    log.info("找到: {found.id}")
```

这正是 PSScript 风格：**"循环找目标 → 存到外层变量 → 块后使用"** 直接写就行。

## 控制流完整速查

| 语句 | 形式 | 备注 |
|---|---|---|
| if | `if 条件:` / `elif 条件:` / `else:` | 条件 truthy 判断 |
| while | `while 条件:` | 死循环有熔断 |
| for | `for x in 可迭代:` | array/dict(键)/string/range() |
| break | `break` | 跳出最内层循环 |
| continue | `continue` | 跳到下一轮 |
| pass | `pass` | 空块占位 |
| return | `return 表达式` / `return` | 函数/事件块内用，见 [07 函数](07-functions.md) |

---

下一篇：[07 · 函数](07-functions.md)
