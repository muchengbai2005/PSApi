# 02 · 内容包系统（III）：编译 DLL 分发

> 阅读时间约 15 分钟。读完你会掌握：把开发好的文件夹包编译成单个
> `PSPack.<id>.dll` 的完整流程、编译器内部做了什么、部署到玩家端后
> 运行时如何加载，以及发布模组时的注意事项。
> 前置阅读：[目录结构与加载规则](02-structure.md)。

## 为什么要编译成 DLL

文件夹包和 DLL 包是同一内容的两种形态：

| | 文件夹包 `packs/<id>/` | DLL 包 `Mods/PSPack.<id>.dll` |
|---|---|---|
| 定位 | **开发期** | **分发期** |
| 修改方式 | 直接改文件，重启生效 | 不可改（内容全部内嵌为资源） |
| 玩家安装 | 要会找 `UserData/PSApi/packs/` | 扔进 `Mods/` 就完事 |
| 误改风险 | 高（JSON 手滑就坏） | 低 |
| 版本固定 | 无 | 有（版本进模组信息，模组列表可见） |
| 依赖 PSApi | 是 | 是（仍需玩家先装 PSApi 两件套） |

> 编译**不改变内容**——包里的 JSON / .pss / .psui 原样内嵌，运行时照样由
> PSApi 宿主解析执行。"编译"只是资源打包 + 一个自动注册的引导壳。

## 前置条件

| 条件 | 检查方法 |
|---|---|
| .NET SDK（可跑 `dotnet build`） | 终端执行 `dotnet --version` 有输出 |
| PSApi.Items 已 Release 构建 | 存在 `_psapi/PSApi.Items/bin/Release/PSApi.Items.dll` |
| 工具在游戏目录树内 | `_tools/pack_compiler/` 位于游戏根目录下（工具靠向上查找 `MelonLoader/net6/MelonLoader.dll` 定位游戏根） |
| 包本身合法 | `pack.json` 存在、JSON 合法、含 `id` |

> 为什么需要 PSApi.Items.dll？生成的包 DLL 引用它（仅编译期引用，`Private=false`
> 不随包拷贝）——引导壳要调用它的注册 API。没构建过的话：
> `cd _psapi/PSApi.Items && dotnet build -c Release`。

## 编译步骤

### 第 1 步：构建工具（一次性）

```powershell
cd "_tools\pack_compiler"
dotnet build -c Release
```

### 第 2 步：编译你的包

```powershell
# 用法: pack_compiler <packDir> [outDir]
# packDir = 包根目录（含 pack.json 的那层）；outDir 缺省 = 工具目录 out/
dotnet run -c Release -- "UserData\PSApi\packs\my_weapon_pack"
```

成功输出（真实格式）：

```text
[pack_compiler] 构建 PSPack.my_weapon_pack (23 文件) ...
（dotnet build 的常规输出）
[pack_compiler] 完成: id=my_weapon_pack version=1.2.0
[pack_compiler] 嵌入文件 23 个, 共 148,326 字节
[pack_compiler] 输出: …\_tools\pack_compiler\out\PSPack.my_weapon_pack.dll (156,720 字节)
```

打包规则（依据 `Program.cs`）：

- **全部文件内嵌**——包括 `pack.json` 本身。不分扩展名，`items/`、`events/`、
  `icons/` 里的所有东西都进去。
- 自动排除：以 `.` 开头的文件名 + `.DS_Store` / `Thumbs.db` / `desktop.ini`。
- 文件按路径名字典序嵌入。
- `version` 缺省按 `0.0.0`；`authors` 为空按 `unknown`（进模组信息）。

### 第 3 步：自检（推荐）

```powershell
dotnet run -c Release -- --verify "out\PSPack.my_weapon_pack.dll"
```

```text
[verify] PSPack.my_weapon_pack.dll: pspack/ 资源 23 个, 共 148,326 字节
[verify] pack.json: id=my_weapon_pack version=1.2.0 prerequisites=[gunworks]
```

`--verify` 只读 DLL 里的资源与 pack.json，不加载类型，可在任何机器上安全执行。
发布前跑一遍，确认资源数与源文件夹文件数一致、id/version/prerequisites 无误。

### 第 4 步：部署

```powershell
Copy-Item "out\PSPack.my_weapon_pack.dll" "Mods\"
```

启动游戏，在 MelonLoader 控制台/日志确认：

