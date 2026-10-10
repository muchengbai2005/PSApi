# 更新日志 (Changelog)

记录 PSApi 框架与随仓库分发的官方内容包（psconsole 指令台 / psapi_manager 管理面板）的版本变更。
安装与升级说明见 [README](README.md)；完整教程见 [api_docs](api_docs/README.md)。

## [2.0.8] - 2026-10-10

v2.0.1 ~ v2.0.8 相对 v2.0.0 的增量合辑。

### 新增 · PSScript builtin（v2.0.2）

- `state.pget / pset / phas / pdel` — 跨包存档 KV，直接读写其他内容包的 state 命名空间
- `crime.list / crime.clear / crime.clear_all / crime.evidence` — 治安档案读取与清除、调查/证据进度
- `items.inventory` — 玩家库存（随身背包 + 后仓）全量物品枚举

### 新增 · 框架能力

- NPC 成交台词 `accept_sell` 通道 — "卖出"与"买入"方向的成交台词分离，注册台词时不再共用一条链（v2.0.3）
- 非抛出机器槽位库存读取 — 外出栏背包等场景下经场景能力服务救回，不再依赖异常路径（v2.0.3）

### 修复

- **对话链选项销毁**：main 通道整段替换会连旧链尾选项一起销毁，替换前先查旧链尾选项（v2.0.1）
- **PSUI 高度累加**：原生 `SetText` 测量早于字号缩放导致 label / 富文本高度只增不减，构建后按最终字号归位（v2.0.4）
- **PSUI 空文本累加**：原生钳制对空文本双向累加（越清越高），改为直清（v2.0.5）
- **NPC 待售货盗窃漏洞**：受管槽位（whitelist 注册）增加所有权闸门，拒收非玩家所有的物品（NPC 待售货），Drag 阶段不产 marker（v2.0.6）
- **模组物品显示 "Translation Error"**：原版本地化管线对未注册键返回错误字符串而非抛异常；v2.0.8 起 LocService 对模组物品 `item_` 键兜底回退文本，并在存档读取时清洗已固化的错误串（v2.0.7 / v2.0.8）

### 内容包

- **psconsole v0.1.2 → v0.2.0**：指令大扩展 20 条 — 犯罪系 `crime` ×5、gunworks 联动 `raid` ×5 + `gw` ×3（地图门禁 / 冷却 / 部件套件 / 修枪 / 流体储罐，需安装 gunworks 内容包）、原版通用 ×7（`power` / `restock` / `showcase` / `attract` / `goto` / `bag clear` / `npc rep`），共 32 条；依赖 PSApi ≥ v2.0.2

## [2.0.0] - 2026-10-07

单 dll 里程碑版本。

### 变更

- **PSApi.Items + PSApi.Events 双组件合并为单 `PSApi.dll`**：单 MelonMod 两段式初始化（数据面 → 逻辑面），命名空间保留 `PSApi.Items.*` / `PSApi.Events.*`；Harmony PatchAll 全程序集仅执行一次
- 新增 **34 个教学示例包**（`examples/`，ex01_hello ~ ex34_mini_mod），每包 README 五段结构（演示知识点 / 安装 / 验证日志 / 逐文件讲解 / 动手练习）
- 教程文档全书重写为 v2.0.0 口径（61 篇），`example_hello` / `gunworks` 引用全部替换为 examples 链接
- **gunworks 不再随仓库分发**（内容全部内嵌进 `PSPack.gunworks.dll`，仅随发布包 / Nexus 渠道分发）

## v2.0.0 之前

Events / Items 双组件形态（Events v1.x · Items v0.9.x），能力版本注记散见各包 `pack.json` 与教程正文；更早的演进史见 git 提交记录。
