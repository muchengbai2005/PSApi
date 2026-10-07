# 09 · 从 0 到 1：做你自己的包

> 最后一篇是**工作流**：前面 34 个示例包拆开了每个知识点，现在把它们装回
> 一条流水线——从空目录到可分发的成品，每一步做什么、按什么顺序、
> 到哪一步该看什么。
>
> 本篇是全书的"毕业路线图"，每个环节都链回对应章节与示例包。

## 路线总览

```text
① 起步        复制 ex01_hello, 改 pack.json, 删掉不要的
② 第一件物品  items/*.json 五件套 + F12 测试分类验收
③ 第一段逻辑  events/*.pss 顶层 log + 一个 day_wake
④ 逐步加料    机器 / NPC / 面板 / inject —— 一次只加一样
⑤ 数值闭环    定价账 / 套利检查 / 概率档位
⑥ 验收清单    日志行逐条对
⑦ 打包分发    目录包 or 编译 DLL
```

## ① 起步：五个决定

1. **包 id**：小写、唯一、好打（它将出现在所有物品 id 前缀 `你的id:名字`
   和日志里）。起名前搜一下别的包有没有用过。
2. **复制哪个示例包起步**：
   - 只加数据（物品/配方）→ 复制 **ex01_hello** 或 **ex02_dirs**，删不要的；
   - 做机器玩法 → 复制 **ex34_mini_mod**（数据+机器+面板+脚本的综合模板）；
   - 做 NPC/剧情 → 复制 **ex30_npc_full** 的**目录结构**（一个 NPC 一个文件
     的拆分方式值得继承）；
   - 纯面板工具 → 复制 **ex25_psui_basic**。
3. **pack.json**：改 `id` / `name` / `version`；`//` 键开始记你的设计账。
4. **依赖声明**：用到机器面板 / cond / buy_pool / price 三系数等后期特性时，
   在 README 和 `//` 里写清最低版本（"需要 PSApi ≥ v2.0.0"）。
5. **开发期全部物品标 `"test": true`** → F12 发放随拿随测，
   发布前再决定去不去掉。

## ② 第一件物品

最小五件套（[03 · 物品](../03-items/02-items.md)，完整示例见
[ex05_item_basic](../../examples/ex05_item_basic/README.md)）：

```json
{ "items": [
  { "id": "mypack:first_item",
    "name": "第一件物品",
    "value": 50,
    "shape": { "w": 1, "h": 1, "cells": [[0,0]] },
    "icon": { "file": "first_item.png" } }
] }
```

验收：重启 → F12 → 物品发到背包。**物品进背包 = 数据面链路通**，
一半的问题（JSON 语法/图标路径/id 前缀）在这一步就暴露。

要不要 template 的判断法（[ex06_item_template](../../examples/ex06_item_template/README.md)）：
要原版行为（被某类收购标签匹配）→ 写 template；纯耗材/纯展示 →
不写，tags 路由。

## ③ 第一段逻辑

```pss
# events/boot.pss
log.info("[mypack] 脚本已加载")          # 启动日志见 = 脚本链路通

on day_wake():
    log.info("[mypack] day_wake: 第 {time.rel_day()} 天")
```

两条日志分别验证**加载期顶层**与**事件 handler**。之后再往上垒：
注册类调用永远放顶层（npc.register / inject.* / shop.block_sale），
玩法逻辑进 handler——[02 · 示例包解剖](02-anatomy.md)第三节那张表。

## ④ 逐步加料：一次只加一样

每加一个系统，先跑通它的"最小日志证据"，再做内容：

