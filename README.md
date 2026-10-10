# PSApi — Probably Stolen 内容包式 Modding 框架

> **PSApi** 是游戏《Probably Stolen》（Demo/playtest）的**内容包式 modding 加载器**，运行在 [MelonLoader](https://melonloader.com) 之上。
> 定位对标 Minecraft 的 Forge / Fabric：模组作者不需要写 C#，只用 **JSON（数据面）+ PSScript 脚本（逻辑面）+ PSUI 声明式界面（界面面）**，就能向游戏注入物品、机器、配方、品质、NPC、玩法与自定义 UI。
>
> Content-pack modding framework for *Probably Stolen* (playtest), built on MelonLoader — think Forge/Fabric, but you write JSON + scripts instead of C#.

## 组件一览

| 组件 | 版本 | 说明 |
|---|---|---|
| PSApi | **v2.0.8** | 框架本体（单 dll，v2.0.0 起合并原 Items/Events 双组件） |
| examples/ | 1.0.0 | **34 个教学示例包**（ex01_hello ~ ex34_mini_mod，每个知识小节一个，全部实跑验证） |
| psapi_manager | — | 系统包：游戏内 F6 管理面板 |
| psconsole | — | 调试控制台包：游戏内指令行 |
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
| `Mods/PSApi.dll` | `<游戏根>/Mods/` | **必须**（框架本体，单 dll） |
| `UserData/PSApi/README.md` | `<游戏根>/UserData/PSApi/` | 推荐（目录说明） |
| `UserData/PSApi/packs/psapi_manager/` | `<游戏根>/UserData/PSApi/packs/` | 推荐（F6 管理面板） |
| `UserData/PSApi/packs/psconsole/` | `<游戏根>/UserData/PSApi/packs/` | 可选（调试控制台） |
| `examples/exNN_xxx/`（任意若干） | `<游戏根>/UserData/PSApi/packs/` | 可选（学习用，装哪个学哪章） |
| `_psapi/` | 不安装 | 框架 C# 源码（开发用） |
| `api_docs/` | 不安装 | 61 篇中文开发者文档 |
| `_tools/` | 不安装 | 开发工具（物品编辑器 / 内容包编译器） |

简单说：**仓库里的 `Mods/` 和 `UserData/PSApi/` 两个目录的内容，原样对应拖进游戏的同名目录即可**；`examples/` 里想学哪个包就复制哪个进 `packs/`；其余目录（`_psapi` `api_docs` `_tools`）是开发资源，不需要放进游戏。

### 安装步骤

1. **前置**：游戏已安装 MelonLoader（IL2CPP 版）。没有的话去 [MelonLoader Releases](https://github.com/LavaGang/MelonLoader/releases) 下载安装器，选中游戏目录安装。
2. **装框架**：把 `Mods/PSApi.dll` 复制到 `<游戏根>/Mods/`。
3. **装内容包**：把想要的包目录复制到 `<游戏根>/UserData/PSApi/packs/`（推荐先装 `psapi_manager`；学习时按文档装对应 `examples/exNN`）。
4. **验证**：启动游戏，MelonLoader 控制台出现 `PSApi v2.0.8 loaded` 与 `rescan(init): N pack(s), ... 0 compile error(s)` 即成功；游戏内按 **F6** 可打开 psapi_manager 管理面板。

---

## 二、⚠️ 冲突与常见坑（必读）

1. **升级时清理旧版残留（尤其 v2.0.0 升级）**
   v2.0.0 合并为单 dll：更新前先删除 `Mods/` 里的旧版 `PSApi.Items.dll`、`PSApi.Events.dll` 及任何 `PSPack.*.dll`，再放入新的 `PSApi.dll`。新旧并存会导致重复注册与不可预期的行为。

2. **从 v1.x 升级的旧存档**
   状态文件（`UserData/PSApi/state/`）格式兼容，无需处理；若曾装过 gunworks 的 DLL 形态，卸载后其注入内容随旧存档自然失效，不影响框架。

3. **前置依赖（prerequisites）**
   内容包 `pack.json` 的 `prerequisites` 字段生效：若所依赖的包未安装，该包整体跳过加载，并在主菜单弹"缺少前置模组"冲突窗。

4. **`UserData/PSApi/state/` 是存档状态，勿手改**
   该目录按存档槽（`state/<slot>/<owner>.json`）由框架自动读写，手改会破坏玩法进度。同理 `logs/` 和 `ui/` 也是运行时产物，无需备份或分发。

5. **排障第一现场**
   出问题先看 `<游戏根>/UserData/PSApi/logs/`（含 `pack_errors`、PSScript 编译错误等），排查方法见[开发工作流文档](api_docs/01-getting-started/04-workflow.md)。

---

## 三、开发者文档（api_docs/，9 章 61 篇中文）

| 章节 | 内容 |
|---|---|
| [01 入门](api_docs/01-getting-started/01-intro.md) | PSApi 是什么、三面一体架构、5 分钟跑通第一个包、开发工作流与排障 |
| [02 内容包系统](api_docs/02-pack/README.md) | pack.json 全字段、目录规范、前置依赖、编译 DLL 分发 |
| [03 数据面 JSON](api_docs/03-items/README.md) | 物品 / 机器 / 配方 / 品质 / 图标，逐字段讲解 |
| [04 PSScript 语言](api_docs/04-psscript/README.md) | 脚本语言教程：变量、控制流、函数、容器、事件订阅 |
| [05 内置 API 参考](api_docs/05-api-reference/README.md) | PSScript 全部内置函数，按主题分类 + 完整示例 |
| [06 PSUI 界面](api_docs/06-psui/README.md) | 声明式 UI：窗口、控件、脚本绑定 |
| [07 进阶主题](api_docs/07-advanced/README.md) | 存档状态、原版内容注入、自定义 NPC（参考级）、调试排障、item_editor |
| [08 实战讲解](api_docs/08-examples/README.md) | **34 个教学示例包总导航** + psapi_manager 带读 + 从 0 到 1 工作流 |
| [09 附录](api_docs/09-appendix/README.md) | 原版物品 id 对照、术语表、速查表、FAQ |

所有语法、字段、函数签名均取自 `_psapi/` 真实源码并经游戏反编译双重核对，示例完整可运行。**新手从[这里](api_docs/01-getting-started/01-intro.md)开始。**

## 四、教学示例包（examples/，34 个）

每个知识小节配一个**最小可运行示例包**——读到哪、装到哪、跑到哪：

| 分组 | 包 | 学什么 |
|---|---|---|
| 入门 | ex01 ~ ex04 | 第一个包、目录全景、依赖声明 |
| 数据面 | ex05 ~ ex11 | 物品字段、模板、次数、机器三路线、配方、品质、图标 |
| PSScript | ex12 ~ ex18 | 类型、字符串、控制流、函数、容器、事件、错误防御 |
| API | ex19 ~ ex24 | state/time、商店事件、items、inject、npc、ui |
| PSUI | ex25 ~ ex29 | 全控件、回调、动态构建、槽位、机器面板 |
| 进阶 | ex30 ~ ex34 | NPC 全配置、场景、战斗、state 深入、迷你 mod 综合 |

用法：把包复制进 `packs/` → 启动游戏 → 先读包内 `README.md` 的"验证"节对照日志 → 改"动手练习"里的值重启看效果。全部 34 包可同时安装、互不冲突、不依赖其他 mod。总导航见[08 章](api_docs/08-examples/README.md)。

## 五、从源码构建（_psapi/）

单工程用相对路径引用游戏侧 DLL（`..\..\MelonLoader\...`），因此**源码必须位于 `<游戏根>/_psapi/`（与 `MelonLoader/` 同级）才能编译**：

```bash
# 把 _psapi/ 复制到游戏根目录后：
cd "<游戏根>/_psapi/PSApi" && dotnet build -c Release
```

产物在 `bin/Release/PSApi.dll`，复制到 `<游戏根>/Mods/` 即可（覆盖旧版前记得先删）。

## 六、开发工具（_tools/）

| 工具 | 用法 |
|---|---|
| `item_editor/` | Python 物品编辑器：`run_editor.bat` 启动 |
| `pack_compiler/` | 内容包 → 单 DLL 编译器：`dotnet build -c Release` 后 `pack_compiler <packDir> [outDir]`，产出 `PSPack.<包id>.dll`；`--verify <dll>` 自检 |
| AssetRipper | **不入库**（可执行文件 130MB，超 GitHub 单文件限制），请自行从 [官网](https://github.com/AssetRipper/AssetRipper) 下载，用于游戏资源逆向 |

## 七、5 分钟写第一个内容包

1. 复制 `examples/ex01_hello/` 整个目录到 `<游戏根>/UserData/PSApi/packs/my_pack/`；
2. 改 `pack.json` 的 `id` 为 `my_pack`（必须与目录名一致）；
3. 全局替换包内 JSON / `.pss` 里的 `ex01_hello` → `my_pack`；
4. 启动游戏，看 `UserData/PSApi/logs/` 没有 `pack_errors` 即加载成功。

完整教程见[5 分钟跑通第一个包](api_docs/01-getting-started/03-first-pack.md)。

---

## Credits

- 框架作者：**Research**（PSApi）
- 本仓库文档（api_docs/）基于源码与游戏反编译逐字段核对编写，共 9 章 61 篇约 31 万字
- 依赖：[MelonLoader](https://github.com/LavaGang/MelonLoader)、Harmony
