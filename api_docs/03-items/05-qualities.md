# 03 · 数据面（V）：品质层 qualities/*.json

> 品质层 = 同一物品的"成色"变体：改名、改价、可被标签打印机改标、可被配方
> 继承。PS-API 提供两种模式：轻量的 **tag 模式**与对齐原版机制的 **feature
> 模式**。对应引擎源码：`QualityService.cs`。

## 两种模式一图流

```text
tag 模式 (默认)                      feature 模式
─────────────────────────            ─────────────────────────
TagSystem 打标签                     原版 ItemFeature 管线
定价: 报价 × priceMul (Postfix)      定价: 原版特性管线 (自动)
tooltip: 注入"品质: X ×N"行          tooltip: 原生 [标签] 行 + 价值预估
标签打印机: ✘ 不识别                  标签打印机: ✔ 下拉可选可改
安检识别: ✘                          安检识别: 原生 (isExposable 默认关)
适合: 纯 mod 品质体系                 适合: 对标原版品质 (如"化学品纯度")
```

## 全字段表

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | **是** | 品质 id（建议 `pack:q_xxx`；物品引用时写全） |
| `tag` | string | tag 模式必填 | TagSystem 标签名（如 `quality_example_rusty`）；feature 模式可空（此时用 id 当标识） |
| `display` | string | **是** | 显示名（如"铁锈"） |
| `namePrefix` | string | 否 | 物品名前缀（v1.1） |
| `priceMul` | float | 否 | 价格系数，默认 1；≤0 修为 1。tag 模式直接乘报价；feature 模式换算成 ±百分比 |
| `tier` | int | 否 | 层级（`inherit_max_input` 品质继承时比大小用） |
| `mode` | string | 否 | `tag`(默认) / `feature` |
| `category` | string | feature | 特性类别：原版常量（如 `CATEGORY_CHEMICAL_PURITY`）或自建（如 `CATEGORY_STAR_QUALITY`）；空 = display |
| `categoryDisplay` | string | 否 | 自建类别的中文标题（打印机界面分组显示用） |

校验规则（违反即跳过并记 error）：缺 `id`、tag 模式缺 `tag`、缺 `display`。
同 id 品质后加载者覆盖前者；tag 模式同 tag 重绑也会警告。

## tag 模式详解

```json
{ "id": "my_pack:q_rusty", "tag": "quality_my_rusty", "display": "铁锈", "priceMul": 0.5, "tier": 0 }
```

工作方式：

- **打层**：给物品 `EnableTag(tag)`；同一物品的品质组（`items[].qualities`）
  互斥——打新层时自动 Disable 同组其他 tag。
- **定价**：收购还价时 `最终报价 = 原报价 × priceMul`（四舍五入、下限 1）。
- **显示**：tooltip 注入一行 `品质: 铁锈 ×0.5`；tag 的英文串经显示名补丁
  翻译成 display。

适合"我的包自己玩"的品质体系，不与原版标签打印机交互。

## feature 模式详解

```json
{ "id": "my_pack:q_lab_pure", "mode": "feature",
  "category": "CATEGORY_CHEMICAL_PURITY", "display": "星尘纯品", "priceMul": 4.0, "tier": 4 }
```

工作方式：

- 走**原版 ItemFeature 管线**：构建 `ItemFeature(category)` + `ItemCondition`
  （`modValue` = (priceMul-1)×100 百分比），自动获得原生 tooltip 格式
  `-星尘纯品 (+300%)`、估价与基础价值分离显示。
- `category` 填**原版已有类别**（并入该表）或**自建类别**（注册全新表，
  打印机下拉出现独立分组，配 `categoryDisplay` 给中文标题）。
- 并入原版标签打印机 `conditionLookup`——玩家可以像改原版品质一样改标。
- `priceMul` 换算为百分比修值，定价由原版管线负责（不二次乘系数）。
- 自建品质默认 `isExposable=false`（不参与"假标签被识破"链路）。

## 品质从哪来：四个入口

| 入口 | 声明处 | 效果 |
|---|---|---|
| 出厂默认 | 物品 `qualities` + `defaultQuality` | 注册时打一层（缺省 `qualities[0]`） |
| 配方继承 | 配方 `qualityRule` | `inherit_frame` / `inherit_max_input` |
| 打印机改标 | feature 模式 + 标签打印机 | 玩家手动操作 |
| 脚本 API | `.pss` 品质函数 | 运行时动态打层（见 [05 API 参考](../05-api-reference/README.md)） |

物品侧挂接（`items/*.json`）：

```json
"qualities": ["my_pack:q_rusty", "my_pack:q_pure"],
"defaultQuality": "my_pack:q_pure"
```

> 注意：互斥组只收录 **tag 模式**的品质；feature 模式品质可以引用（作为
> 出厂默认/配方继承），但互斥清理由原版"同类别替换"语义接管。

## 示例带读

`example_hello/qualities/example_qualities.json` 五条品质覆盖全部玩法：

```json
{ "qualities": [
  // tag 模式基本款: 半价
  { "id": "example_hello:q_rusty", "tag": "quality_example_rusty",
    "display": "铁锈", "priceMul": 0.5, "tier": 0 },

  // tag 模式溢价款
  { "id": "example_hello:q_pure", "tag": "quality_example_pure",
    "display": "纯净", "priceMul": 1.5, "tier": 1 },

  // feature 模式 × 原版类别: 并入"化学品纯度"表, 打印机可改
  { "id": "example_hello:q_lab_pure", "mode": "feature",
    "category": "CATEGORY_CHEMICAL_PURITY",
    "display": "星尘纯品", "priceMul": 4.0, "tier": 4 },

  // feature 模式 × 自建类别: 打印机出现独立的"星辉品级"分组
  { "id": "example_hello:q_star_infused", "mode": "feature",
    "category": "CATEGORY_STAR_QUALITY", "categoryDisplay": "星辉品级",
    "display": "星辉灌注", "priceMul": 5.0, "tier": 5 },

  // 自建类别第二档: 测试下拉切换
  { "id": "example_hello:q_star_faded", "mode": "feature",
    "category": "CATEGORY_STAR_QUALITY",
    "display": "星辉黯淡", "priceMul": 0.5, "tier": 1 }
] }
```

配合 `items/_placeholder.json` 的示例净水瓶（`defaultQuality: q_star_infused`
——出厂即五倍价星辉水）一起读。

---

下一篇：[06 · 图标](06-icons.md)
