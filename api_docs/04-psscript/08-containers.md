# 04-08 · 数组与字典

> array 与 dict 是 PSScript 仅有的两种容器，也是配置、事件载荷、跨函数传数据的
> 通用载体。本篇讲字面量语法（含 GDScript 风格的 `=` 键）、全部操作函数、引用语义。

## 数组（array）

```pss
var empty = []
var nums = [1, 2, 3]
var mixed = ["rifle", 42, [1, 2], {"k": "v"}]   # 元素类型随意, 可嵌套
var tail = [1, 2, 3,]                             # 尾逗号合法
```

| 操作 | 写法 | 说明 |
|---|---|---|
| 读 | `nums[0]` / `nums[-1]` | 负索引从尾数；越界报错 |
| 写 | `nums[1] = 9` | 索引赋值 |
| 追加 | `push(nums, 4)` | 尾部添加，返回 null |
| 弹出 | `pop(nums)` | 删并返回末元素；**空数组返回 null** |
| 查成员 | `"hot" in nums` 或 `has(nums, "hot")` | 按 `==` 语义找 |
| 删除 | `remove(nums, 2)` | 按值删**第一个**匹配，返回 bool |
| 长度 | `len(nums)` | |
| 拼接 | `nums + [5, 6]` | **产生新数组**，不改原数组 |
| 遍历 | `for x in nums:` | 快照遍历（体内增删不影响本轮） |

```pss
var picked = []
push(picked, "contraband")
push(picked, "hot")
log.info("{picked}")          # [contraband, hot]
log.info("{pop(picked)}")     # hot
log.info("{picked}")          # [contraband]

var a = [1, 2]
var b = a                     # 引用! 不是拷贝
push(a, 3)
log.info("{b}")               # [1, 2, 3] —— b 跟着变
```

**没有的操作**（对照 Python）：切片 `a[1:3]`、`insert`、按索引 remove、`sort`、`reverse`、
`index`（找下标）。需要就用 for 手写——容器操作需求在 modding 场景里通常很小。

## 字典（dict）

键**恒为字符串**，值任意。字面量三种键写法：

```pss
var d1 = {"buyer": "老猫", "price": 500}     # 字符串键(带引号)
var d2 = {buyer = "老猫", price = 500}       # 标识符键 + '='  (GDScript 风格)
var d3 = {"buyer": "老猫", price = 500}      # 混用也合法
# d1 == d2 == d3, 键都是 "buyer" 和 "price"
```

- 标识符键 `=` 写法是**语法糖**：`{price = 500}` 完全等价 `{"price": 500}`，
  键就是标识符的文本，教学包里大量使用（如 `{day = time.day()}`，
  见 [ex16_pss_containers](../../examples/ex16_pss_containers/README.md)）。
- 键**不能是表达式**（`{x: 1}` 里 x 会被当成字符串 `"x"`，不会取变量 x 的值）。
  运行时动态键请用赋值语句：`d[k] = v`。

| 操作 | 写法 | 说明 |
|---|---|---|
| 读 | `d["price"]` / `d.price` | **缺键返回 null**（不报错） |
| 写 | `d["price"] = 600` / `d.price = 600` | 键不存在则新建 |
| 查键 | `"price" in d` 或 `has(d, "price")` | |
| 删键 | `remove(d, "price")` | 返回 bool |
| 全键 | `keys(d)` | 返回数组 |
| 全值 | `values(d)` | 返回数组 |
| 长度 | `len(d)` | |
| 遍历 | `for k in d:` | **迭代键**（值用 `d[k]` 取） |

```pss
var deal = {"buyer": "老猫", "price": 500}
deal["done"] = true           # 新增键
deal.price += 50              # 点号复合赋值
log.info("{deal.done}")       # true
log.info("{deal.note}")       # null —— 缺键宽容

for k in deal:
    log.info("{k} = {deal[k]}")
# buyer = 老猫 / price = 550 / done = true

remove(deal, "done")
log.info("{keys(deal)}")      # [buyer, price]
log.info("{join(keys(deal), ', ')}")   # buyer, price
```

## 引用语义（两种容器相同）

数组与字典都是**引用类型**：赋值、传参、存进容器，传递的都是"同一个容器"。

```pss
func add_tag(tags):
    push(tags, "vip")         # 改的是调用方的数组

var my_tags = ["hot"]
add_tag(my_tags)
log.info("{my_tags}")         # [hot, vip] —— 函数内修改对外可见
```

想要拷贝须**手工重建**（没有内置 copy/clone）：

```pss
func copy_dict(src):
    var out = {}
    for k in src:
        out[k] = src[k]       # 一层浅拷贝(嵌套容器仍是引用)
    return out
```

> `==` 也是引用比较：`[1,2] == [1,2]` 为 false。判"内容相同"需逐元素比
> （`len` 相同 + 循环 `in`/`==`）。这是 v1 的已知简化。

## 嵌套结构

容器可任意嵌套，点号/索引链式访问：

```pss
var config = {
    "prices": {"rifle": 100, "medkit": 30},
    "schedule": {mode = "every_ndays", interval = 2, chance = 0.8},
    "loot": ["common_ore:3:1", {id = "bandage_item", count = 2, p = 0.5}],
}

log.info("{config.prices.rifle}")              # 100
log.info("{config['schedule'].interval}")      # 2
log.info("{config.loot[1].count}")             # 2

config.prices.rifle = 120                      # 链式赋值 OK
push(config.loot, "scrap_metal:2:0.3")         # 嵌套数组同样可变
```

打印嵌套容器时递归展开（最深 4 层，更深显示 `[...]`/`{...}`）：

```pss
log.info("{config}")
# {prices: {rifle: 120, medkit: 30}, schedule: {mode: every_ndays, ...}, ...}
```

## 惯用法合集

来自教学包的惯用模式，直接抄：

**累积器**（槽位物品筛选的常用写法，对照
[ex28_psui_slots](../../examples/ex28_psui_slots/README.md)）：

```pss
var targets = []
for it in ui.get_slot_items("feed"):
    if it.id == "scrap_metal":
        push(targets, it)
if len(targets) < 2:
    return
```

**配置查表 + 默认值**（缺键返回 null + `or` 兜底）：

```pss
var rate = config.rate or 0.1     # 键缺失(null)时用默认值
```

**遍历字典构造报表**（hello.pss）：

```pss
var deal = {"buyer": "老猫", "price": 500, "done": false}
log.info("deal: buyer={deal.buyer} keys=[{join(keys(deal), ', ')}]")
```

**二维表**（数组的数组）：

```pss
var grid = [[1, 2], [3, 4]]
log.info("{grid[1][0]}")     # 3
```

---

下一篇：[09 · 事件与 on 块](09-events.md)
