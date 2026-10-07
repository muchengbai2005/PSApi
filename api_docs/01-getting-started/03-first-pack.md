# 03 · 5 分钟做出你的第一个包

> 动手篇。我们将从教学示例包 `ex01_hello` 复制出一个属于你的包 `my_first_pack`，
> 跑通"改文件 → 重启 → 验证"的完整闭环。不需要写任何新代码。
>
> 仓库 [`examples/`](../../examples/) 目录下有 34 个这样的教学示例包，
> 每个对应一个知识小节——本篇用的 `ex01_hello` 是其中最小的一个。

## ex01_hello 长什么样

它只有 4 个文件，却跑通了内容包的两个基本面：

```text
ex01_hello/
├── pack.json              ← 包清单: id/name/version/...
├── items/
│   └── greeting_card.json ← 1 个物品: 问候卡 (template=scrap_metal 兜底图标)
└── events/
    └── hello.pss          ← 1 个脚本: 启动日志 + 3 个事件订阅
```

- **数据面**：`items/greeting_card.json` 定义物品 `ex01_hello:greeting_card`
  （template 继承原版废金属的图标——**不需要任何 png 就能跑**）；
- **逻辑面**：`events/hello.pss` 顶层打一条启动日志，再订阅
  `game_loaded` / `scene_loaded` / `tick` 三个入门事件。

复制改名后，这些内容会以你的包 id 重新注册一份——足够验证整个管线。

## 第 1 步：关闭游戏

PSApi 在**游戏启动时**一次性扫描 `packs/`，运行中改动不生效（无热重载）。
先关游戏再动文件。

## 第 2 步：复制示例包

从仓库 `examples/` 拿到 `ex01_hello`（或下载仓库 zip 后解压），在游戏目录打开
PowerShell：

```powershell
# 假设仓库解压在 D:\PSApi, 游戏在 Steam 默认目录
Copy-Item -Recurse "D:\PSApi\examples\ex01_hello" `
  "D:\game\steam\steamapps\common\Probably Stolen Demo\UserData\PSApi\packs\my_first_pack"
```

> 包文件夹名建议只用**小写字母和下划线**——它将同时作为你的包 id。

## 第 3 步：改 pack.json

打开 `my_first_pack\pack.json`，只需要动 `id` 和 `name`：

```json
{
  "id": "my_first_pack",
  "name": "我的第一个包",
  "version": "0.1.0",
  "authors": ["你的名字"],
  "gameVersions": ["playtest"],
  "//": "我的第一个包: 从 ex01_hello 复制改造。"
}
```

**规则（来自加载器源码，违反即被跳过）：**

- `id` **必填**。加载器不强制它与文件夹名一致，但**强烈建议保持一致**——
  日志、冲突提示、DLL 分发名全都以 id 显示，目录名与 id 不一致只会给自己找麻烦。
- 缺 `pack.json` 或缺 `id` → 整包跳过，控制台警告 `missing pack.json` / `missing required 'id'`。
- JSON 允许 `//` 注释和尾逗号，字段名大小写不敏感。

## 第 4 步：全局替换包 id

包内所有 JSON **和 `.pss` 脚本**都用 `ex01_hello:xxx` 形式引用自家内容
（物品 id、脚本里的包名常量都是 `包id:名字` 格式），必须整体换名：

```powershell
cd "UserData\PSApi\packs\my_first_pack"
Get-ChildItem -Recurse -Include *.json,*.pss,*.psui,*.md |
  ForEach-Object {
    (Get-Content $_.FullName -Raw -Encoding UTF8) -replace 'ex01_hello', 'my_first_pack' |
      Set-Content $_.FullName -Encoding UTF8 -NoNewline
  }
```

替换覆盖三类引用，一处都别漏：

| 引用处 | 例子 |
|---|---|
| JSON 里的内容 id | `"id": "ex01_hello:greeting_card"` |
| `.pss` 脚本里的引用 | `const PACK = "ex01_hello"`、`"ex01_hello:greeting_card"` |
| README 等说明文档 | `ex01_hello` 出现的所有位置 |

> 替换后 `my_first_pack:greeting_card` 这类 id 会自动指向你的新包——id 的前半段
> 永远等于 `pack.json` 的 `id`。引用**原版**物品写裸 id（如 `scrap_metal`），
> 不受本次替换影响。

## 第 5 步：启动游戏，看日志

启动游戏，控制台应出现：

```text
[psapi] rescan(init): N pack(s), ...              ← 比之前多 1 个包
[pss my_first_pack] [my_first_pack] hello.pss 已加载 (第 1 次) — ...   ← 你的脚本在跑
```

然后打开 `UserData/PSApi/logs/` 确认**没有**新生成的 `pack_errors_*.log`
（没有文件 = 零错误；有文件就打开逐条修，通常是 JSON 手滑）。

## 第 6 步：进游戏验证

1. 进入任意存档，等日志出现 `item directory ready ...`。
2. 按 **F12**：全部 `test: true` 物品（含你的 `my_first_pack:greeting_card`）
   发到后仓背包。
3. 等 10 秒，控制台出现 `tick 计数到 10 了`（`on tick` 每秒一次）。

## 发生了什么（原理回顾）

```text
packs/my_first_pack/ ──启动扫描──▶ PackScanner 解析 pack.json
        │
        ▼ 合并去重（与 DLL 内嵌包、其他文件夹包）
  qualities → items → recipes 依次解析，错误汇总进 logs/
        │
        ▼ PSApi 编译 events/*.pss（scenes/**/*.pss 同管线）
  顶层语句立即执行（所以有 hello.pss 已加载 日志）
  on 块登记到事件总线（进场景/每秒 tick 时触发）
        │
        ▼ 进入存档，游戏物品目录就绪
  全部物品注册进目录 → F12/商店/配方全面可用
```

## 改点什么试试（推荐练习）

- 把 `items/greeting_card.json` 里的 `"value": 12` 改成 `120`，重启看背包价格变化。
- 把 `events/hello.pss` 的 `tick_count == 10` 改成 `== 3`，重启看日志提前出现。
- 给 `pack.json` 的 `//` 字段追加一行你自己的设计记录——这是社区惯例的"开发日志"位。

## 下一步

- 想知道一个包**还能有哪些子目录**：看 [ex02_dirs](../../examples/ex02_dirs/README.md)
  （9 个子目录各放一个最小文件的"目录全景"示例包）。
- 想系统学字段：[03 数据面 JSON](../03-items/README.md)
- 想学写脚本：[04 PSScript 语言](../04-psscript/README.md)
- 想了解日常开发怎么高效排障：[04 · 开发工作流](04-workflow.md)
