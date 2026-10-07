# ex02_dirs · 示例 02：一个包的目录全景

> **演示知识点**（对应文档 [02-pack/02-structure.md](../../api_docs/02-pack/02-structure.md)）：
> 包内 9 个子目录各自"谁读它、放什么、不放会怎样" · 数据面（items/ machines/
> recipes/ qualities/ icons/）与逻辑面（events/ scenes/ ui/）的分工 ·
> 机器物品 + 机器声明 + 配方三件套联动 · PSUI 最小面板（window+label+button）·
> 最小场景文件夹（scene.json）· 为什么**没有** state/ 目录。

## 文件清单

```text
ex02_dirs/
├── pack.json                    ← 包清单（唯一必备文件）
├── items/
│   └── demo_furnace.json        ← 1 个物品：机器物品"示例熔炉"
├── machines/
│   └── demo_furnace.json        ← 1 台机器声明（furnace 工厂桥接）
├── recipes/
│   └── demo_smelt.json          ← 1 条配方（联动上面那台机器）
├── qualities/
│   └── demo_polish.json         ← 1 条品质（故意不被引用，见下文）
├── icons/                       ← 【本包有意不建】不写 icon 字段 → 图标走模板切片
├── events/
│   └── tour.pss                 ← 1 个脚本：启动日志 + 开面板 + 按钮回调
├── ui/
│   └── demo_panel.psui          ← 1 张极简面板：window + label + button
└── scenes/
    └── ex02_demo/
        └── scene.json           ← 1 个最小场景文件夹
```

## 安装

