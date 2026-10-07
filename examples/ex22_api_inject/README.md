# ex22_api_inject · 示例 22：物品流通五通道

> **演示知识点**（对应文档 [07-inject.md](../../api_docs/05-api-reference/07-inject.md)）：
> `inject.buy_list` 收购清单 · `inject.sell_shelf` 出售客户货架 ·
> `inject.doctor` 夜间博士商店 · `inject.barter` 野外以物易物商人 ·
> `inject.loot_pool` 原版 LootTable 随机池 · 管理函数 `inject.list/reset_tracking` ·
> 注册类 API 顶层调用 vs 管理类 API 对局内调用的分工。

## 文件清单

```text
ex22_api_inject/
├── pack.json              ← 包清单
├── items/
│   ├── curio.json         ← 古董摆件: 走买池/博士/loot 三条通道
│   └── ammo_pack.json     ← 弹药包: 走货架/野外商人两条通道
└── events/
    └── channels.pss       ← 1 个脚本: 顶层五通道注册 + day_wake 管理演示
```

## 安装

把整个 `ex22_api_inject` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex22_api_inject/
```

## 各通道什么时候能看到效果（重要）

`inject` 不生成新 NPC、不改系统规则——注册后要**等对应的原版渠道运转**才能看到货：

| 通道 | 注册调用 | 什么时候能看到 |
|---|---|---|
| `buy_list` | `inject.buy_list("ex22_api_inject:curio")` | 收货客户（收购/买卖兼有/批发）入队后，点开交易 UI 看他的收购清单里有「古董摆件」 |
| `sell_shelf` | `inject.sell_shelf("ex22_api_inject:ammo_pack", 2)` | 售货客户说完开场白摆货时，他货架上多 2 个「弹药包」（每客户一次） |
| `doctor` | `inject.doctor("ex22_api_inject:curio", 1)` | 夜里进博士商店，货架上出现 1 个「古董摆件」（每次访问一次） |
| `barter` | `inject.barter("ex22_api_inject:ammo_pack", 1)` | 遇到野外以物易物商人，他货架可换 1 个「弹药包」（每商人一次） |
| `loot_pool` | `inject.loot_pool("material", "ex22_api_inject:curio", 0.05)` | 拾荒客/小贼等出售客户的**携带货物**、远征掷骰里随机出现（约 5% 权重，玩家感知为"捡到的"） |

前四条是**必出注入**（渠道一运转就有），`loot_pool` 是**概率参与**（权重与表内
其他条目归一化比大小）。这些全部只在开店存档的对局内发生。

## 验证（10 分钟，需要一点耐心等客户）

1. 启动游戏，控制台应出现（顶层注册完成）：

```text
[ex22_api_inject] inject 五通道已注册: 古董摆件(买池/博士/loot) + 弹药包(货架/野外商人)
```

2. 进开店存档，睡觉到第二天，`day_wake` 里管理函数跑一遍：

```text
[ex22_api_inject] 新的一天: 注入记账已重置, 本包通道 5 条继续生效
```

3. 游戏内逐通道确认：点开收购客户的交易 UI 找「古董摆件」→ 等出售客户摆货
   找「弹药包」→ 夜里逛博士商店 → 遇野外商人看货架 → 多开几天，总会有个
   出售客户"恰好捡到"古董摆件卖给你（loot_pool 生效）。

## 逐文件讲解

### items/*.json —— 两个物品，五条通道

`curio` 走三条通道（买池/博士/loot），`ammo_pack` 走两条（货架/野外商人），
全部 `test: true` 方便 F12 直接领来对照外观。

### events/channels.pss —— 注册五行，管理两行

- **顶层五行注册**：`inject` 注册类 API 与 `store_event.register` 一样属于
  "加载期顶层调用"——主菜单阶段调是安全的（只记账，对局内才生效）。
  物品 id 不存在不会报错，注入执行点跳过 + 日志告警一次（版本更新删物品时
  模组静默降级）。
- **`on day_wake()`**：`inject.reset_tracking()` 清"已注入"记账（货架会话键/
  野外商人/博士标记），之后同一些客户会重新摆货——"每天重摆一次货"的
  标准写法。`inject.list()` 平铺全部包的全部注入条目（`idx/channel/pack/id/
  count/chance/...`），用 `row.pack == PACK` 过滤出自家的。
- **进阶提示**（不在本包代码里）：`inject.sell_shelf` 还支持函数形式
  `fn(client)` 按客户出不同货；`inject.tune(idx, {enabled, count})` 运行时
  开关条目；`inject.force_client(shop.queue() 的 ptr)` 给指定客户立即补货——
  见文档与 psconsole 包 `/inject` 管理指令。

## 动手练习

1. 把 `inject.sell_shelf("ex22_api_inject:ammo_pack", 2)` 加上第 3 参概率：
   `("ex22_api_inject:ammo_pack", "1-3", 0.5)`——每次独立掷 50%、件数 1..3 随机。
2. 给 `curio` 注册第二条 loot 通道：`inject.loot_pool("junk", "ex22_api_inject:curio", 0.03)`
   （表名短名自动补 `Table` 后缀），观察不同池的消费场景差异。
3. 在 `day_wake` 里对 buy_list 条目做隔日开关：`inject.tune(row.idx, {enabled = time.day() % 2 == 0})`
   （先 `if row.channel == "buy_list" and row.pack == PACK:` 过滤）。

## 下一个示例

- [ex23_api_npc](../ex23_api_npc/README.md) —— 从"塞进既有渠道"到"自己造一个客户"
