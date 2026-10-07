# 02 · 内容包系统（I）：pack.json 包清单

> 阅读时间约 12 分钟。读完你会掌握：pack.json 的全部字段与含义、哪些字段真的会被
> 加载器消费、JSON 的宽容语法，以及一个"生产级"清单该怎么写。
> 目录结构、加载顺序与冲突机制见[下一篇](02-structure.md)，编译 DLL 分发见
> [第三篇](03-distribution.md)。

## 本章导航

| 篇 | 主题 | 回答的问题 |
|---|---|---|
| **本篇** | pack.json 全字段 | 清单里能写什么、每个字段谁在用 |
| [02 结构与加载](02-structure.md) | 目录规范 / 加载顺序 / 冲突 | 包放在哪、怎么被扫到、撞车了怎么办 |
| [03 编译分发](03-distribution.md) | pack_compiler → DLL | 怎么把文件夹包变成给玩家用的单个 DLL |

## pack.json 是什么

`pack.json` 是一个内容包的**唯一必备文件**，放在包根目录：

```text
UserData/PSApi/packs/<你的包>/
└── pack.json     ← 没有它，整个包被跳过
```

游戏启动时，PSApi（v2.0.0 起单宿主 `PSApi.dll`，内部数据面/逻辑面两模块）各自扫描
`packs/` 下的每个子目录，读取 `pack.json`，决定这个包：叫什么 id、依赖谁、要不要加载。

**最小合法清单**只要一个字段：

```json
{
  "id": "my_pack"
}
```

就这么短。其余字段全是可选的——但正式发布的包应该写全（原因见下文字段表）。

## JSON 语法宽容性

PS-API 解析 `pack.json`（以及所有数据面 JSON）用的是同一套宽松选项
（源码 `PackScanner.JsonOpts`，与 `pack_compiler` 同款）：

| 宽容特性 | 例子 | 说明 |
|---|---|---|
| `//` 行注释 | `// 这是注释` | 解析时自动跳过 |
| 尾逗号 | `["a", "b",]` | 数组/对象末尾多个逗号不报错 |
| 字段名大小写不敏感 | `"ID"` 等价 `"id"` | 建议仍按文档统一小写 |

由此衍生出**社区惯例**：用 `"//"` 字段当"文档"写进清单里。随游戏分发的
`psapi_manager` 与仓库 examples/ 教学包都这么干：

```json
{
  "id": "psapi_manager",
  "name": "PSApi 管理面板 (M3 · NpcManager F6 全量复刻)",
  "version": "1.0.0",
  "authors": ["psapi"],
  "gameVersions": ["playtest"],
  "//": "F6 开关(MelonPreferences [PSApi] ManagerHotkey 可改)。PSUI 动态面板样板: ..."
}
```

> `"//"` 只是一个普通字段名——解析器不认识它就忽略，人类看得懂就行。
> 内容包版本历史、变更日志、依赖说明都习惯堆在这里，大型包的 `"//"` 字段
> 甚至写几千字。缺点是 JSON 字符串里不能换行，长文案会用 `;` 分隔。

## 全字段参考

| 字段 | 类型 | 必填 | 谁消费 | 一句话说明 |
|---|---|---|---|---|
| `id` | string | **是** | 扫描 / 合并 / 编译器 / 弹窗 | 包的全局唯一标识，一切引用的命名空间前缀 |
| `name` | string | 否 | （纯元数据） | 人类可读的显示名 |
| `version` | string | 否 | 编译器 / verify | 版本号，缺省按 `0.0.0` 处理 |
| `authors` | string[] | 否 | 编译器 | 作者列表，编译进 DLL 的模组信息 |
| `gameVersions` | string[] | 否 | **（保留字段）** | 预期的游戏版本，当前无消费逻辑 |
| `prerequisites` | string[] | 否 | 合并器 / 弹窗 | 前置内容包 id 列表，缺任何一个则整包跳过 |
| `loadAfter` | string[] | 否 | **（保留字段）** | 预期"晚于某包加载"，当前无消费逻辑 |
| `"//"` 等未知字段 | 任意 | 否 | （忽略） | 解析器忽略一切未知字段，可自由充当注释 |

