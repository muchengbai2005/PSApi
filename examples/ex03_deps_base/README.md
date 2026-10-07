# ex03_deps_base · 示例 03：被依赖的地基包

> **演示知识点**（对应文档 [02-pack/README.md](../../api_docs/02-pack/README.md)）：
> "地基包"是什么：一个平平无奇、但被其他包通过 `prerequisites` 依赖的包 ·
> 跨包引用内容的写法（`包id:物品名` 全 id）· 内容 id 的命名空间机制。

## 我是地基包

本包和 ex01 没有本质区别——一个 pack.json + 一个物品。它特殊的唯一原因是：

```text
ex04_deps_user/pack.json
  "prerequisites": ["ex03_deps_base"]   ← 引用了本包的 id
```

依赖关系里两种"被依赖的东西"要分清：

| 被依赖的东西 | 机制 | 本例 |
|---|---|---|
| **包**（id 存在性） | `pack.json` 的 `prerequisites`，缺了整包不加载 | `ex03_deps_base` 这个 id |
| **内容**（物品等） | 脚本/配方里写全 id 引用，如 `items.has` | `ex03_deps_base:base_token` |

命名空间铁律：物品 id 的前半段 = 包 id。所以 `ex03_deps_base:base_token`
的"姓"永远是 `ex03_deps_base`——别的包想引用它就必须写全这个 id。

## 文件清单

```text
ex03_deps_base/
├── pack.json            ← 包清单（没有 prerequisites，地基包不依赖任何人）
└── items/
    └── base_token.json  ← 1 个物品：基础代币（template=metal_ingot）
```

## 安装

把整个 `ex03_deps_base` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex03_deps_base/
```

单独安装本包完全合法、完整可玩；配合 ex04 一起装则是"依赖链"教学。

## 验证（2 分钟）

1. 启动游戏，日志搜 `rescan(init)`，包计数 +1。
2. 进任意存档按 **F12**：`基础代币` 出现在后仓，单价 50。

## 逐文件讲解

- **pack.json**：注意它**没有** `prerequisites` 字段——地基包不依赖任何人，
  这是依赖链的最底层。
- **items/base_token.json**：
  - `id: "ex03_deps_base:base_token"`：全 id 写法，前半段就是包 id。
  - `template: "metal_ingot"`：原版金属锭的裸 id，继承图标/形状/行为基线。
  - `value: 50`、`test: true`：单价 50；进 F12 测试分类，方便验证。

## 动手练习

1. 把本包 `pack.json` 的 `id` 改成 `ex03_renamed`（文件夹名不动），重启：
   物品照常注册（id 与目录名不一致时加载器不拦），但 ex04 的 `prerequisites`
   找的是旧 id → 立刻触发"缺少前置模组"。这就是"id 与文件夹名保持一致"建议的由来。
2. 把物品的 `value` 改成 500，去 ex04 的验证里看跨包读取的结果变化。

## 下一个示例

- [ex04_deps_user](../ex04_deps_user/README.md) —— 依赖方：prerequisites 声明 + 运行时检查
