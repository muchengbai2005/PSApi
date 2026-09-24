# 09 · 从 0 到 1：做你自己的包

> 最后一篇是**工作流**：前八篇拆了三个包，现在把它们装回一条流水线——
> 从空目录到可分发的成品，每一步做什么、按什么顺序、到哪一步该看什么。
>
> 本篇是全书的"毕业路线图"，每个环节都链回对应章节。

## 路线总览

```text
① 起步        复制 example_hello, 改 pack.json, 删掉不要的
② 第一件物品  items/*.json 五件套 + F6 "测试"分类验收
③ 第一段逻辑  events/*.pss 顶层 log + 一个 day_wake
④ 逐步加料    机器 / NPC / 面板 / inject —— 一次只加一样
⑤ 数值闭环    定价账 / 套利检查 / 概率档位
⑥ 验收清单    日志行逐条对
⑦ 打包分发    目录包 or 编译 DLL
```

## ① 起步：五个决定

1. **包 id**：小写、唯一、好打（它将出现在所有物品 id 前缀 `你的id:名字`
   和日志里）。起名前搜一下别的包有没有用过。
2. **复制 example_hello 还是 gunworks 还是空目录**：
   - 只加数据（物品/配方）→ 复制 example_hello，删 machines/recipes 里多余的；
   - 做玩法（NPC/机器/剧情）→ 复制 gunworks 的**目录结构**（文件清空），
     它的 events/ 专题拆分方式值得继承；
   - 学 psapi_manager 的情况很少（纯面板工具）。
3. **pack.json**：改 `id` / `name` / `version`；`//` 键开始记你的设计账。
4. **依赖声明**：用到 v1.5+ 的机器面板 / v1.11+ 的 cond 或 buy_pool /
   v1.13 的 price 三系数时，在 README 和 `//` 里写清最低版本
   （gunworks 的写法："需要 PSApi.Events ≥ v1.11.0 + PSApi.Items ≥ v0.9.3"）。
5. **开发期全部物品标 `"test": true`** → F6 浏览器"测试"分类随拿随测，
   发布前再决定去不去掉（gunworks 全程保留）。

## ② 第一件物品

最小五件套（[03 · 物品](../03-items/02-items.md)）：

```json
{ "items": [
  { "id": "mypack:first_item",
    "name": "第一件物品",
    "value": 50,
    "shape": { "w": 1, "h": 1, "cells": [[0,0]] },
    "icon": { "file": "first_item.png" } }
] }
```

验收：重启 → F6 → "测试"分类 → 拿取。**物品进背包 = 数据面链路通**，
一半的问题（JSON 语法/图标路径/id 前缀）在这一步就暴露。

要不要 template 的判断法（[04 · 数据层](04-gunworks-data.md)第二节）：
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
玩法逻辑进 handler——[02 · 包解剖](02-anatomy.md)第三节那张表。

## ④ 逐步加料：一次只加一样

每加一个系统，先跑通它的"最小日志证据"，再做内容：

| 加什么 | 最小证据 | 内容参考 |
|---|---|---|
| 机器 | 放置→双击开窗 | [06 · 机器层](06-gunworks-machines.md) 四件套骨架 |
| NPC | `npc '...' registered` 日志行 | [05 · NPC 层](05-gunworks-npc.md) 按用途选模板 |
| 对话选项 | dialogue_choice 里 log | guide.pss / forger.pss |
| 面板 | F6 或 ui.open 打开 | [08 · psapi_manager](08-psapi-manager.md) |
| 注入 | F6 注入区出现条目 | [07 · 经济闭环](07-gunworks-economy.md) |

**改一步验一步**：三个包的 `//` 字段记录了 gunworks 十一个版本的
增量历史——每个版本只动一个主题（v0.6.0 打印体系、v0.7.0 证书、
v0.11.0 收购大修……），出问题时回退半径永远是一个主题。

## ⑤ 数值闭环

三个包留下的数值纪律：

- **定价写账**：成本率/毛利算出来写进 JSON 头注（[04 · 数据层](04-gunworks-data.md)第五节）。
- **套利检查**：每条"材料→产物"链问一遍"光倒卖赚不赚"（gunworks 空白卡
  45 的定价就是堵漏的结果）。
- **概率分档**：任何"概率出现"的东西先定档位语义（常见/中档/稀有），
  再填数字（[07 · 经济闭环](07-gunworks-economy.md)的货架档位表）；
  调参过程记进 `//`（gunworks 三轮概率调整全程留痕）。
- **上限意识**：`sell_shelf` count 上限 5、拒绝采样 guard 200、
  池权重按"该池原版权重总和的 N%"估——框架红线在 [09 附录 · 速查表](../09-appendix/03-cheatsheet.md)。

## ⑥ 验收清单

发布前逐条打勾（gunworks README 的验收节是模板）：

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

两条路（详见 [02 内容包系统 · 编译分发](../02-pack/README.md)）：

- **目录包**：整个文件夹 zip 给玩家，解压进 `UserData/PSApi/packs/`。
  开发期与朋友间测试用这个。
- **编译 DLL**：pack_compiler 把包编译成 `PSPack.<id>.dll`（资源内嵌、
  玩家不可改），放 `UserData/PSApi/packs/` 同样生效。正式发布用这个。
  注意：**纯包改动（数值/台词/psui 布局）不用重编译**——组装台 psui 头注
  的"调参区"注释就是为"改完直接跑"设计的。

发布时 README 至少包含：依赖版本、内容清单、验收提示、已知边界
（gunworks 的"面板结构变更 → 旧存档串槽"就是已知边界的范本写法）。

> [待确认] PSApi 模组的正式分发渠道（创意工坊/模组站）尚未有官方说明，
> 当前按"模组分发处"中性表述（见 [09 附录 · 待确认清单](../09-appendix/06-unresolved.md)）。

## 结语

全书到此收束。回头看推荐路径：

- **写数据**：03 章 + 本篇 ①②⑤；
- **写逻辑**：04/05 章 + 本篇 ③④；
- **做剧情**：07 章 NPC 两篇 + gunworks NPC 层带读；
- **做机器**：06 章 + gunworks 机器层带读；
- **做工具**：06 章动态构建 + psapi_manager 带读。

三个包的源文件永远是最新的参考——本书描述与磁盘实况不符时，
**以源码为准**。

---

**本章完。** 下一章：[09 · 附录](../09-appendix/README.md)
