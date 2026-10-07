# ex15_pss_funcs · 示例 15：函数（PSScript 语言篇 4/7）

> **演示知识点**（对应文档 [04-psscript/07-functions.md](../../api_docs/04-psscript/07-functions.md)）：
> `func` 定义与位置传参 · 默认参数 · 忘写 `return` 返回 null 的坑 ·
> 守卫子句与提前 `return` · 函数是值（存变量/当参数）· 数组模拟多返回值 ·
> 递归（**深度上限 64 层**，单段步数预算 **100,000 步**）。

## 文件清单

```text
ex15_pss_funcs/
├── pack.json              ← 包清单: id/name/version/...
└── events/
    └── funcs.pss          ← 1 个脚本: 6 个函数把函数特性各演一段
```

## 安装

把整个 `ex15_pss_funcs` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex15_pss_funcs/
```

## 验证（3 分钟）

1. 启动游戏，打开控制台（或看 `MelonLoader/Latest.log`），应出现：

```text
[PSApi] [pss ex15_pss_funcs] [ex15_pss_funcs] 忘写 return 的返回值: null (不是 3, 是 null)
[PSApi] [pss ex15_pss_funcs] [ex15_pss_funcs] 函数是值: type(plan) = function; plan() = [rifle:2, medkit:5, scrap:10]
[PSApi] [pss ex15_pss_funcs] [ex15_pss_funcs] 递归阶乘: fact(10) = 3628800
```

2. 共 9 行 `[ex15_pss_funcs]` 前缀日志，全部启动时打出。

## 逐文件讲解

### events/funcs.pss —— 函数特性一览

- **默认参数**：`greet(name, greeting = "你好")` 缺省用默认、传参则覆盖；
  默认参数之后不能再有必填参数；没有关键字传参，`f(a=1)` 是编译错误。
- **null 坑**：`forgot_return` 里最后一行是表达式但没写 `return`——函数**不会**
  "返回最后表达式的值"，一律返回 null。90% 的"函数结果拿不到"都是这个。
- **守卫子句**：`try_sell` 先把非法输入用 `return` 挡在门口，主逻辑保持扁平。
- **函数是值**：`plan = restock_plan`（不带括号）拿到的是函数对象，
  `type(plan)` 是 `function`，`plan()` 才是调用。真实场景是 `npc.register`
  时传 `sell_items = fence_stock`（传函数本身，注册后由引擎反复调用）。
- **多返回值**：语言只有单返回；惯用做法 `return [lo, hi]` 用数组模拟。
- **递归**：`fact(10)` = 3628800。深度上限 64 层（超过报 `调用深度超过上限(64), 疑似无限递归`），
  另有单段 10 万步熔断——所以深递归不可靠，大计算改 while。

## 动手练习

1. 写一个 `fib(n)` 递归（`fib(1)`、`fib(2)` 都返回 1），打印 `fib(12)` 应为 144。
2. 把 `fact(10)` 改成 `fact(100)` 重启，亲眼看深度熔断的报错格式（详见 [ex18](../ex18_pss_errors/README.md)）。
3. 再定义一个函数（比如 `clean_plan`）赋给 `plan` 变量，验证换函数不换调用方式。

## 下一个示例

- [ex16_pss_containers](../ex16_pss_containers/README.md) —— list 与 map 容器
