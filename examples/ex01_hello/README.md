# ex01_hello · 示例 01：你的第一个内容包

> **演示知识点**（对应文档 [01-getting-started/03-first-pack.md](../../api_docs/01-getting-started/03-first-pack.md)）：
> pack.json 清单最小写法 · items/ 物品定义 · events/ PSScript 脚本 ·
> template 模板兜底图标（不写 icon 也能跑） · F12 测试分类 ·
> 顶层语句 + `on game_loaded` / `on scene_loaded` / `on tick` 三个入门事件。

## 文件清单

```text
ex01_hello/
├── pack.json              ← 包清单: id/name/version/...
├── items/
│   └── greeting_card.json ← 1 个物品: 问候卡 (template=scrap_metal 兜底图标)
└── events/
    └── hello.pss          ← 1 个脚本: 启动日志 + 3 个事件订阅
```

## 安装

把整个 `ex01_hello` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex01_hello/
```

## 验证（3 分钟）

1. 启动游戏，打开控制台（或看 `MelonLoader/Latest.log`），应出现：

```text
[PSApi] [pss ex01_hello] [ex01_hello] hello.pss 已加载 (第 1 次) — 这是 ex01_hello 示例包
[PSApi] [pss ex01_hello] [ex01_hello] 游戏加载完成, 自定义物品 ex01_hello:greeting_card 已注册进目录
```

2. 数据面确认：日志搜 `rescan(init)`，包计数比不放本包时多 1。
3. 进任意存档后按 **F12**：`问候卡` 出现在背包（F12 发放全部 `test: true` 物品到后仓）。
4. 等约 10 秒，控制台出现 `tick 计数到 10 了`（`on tick` 每秒触发一次）。

## 逐文件讲解

### pack.json —— 包的身份证

- `id` 必填，就是内容 id 的"姓氏"（`ex01_hello:greeting_card` 的前半段）。
- 其余字段（name/version/authors/gameVersions）全可选，用于展示。
- JSON 允许 `//` 注释、尾逗号，字段名大小写不敏感（`"//"` 字段是惯用注释位）。

### items/greeting_card.json —— 数据面最小单位

- 顶层键 `items` 是数组，一个文件可以定义多个物品。
- `template: "scrap_metal"`：继承原版废金属的图标/目录/行为基线——**没有 icons/ 也能跑**。
- `test: true`：进 F12 测试分类，调试期发放物品全靠它。

### events/hello.pss —— 逻辑面最小单位

- 顶层语句（`log.info`、`load_count += 1`）在启动时执行一次。
- `on <事件名>()` 订阅事件；本包演示最常用的三个：
  `game_loaded`（游戏就绪）、`scene_loaded`（进场景，`event.value` 是场景名）、
  `tick`（每秒一次）。

## 动手练习

1. 把 `greeting_card.json` 里的 `"value": 12` 改成 `120`，重启游戏，F12 后看背包价格。
2. 把 `hello.pss` 里的 `tick_count == 10` 改成 `== 30`，观察日志变化。
3. 复制整个包，改 `pack.json` 的 `id` 为 `my_pack`，把文件里所有 `ex01_hello`
   替换成 `my_pack`——你就拥有了自己的第一个包。

## 下一个示例

- [ex02_dirs](../ex02_dirs/README.md) —— 一个包里全部 9 个子目录各放什么
