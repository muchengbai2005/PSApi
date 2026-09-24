# 02 · 内容包系统（II）：目录结构与加载规则

> 阅读时间约 15 分钟。读完你会掌握：包内目录怎么组织、启动时扫描-合并-消费的
> 完整管线、加载顺序由什么决定、id 撞车与缺前置时的真实行为，以及冲突弹窗
> 的工作机制。pack.json 字段本身见[上一篇](README.md)。

## 包内目录规范

一个内容包的标准目录（全部子目录都是可选的，按需创建）：

```text
UserData/PSApi/packs/<你的包>/
├── pack.json        ← 必备：包清单（见上一篇）
├── items/           ← 数据面：物品定义 JSON        （PSApi.Items 读）
├── machines/        ← 数据面：机器声明 JSON         （PSApi.Items 读）
├── recipes/         ← 数据面：配方 JSON             （PSApi.Items 读）
├── qualities/       ← 数据面：品质层 JSON           （PSApi.Items 读）
├── icons/           ← 数据面：物品图标 PNG          （PSApi.Items 读）
├── events/          ← 逻辑面：PSScript 脚本 .pss    （PSApi.Events 读）
└── ui/              ← 界面面：PSUI 面板 .psui       （PSApi.Events 读）
```

规则与细节（依据 `PackScanner.cs` / `PackSource.cs` / 两宿主 `Plugin.cs`）：

| 规则 | 说明 |
|---|---|
| 只扫 `packs/` 的**一层**子目录 | `packs/<包>/` 才是包；`packs/notes/` 这种没 pack.json 的文件夹会被警告并跳过；不存在"包中包" |
| 子目录内部**允许再嵌套** | `items/weapons/rifle.json`、`events/quest/chapter1.pss` 都合法——文件检索是递归的 |
| 文件名即检索顺序 | 每个子目录内的文件按**路径名不区分大小写字典序**依次加载 |
| 逻辑路径用正斜杠 | 引用包内文件时写 `items/gun1.json` 风格（DLL 内嵌包同样如此，见[第三篇](03-distribution.md)） |
| 谁读哪个目录 | Items 读 `items/ machines/ recipes/ qualities/ icons/`；Events 读 `events/ ui/`——互不干涉，只放单面的内容包完全合法 |
| 空包合法 | 只有 pack.json、零内容子目录的包也能正常加载（计数为 0 而已） |

各子目录里 JSON 的**内容格式**（每个字段什么意思）属于数据面话题，展开在
[03 数据面 JSON](../03-items/README.md)；`.pss` / `.psui` 见
[04 PSScript](../04-psscript/README.md) 与 [06 PSUI](../06-psui/README.md)。

## 命名规范速览

| 对象 | 格式 | 例子 |
|---|---|---|
| 包 id | 小写字母 + 下划线（建议） | `gunworks` |
| 自定义内容 id | `包id:名字`（全局唯一） | `gunworks:gw_semi_sniper` |
| 引用原版内容 | `game:裸id` | `game:scrap_metal` |
| 包内图标引用 | 文件名（相对 `icons/`） | `"icon": { "file": "rifle.png" }` |

> 内容 id 前半段**约定**等于 pack.json 的 `id`（引擎按 `:` 切分命名空间）。
> 这不是加载器强制的目录/id 一致性检查，而是 id 解析的既定格式——
> 前半段就是你内容的"姓氏"，跨包引用、冲突判断都靠它。

## 启动管线：从文件夹到游戏内容

两个宿主模组**各自独立**跑一遍"扫描 → 合并 → 消费"，互不依赖对方的扫描结果：

