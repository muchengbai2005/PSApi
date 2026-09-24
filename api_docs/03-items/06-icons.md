# 03 · 数据面（VI）：图标 icons/*.png

> 把 PNG 放进包内 `icons/`，物品就能用自定义图标。对应引擎源码：
> `IconService.cs`（加载）+ `ItemStore.ResolveIconKey`（引用解析）。

## 放置与命名

```text
packs/<你的包>/
└── icons/
    ├── rifle.png          → 图标键 "my_pack:rifle"（包id:文件名去扩展名）
    └── parts/barrel.png   → 图标键 "my_pack:barrel"（嵌套目录不影响键名）
```

- 键 = `包id:文件名（无扩展名）`，命名空间天然防跨包冲突。
- 只有 `.png` 会被扫描（其他格式忽略）。
- 同名 png 先加载者胜（后加载跳过）。
- 建议尺寸：任意（整图转 Sprite，点过滤渲染，与原版像素风一致即可；
  常见 32×32 ~ 96×96）。

## 物品里怎么引用（icon 字段三种写法）

```json
// ① 文件对象（推荐, 最明确）
"icon": { "file": "rifle.png" }        → 键 "my_pack:rifle"（自动取文件名）

// ② 裸字符串（省扩展名）
"icon": "rifle"                        → 键 "my_pack:rifle"

// ③ 全限定字符串（引用其他包的图标时用）
"icon": "other_pack:rifle"             → 键原样
```

三种最终都归一到 `包id:名字` 的图标键。不写 `icon` → 用模板的图集切片。

## 图标优先级（构建时）

```text
自定义键命中 (icons/ 里有对应 png)
  → 否则模板切片 (template 的 spriteAtlasPath/spritePath)
    → 否则硬兜底 (Items/itematlas 图集的 scav_medal)
```

控制台每个物品构建时会打一行**图标诊断**（排"图标不对"必看）：

```text
icon: my_pack:rifle key=my_pack:rifle from=custom atlas=PSApi/icons sprite=my_pack:rifle
                 ↑ 引用键                 ↑ custom=命中 / template=用模板 / fallback=兜底
```

`key` 不为空但 `from=template` → 键没命中：png 没放对位置、文件名与引用
对不上。启动时还有一行全局清单可搜：`icon keys: my_pack:rifle, my_pack:barrel, ...`。

## 技术细节（了解即可）

- PNG 解码为 `Texture2D`（RGBA32、Point 点过滤、Clamp），整图 `Sprite.Create`
  （pivot 居中、100 pixels-per-unit）。
- 注入方式是拦截游戏的两个图标解析入口（`ResolveSpriteByName` 与
  `LoadFromAtlas`）：命中自定义键才接管，原版切片零影响——所以**不要**想
  用自定义图标覆盖原版物品的图（键必须带 `包id:` 前缀，覆盖不到原版命名空间）。
- DLL 分发时 png 字节级原样内嵌（见[02 · 编译分发](../02-pack/03-distribution.md)）。

## 常见问题

**图标显示为兜底勋章图** → `from=fallback`：物品既无自定义图标又无模板。

**改了 png 没变化** → 图标在启动时一次性加载，重启游戏；确认没有
"文件夹+DLL 并存"（DLL 胜出，你改的文件夹没被读）。

**想给机器换图标** → 机器也是物品，同样用 `icon` 字段（`example_processor`
就用 `icon: {file: "mcb_machine_spp.png"}` 复用了包内另一张图）。

---

数据面三章到此完整（物品 → 机器 → 配方 → 品质 → 图标）。接下来：
- [04 PSScript 语言](../04-psscript/README.md)——给内容注入逻辑与玩法
