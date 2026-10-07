# ex16_pss_containers · 示例 16：数组与字典（PSScript 语言篇 5/7）

> **演示知识点**（对应文档 [04-psscript/08-containers.md](../../api_docs/04-psscript/08-containers.md)）：
> list 的 `push`/`pop`/索引/负索引/`remove`/`len` · map 两种键写法（`{"k": v}` 与 `k = v`）·
> `["k"]` 与 `.k` 两种读/写 · 缺键宽容返回 null · `keys`/`values`/`has` ·
> 嵌套容器链式访问 · 引用语义与按引用 `==` · `for` 遍历两种容器。

## 文件清单

```text
ex16_pss_containers/
├── pack.json              ← 包清单: id/name/version/...
└── events/
    └── containers.pss      ← 1 个脚本: list/map/嵌套/引用/遍历各演一段
```

## 安装

把整个 `ex16_pss_containers` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex16_pss_containers/
```

## 验证（3 分钟）

1. 启动游戏，打开控制台（或看 `MelonLoader/Latest.log`），应出现：

```text
[PSApi] [pss ex16_pss_containers] [ex16_pss_containers] 两种键写法等价: deal['buyer'] = 老猫  d2.buyer = 博士
[PSApi] [pss ex16_pss_containers] [ex16_pss_containers] 缺键宽容: deal.note = null (返回 null 不报错 — 读 event 全靠这个)
[PSApi] [pss ex16_pss_containers] [ex16_pss_containers] == 按引用比: a == b (同一对象) → true; [1,2] == [1,2] → false
```

2. 共 22 行 `[ex16_pss_containers]` 前缀日志，全部启动时打出。

## 逐文件讲解

### events/containers.pss —— 两种容器全覆盖

- **list**：`push` 尾部追加、`pop` 弹末尾（空数组返回 null）、索引读写、
  **负索引**从尾数（`stock[-1]`）、`remove` 按值删第一个匹配、`+` 拼接产生**新数组**。
  没有切片/insert/sort，需要就 for 手写。
- **map**：键恒为字符串。三种键写法（`"k": v`、`k = v`、混用）完全等价；
  读和写都有 `d["k"]` 与 `d.k` 两种姿势，点号复合赋值（`deal.price += 50`）也行。
- **缺键宽容**：读不存在的键**返回 null 而不是报错**——事件 payload 是 dict，
  不同事件带的字段不同，宽松读法让你直接写 `if event.save_slot:`。
- **嵌套**：点号/方括号任意链式（`config.loot[1].count`），链式赋值同样合法。
- **引用语义**：赋值/传参传的是同一个容器——`b = a` 后 push(a) b 跟着变；
  `==` 也是按引用比，内容相同的两个数组**不相等**。想要拷贝须手工重建。
- **遍历**：`for x in list` 逐元素；`for k in map` 拿到的是**键**，值用 `d[k]` 取。

## 动手练习

1. 往 `config` 加一层 `"meta": {"author": "你"}`，用 `config.meta.author` 链式读出来。
2. 写一个 `func copy_dict(src)` 手工浅拷贝（提示：`for k in src: out[k] = src[k]`）。
3. 故意写 `stock[99]` 看运行错误 `索引越界: 99(长度 2)` 长什么样（错误详解见 [ex18](../ex18_pss_errors/README.md)）。

## 下一个示例

- [ex17_pss_events](../ex17_pss_events/README.md) —— 事件全景与自定义事件
