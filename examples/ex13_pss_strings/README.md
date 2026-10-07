# ex13_pss_strings · 示例 13：字符串与插值（PSScript 语言篇 2/7）

> **演示知识点**（对应文档 [04-psscript/05-strings.md](../../api_docs/04-psscript/05-strings.md)）：
> `{表达式}` 插值 · 外双内单引号规则 · `{{` 花括号转义与 `\n` 等转义序列 ·
> `+` 拼接 · `format("{{}}", ...)` 位置模板 · `join`/`split` 互逆 · `len`/索引 `[i]`/`in` 子串 · `str()`。

## 文件清单

```text
ex13_pss_strings/
├── pack.json              ← 包清单: id/name/version/...
└── events/
    └── strings.pss         ← 1 个脚本: 顶层把字符串工具全部 log 一遍
```

## 安装

把整个 `ex13_pss_strings` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex13_pss_strings/
```

## 验证（3 分钟）

1. 启动游戏，打开控制台（或看 `MelonLoader/Latest.log`），应出现：

```text
[PSApi] [pss ex13_pss_strings] [ex13_pss_strings] 插值: 买家 老猫 出价 500
[PSApi] [pss ex13_pss_strings] [ex13_pss_strings] 字面花括号双写: {name} 不会被当成插值
[PSApi] [pss ex13_pss_strings] [ex13_pss_strings] join(数组→字符串): 老猫、博士、警长
```

2. 共 17 行 `[ex13_pss_strings]` 前缀日志，全部启动时打出。其中"转义序列"一行在控制台里
   是真的换行 + 制表效果。

## 逐文件讲解

### events/strings.pss —— 字符串能力一览

- **插值**：字符串里 `{...}` 中的内容按表达式求值再拼进文本，函数调用、索引、逻辑运算都行；
  容器整体插值按 `[a, b]` 递归展开。
- **引号规则**：插值内还要写字符串时必须换引号——外层 `"` 内层 `'`，反过来也合法；
  内外同引号是编译错误。
- **转义**：`{{` `}}` 输出字面花括号；`\n` `\t` `\\` `\"` `\'` `\{` `\}` 有效，未知转义原样保留。
- **format**：模板是变量时用 `format()`；占位符 `{}`（顺序）与 `{0}`（索引）可引用参数。
  **注意坑**：字符串里裸 `{}` 是"空插值"编译错误，所以 format 占位要写成 `{{}}`——
  真实包 example_hello 的写法就是 `format("{{}} 掷骰 d6 得 {{}}", ...)`。
- **join/split**：数组 ↔ 字符串的桥，互为逆操作；分隔符不能是空串。
- **索引与 in**：`s[i]` 取单字符（负索引可用），`in` 判子串；`len` 按字符计。
- **str()**：与插值完全同一套"值转文本"规则，调试打印容器时常用。
- **语言边界**：没有 upper/lower/trim/replace——判断用 `in`，复杂处理放数据面。

## 动手练习

1. 把 `join(names, '、')` 的分隔符换成 `' | '`，看输出变化。
2. 用 `split("rifle:10:0.5", ":")` 拆开，取 `[1]` 用 `int()` 转成数字（进价 10）。
3. 写一行同时包含索引与函数调用的插值：`log.info("第 1 件是 {stock_names[0]}, 共 {len(stock_names)} 件")`。

## 下一个示例

- [ex14_pss_flow](../ex14_pss_flow/README.md) —— if / while / for 控制流与九九乘法表
