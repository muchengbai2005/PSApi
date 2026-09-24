# 05 · 06 items / quality / machine：物品三命名空间

> 物品是 PS-API 的核心对象。`items` 管发放/检索/估价/消耗与电力次数，
> `quality` 管品质层，`machine` 管多夜加工。三者共享**物品句柄**（`items.find`
> 等的返回值），句柄本身只有四个只读成员，操作全走命名空间函数。

## 物品句柄（速览）

```pss
var it = items.find("game:scrap_metal")
if it != null:
    log.info("{it.id} x{it.count} 单价{it.value} uid={it.uid}")
```

成员：`id`（物品 id）/ `count`（堆叠数）/ `value`（单价）/ `uid`（存档内唯一号）。
只读、无方法；失效后访问报友好错误。详见 [10 句柄](10-handles.md)。

## items：发放 / 持有 / 检索 / 消耗

| 函数 | 签名 | 返回 | 自 |
|---|---|---|---|
| 发进后仓 | `items.give(id[, count = 1[, uses = 0]])` | `bool` | — |
| 摆上柜台 | `items.give_counter(id[, count = 1[, uses = 0]])` | `bool` | v1.9.1 |
| 持有判定 | `items.has(id[, count = 1])` | `bool` | — |
| 单价 | `items.value(id)` | `int`（未知/不在对局 = `-1`） | — |
| 显示名 | `items.name(id)` | `string \| null`（未知 = `null`） | v1.11.0 |
| 找一个 | `items.find(id)` | 物品句柄 \| `null` | — |
| 找全部 | `items.find_all(id)` | `[物品句柄]` | v1.7.0 |
| 消耗 | `items.consume(item_handle)` | `bool` | — |

**发放与检索的范围**（源码保证）：

- `items.give` 发进**后仓**（对局外调用 = `false`）。
- `items.give_counter` 摆到**店门口柜台**（桌面直售区），归玩家所有；
  贴原版送礼路径，玩家可以直接拿起。不在对局 = `false`；柜台满位时
  剩余物品悬空（原生不处理，日志有警告）。
- `items.has / find / find_all` 搜**随身背包 + 后仓**（含容器嵌套——包里有盒子、
  盒子里有物品也能搜到；`has` 计入堆叠数）。
- `uses > 0`（1..9999）时发放的是**次数物品**（用完即毁，见下文"使用次数"）。

```pss
# e5_demo.pss 真实用法（原为注释演示）
var ok = items.give("game:newspaper", 2)
var has2 = items.has("game:newspaper", 2)
var price = items.value("game:newspaper")
log.info("赠报: give={ok} has(>=2)={has2} 单价={price}")

# find + 判空是标准姿势
var scrap = items.find("game:scrap_metal")
if scrap != null:
    ...
```

`items.consume` 接**句柄**（不是 id）：从所在库存取出并销毁，与机器配方的
探测物回收同路径。`items.find` 没找到时返回 `null`，直接把 `null` 传进来会报
`第一个参数是 null (items.find/machine.find 没找到? 请先判空)`——先判空。

## items：电力与使用次数（v1.6.0）

电池与次数物品的运行时操控（机器隔夜结算、有限次工具等玩法）：

| 函数 | 签名 | 返回 |
|---|---|---|
| 电量 | `items.power(item_handle)` | `int`（非电源 = `0`） |
| 抽电 | `items.power_draw(item_handle, amount)` | `bool`（**全有或全无**：不足不扣返回 `false`） |
| 剩余次数 | `items.uses(item_handle)` | `int`（无次数机制 = `0`） |
| 次数上限 | `items.uses_max(item_handle)` | `int`（无次数机制 = `0`） |
| 用 n 次 | `items.use(item_handle[, n = 1])` | `bool`（不足返回 `false`；归零销毁） |
| 补启用次数 | `items.use_init(item_handle, max[, base_value = 0[, value_per_use = 0]])` | `bool` |

`use_init` 给一个**已有物品实例**启用次数机制——语义同数据面 `items/*.json` 的
`useCount` 字段（见 [03 · 数据面 JSON](../03-items/README.md)），适合给原版物品
临时挂次数（如"这把刀只能再用 3 次"）。`base_value / value_per_use` 控制次数
折算价（默认 0 = 不折算）。

```pss
var bat = items.find("game:battery")
if bat != null:
    if items.power_draw(bat, 10):
        log.info("抽了 10 电, 剩 {items.power(bat)}")
```

## items：物品目录浏览器（M3 管理面板配套）

| 函数 | 签名 | 返回 |
|---|---|---|
| 分类列表 | `items.catalog()` | `{cats: [30 个中文标签], total: 总物品数}` |
| 分页浏览 | `items.browse(cat_idx, 搜索词[, page = 0[, page_size = 14]])` | `{total, page, pages, items}` |

