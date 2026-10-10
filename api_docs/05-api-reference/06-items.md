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
| 全量枚举 | `items.inventory()` | `[物品句柄]` | v2.0.2 |
| 消耗 | `items.consume(item_handle[, count])` | `bool` | count 参数 v1.39.0 |

**发放与检索的范围**（源码保证）：

- `items.give` 发进**后仓**（对局外调用 = `false`）。
- `items.give_counter` 摆到**店门口柜台**（桌面直售区），归玩家所有；
  贴原版送礼路径，玩家可以直接拿起。不在对局 = `false`；柜台满位时
  剩余物品悬空（原生不处理，日志有警告）。
- `items.has / find / find_all` 搜**随身背包 + 后仓**（含容器嵌套——包里有盒子、
  盒子里有物品也能搜到；`has` 计入堆叠数）。
- `items.inventory()` 不按 id 过滤，**全量枚举**随身背包 + 后仓（v2.0.2；
  psconsole 的 `bag clear` 清包指令即基于它）；无物品 = 空表。
- `uses > 0`（1..9999）时发放的是**次数物品**（用完即毁，见下文"使用次数"）。

```pss
# 教学写法
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

v1.39.0 起 `consume` 有第 2 可选参 `count`：省略或 `count ≥ 堆叠数` = 整堆销毁
（旧行为）；`count < 堆叠数` = 直接扣减 `unitCount` 返回 `true`，不整堆移除。
部分消耗（如 `items.on_target` 回调里吃掉拖拽物 1 件）务必传 count。

## items：物品对物品 use（v1.39.0）

`items.on_target(source_id, target_id, fn)` 注册「把物品 A 拖到物品 B 上」的脚本回调，
通用化原版 `module_bay_expansion_kit` 拖机器升级那套交互（原生判定走
`MachineHelper.CanExpand`，对自定义机器必然 false，框架用 Harmony 接管）。

```pss
func expand(h_source, h_target):
    if h_target.get_data("mod_upgraded", 0) == 1:
        return false            # 放行原生 (自定义机器上 = 无事发生, 不消耗)
    h_target.set_data("mod_upgraded", 1)
    items.consume(h_source, 1)  # 吃掉套件 1 个
    return true                 # 跳过原生 (事件已处理完)

items.on_target("game:module_bay_expansion_kit", "my_pack:refinery_furnace", expand)
```

- **三方法补丁**：`GameItem.MayTarget/CanTarget` postfix 只按 **id 对**命中注册表就放行
  （拖拽高亮每帧调用，**不调 pss**，避免每帧脚本开销）；`GameItem.Target` prefix
  才真正调 `fn(h_source, h_target)`——`fn` 的两个参数都是物品句柄。
- **返回值语义**：`fn` 明确返回 `false` = 放行原生 `Target`；返回其他任何值
  （含 `true` / 无返回）= 跳过原生。脚本异常被隔离记警告，本笔放行原生。
- **id 归一**：注册与查找两侧都过 `NormalizeId`（`game:` 前缀大小写不敏感剥除），
  写 `"game:xxx"` 或裸 `"xxx"` 等价。
- **重复注册**同 (source, target) 键 = 覆盖 + 警告（包脚本顶层每次加载重新注册，
  可调对象不随存档持久化）。
- 注册在**顶层**执行一次即可；`fn` 必须是 pss 函数（传其他类型报参数错）。

## items：自定义 tooltip 行（ps_tooltip，Items v0.9.16）

任意物品句柄写 `ps_tooltip` NBT（列表），tooltip 会在原有内容之后**原样逐行追加**
（行序 = 写入序；逐行容错，单行异常不影响其余行）。`set_data("ps_tooltip", null)` 删键 =
不显示。通用机制，机器信息栏聚合效果、储罐内容物等自定义状态行都走这里。

```pss
m.set_data("ps_tooltip", ["模组效果:", "-质量: +75% (熔炼档 2)", "-性能: -6% 燃料"])
# v0.9.17 起: dict 行 {text, color} 按色上色 (color 缺省/非法回退纯色行, 不丢文本)
t.set_data("ps_tooltip", [{text = "内容物: 铜液", color = "#D88C4A"}, "储量: 36/100 mB"])
# 无内容时删键: m.set_data("ps_tooltip", null)
```

- 读取路径与鉴定 tooltip 相同（`ItemsFacade.GetData(item, "ps_tooltip")` → JsonArray），
  机器/容器被拿起（物品形态）时同样生效 — 缓存写在 NBT 里，不依赖面板存活。
- 行元素：纯字符串 = 纯色行；dict `{text=..., color="#RRGGBB"/"#RRGGBBAA"}` = 上色行
  （v0.9.17；8 位色 alpha 忽略）。其他类型的行静默跳过。

## items：武器耐久三键约定（ps_dur / ps_dur_max / ps_wear / ps_broken，Events v1.43.0）

带 `ps_dur_max` NBT 的武器参与耐久体系（gunworks 组装枪在 `bench_apply_mat` 时写入；
**没有的武器 — 原版枪/战利品枪 — 完全不受影响，零开销**）：

| 键 | 类型 | 含义 |
|---|---|---|
| `ps_dur_max` | long | 耐久上限（枪种基础 320-600 × 机匣硬度修正；gunworks v0.38.0 起，旧为 80-150） |
| `ps_dur` | double（两位小数） | 当前耐久；≤0 钳 0 并进入破损 |
| `ps_wear` | double | 单次损耗（=枪种基础 1.0-1.5 ×(1+枪机硬度/100)，下限 0.1；gunworks v0.38.0 起，旧恒以 1 为基数） |
| `ps_broken` | long=1 | 破损标记（存在即破损；修理删键复原） |

- **扣减**（C# 侧，pss 无需调用）：rt 战斗实际开火每发（连发每发；卡壳未射出不扣）+
  近战抡/枪托抡每次挥击，`ps_dur -= ps_wear`（两位小数写回），并同步重写 `ps_tooltip`
  「耐久： x/y」行（x 取整）；扣穿钳 0 + 写 `ps_broken=1` + 追加「已破损」行。
  破损**不毁枪不禁止开火**，伤害 ×0.3（RtWeapon 解析处单点，枪抡/枪托/近战同源）。
- **修理**（包侧脚本，走 `items.on_target`）：拖修理物到枪上 → 消耗 1 件、
  `ps_dur=ps_dur_max`、删 `ps_broken`、tooltip 回满去「已破损」。
  （gunworks v0.38.0 起修理物为打印维修件 `repair_kit`，材质须匹配机匣 `ps_mat_receiver`；
  旧版为 17 种原料反查匹配。）
  耐久行/破损行的重写约定与 C# `WeaponDurability.RewriteTip` 一致（「耐久： 」前缀行替换
  否则插行首，「已破损」行按状态增删），包侧自行维护 tooltip 时照此保持兼容。

## items：原生实例 tag 读取（tag_get_int / tag_get_string，v1.42.0 / Items v0.9.18）

读物品实例的原生 tag（非 PSD_ 自定义数据）——原版模组效果值、MODULE_TYPE 等。

```pss
var p = items.tag_get_int(it, "TEMP_PERCENTAGE_PERFORMANCE_INT")
if p == null:    # 未初始化的模组可能无 TEMP 键 → 回退模板基值
    p = items.tag_get_int(it, "BONUS_PERCENTAGE_PERFORMANCE_INT")
