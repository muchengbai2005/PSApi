# ex10_qualities · 示例 10：品质层

> **演示知识点**（对应文档 [03-items/05-qualities.md](../../api_docs/03-items/05-qualities.md)）：
> 品质层 = 同一物品的"成色"变体（改名、改价、可被打印机改标、可被配方继承）·
> **tag 模式**（轻量）与 **feature 模式**（对齐原版管线）双模式 ·
> feature 挂**原版 category** vs **自建 category + categoryDisplay** ·
> `priceMul` 定价系数（含低至 0.5 的对比）· 物品 `qualities`/`defaultQuality`
> 出厂默认 · 脚本三件套 `quality.set/get/price_factor`。

## 两种模式一图流

```text
tag 模式 (①④)                      feature 模式 (②③)
──────────────────────────         ──────────────────────────
TagSystem 打标签                    原版 ItemFeature 管线
定价: 报价 × priceMul (Postfix)     定价: 原版特性管线 (自动)
tooltip: 注入"品质: X ×N"行         tooltip: 原生 [标签] 行 + 价值预估
标签打印机: ✘ 不识别                标签打印机: ✔ 下拉可选可改
适合: 纯 mod 品质体系               适合: 对标原版品质 (如"化学品纯度")
```

## 文件清单

```text
ex10_qualities/
├── pack.json
├── qualities/
│   └── tour.json          ← 4 条品质（下表）
├── items/
│   └── demo_ingot.json   ← 1 个物品：defaultQuality 引用 q_fine
└── events/
    └── quality_demo.pss   ← on game_loaded 发锭 + 打层/读层/系数对比
```

| 品质 | 模式 | category | priceMul | 演示点 |
|---|---|---|---|---|
| `q_fine` 精良 | tag | — | 1.5 | 基本款溢价（物品出厂层） |
| `q_lab` 实验室纯品 | feature | `CATEGORY_CHEMICAL_PURITY`（原版） | 3.0 | 并入原版"化学品纯度"表，打印机可改 |
| `q_star` 星辉灌注 | feature | `CATEGORY_EX10_STAR`（自建）+ categoryDisplay"星辉品级" | 5.0 | 打印机出现独立分组 |
| `q_bargain` 瑕疵品 | tag | — | 0.5 | 低 priceMul 对比（半价） |

## 安装

把整个 `ex10_qualities` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex10_qualities/
```

## 验证（5 分钟）

1. 新开一局（或读档），第一次 `on game_loaded` 自动发 1 块银锭并演示打层，日志出现：

```text
[PSApi] [pss ex10_qualities] [ex10_qualities] 出厂品质: ex10_qualities:q_fine 系数 1.5 (defaultQuality=q_fine ×1.5)
[PSApi] [pss ex10_qualities] [ex10_qualities] 打上 q_bargain 后: ex10_qualities:q_bargain 系数 0.5 (半价对比)
[PSApi] [pss ex10_qualities] [ex10_qualities] 换成 feature 模式 q_lab: ex10_qualities:q_lab — tooltip 是原生 [标签] 行, 标签打印机可改
```

   （演示只在每个存档第一次跑——脚本里用 `state.has` 防重。）
2. F12 再发一块 `示例银锭`，背包看 tooltip：`品质: 精良 ×1.5`。
   拿去卖货还价，报价约为原价 1.5 倍。
3. 标签打印机（原版机器）实验：
   - 把 F12 发的银锭放进打印机 → 下拉出现原版"化学品纯度"表里的
     **实验室纯品**（②），以及全新的 **"星辉品级"分组**（③，两条：星辉灌注/缺一条
     第二档时可做练习 2）；
   - `q_fine`/`q_bargain` 是 tag 模式 → **不出现在打印机下拉**（不识别）。
4. 低 priceMul 对比：脚本第二步把锭打上 `q_bargain` 后系数 0.5——
   同一物品，品质层决定它是 150 还是 50。

## 逐文件讲解

### qualities/tour.json —— 四条品质

- **tag 模式**（`mode` 缺省即 tag）：`tag` 是 TagSystem 标签名（必填），
  `display` 显示名（必填），`priceMul` 直接乘收购报价（≤0 修为 1）。
- **feature 模式**：`category` 填**原版已有类别**（并入该表）或**自建类别**
  （注册全新表）；`categoryDisplay` 给自建类别配中文标题（打印机分组显示）；
  `priceMul` 换算成 ±百分比（3.0 → +200%），定价由原版管线负责。
- **tier**：`inherit_max_input` 品质继承比大小用（见 ex09 的 qualityRule）。
- **校验**：缺 `id`、缺 `display`、tag 模式缺 `tag` → 跳过记 error。
  同 id 后加载者覆盖。

### items/demo_ingot.json —— 挂接

```json
"qualities": ["ex10_qualities:q_fine", "ex10_qualities:q_bargain", "ex10_qualities:q_lab", "ex10_qualities:q_star"],
"defaultQuality": "ex10_qualities:q_fine"
```

- 引用必须写**全 id**，且品质必须已注册（数据面按 qualities→items 顺序加载，
  顺序天然保证）。
- `defaultQuality` 不写取 `qualities[0]`；出厂时自动打这一层。
- 互斥组只收录 **tag 模式**品质（q_fine/q_bargain 互斥，打新层自动清旧层）；
  feature 模式可以引用，互斥清理由原版"同类别替换"语义接管。

### events/quality_demo.pss —— 三件套 API

```pss
quality.set(h, "ex10_qualities:q_bargain")   # 打层（未注册 id → false 不报错）
quality.get(h)                              # 全限定品质 id | null
quality.price_factor(h)                     # 系数（无品质/异常 = 1）
```

都接**物品句柄**（先 `items.find` 判空）。脚本开头用
`state.has` / `state.set` 做"每个存档只演示一次"——这正是
[05-api-reference/02-state.md](../../api_docs/05-api-reference/02-state.md)
的"只做一次"标准姿势。

## 品质从哪来：四个入口

| 入口 | 声明处 | 本包用到 |
|---|---|---|
| 出厂默认 | 物品 `qualities` + `defaultQuality` | ✔（q_fine） |
| 配方继承 | 配方 `qualityRule`（见 ex09） | — |
| 打印机改标 | feature 模式 + 标签打印机 | ✔（q_lab/q_star） |
| 脚本 API | `quality.set` | ✔（q_bargain/q_lab） |

## 动手练习

1. 把物品的 `defaultQuality` 改成 `q_star` 重启：F12 发的锭 tooltip 变原生
   `[星辉灌注] (+400%)` 行——feature 模式的 tooltip 格式。
2. 仿照 example_hello 给 `CATEGORY_EX10_STAR` 再加第二档 `q_star_faded`
   （`priceMul: 0.5, tier: 1`），打印机"星辉品级"分组里就能上下切换了。
3. 把 `q_fine` 的 `tag` 删掉重启：日志出现校验 error，品质被跳过，
   物品引用它时警告 `references unknown quality`。

## 下一个示例

- [ex11_icons](../ex11_icons/README.md) —— 图标专题：png 引用三写法与诊断行
