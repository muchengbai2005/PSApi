# PS-API 开发者文档

> **PS-API** 是游戏《Probably Stolen》（Demo/playtest）的**内容包式 modding 加载器**，
> 运行在 MelonLoader 之上。它的定位对标 Minecraft 的 Forge / Fabric：
> 你不需要写 C#、不需要碰 `Mods/` 目录，只用 **JSON + PSScript 脚本 + PSUI 界面**，
> 就能向游戏注入物品、机器、配方、品质、NPC、玩法与自定义界面。
>
> 本文档面向**有编程基础的模组开发者**，以及使用 AI 辅助编程的用户。
> 所有语法、字段、函数签名均取自 `_psapi/` 真实源码，示例完整可运行。

## 当前版本

| 组件 | 版本 | 说明 |
|---|---|---|
| PSApi.Items | v0.9.3 | 数据面宿主：物品 / 机器 / 配方 / 品质 / 图标 |
| PSApi.Events | v1.13.1 | 逻辑面 + 界面面宿主：PSScript / PSUI / NPC / 注入 |
| 适配游戏版本 | playtest | Probably Stolen Demo（Steam 商店名 *Probably Stolen Demo*） |

## 文档地图

| 章节 | 内容 | 适合谁 |
|---|---|---|
| [01 入门](01-getting-started/01-intro.md) | PS-API 是什么、三面一体架构、5 分钟跑通第一个包、开发工作流与排障 | **所有人从这里开始** |
| [02 内容包系统](02-pack/README.md) | pack.json 全字段、目录规范、前置依赖、编译 DLL 分发 | 所有模组作者 |
| [03 数据面 JSON](03-items/README.md) | 物品 / 机器 / 配方 / 品质 / 图标，逐字段讲解 | 所有模组作者 |
| [04 PSScript 语言](04-psscript/README.md) | 脚本语言教程：变量、控制流、函数、容器、事件订阅 | 需要写逻辑/玩法的人 |
| [05 内置 API 参考](05-api-reference/README.md) | PSScript 全部内置函数，按主题分类 + 完整示例 | 写脚本时的查阅手册 |
| [06 PSUI 界面](06-psui/README.md) | 声明式 UI：窗口、控件、脚本绑定 | 需要自定义界面的人 |
| [07 进阶主题](07-advanced/README.md) | 存档状态、原版内容注入、自定义 NPC（完整参考级）、调试与错误对照、item_editor 工具 | 深入开发者 |
| [08 实战讲解](08-examples/README.md) | example_hello / psapi_manager / gunworks 三个真实内容包逐文件带读 | 想看完整范例的人 |
| [09 附录](09-appendix/README.md) | 原版物品 id 对照、术语表、FAQ | 查资料 |

## 推荐阅读路径

```text
新手（第一次接触 PS-API）
  01-intro → 01-setup → 01-first-pack → 01-workflow
  → 02-pack → 03-items（物品先看）→ 04-psscript（跟着敲）
  → 按需查 05-api-reference

有 modding 经验、想直接上手
  01-first-pack（5 分钟）→ 03-items 字段表 → 04/05 按需

想做剧情 / NPC / 商店玩法
  04-psscript → 05-api-reference（npc / shop / inject 篇）
  → 07-advanced（NPC 完整参考）→ 08-examples/gunworks 带读
```

## 全书约定

- **id 命名**：自定义内容一律 `包id:名字`（如 `example_hello:example_water`）；
  引用原版物品**建议一律写裸 id**（如 `scrap_metal`）——npc.* 配置侧不归一化，
  带 `game:` 前缀会查无此物并打 WARNING；物品/配方 JSON 等侧虽兼容 `game:` 前缀但非必需。
  逐系统对照表见[附录 · 速查表 §一](09-appendix/03-cheatsheet.md)。
- **代码块语言标记**：`json` = 数据面文件，`pss` = PSScript 脚本，`psui` = 界面文件。
- **PS-API 的 JSON 允许写注释**（`//` 行注释与尾逗号均可，字段名大小写不敏感），
  示例中大量使用 `"//": "..."` 字段充当说明，这是真实包中的惯用写法。
- 文档中标注 **[待确认]** 的内容表示源码中存在歧义，请以官方答复为准。
- 排障第一现场永远是 `UserData/PSApi/logs/`，详见[开发工作流](01-getting-started/04-workflow.md)。
