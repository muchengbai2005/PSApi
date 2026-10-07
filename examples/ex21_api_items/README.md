# ex21_api_items · 示例 21：物品 API 全家桶

> **演示知识点**（对应文档 [06-items.md](../../api_docs/05-api-reference/06-items.md) ·
> [10-handles.md](../../api_docs/05-api-reference/10-handles.md)）：
> `items.give/has/value/name/find/find_all/consume/give_counter` 发放·持有·估价·检索·消耗·柜台摆货 ·
> `give` 第三参两种形态（整数 = 次数物品；opts 字典 = 实例定制 `name/desc/value/quality/uses/data/to`） ·
> 物品句柄四只读成员（`id/count/value/uid`）与句柄函数（`get_data/set_data/set_name`） ·
> `items.tag_get_int` 读原生整数 tag（次数物品的 `CURRENT_USE_COUNT_INT/MAX_USE_COUNT_INT`）。

## 文件清单

```text
ex21_api_items/
├── pack.json              ← 包清单
├── items/
│   ├── gift_box.json      ← 礼盒: give/find/consume 演示靶
│   ├── lucky_coin.json    ← 幸运币: 实例定制(name + data)演示靶
│   └── energy_cell.json   ← 能量电池: 次数物品/tag_get_int 演示靶
└── events/
    └── items_demo.pss     ← 1 个脚本: game_loaded 里 8 组演示
```

## 安装

把整个 `ex21_api_items` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex21_api_items/
```

## 验证（3 分钟）

1. 读档/新开任意存档（`game_loaded` 触发，脚本自动跑完 8 组演示），控制台应出现：

```text
[ex21_api_items] give 礼盒x2: true, has(>=2)=true, 单价 50, 名称「礼盒」
[ex21_api_items] 定制实例: 名称「压岁币」, data.msg=岁岁平安, data.level=7
[ex21_api_items] 次数物品: 当前=3, 上限=3(原生 int tag 用 tag_get_int 读)
```

2. 背包确认：后仓有礼盒、名为「压岁币」的幸运币、3 次型的能量电池；
   店门口柜台多了一件幸运币（`give_counter` 摆的）。
3. **F12 再领一轮**：三个物品都在测试分类里（`test: true`），领取后
   注意 `find_all` 的堆数变化——F12 发的和脚本发的是不同堆。

## 逐文件讲解

### items/*.json —— 三个物品各演示一条特性

- `gift_box`：最朴素的物品，只当发放/消耗靶子。`template` 兜底图标，无 png。
- `lucky_coin`：给 `give` 的 opts 演示实例定制——`{name = "压岁币", data = {...}}`
  把这一件实例改名并写入自定义数据。
- `energy_cell`：给次数物品演示——`give` 第三参传整数 `3`，发出来就是
  "还能用 3 次"的次数物品（用完即毁）。

### events/items_demo.pss —— 8 组演示的分组逻辑

1. **基础四连**：`give` 发进后仓 → `has(id, 2)` 判"至少 2 件" →
   `value(id)` 单价（未知/不在对局 = `-1`）→ `name(id)` 显示名（未知 = `null`）。
2. **find / find_all**：句柄是活引用，四个只读成员之外的操作全走函数。
   `find` 找不到返回 `null`——**先判空**是标准姿势。
3. **实例定制**：`give` 的 opts 写 `data` 进的是 **PSD_ 自定义数据**，
   读回用**句柄 `get_data(key)`**（本演示），脚本侧数据专属通道。
4. **次数物品 + 原生 tag**：`tag_get_int(句柄, key)` 读的是游戏**原生整数
   tag**（次数/模组效果 `BONUS_*_INT` 等），与 PSD_ 数据是两套体系——
   这是新手最容易混的一处：**give 的 data 用 get_data 读；原生 tag 用
   tag_get_int 读**。缺键/句柄失效返回 `null`，循环里逐件调用也安全。
5. **consume 部分消耗**：`consume(句柄, 1)` 只扣 1 件（v1.39.0 起），
   省略 count = 整堆销毁。
6. **give_counter**：摆到店门口柜台（玩家可直接拿），柜台满位时剩余悬空
   （日志有警告），开店存档里才有效。

## 动手练习

1. 把 `items.give("ex21_api_items:lucky_coin", 1, {...})` 的 opts 里加
   `value = 999`，重进游戏看「压岁币」单价；再补 `quality = "ex21_api_items:q_pure"`
   看报错（品质未注册返回 false——想用品质需先在数据面定义）。
2. 在 `energy_cell` 演示后追加两行：
   `var used = items.use(cell)` 与 `log.info("[{PACK}] 用 1 次: {used}, 剩 {items.uses(cell)}")`，
   观察次数物品的消耗路径。
3. 给 `gift_box.json` 加 `"useCount": 5` 字段（数据面直接定义次数物品，语义同
   `items.use_init`），重启后 F12 领取，对比与脚本 `give(..., 3)` 发出的异同。

## 下一个示例

- [ex22_api_inject](../ex22_api_inject/README.md) —— inject 五通道：把物品塞进游戏的既有出货渠道