```text
（模组加载阶段）PSPack.my_weapon_pack 已加载          ← MelonLoader 模组列表
（PSApi.Items 初始化后）rescan(init): N pack(s), …    ← 包数 +1，errors=0
```

玩家端只需要：装好 MelonLoader + `PSApi.Items.dll` + `PSApi.Events.dll`，
再把 `PSPack.<id>.dll` 扔进 `Mods/`。无需创建任何文件夹。

## 编译器内部做了什么

理解内部原理，出问题时才知道去哪查。`pack_compiler` 干三件事：

```text
packs/my_weapon_pack/                  _tools/pack_compiler/obj/packbuild_my_weapon_pack/
├── pack.json          ──嵌入──▶       ├── PSPack.my_weapon_pack.csproj
├── items/*.json       ──嵌入──▶       │     （每个文件一条 EmbeddedResource，
├── events/*.pss       ──嵌入──▶       │       LogicalName = pspack/<相对路径>）
├── icons/*.png        ──嵌入──▶       └── PackPlugin.cs
└── …                                  （MelonMod 引导壳源码）
                                              │
                                               ▼  dotnet build -c Release
                                       bin/Release/PSPack.my_weapon_pack.dll
                                       （拷贝到 outDir，临时目录下次重建）
```

**① 资源打包**：包内每个文件变成程序集内嵌资源，逻辑名加 `pspack/` 前缀
（如 `pspack/items/rifle.json`）。运行时 `EmbeddedPackSource` 按前缀截出逻辑
路径，读取行为与文件夹包完全一致（`HasDir` / `ListFiles` / `ReadText`）。

**② 引导壳生成**（`PackPlugin.cs`，机器生成勿手改）：

```csharp
[assembly: MelonInfo(typeof(PSPack_my_weapon_pack.PackPlugin),
    "PSPack.my_weapon_pack", "1.2.0", "你的名字")]
[assembly: MelonGame("Questing Goose Studio", "Probably Stolen")]
[assembly: MelonPriority(5)]                       // 必须先于 PSApi.Items(10)
[assembly: MelonOptionalDependencies("PSApi.Items")] // Items 缺失也不崩

public class PackPlugin : MelonMod
{
    public override void OnInitializeMelon()
    {
        try { RegisterCore(); }
        catch (System.Exception e)
        {
            LoggerInstance.Warning("[PSPack.my_weapon_pack] PSApi.Items 缺失或版本不兼容, 包未注册: " + e.GetType().Name);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterCore()
    {
        PSApi.Items.EmbeddedPackRegistry.Register("my_weapon_pack", "1.2.0",
            new string[] { "gunworks" }, typeof(PackPlugin).Assembly, "pspack/");
    }
}
```

关键设计：

| 设计 | 原因 |
|---|---|
| `MelonPriority(5)` | 必须先于 PSApi.Items(10) 执行 `Register`，否则 Items 合并扫描时内嵌包未就位、冲突弹窗永远为空 |
| `MelonOptionalDependencies("PSApi.Items")` + try/catch | 玩家没装 PSApi 时，包 DLL 只打警告不崩游戏 |
| `Register(id, version, prerequisites, assembly, "pspack/")` | 把包身份与资源位置登记进 Items 的公共注册表，等两个宿主来合并消费 |

**③ 生成 csproj 调 `dotnet build`**：目标框架 net6.0，只引用 MelonLoader.dll
和 PSApi.Items.dll（`Private=false`），不需要游戏程序集。构建失败时临时目录
`obj/packbuild_<id>/` 会保留备查。

## 运行时的完整加载链

```text
玩家启动游戏
│
├─ MelonLoader 扫描 Mods/
│    ├─ PSPack.my_weapon_pack.dll (priority 5) → OnInitializeMelon → Register(…)
│    ├─ PSApi.Items.dll (10)  → 扫描 packs/ + 读注册表 → 合并去重 → 解析数据面
│    └─ PSApi.Events.dll (20) → 扫描 packs/ + 读注册表 → 合并去重 → 编译 .pss/.psui
│
└─ DLL 包与文件夹包在"合并"处汇合，此后一视同仁：
     同 id → DLL 胜（文件夹版记 duplicate 冲突，弹窗提醒）
     prerequisites → 统一求值（DLL 包的 pack.json 为准）
```