> **[保留字段]** 的意思是：字段在数据模型（`PackManifest` DTO）中声明了，
> 但当前版本加载器没有任何代码读取它。写上无害，也方便未来版本启用；
> 但**今天**不要指望它们起作用。逐字段核实依据：
> `_psapi/PSApi/Shared/PackModels.cs`（DTO）、`PackScanner.cs` / `PackMerger.cs`
> （运行时消费）、`_tools/pack_compiler/Program.cs`（编译期消费）。

下面逐个展开。

### id —— 包的唯一标识

```json
{ "id": "my_pack" }
```

- **必填**。缺失或为空白字符串 → 整包跳过，警告
  `pack '<目录名>': pack.json missing required 'id'`。
- **全局唯一，大小写不敏感**。两个文件夹包同 id → 目录名排序**先加载者胜**；
  文件夹包与 DLL 包同 id → **DLL 胜**。详见[结构与加载](02-structure.md)。
- **它是你所有内容的命名空间**：包内物品、机器、脚本面板的 id 一律
  `包id:名字`（如 `my_pack:my_sniper`）。改 id = 全部内容改身份证。
- **建议与文件夹名一致**。加载器不强制检查 id == 目录名，但日志、冲突弹窗、
  编译产物（`PSPack.<id>.dll`）全都只认 id，两边不一致纯属给自己埋坑。
- 命名建议：小写字母 + 下划线（`my_pack`、`psapi_manager`），别用空格和中文。

### name —— 显示名

```json
{ "name": "我的枪械包 · 3 把组装枪 + 2 台机器" }
```

人类可读的包名。**当前加载器不消费它**——启动日志、冲突弹窗、管理面板显示的都是
id。它现在的价值是给翻文件夹的人和未来的工具看。社区惯例是把重要说明
浓缩在 name 里（可以很长），细节再堆进 `"//"` 字段。

### version —— 版本号

```json
{ "version": "0.13.0" }
```

- 运行时日志**不打印**每个包的版本；它真正生效是在**编译分发**时：
  `pack_compiler` 把它写进 `PSPack.<id>.dll` 的 MelonLoader 模组信息
  （玩家在模组列表里能看到），`--verify` 自检也会打印它。
- 缺省时编译器按 `0.0.0` 处理。格式随意（`1.0`、`0.13.0`、`v2-beta` 都行），
  没有语义化版本的机器校验。
- 注意：版本依赖（"需要 Events ≥ v1.9.0" 这类）**没有机器检查**，只能写在
  `"//"` 里给人类看；机器可检查的只有包级存在性——`prerequisites`。

### authors —— 作者列表

```json
{ "authors": ["psapi"] }
```

仅在编译分发时消费：写进 DLL 的 MelonLoader 模组信息（`MelonInfo` 的作者位）。
列表为空或缺省时编译器填 `unknown`。

### gameVersions —— 适配游戏版本【保留字段】

```json
{ "gameVersions": ["playtest"] }
```

声明的本包适配的游戏版本。**当前加载器与编译器都不读取它**——装了不兼容版本的
游戏也不会有任何提示。写 `["playtest"]` 是惯例，属于"给未来的承诺"。

### prerequisites —— 前置内容包

```json
{ "prerequisites": ["some_library_pack", "another_pack"] }
```

本包**依赖的其他内容包的 id**（不是 PSApi 模组版本，不是游戏版本）。这是
`pack.json` 里除 `id` 外唯一有运行时行为的三字段之一，规则全部来自
`PackMerger.cs`：

| 规则 | 说明 |
|---|---|
| 语义 | 每项填一个**内容包 id**，如 `"my_weapon_lib"`；大小写不敏感，前后空白会被忽略 |
| 求值时机 | 包合并阶段（所有包扫描完、去重后）统一求值，**不改变加载顺序** |
| 缺前置 | 只要有任何一个前置 id 不在"已接受的包集合"里 → **整包跳过**，记入冲突 |
| 弹窗 | 主菜单弹"缺少前置模组"窗：`<你的id> 需要 <缺失的id列表>` |
| 日志 | `pack '<你的id>': missing prerequisites: <列表>, skipped` |
| 不传递 | 检查的只是"id 存在"，**不检查那个包自己是否也因缺前置被跳过**（见下） |