| 加什么 | 最小证据 | 内容参考 |
|---|---|---|
| 机器 | 放置→双击开窗 | [ex08_machines](../../examples/ex08_machines/README.md) 三路线 |
| 配方 | 机器过夜出货 | [ex09_recipes](../../examples/ex09_recipes/README.md) |
| NPC | `npc '...' registered` 日志行 | [ex30_npc_full](../../examples/ex30_npc_full/README.md) 三型 |
| 对话选项 | dialogue_choice 里 log | ex30 的 npc_story.pss |
| 面板 | 快捷键或 ui.open 打开 | [ex25](../../examples/ex25_psui_basic/README.md) → [ex29](../../examples/ex29_psui_machine/README.md) |
| 注入 | F6 注入区出现条目 | [ex22_api_inject](../../examples/ex22_api_inject/README.md) |

**改一步验一步**：给每个版本只动一个主题（像教学包 exNN 一个主题一个包那样
切分增量），出问题时回退半径永远是一个主题。

## ⑤ 数值闭环

教学包留下的数值纪律：

- **定价写账**：成本率/毛利算出来写进 JSON 头注（`//` 字段）。
- **套利检查**：每条"材料→产物"链问一遍"光倒卖赚不赚"。
- **概率分档**：任何"概率出现"的东西先定档位语义（常见/中档/稀有），
  再填数字（ex22 的货架档位表）；调参过程记进 `//`。
- **上限意识**：`sell_shelf` count 上限 5、拒绝采样 guard 200、
  池权重按"该池原版权重总和的 N%"估——框架红线在
  [09 附录 · 速查表](../09-appendix/03-cheatsheet.md)。

## ⑥ 验收清单

发布前逐条打勾：

```text
□ 启动无 WARN/ERROR（grep MelonLoader/Latest.log）
□ 注册数对得上（N 行 npc registered / F6 注入区条目数）
□ 剧情链走一遍：每个选项分支都触发且 state 落盘
  （换存档槽重进，状态还在）
□ 机器过一夜：消耗/产出/失败重试三态都见过
□ 隔夜状态可恢复：睡觉→存档→重启→读档，机器清单无死条目
□ NPC 全员见过一面（npc eval 日志逐个 grep）
□ 旧存档兼容性：结构变了（面板/槽位）要在 README 写清迁移办法
□ game: 前缀自查：NPC/机器白名单侧用裸 id，别混写
□ base_template 对照 247 总表（07-advanced/04-npc-pipeline.md）
```

排障第一站永远是日志与 F6（[07 · 调试排障](../07-advanced/06-debugging.md)
有症状→排查对照表）。

## ⑦ 打包分发

两条路（详见 [02 内容包系统 · 编译分发](../02-pack/03-distribution.md)）：

- **目录包**：整个文件夹 zip 给玩家，解压进 `UserData/PSApi/packs/`。
  开发期与朋友间测试用这个。
- **编译 DLL**：pack_compiler 把包编译成 `PSPack.<id>.dll`（资源内嵌、
  玩家不可改），放 `UserData/PSApi/packs/` 同样生效。正式发布用这个。
  注意：**纯包改动（数值/台词/psui 布局）不用重编译**——psui 头注的
  "调参区"注释就是为"改完直接跑"设计的。

发布时 README 至少包含：依赖版本、内容清单、验收提示、已知边界
（"面板结构变更 → 旧存档串槽"这类影响存档的变更要写明迁移办法）。

> [待确认] PSApi 模组的正式分发渠道（创意工坊/模组站）尚未有官方说明，
> 当前按"模组分发处"中性表述（见 [09 附录 · 待确认清单](../09-appendix/06-unresolved.md)）。

## 结语

全书到此收束。回头看推荐路径：

- **写数据**：03 章 + ex05～ex11 + 本篇 ①②⑤；
- **写逻辑**：04/05 章 + ex12～ex24 + 本篇 ③④；
- **做剧情**：07 章 NPC 两篇 + ex30；
- **做机器**：06 章 + ex28/ex29 + ex34；
- **做工具**：06 章动态构建 + ex27 + psapi_manager 带读。

34 个示例包的源文件永远是最新的参考——本书描述与磁盘实况不符时，
**以源码为准**。

---

**本章完。** 下一章：[09 · 附录](../09-appendix/README.md)
