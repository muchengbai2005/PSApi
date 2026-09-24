# PSApi — Probably Stolen 内容包式 Modding 框架

> **PSApi** 是游戏《Probably Stolen》（Demo/playtest）的**内容包式 modding 加载器**，运行在 [MelonLoader](https://melonloader.com) 之上。
> 定位对标 Minecraft 的 Forge / Fabric：模组作者不需要写 C#，只用 **JSON（数据面）+ PSScript 脚本（逻辑面）+ PSUI 声明式界面（界面面）**，就能向游戏注入物品、机器、配方、品质、NPC、玩法与自定义 UI。
>
> Content-pack modding framework for *Probably Stolen* (playtest), built on MelonLoader — think Forge/Fabric, but you write JSON + scripts instead of C#.

## 组件一览

| 组件 | 版本 | 说明 |
|---|---|---|
| PSApi.Items | v0.9.3 | 数据面宿主：物品 / 机器 / 配方 / 品质 / 图标 |
| PSApi.Events | v1.13.1 | 逻辑面 + 界面面宿主：PSScript / PSUI / NPC / 注入 |
| example_hello | — | 入门示范内容包（逐文件带读见文档 08 章） |
| gunworks | — | 全特性实战内容包（枪械零件 / 工作台 / 打印机 / 分析仪） |
| psapi_manager | — | 系统包：游戏内 F6 管理面板 |
| 适配游戏版本 | playtest | *Probably Stolen Demo*（Steam） |

---

## 一、安装：每个文件夹放哪里

**游戏根目录** = Steam 安装位置下的 `Probably Stolen Demo` 目录（右键游戏 → 管理 → 浏览本地文件），例如：

```text
D:\game\steam\steamapps\common\Probably Stolen Demo\
```

### 安装映射表

| 仓库路径 | 放到哪里 | 是否必须 |
|---|---|---|
| `Mods/PSApi.Items.dll` | `<游戏根>/Mods/` | **必须**（框架本体） |
| `Mods/PSApi.Events.dll` | `<游戏根>/Mods/` | **必须**（框架本体） |
| `Mods/PSPack.gunworks.dll` | `<游戏根>/Mods/` | 可选（与 `packs/gunworks` **二选一**，见下文冲突说明） |
| `UserData/PSApi/README.md` | `<游戏根>/UserData/PSApi/` | 推荐（目录说明） |
| `UserData/PSApi/packs/example_hello/` | `<游戏根>/UserData/PSApi/packs/` | 可选（学习用） |
| `UserData/PSApi/packs/gunworks/` | `<游戏根>/UserData/PSApi/packs/` | 可选（与 `PSPack.gunworks.dll` **二选一**） |
| `UserData/PSApi/packs/psapi_manager/` | `<游戏根>/UserData/PSApi/packs/` | 推荐（F6 管理面板） |
| `_psapi/` | 不安装 | 框架 C# 源码（开发用） |
| `api_docs/` | 不安装 | 65 篇中文开发者文档 |
| `_tools/` | 不安装 | 开发工具（物品编辑器 / 内容包编译器） |

简单说：**仓库里的 `Mods/` 和 `UserData/PSApi/` 两个目录的内容，原样对应拖进游戏的同名目录即可**；其余目录（`_psapi` `api_docs` `_tools`）是开发资源，不需要放进游戏。

### 安装步骤

1. **前置**：游戏已安装 MelonLoader（IL2CPP 版）。没有的话去 [MelonLoader Releases](https://github.com/LavaGang/MelonLoader/releases) 下载安装器，选中游戏目录安装。
2. **装框架**：把 `Mods/PSApi.Items.dll` 和 `Mods/PSApi.Events.dll` 复制到 `<游戏根>/Mods/`。
3. **装内容包**（两种形态二选一，见冲突说明）：
   - **DLL 形态**（推荐普通玩家）：把 `Mods/PSPack.gunworks.dll` 复制到 `<游戏根>/Mods/`；
   - **文件夹形态**（推荐想学习/修改的作者）：把 `UserData/PSApi/packs/` 下的包目录复制到 `<游戏根>/UserData/PSApi/packs/`。
4. **验证**：启动游戏，MelonLoader 控制台出现 `PSApi.Events v1.13.1 loaded. packs=N` 即成功；游戏内按 **F6** 可打开 psapi_manager 管理面板。

---

## 二、⚠️ 冲突与常见坑（必读）

1. **gunworks 双形态冲突（最重要的坑）**
   gunworks 同时以两种形态存在于本仓库：`Mods/PSPack.gunworks.dll`（编译版）和 `UserData/PSApi/packs/gunworks/`（文件夹版）。
   **两者只能装一个**：同 id 并存时启动主菜单会弹"重复加载"冲突窗，且 **DLL 胜出、文件夹版被忽略**。
   - 普通玩家 → 装 DLL（版本固定、防误改）；
   - 想学习/魔改 → 装文件夹版，改文件重启游戏即生效（此时**不要**把 DLL 留在 `Mods/`）。

2. **两个框架 DLL 必须配对安装**
   `PSApi.Events` 硬引用 `PSApi.Items`（只装 Events 会降级甚至异常），且 Events（优先级 20）必须在 Items（优先级 10）之后加载——MelonLoader 自动保证顺序，你只需要把两个 DLL 都放进 `Mods/`。

3. **升级时清理旧版残留**
   更新版本前先删除 `Mods/` 里的旧版 `PSApi.*.dll` / `PSPack.*.dll`，新旧并存会导致重复注册与不可预期的行为。

4. **前置依赖（prerequisites）**
   内容包 `pack.json` 的 `prerequisites` 字段生效：若所依赖的包未安装，该包整体跳过加载，并在主菜单弹"缺少前置模组"冲突窗。

5. **`UserData/PSApi/state/` 是存档状态，勿手改**
   该目录按存档槽（`state/<slot>/<owner>.json`）由框架自动读写，手改会破坏玩法进度。同理 `logs/` 和 `ui/` 也是运行时产物，无需备份或分发。

6. **排障第一现场**
   出问题先看 `<游戏根>/UserData/PSApi/logs/`（含 `pack_errors`、PSScript 编译错误等），排查方法见[开发工作流文档](api_docs/01-getting-started/04-workflow.md)。

---

## 三、开发者文档（api_docs/，65 篇中文）

| 章节 | 内容 |
|---|---|
| [01 入门](api_docs/01-getting-started/01-intro.md) | PSApi 是什么、三面一体架构、5 分钟跑通第一个包、开发工作流与排障 |
| [02 内容包系统](api_docs/02-pack/README.md) | pack.json 全字段、目录规范、前置依赖、编译 DLL 分发 |
| [03 数据面 JSON](api_docs/03-items/README.md) | 物品 / 机器 / 配方 / 品质 / 图标，逐字段讲解 |
| [04 PSScript 语言](api_docs/04-psscript/README.md) | 脚本语言教程：变量、控制流、函数、容器、事件订阅 |
| [05 内置 API 参考](api_docs/05-api-reference/README.md) | PSScript 全部内置函数，按主题分类 + 完整示例 |
| [06 PSUI 界面](api_docs/06-psui/README.md) | 声明式 UI：窗口、控件、脚本绑定 |
| [07 进阶主题](api_docs/07-advanced/README.md) | 存档状态、原版内容注入、自定义 NPC（参考级）、调试排障、item_editor |
| [08 实战讲解](api_docs/08-examples/README.md) | example_hello / psapi_manager / gunworks 三个真实包逐文件带读 |
| [09 附录](api_docs/09-appendix/README.md) | 原版物品 id 对照、术语表、速查表、FAQ |

所有语法、字段、函数签名均取自 `_psapi/` 真实源码并经游戏反编译双重核对，示例完整可运行。**新手从[这里](api_docs/01-getting-started/01-intro.md)开始。**

## 四、从源码构建（_psapi/）

两个 csproj 用相对路径引用游戏侧 DLL（`..\..\MelonLoader\...`），因此**源码必须位于 `<游戏根>/_psapi/`（与 `MelonLoader/` 同级）才能编译**：

```bash
# 把 _psapi/ 复制到游戏根目录后：
cd "<游戏根>/_psapi/PSApi.Items"  && dotnet build -c Release   # 先构建 Items
cd "<游戏根>/_psapi/PSApi.Events" && dotnet build -c Release   # Events 引用 Items 的 Release 产物
```

产物在各自 `bin/Release/`，复制到 `<游戏根>/Mods/` 即可（覆盖旧版前记得先删）。

## 五、开发工具（_tools/）

| 工具 | 用法 |
|---|---|
| `item_editor/` | Python 物品编辑器：`run_editor.bat` 启动，含 gunworks 物品 id 对照表 |
| `pack_compiler/` | 内容包 → 单 DLL 编译器：`dotnet build -c Release` 后 `pack_compiler <packDir> [outDir]`，产出 `PSPack.<包id>.dll`；`--verify <dll>` 自检 |
| AssetRipper | **不入库**（可执行文件 130MB，超 GitHub 单文件限制），请自行从 [官网](https://github.com/AssetRipper/AssetRipper) 下载，用于游戏资源逆向 |

## 六、5 分钟写第一个内容包

1. 复制 `UserData/PSApi/packs/example_hello/` 整个目录为 `packs/my_pack/`；
2. 改 `pack.json` 的 `id` 为 `my_pack`（必须与目录名一致）；
3. 全局替换包内 JSON 里的 `example_hello:` → `my_pack:`；
4. 启动游戏，看 `UserData/PSApi/logs/` 没有 `pack_errors` 即加载成功。

完整教程见[5 分钟跑通第一个包](api_docs/01-getting-started/03-first-pack.md)。

---

## Credits

- 框架作者：**Research**（PSApi.Items / PSApi.Events）
- 本仓库文档（api_docs/）基于源码与游戏反编译逐字段核对编写，共 9 章 65 篇约 31 万字
- 依赖：[MelonLoader](https://github.com/LavaGang/MelonLoader)、Harmony