> DLL 包注册时携带的 id/version/prerequisites 若与内嵌 pack.json 不一致，
> **以 pack.json 为准**（编译器同源生成，理论上一致——手改 DLL 资源才会出现）。

## 并存冲突：开发期的经典坑

```text
packs/gunworks/  +  Mods/PSPack.gunworks.dll   同时存在
→ DLL 胜出，文件夹版被完全忽略
→ 日志: pack conflict [duplicate]: gunworks 同时存在 dll 与 pack, 已默认加载 dll
→ 主菜单弹"重复加载"窗
```

症状：**改了 `packs/` 里的文件，重启后毫无变化**——因为你改的那份根本没被加载。
排查口诀：日志搜 `duplicate`，有则移走 `Mods/` 里的同名 DLL（或反向，删掉
`packs/` 文件夹）。

## 退出码与错误对照

| 退出码 | 场景 | 典型输出 |
|---|---|---|
| 0 | 成功 | `完成: id=… version=…` |
| 2 | 用法错误 | `用法: pack_compiler <packDir> [outDir] \| pack_compiler --verify <dllPath>` |
| 3 | 包错误 | `[pack_compiler] 错误: 包目录不存在: …`（附三行检查清单） |
| 5 | 构建失败 | `dotnet build 失败 (exit=…), 构建目录保留备查: …` |

包错误（退出码 3）的检查清单（工具原样打印）：

```text
[pack_compiler] 错误: pack.json 解析失败: …（或缺 id / 目录不存在 / 无可嵌入文件）
检查清单:
  1. packDir 是否指向内容包根目录 (含 pack.json 的那层)
  2. pack.json 是否为合法 JSON 且含必填字段 "id"
  3. PSApi.Items 是否已构建 (_psapi/PSApi.Items → dotnet build -c Release)
```

> 另有两类环境错误直接抛异常文本：找不到 `MelonLoader/net6/MelonLoader.dll`
>（工具不在游戏目录树内）与找不到已构建的 PSApi.Items.dll。

## 发布模组的建议清单

发布你的包之前，照着过一遍：

1. `pack.json` 的 `id` / `version` / `authors` 已更新（版本号会进玩家模组列表）。
2. `prerequisites` 列齐依赖的**内容包 id**（不依赖其他包就留空/删掉）。
3. 编译 + `--verify`：资源数与源文件数一致，pack.json 打印无误。
4. 干净环境验证：把 `packs/` 里你的包暂时移走，只留 `Mods/PSPack.<id>.dll`，
   重启确认加载正常（避免被"文件夹+DLL 并存"假象骗过）。
5. 发布页写明：需要 MelonLoader + PSApi（Items/Events 两件套）+ 你的 DLL；
   涉及的前置包列表；适配的游戏版本（`gameVersions` 字段当前无机器检查，
   信息要靠你写给玩家看）。

安装说明模板（可直接抄给你的玩家）：

```text
安装（需先装好 MelonLoader 与 PSApi）:
1. 把 PSPack.my_weapon_pack.dll 放进游戏目录的 Mods/ 文件夹
2. （如声明了前置包）同样安装前置包的 DLL
3. 启动游戏，主菜单不弹冲突窗即为安装成功

卸载: 删除 Mods/PSPack.my_weapon_pack.dll 即可
```

## 本章 FAQ

**编译后的 DLL 还能改内容吗？**
不能直接改。改源文件夹 → 重新编译 → 覆盖 `Mods/` 里的 DLL。

**玩家的 `packs/` 里没有我的包，DLL 也能加载吗？**
能——内容全部在 DLL 资源里，`packs/` 不需要存在任何东西。

**DLL 包和文件夹包的行为有差别吗？**
加载合并层面零差别（同一套 `IPackSource` 抽象）。唯一可见差异：冲突时
DLL 优先，以及 MelonLoader 模组列表里会多出 `PSPack.<id>` 条目。

**编译时报"未找到 PSApi.Items.dll"？**
按提示先构建：`cd _psapi/PSApi.Items && dotnet build -c Release`。

**图标等二进制资源嵌入后会被改动吗？**
字节级原样嵌入（`ReadBytes` 原样读出），PNG 不会被重编码。

---

本章（02 内容包系统）完。接下来：
- [03 数据面 JSON](../03-items/README.md)——物品 / 机器 / 配方 / 品质逐字段
- 或回到[首页](../README.md)按阅读路径继续
