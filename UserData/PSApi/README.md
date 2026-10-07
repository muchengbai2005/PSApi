# PSApi 内容包目录 (UserData/PSApi/)

> 本目录是 **PSApi**（MelonLoader 模组，v2.0.0 起单 dll）的内容加载根目录。
> 定位是"内容包加载器"（对标 Minecraft 的 Forge/Fabric）：第三方用 **JSON（数据面）
> + PSScript 脚本（逻辑面）+ PSUI 声明式 UI（界面面）** 即可向游戏注入
> 物品/机器/配方/品质/玩法/界面，不动 `Mods/` 目录。
> 开发者文档见仓库 `api_docs/`（9 章 61 篇中文教程）。

## 目录总览

| 目录 | 放什么 | 谁读它 |
|---|---|---|
| `packs/` | 内容包根目录，**每个子目录 = 一个包** | PSApi（数据面 + `events/*.pss` 脚本 + `ui/*.psui` 面板） |
| `ui/` | UI 配置（`button_config.json`，配方锁定窗口按钮读取） | PSApi |
| `state/` | 存档槽状态（`state/<slot>/<owner>.json`） | SaveStates 自动管理，**勿手改** |
| `logs/` | 运行日志（`pack_errors` 等；PSScript 编译/运行错误也落这里） | PSApi 写入；排障第一现场 |

包内子目录约定：`items/` `machines/` `recipes/` `qualities/` `icons/`（JSON 数据面）
+ `events/`（**.pss 脚本**，逻辑面）+ `ui/`（**.psui 面板**，界面面）
+ `scenes/`（场景定义）。全部可选——包只需要它用到的东西。

v0.6.1 起旧的 legacy 目录（custom_items / extraitems_icons / PSApi/icons）已废弃移除，
全部内容统一走 `packs/`。

## 命名规范

- **包 id**：小写字母 + 下划线（如 `ex01_hello`），与目录名一致。
- **物品/机器/配方/品质 id**：`包id:名字`（如 `ex01_hello:greeting_card`），全局唯一。
- **引用原版物品**：配方/NPC 白名单等处直接写原版裸 id（如 `scrap_metal`）。
- **图标**：放在包内 `icons/`，物品 JSON 用文件名引用（如 `"icon": { "file": "xxx.png" }`）。

## 新开发者 5 分钟上手

1. 从仓库 `examples/` 复制 `ex01_hello/` 整个目录为 `packs/my_pack/`。
2. 改 `pack.json`：`id` 改为 `my_pack`（必须与目录名一致），顺手改 `name`。
3. 全局替换包内 JSON 与 `.pss` 里的 `ex01_hello` → `my_pack`。
4. 启动游戏，看 `logs/` 下有没有 `pack_errors` —— 没有即加载成功。
5. 游戏内验证：按 **F12** 发放全部测试物品（`test: true`），或看控制台
   `[pss my_pack] ... 已加载` 日志行。

之后按文档逐章学习：每个知识小节在仓库 `examples/` 有配套教学包
（总导航见 `api_docs/08-examples/README.md`）。

## 编译为 DLL 分发

文件夹包改文件即生效，适合开发调试；分发时可编译成单个 DLL（防误改/版本固定）：

1. 构建工具：`cd _tools/pack_compiler && dotnet build -c Release`。
2. 编译：`pack_compiler <packDir> [outDir]` → 产出 `PSPack.<id>.dll`
   （包内全部文件内嵌为资源 + MelonMod 引导 stub；outDir 缺省 = 工具目录 `out/`）。
   `pack_compiler --verify <dllPath>` 可自检资源数与 pack.json。
3. 部署：把 `PSPack.<id>.dll` 放进 `Mods/` 即可 —— MelonPriority=5 先于 PSApi(10)
   自动注册，无需任何手工配置。

规则：

- **同 id 文件夹包 + DLL 并存时 DLL 胜**，主菜单弹冲突窗（"重复加载"栏）。开发期并存可用于验证，
  正式分发建议二选一。
- **pack.json 的 `prerequisites` 字段生效**：填所依赖的内容包 id 列表；缺前置的包跳过加载，
  主菜单弹冲突窗（"缺少前置模组"栏）。
- 包 DLL 内嵌 pack.json 为准；编译器与包同源生成，版本号随 pack.json。