- `cat_idx`：`-1` = 全部分类；`0..29` = 分类下标（`cats` 数组同序：
  0 杂项 / 1 工具 / 2 药品 / 3 食物 / 4 酒类 / 5 水培 / 6 畜牧 / 7 材料 / 8 枪械 /
  9 枪械配件 / 10 近战武器 / 11 爆炸物 / 12 护甲 / 13 容器 / 14 家具 / 15 模块 /
  16 Mod物品 / 17 站台机械 / 18 废弃机械 / 19 钥匙 / 20 器官 / 21 便利设施 /
  22 建造 / 23 装备 / 24 飞船 / 25 飞船系统 / 26 技师背包 / 27 玩家能力 /
  28 未使用 / 29 测试）。
- `搜索词`：大小写不敏感，同时匹配 id 与显示名；空串 = 不过滤。
- `page_size` 1..100；`page` 越界自动收敛到最后一页。
- `items` 每行：`{id, name, value, cat}`。

```pss
var r = items.browse(8, "", 0, 14)     # 枪械类第一页
log.info("枪械共 {r['total']} 件, 本页:")
for e in r["items"]:
    log.info("  {e['name']} ({e['id']}) 单价{e['value']}")
```

## quality：品质层

品质是"贴在物品实例上的一层修饰"（数据面定义见
[03 · 品质 JSON](../03-items/05-qualities.md)），三个函数都接**物品句柄**：

| 函数 | 签名 | 返回 |
|---|---|---|
| 打层 | `quality.set(item_handle, quality_id)` | `bool` |
| 读层 | `quality.get(item_handle)` | `string \| null`（全限定品质 id，如 `"my_pack:q_pure"`；无品质 = `null`） |
| 定价系数 | `quality.price_factor(item_handle)` | `float`（无品质/异常 = `1`） |

```pss
# e5_demo.pss 真实用法: 打层前后 price_factor 对比
items.give("game:scrap_metal", 1)
var scrap = items.find("game:scrap_metal")
if scrap != null:
    var before = quality.price_factor(scrap)
    var setok = quality.set(scrap, "example_hello:q_pure")
    var after = quality.price_factor(scrap)
    log.info("品质演示: set={setok} 品质={quality.get(scrap)} price_factor {before} -> {after}")
```

品质 id 用**全限定**（`包id:品质id`）；未注册的品质 id 返回 `false`（不报错）。

## machine：多夜加工机器

| 函数 | 签名 | 返回 |
|---|---|---|
| 找机器 | `machine.find(item_id)` | 物品句柄 \| `null` |
| 加工进度 | `machine.progress(item_handle)` | `float` 0..1；**批次机/非进度机 = `-1`** |
| 正在生产 | `machine.producing(item_handle)` | 产出物品 id `string \| null`（无匹配配方 = `null`） |

`machine.find` 的搜索范围是**全店活动库存**（后仓/柜台/展示区/隐藏区/已售区/
垃圾箱/博士柜/地摊/夜市/雇员包/交换缓冲等十五处）——机器放店里任何角落都找得到，
这也是它和 `items.find`（只搜随身+后仓）的关键区别。

```pss
# e5_demo.pss 真实用法
var mach = machine.find("example_hello:example_desequencer")
if mach == null:
    mach = machine.find("example_hello:example_processor")
if mach != null:
    log.info("机器: progress={machine.progress(mach)} producing={machine.producing(mach)}")
```

机器的数据面定义（`machines/*.json`、进度机/批次机区别）见
[03 · 数据面 JSON](../03-items/README.md)；机器**界面**（`ui.machine_*`）见
[09 UI 函数](09-ui.md)。

## 本篇函数速查

```pss
items.give(id[, n[, uses]])        # 发进后仓
items.give_counter(id[, n[, uses]])# 摆上柜台
items.has(id[, n])                 # 持有判定
items.value(id)                    # 单价 | -1
items.name(id)                     # 显示名 | null
items.find(id)                     # 句柄 | null (随身+后仓)
items.find_all(id)                 # [句柄] (全量)
items.consume(h)                   # 销毁
items.power(h) / power_draw(h, n)  # 电量/抽电
items.uses(h) / uses_max(h) / use(h[, n]) / use_init(h, max[, bv[, vpu]])  # 次数
items.catalog() / browse(cat, search[, page[, size]])                     # 目录

quality.set(h, qid) / get(h) / price_factor(h)

machine.find(id)                   # 句柄 | null (全店)
machine.progress(h)                # 0..1 | -1
machine.producing(h)               # 产出 id | null
```

---

**本篇完。** 下一篇：[07 · inject：物品池注入](07-inject.md)。
