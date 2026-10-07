# ex12_pss_basics · 示例 12：值与变量（PSScript 语言篇 1/7）

> **演示知识点**（对应文档 [04-psscript/03-values.md](../../api_docs/04-psscript/03-values.md) 与
> [04-expressions.md](../../api_docs/04-psscript/04-expressions.md)）：
> 七种基础值与 `type()` · `var`/`const` 声明 · `len()` ·
> 算术运算（`7/2=3` 的截断坑）· 比较 · `and`/`or`/`not` 短路 · 字符串 `+` 拼接 · `null` 占位惯用法。

## 文件清单

```text
ex12_pss_basics/
├── pack.json              ← 包清单: id/name/version/...
└── events/
    └── basics.pss         ← 1 个脚本: 顶层语句把每个知识点 log 一遍
```

## 安装

把整个 `ex12_pss_basics` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex12_pss_basics/
```

## 验证（3 分钟）

1. 启动游戏，打开控制台（或看 `MelonLoader/Latest.log`），应出现：

```text
[PSApi] [pss ex12_pss_basics] [ex12_pss_basics] type() 一览: int / float / bool / string / null / array / dict
[PSApi] [pss ex12_pss_basics] [ex12_pss_basics] 坑! int/int 截断: 7/2=3; 想要小数必须有一方是 float: 7/2.0=3.5
[PSApi] [pss ex12_pss_basics] [ex12_pss_basics] and/or 返回操作数本身: 1 and 2 → 2  0 or 5 → 5 (不是 true/false!)
```

2. 共 15 行 `[ex12_pss_basics]` 前缀日志，全部在启动时一次性打出——纯顶层脚本包，不进存档就能验证。

## 逐文件讲解

### pack.json —— 包的身份证

- 纯脚本包完全合法：不需要 items/ 或 icons/，`pack.json` + `events/*.pss` 就能跑。
- `"//"` 字段是惯用注释位，写一句话说明本包演示什么。

### events/basics.pss —— 15 行日志讲完"值与变量"

顶层语句（不在任何 `on` 块里）在游戏启动时顺序执行一遍，每段演示一个知识点：

- **七种值**：`int`/`float`/`bool`/`string`/`null`/`array`/`dict`，`type()` 返回类型名字符串。
  数字有无小数点决定 int 还是 float——算折扣前先想想类型。
- **var / const**：`var` 可重新赋值；`const` 必须立刻初始化且不可再赋值。
- **算术**：`int / int` 截断取整（`7/2=3`），任一方是 float 才有小数（`7/2.0=3.5`）；
  除以零是运行错误而不是 inf。
- **比较**：数字跨 int/float 按数值比（`1 == 1.0` 为 true）；字符串按码点序；
  不支持链式比较 `1<x<10`。
- **逻辑短路**：`and`/`or` 返回操作数本身（`1 and 2` → 2），`not` 恒返回 bool；
  左边为假时右边不执行——`x != null and x.count > 0` 因此安全。
- **拼接**：`+` 任一侧是 string 就把另一侧转文本（`"#" + 7` → `"#7"`）。
- **null**：先占位、事件里再填，是处理"可能没有"的标准姿势。

## 动手练习

1. 在 `const TAX = 0.1` 行后加一句 `TAX = 0.2`，重启 → 运行错误 `不能给常量 'TAX' 赋值`
   （错误怎么读详见 [ex18](../ex18_pss_errors/README.md)）。
2. 把 `bool('')` 依次改成 `bool('0')`、`bool([])`、`bool(0.0)`，对照文档的 truthy 表验证。
3. `7 / 2` 不改动数字，只用类型转换函数凑出 3.5（提示：`float(...)`）。

## 下一个示例

- [ex13_pss_strings](../ex13_pss_strings/README.md) —— 字符串插值与工具函数