> **不传递（重要推论）**：假设 A 依赖 B，B 依赖 C，而 C 没装。
> 求值时 B 因缺 C 被跳过——但 B 的 id 依然在"已扫描到的 id 集合"里，
> 于是 A 的前置检查**通过**，A 照常加载（然后大概率在运行时找不到 B 的物品）。
> 换句话说 prerequisites 是"存在性检查"而不是"可用性检查"。依赖链深的包，
> 请确保链条完整。
>
> 循环依赖（A 依赖 B、B 依赖 A）同理：两个 id 都存在，双方都通过，都正常加载，
> 不会报错。

依赖 **PSApi 模组本身**（想要"我的包需要 PSApi v2.0+"）怎么办？
当前没有字段能表达——版本需求写进 `"//"` 给人类，玩家装不装对版本只能靠自觉。
（历史包袱：v2.0.0 之前分 Events/Items 两组件，老包注释里的"需 Events vX / Items vY"
指的是合并前能力版本，对应能力在 v2.0.0 单 dll 中全量保留。）

### loadAfter —— 加载顺序声明【保留字段】

```json
{ "loadAfter": ["some_pack"] }
```

意图是"让我的包在 some_pack 之后加载"。**当前没有任何代码消费它**——文件夹包
的加载顺序完全由目录名字典序决定（详见[结构与加载](02-structure.md)）。
字段在 DTO 中声明、全库无读取逻辑，属于为未来留的坑位。今天写了不起作用，
不写也不影响任何行为。

## 完整示例

一份"该写的都写了"的生产级清单（字段顺序无所谓）：

```json
{
  // 必填：包 id，全局唯一，与目录名保持一致
  "id": "my_weapon_pack",

  // 人类可读名（可长，社区惯例会塞摘要）
  "name": "我的枪械包 · 3 把组装枪 + 2 台机器",

  // 编译 DLL 时进模组信息；--verify 会打印
  "version": "1.2.0",

  // 编译 DLL 时进模组信息；缺省 unknown
  "authors": ["你的名字"],

  // 惯例写法；当前无机器检查
  "gameVersions": ["playtest"],

  // 机器检查的前置：这两个包 id 缺任何一个，本包整包跳过并弹窗
  "prerequisites": ["my_weapon_lib"],

  // 保留字段，当前无效
  "loadAfter": [],

  // 社区惯例：变更日志 / 依赖说明 / 备注都堆这里
  "//": "v1.2.0: 新增狙击枪 my_sniper_v2 (需 PSApi v2.0+); v1.1.0: 平衡弹匣价格"
}
```

对照真实包看：随游戏分发的 `UserData/PSApi/packs/psapi_manager/pack.json`，
以及仓库 examples/ 各教学包的 pack.json——如 [ex01_hello](../../examples/ex01_hello/README.md)
（最小清单）与依赖对 [ex03_deps_base](../../examples/ex03_deps_base/README.md) /
[ex04_deps_user](../../examples/ex04_deps_user/README.md)。

## 字段消费总表（速查）

同一字段，"游戏启动加载文件夹包"与"编译成 DLL 分发"两条链路上的消费情况：

| 字段 | 运行时（Items/Events 扫描合并） | 编译期（pack_compiler） | 玩家可见处 |
|---|---|---|---|
| `id` | 去重键 / 前置检查键 / 命名空间 | DLL 文件名 `PSPack.<id>.dll` / 模组名 | 冲突弹窗、启动日志 |
| `name` | — | — | — |
| `version` | —（不打印） | MelonInfo / `--verify` 输出 | 模组列表（仅 DLL 版） |
| `authors` | — | MelonInfo（缺省 `unknown`） | 模组列表（仅 DLL 版） |
| `gameVersions` | —（保留） | — | — |
| `prerequisites` | 缺则跳过 + 冲突条目 | 读出转交给运行时注册 | "缺少前置模组"弹窗 |
| `loadAfter` | —（保留） | — | — |

> 一句话总结：**运行时真正干活的是 `id` 和 `prerequisites`，其余都是元数据；
> 编译期多消费 `version` 和 `authors`。**

---

下一篇：[02 · 目录结构与加载规则](02-structure.md)