```text
游戏启动
│
├─ MelonLoader 按 MelonPriority 加载 Mods/ 下的 DLL
│    PSPack.*.dll (5) → PSApi.Items (10) → PSApi.Events (20)
│         │                 │                   │
│         ▼                 │                   │
│    Register 登记          │                   │
│    内嵌包资源 ────────────┤                   │
│                          ▼                   ▼
│              ┌──────────────────────────────────────┐
│              │ ① 扫描 PackScanner                    │
│              │  packs/ 一层子目录按目录名排序          │
│              │  逐个读 pack.json（缺/坏 → 记错误跳过）│
│              │  文件夹包同 id：先加载者胜             │
│              ├──────────────────────────────────────┤
│              │ ② 合并 PackMerger                     │
│              │  DLL 内嵌包优先入列（同 id DLL 胜，    │
│              │  被压制的文件夹包记 duplicate 冲突）    │
│              │  再放文件夹包                          │
│              │  统一做 prerequisites 检查             │
│              │  （缺前置 → 记 missing_prereq 冲突跳过）│
│              ├──────────────────────────────────────┤
│              │ ③ 消费（各自为政）                     │
│              │  Items: qualities → items → recipes   │
│              │  Events: events/*.pss + ui/*.psui     │
│              │  错误汇总 → logs/pack_errors_*.log     │
│              └──────────────────────────────────────┘
│
└─ 冲突弹窗（有冲突才弹）
     首次进入 EmporiumMenu 时读取合并结果，弹"PS-API 内容包冲突"窗
```

两个宿主都跑完合并后，会**各打印一遍**冲突警告（所以同一条冲突在日志里
可能出现两次，一次带 `[PSApi.Items]` 前缀、一次带 `[PSApi.Events]` 前缀，
这是正常现象）。

## 加载顺序详解

"加载顺序"在 PS-API 里有两层，分开理解：

### 第一层：模组间顺序（MelonPriority）

```text
PSPack.<id>.dll   priority = 5    ← 编译包 DLL，先登记内嵌资源
PSApi.Items.dll   priority = 10   ← 数据面宿主
PSApi.Events.dll  priority = 20   ← 逻辑/界面面宿主（晚于 Items，脚本可安全引用物品）
```

这一层决定**谁先初始化**。包 DLL 必须先于 Items 注册，否则合并时内嵌包
还没就位——这是编译器把 stub 的 priority 定为 5 的原因（见[第三篇](03-distribution.md)）。

### 第二层：包与包之间（扫描顺序）

文件夹包之间**没有任何依赖排序机制**，顺序完全机械：

```text
packs/
├── alpha_pack/     ← 先加载（目录名靠前）
├── beta_lib/       ← 再加载
└── gunworks/       ← 再加载
```

- 排序规则：目录名**不区分大小写的字典序**（OrdinalIgnoreCase）。
- `prerequisites` **不参与排序**——它只做"缺了就跳过"的存在性检查
  （源码 `PackMerger` 合并完才两遍求值，与顺序无关）。
- `pack.json` 的 `loadAfter` 字段当前无任何消费逻辑（保留字段）。

> **什么时候顺序真的重要？** 只有**同 id 竞争**时：两个文件夹包同 id，
> 排序靠前者胜、后者被丢弃。内容层面（物品定义、脚本）的加载互不覆盖、
> 顺序无关——各包内容靠 `包id:` 前缀天然隔离，不存在"后加载的包覆盖
> 先加载的包的物品"这种事。
>
> 想让脚本在另一个包"之后"做事？不需要控制加载顺序——脚本通过事件
> （`on scene_loaded()` 等）响应运行时发生的事，而不是靠启动次序。

## id 冲突的三种情形

| 情形 | 结果 | 日志 / 弹窗 |
|---|---|---|
| 两个**文件夹包**同 id | 目录名排序**先者胜**，后者整包丢弃 | 警告 `pack '<目录名>': duplicate id '<id>', skipped`；不弹窗 |
| **文件夹包** vs **DLL 包**同 id | **DLL 胜**，文件夹包整包丢弃 | 警告 `pack conflict [duplicate]: <id> 同时存在 dll 与 pack, 已默认加载 dll`；弹"重复加载"窗 |
| 两个 **DLL** 同 id | 先注册者胜，后者丢弃 | 仅警告 `embedded pack '<id>': duplicate registration, first registered wins, skipped`；不弹窗 |

> 开发期"文件夹 + DLL 并存"是官方认可的对照验证手法（本仓库 `gunworks`
> 就是活例子：`packs/gunworks/` 与 `Mods/PSPack.gunworks.dll` 并存，DLL 胜出）。
> 但要记得：**你改的文件夹版根本没被加载**——验证改动前先移走 DLL。

id 比较一律**不区分大小写**（`Gunworks` 与 `gunworks` 视为同一个 id）。

## prerequisites 的精确语义

上一章讲了字段含义，这里补齐**加载器真实行为**（全部依据 `PackMerger.Merge`）：

