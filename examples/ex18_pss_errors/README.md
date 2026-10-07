# ex18_pss_errors · 示例 18：错误与排障（PSScript 语言篇 7/7）

> **演示知识点**（对应文档 [04-psscript/10-errors.md](../../api_docs/04-psscript/10-errors.md)）：
> 编译错误 vs 运行错误的格式与后果 · `文件:行:列` 定位 · 错误汇总文件
> `UserData/PSApi/logs/pack_errors_*.log` · 防御性写法（判零/判 null/判索引）·
> 调试三问（type/值/len）· 三个日志级别 · 两道熔断（10 万步/64 层）。
> PSScript **没有 try/catch**，所以不演示——防线是"动手前先判"。

## 文件清单

```text
ex18_pss_errors/
├── pack.json              ← 包清单: id/name/version/...
└── events/
    ├── demo.pss           ← 正常运行: 防御性写法 + 调试套路, 8 行日志
    └── errors_commented.pss ← 错误示例 5 组, 全部用 # 注释 (去 # 亲眼看报错)
```

## 安装

把整个 `ex18_pss_errors` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex18_pss_errors/
```

## 验证（3 分钟）

1. 启动游戏，控制台应出现（共 9 行 `[ex18_pss_errors]` 前缀日志）：

```text
[PSApi] [pss ex18_pss_errors] [ex18_pss_errors] 调试三问: type=array  值=[rifle, medkit, scrap]  len=3
[PSApi] [pss ex18_pss_errors] [ex18_pss_errors] safe_div(7, 0) = null — 守卫返回 null, 不报错
[PSApi] [pss ex18_pss_errors] [ex18_pss_errors] errors_commented.pss 已加载 — 错误示例全部注释中, 去 # 亲自试
```

2. 确认**零错误零警告**：控制台搜 `compile error` 与 `未知事件` 都应无结果。

## 逐文件讲解

### events/demo.pss —— 不出错的惯用姿势

- **调试三问**：`type(x)` / 打印值 / `len(x)`——90% 的运行错误是"以为 A 类型实际是 B"。
- **除零守卫**：`safe_div` 先判 `b == 0` 再除——`1/0` 是运行错误 `除以零`，不是 inf。
- **null 判空**：`x != null and x.count > 0`——短路保证左边为假时右边不执行。
- **索引守卫**：越界索引是运行错误 `索引越界: N(长度 M)`，取值前先比 `len`。
- **日志三级**：`log.info` / `log.warn`(黄) / `log.err`(红)，全落 `MelonLoader/Latest.log`。

### events/errors_commented.pss —— 亲手触发五类错误

| 示例 | 类型 | 控制台长这样（行:列 以实际为准） | 后果 |
|---|---|---|---|
| 缺冒号 | 编译 | `...errors_commented.pss:26:8: 应为 ':', 实为 标识符 'log' (该文件未加载)` | **整个文件不加载** |
| 缩进 2 空格 | 编译 | `空格缩进须为 4 的倍数(当前 2 个)` | 同上 |
| 调未定义函数 | 运行 | `...:58 in 顶层: 未定义变量 'teleport_home'` | 只断本文件顶层 |
| 未定义变量 | 运行 | `...:65 in 顶层: 未定义变量 'nosuchvar'` | 同上 |
| 除以零 | 运行 | `...:72 in 顶层: 除以零` | 同上 |

排障要点（详见文档 10 篇）：

- **编译错误**报 `文件:行:列`，指向出错 token（错误常出在上一行末尾）；
  一个文件只报第一个错，修一个重启动再看下一个；汇总同时写
  `UserData/PSApi/logs/pack_errors_*.log`。
- **运行错误**被引擎 catch 后打日志**不断链**：顶层只断本文件，事件只断本次触发，
  其他文件与其他订阅者照常——反复出现的同一条错误通常是事件驱动 bug 的特征。
- **熔断**：单段执行超 100,000 步报"疑似死循环"；递归超 64 层报"疑似无限递归"。
  触发熔断只终止该段，游戏本体不受影响。

## 动手练习

1. **核心练习**：每次只去掉一个示例的 `#` 重启，对照上表看报错格式；
   解开"缺冒号"时，观察 `errors_commented.pss 已加载` 这行日志是否消失（该文件未加载）。
2. 把除零示例修成 `safe_div` 风格（先判零），让错误消失。
3. 在 demo.pss 里故意写 `data[99]`，用"调试三问"思路定位并修掉。

## 下一个示例

PSScript 语言篇（ex12–ex18）到此完结——值、字符串、控制流、函数、容器、事件、错误
七篇凑齐了语言的全貌。接下来回到 [examples 目录](../) 浏览游戏 API 专题示例
（state 存档、items 物品、npc、PSUI 界面等）。
