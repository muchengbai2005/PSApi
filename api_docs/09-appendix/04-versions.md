# 04 · 版本纪要

> "这个键/函数从哪个版本开始有？""游戏更新会弄坏什么？"——两份时间线，
> 加兼容写法建议。节点信息汇总自源码版本注释与 gunworks 包的
> 版本依赖声明（该包十一个版本的迭代史是最佳实证）。

## 一、当前版本

| 组件 | 版本 | 角色 |
|---|---|---|
| PSApi.Items | **v0.9.3** | 数据面（MelonPriority 10，先加载） |
| PSApi.Events | **v1.13.1** | 逻辑面+界面面（MelonPriority 20） |
| 适配游戏 | playtest | Probably Stolen Demo |

## 二、Events 能力节点（按版本升序）

| 版本 | 关键能力 |
|---|---|
| v1.1.0 | `multi_buy_disabled` / `no_contraband` 新键 |
| v1.3.0 | 图元槽位面板（slot/grid_slot → PixelWindow 图元树后端） |
| v1.4.0 | **机器绑定 PSUI 面板**（`ui: "psui"` + persistent + machine 回调八函数） |
| v1.5.0 | 机器面板能力完善（组装台 v0.3.x 所需） |
| v1.6.0 | `items.power/power_draw/uses/uses_max/use/use_init` 电力与次数 + `ui.machine_slot_items` |
| v1.7.0 | `crime.exempt_guns` / `shop.showcase_items` / `shop.block_sale`/`unblock_sale` / `items.find_all` |
| v1.8.0 | 摆货**并集防双份**（`_stockedSessionKey` 两路共享） |
| v1.9.0 | `texts` 多段对话链 / `auto_leave` / `sprite` 指定 + FNV-1a 立绘固定 / `items.give(id,count,uses)` / `{uses=N}` |
| v1.9.1 | `items.give_counter`（门口柜台路径）/ 摆货时机对齐原版（对话末行）/ **auto_leave 收窄至 DIALOGUE intent** |
| v1.10.0 | `inject.loot_pool` / sell_shelf count 区间 `"1-3"`（上限 5）/ 脚本 NPC 摆货链尾判定去 endAction 要求 |
| v1.11.0 | `buy_pool`+`buy_count` / `choices[].cond` / `{buy_list}` 插值 / **注入执行点无效 id 跳过+WARNING**（存在性校验） |
| v1.12.0 | 价格体系重写（`buy_price_mod`/`sell_price_mod` **废弃**） |
| v1.13.0 | `price` 三系数缺省 **1.0/1.0/1.0**（中立）/ 克隆模板**自动白板化**（清摆货回调/收购特性/sellPriceModifier） |
| v1.13.1 | 收购特性方向闸门（当前版） |

## 三、Items 能力节点

| 版本 | 关键能力 |
|---|---|
| v0.8.0 | `"test": true` F6 测试分类（第 29 类） |
| v0.8.1 | useCount 原生次数体系（tooltip 余/N、折价、归零销毁） |
| v0.8.2 | （证书体系所需物品侧配合） |
| v0.9.1 | v0.9.0 系列物品侧依赖 |
| v0.9.2 | v0.9.1 系列物品侧依赖 |
| v0.9.3 | **当前版**（v1.11.0 系列物品侧依赖） |

## 四、游戏版本事件线

| 日期 | 事件 | 影响 |
|---|---|---|
| 2026-09-04 | items_catalog.md 生成 | 旧物品参考之一（后过期） |
| 2026-09-09 | IsilDump | 旧反编译参考（后过期） |
| 2026-09-18 | **游戏 playtest 更新** | ① 旧物品 id 参考全部过期 ② `buy_price_mod` 语义翻转（变成"玩家买单加价%"，曾致 +110% bug → v1.12.0 重写废弃）③ 模板/对话等原生参考需重 dump |
| 2026-09-20 | game_templates.json 重生成 | 新模板参考（247 总表核对源之一） |
| 2026-09-21 | probe loot table dump | 当前唯一新实证的物品 id 子集 |

**给包作者的启示**：游戏更新主要破坏三类引用——原版物品裸 id、
base_template 模板 id、皮肤 sprite 键。PSApi 侧（Events ≥ v1.11.0）
对无效物品 id 已有**运行时跳过+WARNING**兜底；模板与皮肤打错仍部分静默
（模板回退拾荒者无警告，见 FAQ#1）。更新后第一件事：启动一遍、
grep `WARNING`、对照 F6 逐个修。

## 五、兼容写法建议

1. **依赖写进 README + pack.json `//`**（gunworks 惯例）：
   `"需要 PSApi.Events ≥ v1.11.0 + PSApi.Items ≥ v0.9.3"`。
2. **别赌废弃键**：`buy_price_mod`/`sell_price_mod` 仍可解析但只用于告警
   迁移——统一改 `price = {buy = ...}`（注意语义：v1.12.0 前后含义不同，
   9-18 后旧键=玩家买单加价）。
3. **新键做能力探测**时用 `state`/事件驱动降级，而不是版本号硬判断
   （PSScript 没有版本查询函数）。
4. **概率/数量语义随版本变过**：sell_shelf 的 count 区间（v1.10.0）、
   price 缺省（v1.13.0 中立化）——从旧包抄配置时按本书各字段表核对当前语义，
   别抄 gunworks 早期版本注释里的数字。

---

下一篇：[05 · FAQ 常见坑](05-faq.md)
