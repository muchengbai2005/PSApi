# ex04_deps_user · 示例 04：依赖前置包（prerequisites）

> **演示知识点**（对应文档 [02-pack/README.md](../../api_docs/02-pack/README.md)）
> 与 [02-pack/02-structure.md](../../api_docs/02-pack/02-structure.md)）：
> `pack.json` 的 `prerequisites` 字段 · 缺前置时的**真实行为**（日志 + 弹窗）·
> 存在性检查的三条推论（不传递 / 循环不报错 / 不改顺序）·
> 脚本侧 `items.name`（注册检查）与 `items.has`（持有检查）的区别。

## 文件清单

```text
ex04_deps_user/
├── pack.json            ← 包清单，比普通包多一个 prerequisites 字段
└── events/
    └── check_dep.pss    ← on game_loaded 里做双层运行时检查
```

⚠ 本包**必须**和 [ex03_deps_base](../ex03_deps_base/README.md) 一起安装，
单装会被整包跳过（这正是本示例要教的现象）。

## 安装

两个文件夹都复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex03_deps_base/
<游戏目录>/UserData/PSApi/packs/ex04_deps_user/
```

## 验证 A：两个包都在（正常态）

1. 启动游戏，日志应出现：

```text
[PSApi] [pss ex04_deps_user] [ex04_deps_user] check_dep.pss 已加载 — 本包声明了前置 ex03_deps_base, 加载器已保证'它在'才会加载我们
```

2. 进任意存档（`on game_loaded` 触发）后应出现：

```text
[PSApi] [pss ex04_deps_user] [ex04_deps_user] 前置物品已注册: ex03_deps_base:base_token (显示名: 基础代币)
```

   按 F12 发放过基础代币的话，紧跟一行 `背包/后仓里也有实体 — 前置完全就绪`。

## 验证 B：删掉 ex03 重启（缺前置态）——本示例的重头戏

1. 把 `ex03_deps_base` 文件夹移出 `packs/`，重启游戏。
2. 启动日志出现（数据面/逻辑面两模块各打一遍，**同一条出现两次是正常现象**）：

```text
pack 'ex04_deps_user': missing prerequisites: ex03_deps_base, skipped
```

3. 首次进入主菜单（EmporiumMenu）弹出「PS-API 内容包冲突」窗：

```text
┌─ PS-API 内容包冲突 ──────────────┐
│  缺少前置模组:                   │
│    ex04_deps_user 需要 ex03_deps_base │
│                [ 确定 ]          │
└──────────────────────────────────┘
```

4. 日志里还有两条配套行（弹窗明细 + 确认行）：

```text
pack conflict [missing_prereq]: ex04_deps_user 需要 ex03_deps_base
pack conflict popup shown: missing_prereq=1 duplicate=0
```

5. `check_dep.pss` 的日志一条都不会出现——整包被跳过，脚本根本没加载。

## 逐文件讲解

### pack.json —— 比普通包多一个字段

```json
"prerequisites": ["ex03_deps_base"]
```

- 每项填一个**内容包 id**（不是 PSApi 版本，不是游戏版本），大小写不敏感。
- 求值发生在**所有包扫描合并完之后**，只看"id 是否被扫描到"，**不改变加载顺序**。
- 缺任何一个 → 整包跳过 + 记冲突 + 弹窗。

### events/check_dep.pss —— 为什么还要运行时再查？

`prerequisites` 是**存在性检查**，三条推论（见结构篇文档）：

1. **不传递**：A 依赖 B、B 依赖 C，C 没装 → B 被跳过，但 B 的 id 仍在集合里，
   A 照常加载（然后运行时找不到 B 的内容）。
2. **循环依赖不报错**：A↔B 互相依赖 → 双双通过、正常加载。
3. **不改变顺序**：声明依赖不会让任何包提前或延后。

所以依赖链深的包要在脚本里"自证"前置内容真的可用。本包演示两层：

| 检查 | 函数 | 答的问题 | 未命中时 |
|---|---|---|---|
| 注册检查 | `items.name("ex03_deps_base:base_token")` | 物品注册进目录了吗 | 返回 `null` |
| 持有检查 | `items.has("ex03_deps_base:base_token")` | 随身背包+后仓里有吗（计入堆叠） | 返回 `false` |

函数细节见 [05-api-reference/06-items.md](../../api_docs/05-api-reference/06-items.md)。

## 动手练习

1. 做"不传递"实验：给 ex03 的 `pack.json` 加上 `"prerequisites": ["not_installed_pack"]`
   重启 → ex03 被跳过，但 ex04 照常加载（它的日志出现，`items.name` 返回 null 触发 warn）。
2. 把 ex04 的 `prerequisites` 里 id 改成 `EX03_DEPS_BASE`（大写）重启 → 照常加载
   （id 比较不区分大小写）。
3. 给 ex03 和 ex04 互相声明 prerequisites（循环依赖）→ 两个包都正常加载，不报错。

## 下一个示例

- [ex05_item_basic](../ex05_item_basic/README.md) —— 物品字段全家桶