```text
合并去重后的包列表:  [A, B, C]          ← 注意 C 因缺前置 D 即将被跳过
已扫描到的 id 集合:  {A, B, C}          ← 第二遍求值用的"存在性"基准
逐包检查:
  A.prerequisites = ["B"]   B 存在        → A 通过
  B.prerequisites = ["D"]   D 不存在      → B 跳过 + missing_prereq 冲突
  C.prerequisites = ["B"]   B 在集合里!   → C 通过（尽管 B 刚被跳过）
```

三条推论：

1. **不传递**：前置检查只看"id 是否被扫描到"，不看那个包是否加载成功。
   依赖链上游断了，下游未必知道。
2. **循环依赖不报错**：A↔B 互相依赖时双方 id 都存在，双双通过、正常加载。
3. **不改变顺序**：声明了 prerequisites 不会让任何包提前或延后——顺序
   永远是"DLL 先、文件夹按目录名"。

## 冲突弹窗机制

当合并产出冲突条目（缺前置 / 重复加载）时，玩家会在**首次进入主菜单**时
看到弹窗（依据 `PackConflictPopup.cs`；游戏没有独立主菜单场景，启动即
EmporiumMenu，故以它为触发点）：

```text
┌─ PS-API 内容包冲突 ──────────────────────────┐
│                                               │
│  缺少前置模组:                                 │
│    my_pack 需要 gunworks                       │
│                                               │
│  重复加载:                                     │
│    gunworks 同时存在 dll 与 pack, 已默认加载 dll │
│                                               │
│                 [ 确定 ]                       │
└───────────────────────────────────────────────┘
```

| 行为 | 说明 |
|---|---|
| 触发时机 | 首次进入 EmporiumMenu（主菜单） |
| 弹几次 | 每次游戏启动**最多一次**（进程级标记，构建失败也不重试刷屏） |
| 窗口属性 | 可拖拽、ESC 可关、宽 460、高度随行数 |
| UI 不可用时 | 降级为仅控制台日志 `pack conflict popup: CustomUIManager 不可用, 冲突明细见启动日志` |
| 弹过之后 | 关掉就没了；明细永远可以在启动日志里搜 `pack conflict` |

## 边缘情况清单

| 情况 | 真实行为 |
|---|---|
| `packs/` 下的文件夹没有 pack.json | 警告 `pack '<目录名>': missing pack.json, skipped`，整包跳过 |
| pack.json 是非法 JSON | 警告 `pack '<目录名>': pack.json parse error: <原因>`，整包跳过 |
| pack.json 有 id 但为空白串 | 视同缺 id，整包跳过 |
| id 与目录名不一致 | 照常加载（无一致性检查）；日志/弹窗显示 id |
| 包目录为空（只有 pack.json） | 正常加载，内容计数为 0 |
| `packs/` 根下直接放散文件 | 不被扫描（只有一层子目录是包） |
| DLL 包的 pack.json 与注册 id 不一致 | 以**pack.json 为准**，仅警告 `pack.json id is '<x>', using pack.json` |
| `UserData/PSApi/` 整个不存在 | 自动创建（含 packs/state/logs） |

## 本章日志速查

| 日志行 | 含义 |
|---|---|
| `pack '<目录>': missing pack.json, skipped` | 文件夹缺清单，整包跳过 |
| `pack '<目录>': pack.json missing required 'id'` | 清单缺 id，整包跳过 |
| `pack '<目录>': pack.json parse error: …` | 清单 JSON 语法错误，整包跳过 |
| `pack '<目录>': duplicate id '<id>', skipped` | 文件夹包 id 撞车，后者被丢 |
| `pack conflict [duplicate]: <id> 同时存在 dll 与 pack, 已默认加载 dll` | DLL 压制文件夹包（弹窗） |
| `pack '<id>': missing prerequisites: <列表>, skipped` | 缺前置，整包跳过（弹窗） |
| `pack conflict [missing_prereq]: <id> 需要 <列表>` | 同上的弹窗条目形式 |
| `pack conflict popup shown: missing_prereq=N duplicate=N` | 弹窗已展示的确认行 |
| `embedded pack '<id>': duplicate registration, first registered wins, skipped` | 两个 DLL 同 id |

> 提示：`<目录>` 是文件夹名，`<id>` 是清单里的 id——两者不一致时日志里
> 会混着出现，这也是"id 与目录名保持一致"建议的由来。

---

下一篇：[03 · 编译 DLL 分发](03-distribution.md)
