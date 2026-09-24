# 03 · 5 分钟做出你的第一个包

> 动手篇。我们将从官方示例包 `example_hello` 复制出一个属于你的包 `my_first_pack`，
> 跑通"改文件 → 重启 → 验证"的完整闭环。不需要写任何新代码。

## 你将得到什么

`example_hello` 是官方教学包，包含：3 台机器（熔炉/读卡器/加工机）、6 个物品、
品质层、配方、9 个演示脚本、2 个 PSUI 面板。复制改名后，这些内容会以你的包 id
重新注册一份——足够验证整个管线。

## 第 1 步：关闭游戏

PS-API 在**游戏启动时**一次性扫描 `packs/`，运行中改动不生效（无热重载）。
先关游戏再动文件。

## 第 2 步：复制示例包

在游戏目录打开 PowerShell：

```powershell
cd "UserData\PSApi\packs"
Copy-Item -Recurse example_hello my_first_pack
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
  "gameVersions": ["playtest"]
}
```

**规则（来自加载器源码，违反即被跳过）：**

- `id` **必填**。加载器不强制它与文件夹名一致，但**强烈建议保持一致**——
  日志、冲突提示、DLL 分发名全都以 id 显示，目录名与 id 不一致只会给自己找麻烦。
- 缺 `pack.json` 或缺 `id` → 整包跳过，控制台警告 `missing pack.json` / `missing required 'id'`。
- JSON 允许 `//` 注释和尾逗号，字段名大小写不敏感。

## 第 4 步：全局替换包 id

包内所有 JSON **和 `.pss` 脚本**都用 `example_hello:xxx` 形式引用自家内容
（物品 id、事件名、面板 id 都是 `包id:名字` 格式），必须整体换名：

```powershell
cd my_first_pack
Get-ChildItem -Recurse -Include *.json,*.pss,*.psui |
  ForEach-Object {
    (Get-Content $_.FullName -Raw) -replace 'example_hello', 'my_first_pack' |
      Set-Content $_.FullName -Encoding UTF8
  }
```

替换覆盖三类引用，一处都别漏：

| 引用处 | 例子 |
|---|---|
| JSON 里的内容 id | `"id": "example_hello:example_smelter"` |
| `.pss` 脚本里的引用 | `machine.find("example_hello:example_desequencer")`、`ui.open("example_hello:u4_printer")` |
| `.pss` 顶层的包名常量 | `const PACK = "example_hello"` |

> 替换后 `my_first_pack:example_smelter` 这类 id 会自动指向你的新包——id 的前半段
> 永远等于 `pack.json` 的 `id`。引用**原版**物品写裸 id（如 `scrap_metal`），
> 不受本次替换影响。

## 第 5 步：启动游戏，看日志

启动游戏，控制台应出现：

```text
[psapi] rescan(init): 4 pack(s), ...        ← 比之前多 1 个包
[pss my_first_pack] hello.pss 已加载(第 1 次): ...   ← 你的脚本在跑
```

然后打开 `UserData/PSApi/logs/` 确认**没有**新生成的 `pack_errors_*.log`
（没有文件 = 零错误；有文件就打开逐条修，通常是 JSON 手滑）。

## 第 6 步：进游戏验证

1. 进入任意存档，等日志出现 `item directory ready ...`。
2. 按 **F12**：全部自定义物品（含你的 `my_first_pack:example_smelter` 等）发到后仓。
3. 从背包放置"示例熔炉"，双击它——打开原版熔炉窗口。
4. 放入电池 + 2 个废金属，睡觉过夜——第二天产出报纸（这就是一条跨夜配方在工作）。

## 发生了什么（原理回顾）

```text
packs/my_first_pack/ ──启动扫描──▶ PackScanner 解析 pack.json
        │
        ▼ 合并去重（与 DLL 内嵌包、其他文件夹包）
  qualities → items → recipes 依次解析，错误汇总进 logs/
        │
        ▼ PSApi.Events 编译 events/*.pss
  顶层语句立即执行（所以有 hello.pss 已加载 日志）
  on 块登记到事件总线（进场景/每秒 tick 时触发）
        │
        ▼ 进入存档，游戏物品目录就绪
  全部物品注册进目录 → F12/商店/配方全面可用
```

## 改点什么试试（推荐练习）

- 把 `items/_placeholder.json` 里的 `"name": "示例净水瓶"` 改成别的名字，`"value": 12`
  改成 `120`，重启看背包里价格变化。
- 把 `recipes/smelter_tests.json` 的产出数量改一下，过夜验证。
- 打开 `events/hello.pss`，把 `log.info` 的中文文案改掉，重启在控制台找你的新文案。

## 下一步

- 想系统学字段：[03 数据面 JSON](../03-items/README.md)
- 想学写脚本：[04 PSScript 语言](../04-psscript/README.md)
- 想了解日常开发怎么高效排障：[04 · 开发工作流](04-workflow.md)
