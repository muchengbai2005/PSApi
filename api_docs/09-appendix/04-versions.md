# 04 · 版本纪要

> "这个键/函数从哪个版本开始有？""游戏更新会弄坏什么？"——两份时间线，
> 加兼容写法建议。节点信息汇总自源码版本注释与 gunworks 包的
> 版本依赖声明（该包几十个版本的迭代史是最佳实证）。

## 一、当前版本

| 组件 | 版本 | 角色 |
|---|---|---|
| PSApi | **v2.0.0** | 统一宿主（`Mods/PSApi.dll`，MelonPriority 10）：数据面+逻辑面+界面面 |
| 适配游戏 | playtest | Probably Stolen Demo |

**v2.0.0 合并事件（2026-10-07）**：原双组件 `PSApi.Items.dll`（v0.9.21，优先级 10）
+ `PSApi.Events.dll`（v1.48.8，优先级 20）合并为单个 `PSApi.dll`。
命名空间 `PSApi.Items.*` / `PSApi.Events.*` 与全部能力原样保留；源码树改为
单工程 `_psapi/PSApi/`（`Items/` + `Events/` + `Shared/` 三目录）。
升级要点：先删 Mods/ 里两个旧 dll 再放新版（并存弹告警且功能双挂）；
旧 `[PSApi.Events]` / `[PSApi.Items]` 配置分类的自定义热键自动迁移到 `[PSApi]` 分类；
自家编译包需用新 pack_compiler 重编译（`--verify` 的程序集引用表应见 `PSApi`
而非旧组件名）。本文档正文中 v1.x / v0.9.x 的版本注记均为**合并前能力版本号**，
用于回答"某能力何时引入"，能力本身在 v2.0.0 中全量保留。

## 二、Events 能力节点（合并前逻辑面版本线，按版本升序）

### 早期（v1.1 ~ v1.13）

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
| v1.13.1 | 收购特性方向闸门 |

### 场景时代（v1.14 ~ v1.33）——自定义外出场景从 M1 到 script 单路由

| 版本 | 关键能力 |
|---|---|
| v1.14.x | 批发折扣 `wholesale_discount`；退出不再 FlushAll（崩溃窗口收窄） |
| v1.15.0 | SellSpec.Opts 带定价 |
| v1.16.0 | psui input `on_submit` 回车提交 |
| v1.17.0 | `game.save` 手动存档 |
| v1.18.0 | **自定义外出场景 M1**：`scenes/*.json` 声明 + 地图入口注入 + 搜索点/撤离点 |
| v1.19.x | 场景 bgm 环境音；raid 背包并入原版外出背包（`raid_inv` 键废弃） |
| v1.20.0 | `scenes.*` API 上岗（enter/back/leave/active/list） |
| v1.24.0 | **F10 布局热重载** |
| v1.26.0 | 场景文件夹形态 `scenes/<id>/scene.json`（P1）；driver 显式声明 |
| v1.27.0 | `ui.panel` 场景 uGUI（P2） |
| v1.28.0 | `grid.*` / `combat.*` / `scene.log` 能力 API（P3）；`_` 前缀库目录（`scenes/_raid`） |
| v1.29.0 | **实时动作战斗**：`combat.rt_start` 方向 QTE 会话 + rt 公式件 |
| v1.31.0 | **HUD 布局三层覆写**（代码默认 < 包级 < 场景级）+ rt 区布局；旧全局 `raid_layout.json` 废弃 |
| v1.32.0 | 第三驱动 `picker`（选图面板场景化，内置 `psapi:picker` 兜底）；`entry: "hidden"`；场景导航栈（enter/back/goto） |
| v1.33.0 | **builtin_raid 驱动退役**（波 3）：C# raid 服务删除，raid 玩法只剩 `scenes/_raid` pss 库一套实现；v2 raid 内容键移出 schema |

### 实时战斗打磨期（v1.34 ~ v1.48）

