# ex11_icons · 示例 11：图标专题

> **演示知识点**（对应文档 [03-items/06-icons.md](../../api_docs/03-items/06-icons.md)）：
> PNG 放进 `icons/` 即自定义图标 · 图标键 = `包id:文件名（无扩展名）`，
> **嵌套目录不影响键名** · `icon` 字段三种引用写法（文件对象 / 裸串 / 全限定）·
> 图标优先级三级回退 · `icon: ... from=custom/template/fallback` 诊断行 ·
> `icon keys:` 全局清单。

## 文件清单

```text
ex11_icons/
├── pack.json
├── icons/                       ← 本示例包的核心目录
│   ├── red_gem.png              ← 64×64 红宝石（随示例包提供）
│   └── sub/
│       └── blue_gem.png         ← 64×64 蓝宝石（嵌套目录演示）
└── items/
    └── gems.json                ← 3 个物品，各用一种 icon 写法
```

> 若你拿到的副本缺这两张 png：先做"验证 A（无 png 态）"，物品照样能加载
> （图标回退模板切片）——这本身就是本示例要演示的回退机制。

## 键是怎么来的

```text
icons/red_gem.png        → 图标键 "ex11_icons:red_gem"（包id:文件名去扩展名）
icons/sub/blue_gem.png   → 图标键 "ex11_icons:blue_gem"（嵌套目录不影响键名！）
```

- 只有 `.png` 会被扫描；同名 png 先加载者胜。
- 命名空间天然防跨包冲突；键必须带 `包id:` 前缀，所以**覆盖不到原版物品的图**。

## icon 字段三种写法（items/gems.json）

```json
// ① 文件对象（推荐, 最明确）—— 自动取文件名
"icon": { "file": "red_gem.png" }     → 键 "ex11_icons:red_gem"

// ② 裸字符串（省扩展名）—— png 在嵌套子目录也不影响
"icon": "blue_gem"                    → 键 "ex11_icons:blue_gem"

// ③ 全限定字符串（引用其他包/本包已有键时用）
"icon": "ex11_icons:red_gem"          → 键原样（吊坠复用红宝石的图）
```

## 安装

把整个 `ex11_icons` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex11_icons/
```

## 验证 A：无 png 态（回退演示）

1. 把 `icons/` 整个移走（或删掉两张 png），重启游戏。
2. 进存档按 **F12**：3 件物品都在——红/蓝宝石显示**模板 common_ore 的切片**、
   吊坠显示 metal_ingot 的切片（回退不炸、不丢单）。
3. 日志搜 `icon: ex11_icons:`，3 行的 `from=template`——
   **`key` 非空但 `from=template` = 图标键没命中**（png 没放对位置/名字对不上），
   这是"图标不对"排障的第一行。

## 验证 B：png 就位态（命中演示）

1. 放回两张 png，重启游戏。启动日志搜 `icon keys`，全局清单里应有：

```text
icon keys: ex11_icons:red_gem, ex11_icons:blue_gem, ...
```

2. F12 后背包里：红宝石/吊坠显示**红宝石图**（同一键两张图共用）、
   蓝宝石显示**蓝宝石图**（png 在 `sub/` 嵌套目录照样命中）。
3. 诊断行变成 `from=custom`：

```text
icon: ex11_icons:red_gem key=ex11_icons:red_gem from=custom atlas=PSApi/icons sprite=ex11_icons:red_gem
```

## 图标优先级（构建时三级回退）

```text
自定义键命中 (icons/ 里有对应 png)
  → 否则模板切片 (template 的 spriteAtlasPath/spritePath)
    → 否则硬兜底 (Items/itematlas 图集的 scav_medal 勋章图)
```

排障口诀：`from=custom` 命中 / `from=template` 键没命中或没写 / `from=fallback`
物品既无自定义图标又无模板。改了 png 没变化 → 图标启动时一次性加载，
**重启游戏**（并确认没有"文件夹+DLL 并存"，DLL 胜出时你改的文件夹根本没被读）。

## 逐物品讲解

- **红宝石**：① 对象写法。最明确也最推荐——文件名、扩展名一目了然。
- **蓝宝石**：② 裸串写法。省扩展名、省大括号；png 放在 `icons/sub/`
  嵌套子目录里，键名**不带 sub/**——文件检索递归、键只看文件名。
- **红宝吊坠**：③ 全限定写法。跨包引用图标（或本包复用）时写 `包id:名`；
  这一件证明多个物品可以共享同一个键。

## 动手练习

1. 把蓝宝石的 `icon` 改成 `"sub/blue_gem"` 重启——键变成
   `ex11_icons:sub/blue_gem`，与实际键 `ex11_icons:blue_gem` 对不上 →
   `from=template` 回退。体会"键只看文件名"。
2. 在 `icons/` 里再放一张 `green_gem.png`，给吊坠换 `"icon": "green_gem"`。
3. 试着给物品写 `"icon": "scrap_metal"` 覆盖原版废金属的图——不行：
  键会被归一成 `ex11_icons:scrap_metal`，原版命名空间碰不到
  （这是刻意的注入边界）。

## 恭喜，11 个示例全部完成

你已经走完：包清单 → 目录全景 → 依赖机制 → 物品字段 → 模板克隆 → 次数体系 →
机器三路线 → 配方 → 品质 → 图标。接下来：

- 通读 [api_docs 目录](../../api_docs/README.md) 补齐脚本（04-psscript）、
  PSUI（06-psui）、API 参考（05-api-reference）等专题；
- 复制 ex01 改 id 做你自己的包（各示例的"动手练习"串起来就是一个小 mod）。