把整个 `ex02_dirs` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex02_dirs/
```

## 验证（5 分钟）

1. 启动游戏，控制台应出现：

```text
[PSApi] [pss ex02_dirs] [ex02_dirs] tour.pss 已加载 — 本包在 8 个子目录各放一个最小文件 (icons/ 有意留空走模板兜底)
```

2. 数据面确认：日志搜 `rescan(init)`，包计数 +1；搜 `machine audit` 应有：

```text
machine audit: ex02_dirs:demo_furnace ui=furnace in=2 out=3 power=8 battery=True speed=1 sort=priority recipes=1
```

3. 场景面确认：日志搜 `[scenes]` 注册数 +1；进对局后打开**外出地图面板**，
   「垃圾场」下方出现「示例02·演示场」。
   ⚠ 本场景 `driver=script` 且没有配任何脚本/撤离点——**只验证列表注册，别真的点进去**。
4. 进任意存档（或新开一局），第一次进场景时面板自动弹出，日志出现：

```text
[PSApi] [psui] opened: ex02_dirs:demo_panel (3 root element(s))
```

   点「点我试试」按钮 → 日志出现 `你点了面板按钮`，面板关闭。
5. 按 **F12**：`示例熔炉` 出现在后仓（`test: true` 物品全部发放）。

## 逐目录讲解：谁读它、放什么、不放会怎样

| 目录 | 谁读它 | 放什么 | 不放会怎样 |
|---|---|---|---|
| `pack.json`（文件） | 扫描器（两模块都读） | 包清单：id 等元数据 | 整包跳过，警告 `missing pack.json` |
| `items/` | **数据面**模块 | 物品定义 JSON（顶层键 `items`） | 没有自定义物品；machines/ 声明会警告 `has no items/*.json definition → never built` |
| `machines/` | **数据面**模块 | 机器声明 JSON（顶层键 `machines`） | 物品即使模板是熔炉也只是纯装饰（audit：`has no ui → built as plain item`） |
| `recipes/` | **数据面**模块 | 配方 JSON（顶层键 `recipes`） | 机器 audit 警告 `has no recipes → native behaviour only`，本包配方线跑不起来 |
| `qualities/` | **数据面**模块 | 品质层 JSON（顶层键 `qualities`） | 物品 `qualities` 引用会警告 `references unknown quality` |
| `icons/` | **数据面**模块 | 物品图标 PNG | 图标走 template 的图集切片 → 再没有就硬兜底勋章图。**本包有意不建**：`demo_furnace` 不写 `icon` 字段，图标来自模板 `furnace` |
| `events/` | **逻辑面**模块 | PSScript 脚本 `.pss` | 没有任何逻辑；本包面板按钮会因回调未定义而点击无效 |
| `ui/` | **逻辑面**模块 | PSUI 面板 `.psui`（**只扫顶层**） | `ui.open` 报"未知面板"；`psui` 路线机器开不了窗 |
| `scenes/` | **逻辑面**模块 | `<id>.json` 扁平或 `<id>/scene.json` 文件夹 | 地图列表没有你的外出地点 |

三条加载规则（详见结构篇文档）：

- **全部子目录都是可选的**——只有 pack.json 的空包也合法（计数为 0）。
- 数据面按 `qualities → items → recipes` 顺序消费，所以物品可以放心引用品质。
- 各子目录内部允许再嵌套（`items/weapons/rifle.json` 合法），文件按路径名字典序加载。

### 为什么没有 state/ 目录？

**PSApi 的包内没有 `state/` 目录**。脚本的持久状态走 `state.*` API
（`state.set/get/has/del`），框架自动落盘到包外：

```text
UserData/PSApi/state/<存档槽>/<包id>.json
```

按存档槽隔离、换档自动重载，你不需要（也不应该）在包里管任何状态文件。
API 细节见 [05-api-reference/02-state.md](../../api_docs/05-api-reference/02-state.md)。

## 逐文件讲解

- **items/demo_furnace.json**：机器物品。`directory: "StationMachinery"` 是机器物品
  的固定落点；`template: "furnace"` 借原版熔炉的图标/形状当底子——这就是"没有
  icons/ 也能跑"的原因。
- **machines/demo_furnace.json**：机器声明。`ui: "furnace"` 走原版工厂桥接，
  槽位 `0=电池 1=模组 2=输入 3=输出` 贴原生布局（写错 audit 会警告
  `deviates from native layout`）。
- **recipes/demo_smelt.json**：`machine` 字段写自己包的机器全 id；
  原版材料用 `game:scrap_metal` 前缀写法。
- **qualities/demo_polish.json**：一条 tag 模式品质。它**故意不被任何物品引用**——
  品质是"注册后等物品来引用"的资源，引用效果见 ex10。
- **events/tour.pss**：启动日志 + `on scene_loaded` 里用标志位保证面板只弹一次 +
  `func demo_btn_clicked()` 作为 PSUI 回调。
- **ui/demo_panel.psui**：极简面板。window 根 + 两个 label + 一个 button，
  `on_click: demo_btn_clicked` 是**裸串函数名**（不加引号），启动期校验它在本包
  `events/*.pss` 里存在。
- **scenes/ex02_demo/scene.json**：文件夹形态场景。`entry: "map"` 注入地图面板；
  `driver: "script"` 声明纯脚本驱动（不要求 exit 点，撤离逻辑由脚本负责——本包
  没写，所以别点进去）；`bg: "bg_demo"` 是预留的背景图键，png 由示例包后续补充，
  缺失不影响加载。

## 动手练习

1. 把 `items/demo_furnace.json` 里的 `"directory": "StationMachinery"` 删掉重启：
   audit 出现什么警告？（提示：机器物品必须落 StationMachinery，靠 `tags: ["MACHINE"]`
   映射也行）
2. 把 `ui/demo_panel.psui` 里 `button ok_btn:` 的 `ok_btn` 改成 `ok-btn`（中划线）：
   启动日志会警告 id 不是合法标识符。
3. 删掉 `recipes/` 整个目录重启，看 audit 的 `has no recipes` 警告长什么样。

## 下一个示例

- [ex03_deps_base](../ex03_deps_base/README.md) —— 做一个"被别人依赖"的地基包