var mtype = items.tag_get_string(it, "MODULE_TYPE")
```

- 读取路径 `modifiedState?.GetTag(key) ?? state?.GetTag(key)`（同 ItemsFacade 私有
  ReadIntTag 模式）。
- **容错语义**：缺键 / 句柄失效 / 读取异常一律返回 `null` 不抛（聚合循环内逐件调用安全；
  参数个数错仍抛）。注意 `tag_get_int` 缺键是 `null` 不是 0——`TEMP=0` 是有值，不会回退。
- 原版模组效果键（ModuleHelper.InitModuleItem 写入，int 有符号可负）：
  `BONUS_PERCENTAGE_PERFORMANCE_INT` / `BONUS_PERCENTAGE_EFFICIENCY_INT` /
  `BONUS_PERCENTAGE_QUALITY_INT`（模板基值）+ `TEMP_PERCENTAGE_*_INT`（当前生效值，会退化）。

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
# 教学写法: 打层前后 price_factor 对比
items.give("game:scrap_metal", 1)
var scrap = items.find("game:scrap_metal")
if scrap != null:
    var before = quality.price_factor(scrap)
    var setok = quality.set(scrap, "my_pack:q_pure")
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
# 教学写法
var mach = machine.find("my_pack:desequencer")
if mach == null:
    mach = machine.find("my_pack:processor")
if mach != null:
    log.info("机器: progress={machine.progress(mach)} producing={machine.producing(mach)}")
```

机器的数据面定义（`machines/*.json`、进度机/批次机区别）见
[03 · 数据面 JSON](../03-items/README.md)；机器**界面**（`ui.machine_*`）见
[09 UI 函数](09-ui.md)。本篇 API 的可运行对照包见
[ex21_api_items](../../examples/ex21_api_items/README.md)。

## 本篇函数速查

```pss
items.give(id[, n[, uses]])        # 发进后仓
items.give_counter(id[, n[, uses]])# 摆上柜台
items.has(id[, n])                 # 持有判定
items.value(id)                    # 单价 | -1
items.name(id)                     # 显示名 | null
items.find(id)                     # 句柄 | null (随身+后仓)
items.find_all(id)                 # [句柄] (全量)
items.consume(h[, n])              # 销毁 (n<堆数=部分扣减, v1.39.0)
items.on_target(src_id, dst_id, fn)# 物品拖到物品上 (v1.39.0)
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
