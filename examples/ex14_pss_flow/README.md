# ex14_pss_flow · 示例 14：控制流与循环（PSScript 语言篇 3/7）

> **演示知识点**（对应文档 [04-psscript/06-control-flow.md](../../api_docs/04-psscript/06-control-flow.md)）：
> `if`/`elif`/`else` 与单行块 · `while true` + `break` · `continue` ·
> `for-in` 遍历数组/字典（字典迭代键）· `range(start, stop, step)` · 嵌套循环（九九乘法表）·
> 缩进规则（4 空格一级，禁混 tab）。

## 文件清单

```text
ex14_pss_flow/
├── pack.json              ← 包清单: id/name/version/...
└── events/
    └── flow.pss           ← 1 个脚本: 分支/循环/嵌套循环各演一段
```

## 安装

把整个 `ex14_pss_flow` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex14_pss_flow/
```

## 验证（3 分钟）

1. 启动游戏，打开控制台（或看 `MelonLoader/Latest.log`），应出现：

```text
[PSApi] [pss ex14_pss_flow] [ex14_pss_flow] if/elif/else: 120 → 高价
[PSApi] [pss ex14_pss_flow] [ex14_pss_flow] range(5, 0, -1) = [5, 4, 3, 2, 1]  (步长可负, 倒着数)
[PSApi] [pss ex14_pss_flow] [ex14_pss_flow] 乘法表  1x2=2  2x2=4
```

2. 共 25 行 `[ex14_pss_flow]` 前缀日志：4 行 if 演示 + 1 行单行块 + while/continue 各 1 行 +
   for-in 6 行 + range 3 行 + 乘法表 9 行（最后一行是 `1x9=9 ... 9x9=81`）。

## 逐文件讲解

### events/flow.pss —— 分支与循环

- **if/elif/else**：条件用 truthy 规则；`price_level()` 演示"守卫式分层判断"——
  同一函数被 4 个价格各调一次，看到分支怎么走。
- **单行块**：`if cond: 语句` 是合法便捷式，但冒号后只能放简单语句，
  `if a: if b: pass` 是编译错误。
- **while + break**：`while true:` 加 `break` 是标准模式，单段超过 10 万步会被熔断
  报"疑似死循环"（见 ex18），所以不怕写死。
- **continue**：跳过本轮剩余进入下一轮——`1~9` 只累加奇数得 25。
- **for-in**：数组逐元素；**字典迭代的是键**，值用 `d[k]` 取；块内 `var` 声明在外层依然可见
  （PSScript 只有函数/包全局两层作用域）。
- **range**：返回真实数组，终点不含；步长可负、可省略。
- **嵌套循环**：九九乘法表——内层 `push` 拼一行、`join` 连起来，外层打一行，
  81 次循环只产生 9 行日志（输出量控制的标准手法）。

## 动手练习

1. 把乘法表外层 `range(1, 10)` 改成 `range(1, 6)`，输出变 5 行。
2. 给 `price_level` 最前面加一档 `if p > 1000: return "天价"`，再把 120 换成 1200 试试。
3. 把 `continue` 改成 `break`，奇数和从 25 变成多少？先预测再验证。

## 下一个示例

- [ex15_pss_funcs](../ex15_pss_funcs/README.md) —— 函数、默认参数与递归