| 版本 | 关键能力 |
|---|---|
| v1.34.0 | rt cfg `hp` 语义 = 直接 HP 值（≤10 回退旧 pip×15） |
| v1.35.0 | 经典回退战斗删除（rt 成唯一战斗形态）；RPM 键 `w_rate → w_rpm` |
| v1.36.0 | 敌人受击箱个体化 `hitbox_scale`；新行为（zigzag/dash/feint/circle/leap） |
| v1.37.0 | `loot_hint` picker 双栏详情；敌人 `tempo` 行为间隙系数；loot `rarity` 稀有度两阶段掷签；`pocket` 口袋兜底窗 |
| v1.37.1 | 库目录（`_` 前缀）脚本先于场景脚本装载 |
| v1.40.0 | `ui.machine_set_shape` 逐格格态串 |
| v1.41.0 | shape 第三态 `2` = 锁定格（禁放但暗色可见） |
| v1.42.0 | 原生实例 tag 读取（`items.tag_get_int/tag_get_string`） |
| v1.43.0 | 武器耐久三键约定（`ps_dur`/`ps_dur_max`/`ps_wear`/`ps_broken`） |
| v1.44.0 | 深度难度乘区：hp/atk ×(1+0.10×depth) 钳 0-8 |
| v1.45.0 | `grid.ground_steal` 随机抽件；fights 场次目标配套 |
| v1.47.0 | **`scenes.set_gate` 场景门禁**（hide/锁定原因）；`scenes.list` 带 locked/lock_reason |
| v1.48.0 | psui 槽 `lock_interactions`（锁死除移动/装备/排序外交互） |
| v1.48.2~4 | 容器开启路径补全：右键菜单 Open/Use → 双击外路径 → 拖放存放 |
| v1.48.8 | **布局第四层：用户覆写** `UserData/PSApi/layout_overrides/<pack>/<sceneId>.json`（内嵌 DLL 包 M 写回也落这里） |

## 三、Items 能力节点（合并前数据面版本线）

| 版本 | 关键能力 |
|---|---|
| v0.8.0 | `"test": true` F6 测试分类（第 29 类） |
| v0.8.1 | useCount 原生次数体系（tooltip 余/N、折价、归零销毁） |
| v0.8.2 | （证书体系所需物品侧配合） |
| v0.9.1~v0.9.3 | v0.9.x 系列物品侧依赖 |
| v0.9.6 | PSD_ 数据标签剥前缀 |
| v0.9.8 | 称重台对接 |
| v0.9.9 | 白名单统一 |
| v0.9.10 | 机器 `mode/category/categoryDisplay` |
| v0.9.11 | pack 图标查询 |
| v0.9.13 | `"#DATA:key=value"` 条目 |
| v0.9.16 | 通用 NBT tooltip 注入器（`ps_tooltip`） |
| v0.9.18 | 物品字符串 tag 读取配套 |
| v0.9.20 | psui slot `strict_footprint` 登记 |
| v0.9.21 | `"*"` 全放通配（合并前最终版） |

## 四、游戏版本事件线

| 日期 | 事件 | 影响 |
|---|---|---|
| 2026-09-04 | items_catalog.md 生成 | 旧物品参考之一（后过期） |
| 2026-09-09 | IsilDump | 旧反编译参考（后过期） |
| 2026-09-18 | **游戏 playtest 更新** | ① 旧物品 id 参考全部过期 ② `buy_price_mod` 语义翻转（变成"玩家买单加价%"，曾致 +110% bug → v1.12.0 重写废弃）③ 模板/对话等原生参考需重 dump |
| 2026-09-20 | game_templates.json 重生成 | 新模板参考（247 总表核对源之一） |
| 2026-09-21 | probe loot table dump | 当前唯一新实证的物品 id 子集 |
| 2026-10-07 | **PSApi v2.0.0 双组件合并** | 单 dll 宿主；旧编译包需重编译；配置分类迁移（见 §一） |

**给包作者的启示**：游戏更新主要破坏三类引用——原版物品裸 id、
base_template 模板 id、皮肤 sprite 键。PSApi 侧（合并前 Events ≥ v1.11.0 起）
对无效物品 id 已有**运行时跳过+WARNING**兜底；模板与皮肤打错仍部分静默
（模板回退拾荒者无警告，见 FAQ#1）。更新后第一件事：启动一遍、
grep `WARNING`、对照 F6 逐个修。

## 五、兼容写法建议

1. **依赖写进 README + pack.json `//`**（gunworks 惯例）：
   现行写法 `"需要 PSApi ≥ v2.0.0"`；老注释里的 `"需 Events vX + Items vY"`
   是合并前口径，按上文时间线折算能力是否存在即可。
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
