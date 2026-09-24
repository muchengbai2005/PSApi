# gunworks 内容包

**Gunworks · 老祝剧情 + 革命军潜伏网络** — PSApi 内容包, 需要 PSApi.Events ≥ v1.11.0 + PSApi.Items ≥ v0.9.3。
本包已可编译为 `PSPack.gunworks.dll` 分发 (编译方法与冲突规则见 `UserData/PSApi/README.md`「编译为 DLL 分发」)。

## 内容清单

| 文件 | 内容 |
|---|---|
| `events/guide.pss` | 引导员朱利安剧情(E7): 周五到店三选项对话、接受 +200 定金·rev+10·**见面礼(组装台+10天证书+随机3~4部件)**、每周五津贴 +100、连续 3 周推脱→再也不来; 接受分支触发革命军池权重×3 |
| `events/cells.pss` | 革命军潜伏网络 14 细胞 NPC(M2): 手写复刻自 NpcManager `npc_json/revolutionaries.json`; 文件尾 CELL_REP + `on trade_completed` 实现声望奖励; v0.9.0 全员英译名+指定皮肤+texts 分段对话 |
| `events/forger.pss` | 伪造师费边(v0.9.0; v0.11.0 改固定价): 每周随机 1-2 天进城, 对话双选项 — 证件全套 300 块(随机 3-30 天证书+3 件货) / 只要货 150 块(随机 3 件模组部件或数据卡); choices[].cond 钱不够不显示 |
| `events/cat_shadow.pss` | 黑市奸商老猫·影(v0.11.0, 自 example_hello e4_demo 迁入): 隔 2 天 80% 概率+bm 池双通道到店, sell_pool 27 种模组货(部件/零件/数据卡/组装枪/证书)+common_ore 加权不放回抽 6-10 种, 收 printed_gun |
| `events/distribution.pss` | 流通渠道(v0.9.0): 博士夜间商店三台机器按天数阈值+概率上架 + 原版 NPC 货架概率流通模组部件/数据卡/零件/枪 |
| `items/mcb_weapon_gun1.json` | 自定义物品「解放轻步枪H1」(id `gunworks:mcb_weapon_gun1`) |
| `icons/mcb_weapon_gun1.png` | 上述物品图标 |

## 与 revolutionaries.json 的映射

id 映射: 原 `rev_cell_*` → 本包 `cell_*`(脚本内事件 npc_id 为裸 id):

| 原 id | 本包 id | 角色 (v0.9.0 英译名, 括号=道上绰号) | sprite |
|---|---|---|---|
| rev_cell_gunsmith | cell_gunsmith | 马洛夫(老枪, 下层枪匠) | maleScav5 |
| rev_cell_junker | cell_junker | 裘德(老周, 拾荒) | maleScav2 |
| rev_cell_water | cell_water | 阿莱娜(阿兰, 水贩) | femaleLower1 |
| rev_cell_pilot | cell_pilot | 汉森(独眼老韩, 飞行员) | maleScav11 |
| rev_cell_electrician | cell_electrician | 邓肯(老邓, 电修铺) | maleEng3 |
| rev_cell_medic | cell_medic | 玛妮(小满, 救护员) | femaleSci2 |
| rev_cell_tobacconist | cell_tobacconist | 朱尔斯(九叔, 烟摊) | maleLower3 |
| rev_cell_pharma | cell_pharma | 拜伦(白医生, 上层药商) | maleUpper5 |
| rev_cell_broke | cell_broke | 费尔南(冯先生, 没落贵族) | maleUpper3 |
| rev_cell_sec | cell_sec | 切尼警官(陈警官, 治安部) | maleSec3 |
| rev_cell_rat | cell_rat | 瑞特(黑市鼠) | maleBM1 |
| rev_cell_fence | cell_fence | 格雷(老鬼, 销赃商) | shadyMerchant |
| rev_cell_contact | cell_contact | 诺曼(小北, 本部交通员) | merchant |
| rev_cell_cartel | cell_cartel | 墨菲(老墨, 卡特尔眼线) | maleMerc2 |
| (RevDeal rev_guide) | guide / guide_pay | 朱利安(老祝, 引导员) | mentor |
| (v0.9.0 新增) | forger | 费边(伪造师, 黑市) | maleUpper8 |
| (v0.11.0 新增) | cat_shadow | 老猫·影(黑市奸商, 自 example_hello 迁入) | shadyMerchant(模板随机) |

皮肤 key 全部取自 `UserData/probe/` 实测集合; 革命军成员用伪装身份皮肤 (probe 无 maleRev/femaleRev 实测键)。

字段映射:

| 原 JSON 字段 | 脚本侧 |
|---|---|
| id/name/faction/intent/budget[b,r]/clearBuying/buyingIds/pools(weightPct) | 同名键直写(clear_buying / buying_ids / weight_pct); ⚠ buyPriceModifier 旧映射 buy_price_mod **v0.12.0 起废弃**(9-18 起该字段=玩家买单加价%, 曾致 +110% bug), 收购溢价改用 `price={buy=1.05}` |
| sellItems 常见层+特殊层 | 静态 `sell_items`, 每条 `id:数量:概率` 独立掷骰, 分层概率逐字保留 |
| keepPipelineBudget: true | 无需键 — 脚本默认语义(工厂期 SetClientBudget, 管线吸引力加成照常叠加) |
| reputationBonus | `on trade_completed` handler + CELL_REP dict 纯脚本实现(玩家卖出清单物品 → 该客户阶层声望+) |
| isMultiBuyDisabled / noAcceptingContraband(仅陈警官) | `multi_buy_disabled` / `no_contraband`(v1.1.0 新键) |
| 五通道对话(main/wrong_item/right_item/accept/all_done) | `dialogues` 同名通道 |
| RevPoolMultiplier(接受后池×3) | `npc.pool_scale("cell_*", 3)`(guide.pss 接受分支 + day_wake 每日幂等收敛) |

物品 id: 解放轻步枪H1 = `gunworks:mcb_weapon_gun1`(本包自定义); 废土冲锋枪 = `mymod_scrap_smg`(原版裸 id); 其余全为原版裸 id。

## 未平移项(无对应脚本 API)

- `global.lootTables`: makeshiftWeapon 战利品池 3% 解放步枪 — **v0.10.0 已由 inject.loot_pool 平移**(见 distribution.pss)。
- `global.dialogue`: 全局 acceptDeal/allDone 对话追加 — 全局对话池无 API。

## 验收提示

1. 启动日志应见 18 行 `npc '...' registered (pack=gunworks, ...)`(14 细胞 + guide + guide_pay + forger + cat_shadow)。
2. 细胞由加权池自然到店(如马洛夫 client 池、瑞特 bm 池), 到店说完话展示区直接见货。
3. 卖枪械零件(gun_part)给切尼警官 → 日志 sec 声望 +5。
4. 朱利安周五接受后日志「革命军池权重×3」+ 见面礼摆上门口柜台(v0.9.1 起, 原入背包), 之后细胞到店频率可见上升。

---

# 枪械组装台 (v0.5.1, 需要 PSApi.Events ≥ v1.5.0 + PSApi.Items ≥ v0.8.0)

**v1.4.0 机器绑定 PSUI 面板的模板实现** — 新物品机器, 放店里/家里, **双击打开 PSUI 面板**
(SetContentWindow 原生链路, 不是 ui.open); 材料存机器里随存档序列化; 关窗不退回玩家。

## 面板布局 (v0.5.1 经典合成台式·纯文字按钮版)

> v0.5.0 的展示槽/图标方案被用户裁定"越改越乱", 已回退纯文字按钮 (见 `_api_design/ui/11` §4.4)。

- **左列**: 6 个**纯文字武器按钮**竖排 (100x18 紧凑小按钮, font_size 0.75 字大按钮小, 宽度按最长文字
  「半自动狙击枪」+`▶` 前缀留够); 当前选中带 `▶` 前缀高亮 (未选中用全角空格对齐)。
- **中部**: 5 个部件槽 + 1 个零件槽 (v0.8.0) 排成 **2 列网格**, 每单元 = 槽正上方小字标签 (只写所需部件名, 如「大机匣」
  「长枪管」) + 2x2 槽, 对齐工整; 不需要的槽显示「—」且锁死拒放:
  ```
  (0,0) 机匣 s0   (1,0) 枪管 s1
  (0,1) 手柄 s2   (1,1) 枪机 s3
  (0,2) 弹匣 s4   (1,2) 零件 s5
  ```
- **右列**: 文本箭头 `→` (font_size 1.2) +「成品」小字 + 输出槽 (4x2)。
- **底部**: 武器描述行 (font_size 0.7) + 开始/取消组装按钮 (84x22 紧凑, font_size 0.7) + 状态行。
- 窗口 400x330; 全部数值集中在 psui 头部「调参区」注释, 挤/空改 psui 即可 (结构不变随便调)。

## 图标素材 (SVG → PNG, 素材库)

- `ui/external/icon_*.svg` — 7 个 64x64 viewBox 单色剪影 (#2b2135 透明底): 六把枪靠枪管长短/
  枪托/瞄镜/弹匣/拉栓柄区分 (pistol 短套筒 · auto_pistol 长套筒+加长弹匣 · smg 紧凑+直弹匣+简易托 ·
  rifle 中长管+斜弹匣 · bolt_sniper 特长细管+大托+拉栓柄 · semi_sniper 长管+瞄镜+弹匣), 加 arrow_right。
- `ui/external/rasterize.py` — 光栅化脚本: `python rasterize.py` (svglib+reportlab/rlPyCairo+Pillow),
  白底渲染 2x 超采样 → 覆盖率反解 alpha 透明底 → LANCZOS 降到 64px, 产物进 `icons/`。
- **v0.5.0 起组装台面板不再使用这些图标**(v0.5.1 已回退纯文字按钮, 见「面板布局」) ——
  image 元素在 IL2CPP 下尺寸失控且 Sprite override 不生效 (见 `_api_design/ui/11` §4.4)。
  素材保留在库中供其他面板/实验用途; 游戏内键 = `gunworks:icon_<名>` (命名空间键红线照旧)。

## 玩法

1. F6 物品浏览器 → **测试** 分类 → 拿取「枪械组装台」+ 部件 + 对应枪械零件, 把台子放进店里。
2. 双击台子开面板: 左列选武器 → 中间按配方放部件 + 零件 (每槽一种, 槽名/白名单随武器切换) →
   全部放齐输出槽出现武器预览 (悬停有描述, 取不下来) → 点「开始组装」。
3. 所有部件与零件锁死 (按钮变「取消组装」) → 过一夜 → 部件+零件消耗, 武器可取。
4. **取走武器才能装下一把** (输出槽被占时部件槽拒收新部件)。
5. 切换限制: 任何部件/零件槽有货时禁止切换武器 (给提示不切换)。

## 配方 (槽位固定: 机匣/枪管/手柄/枪机/弹匣 + v0.8.0 零件)

| 武器 | 机匣 | 枪管 | 手柄 | 枪机 | 弹匣 | 零件 |
|---|---|---|---|---|---|---|
| 手枪 | 小 | 短 | 短 | — | — | 手枪零件 |
| 自动手枪 | 小 | 短 | 短 | ✓ | — | 手枪零件 |
| 冲锋枪 | 小 | 短 | 短 | ✓ | ✓ | 冲锋枪零件 |
| 步枪 | 大 | 中 | 长 | ✓ | ✓ | 步枪零件 |
| 栓动狙击枪 | 大 | 长 | 长 | — | — | 狙击枪零件 |
| 半自动狙击枪 | 大 | 长 | 长 | ✓ | ✓ | 狙击枪零件 |

## 文件

| 文件 | 内容 |
|---|---|
| `items/gunworks_bench.json` | 16 个测试物品 (1 台 + 9 部件 + 6 枪), 全部 `"test": true` → F6「测试」分类; v0.8.0 起显式 shape/icon/tags/contraband (物品编辑器素材), template 保留作基底 (显式 shape/icon 覆盖克隆结果, tags 与模板类型合并) |
| `machines/gun_bench.json` | 机器声明: `"ui": "psui"` + `"panel": "gun_bench"` |
| `ui/gun_bench.psui` | 面板定义: `persistent: true` + `on_open`; **结构恒定** (6 武器按钮 + 5 部件槽 + 1 零件槽 + 1 输出槽 + 全部 spacer, 切武器只改槽名/白名单/锁/按钮文本, 不增删元素 — 存档 BFS 节点索引才能对上) |
| `ui/external/*.svg` + `rasterize.py` | 枪械剪影/箭头 SVG 源 + 光栅化脚本 (**素材库**, v0.5.0 起面板不再使用, 见「图标素材」) |
| `events/bench/01_data.pss` | 配方表 (events/ 子目录递归扫描演示) |
| `events/bench/02_bench.pss` | 状态机 (idle→assembling→done) + 面板回调 + `on day_wake` 隔夜完成 |

## 模板要点 (照抄到你的包)

- 机器脚本回调参数: 按钮 `{elem, machine}`, 槽位 `{elem, value, machine}`, on_open `{machine}`。
- 状态存 `state` (key `"bench:<uid>"`); **句柄不能进 state, uid 可以** — 隔夜用 `ui.machine_find_uid(uid)` 找回机器。
- `ui.machine_*` 八函数: set_text / slot_item / set_whitelist / spawn / lock / clear / is_open / find_uid。
- `ui.machine_lock` = 插入锁+取出锁双锁; 输出槽"只出不进"用**占位白名单** (不存在的 id, 排他注册全拒)。
- 文件排序即加载顺序: `01_data.pss` 先于 `02_bench.pss` (同包共享全局环境, 数据先定义)。

## 已知边界

- **v0.3.1/v0.4.0/v0.5.0/v0.5.1/v0.8.0 五次布局重排后, 此前存档里已放出的组装台面板结构对不上** (元素树变了, 存档 BFS 索引串槽) —
  旧台子读档后槽内物品可能串位; 把旧台子物品取回、卖掉重买一台即可 (演示模板包, 不做迁移)。
- 读档恢复: 槽内物品/武器选择/组装状态全部恢复 (on_open 时 bench_apply 重对齐锁与白名单);
  窗口必须在首次打开后才应用脚本布局 (首帧前静态 psui 白名单是并集, 可能放进"错位置"部件,
  bench_parts_complete 会判不齐不给预览, 取回即可)。
- 全新未入库的台子 uid 可能为 0 (uniqueId 入库才分配), 此时状态键冲突 — 放下台子再玩。

# 部件打印机 + 物品分析仪 (v0.6.1, 需要 PSApi.Events ≥ v1.6.0 + PSApi.Items ≥ v0.8.1)

## 部件打印机 (gunworks:part_printer, 面板 480x360, 售价 1200)

- **左=熔化区**: 2x6 金属锭网格 (`grid_slot`, 只收 `metal_ingot`, 原矿不行) + 液量面板 (`lbl_liquid`, 上限 30 mB, 1 锭=3 mB)
  + 「常开熔炼」按钮 (开关态, 图元后端不支持 toggle 控件故用按钮切文本)。
- **右=打印区**: 数据卡槽 (五种成品卡白名单, 拿走卡选择按钮归"—") + 3 个常驻选择按钮 (▶ 标记当前选中)
  + 「开始打印/取消打印」+ 成品槽 (占位白名单, 只出不进)。
- **底部=共用电池槽** (v0.6.1 用户裁定: 双电池槽合并为单槽; 元素树变更, 旧存档打印机需取回物品重买)。
- **隔夜结算** (`events/printer/02_printer.pss` `on day_wake`): 单电池**打印优先** — 先扣 10 电/件,
  熔化只吃剩余 (2 电/锭, 整堆处理, 堆叠数>剩余容量跳过该堆), 全部"全有或全无"。
  打印成功: 扣液扣电 → 数据卡 `items.use` 扣 1 次 (归零原生自动销毁) → 成品 `ui.machine_spawn` 入成品槽。
  电不够: 任务保留, 明晚重试, 状态行提示。
- 打印中数据卡双锁; 点「取消打印」解锁, 不扣液不扣次。
- 卡价随次数折算已实测确认 (v0.6.1 调试日志验证后移除): 打印一次 100→82, 归零销毁。

## 物品分析仪 (gunworks:item_analyzer, 面板 340x280, 售价 900)

- 被测部件槽 (9 种部件白名单) + 空白数据卡槽 + 电池槽 + 「开始分析/取消分析」+ 成品槽。
- 隔夜耗 15 电: 成功消耗部件+空白卡, 析出对应数据卡; **同类型部件析出同种卡** (长/中/短枪管 → 枪管数据卡)。
- 电不够: 任务保留明晚重试; 分析中部件/卡双锁, 取消解锁不动物品。

## 数据卡次数体系 (Items v0.8.1 原生 UseCountHelper)

- 五种成品卡 JSON: `useCount: 5, useBaseValue: 10, useValuePerUse: 18` → tooltip 自动显示 余/5,
  满卡值 100, 售价随剩余次数折算, 归零自动销毁 (=打印 5 次后第二天消失)。
- 获取: 空白卡 (45) + 两台机器由老枪 (cell_gunsmith) 售卖; 黑市老鬼低概率加售空白卡;
  **五种成品卡只能分析仪自产**。
- **回收出口 (v0.6.1)**: 老枪收购五种数据卡 (用过的卡按剩余次数折算价收, 另加 ll 声望 +2/张)。
- **数值平衡 (v0.6.1)**: 打印机 800→1200 / 分析仪 600→900 (两台都是印钞机, 需要成长门槛);
  空白卡 20→45 — 堵"空白卡 20 + 废部件 45 → 分析成卡 100 光倒卖就赚"的漏洞 (现在 45+45=90 → 100 只剩薄利,
  卡的真正价值在 5 次打印)。打印成本锚点: 1 锭=50 元=3mB → 弹匣约 18 元/个 (售 30), 长枪管约 68 元/个 (售 80)。
- 脚本 API (Events v1.6.0): `items.power / power_draw / uses / uses_max / use / use_init` + `ui.machine_slot_items` (grid_slot 多物品遍历)。

## 文件 (v0.6.0 新增)

| 文件 | 内容 |
|---|---|
| `items/gunworks_machines.json` | 部件打印机 (labeler 模板) + 物品分析仪 (desequencer 模板) |
| `items/gunworks_datacards.json` | 空白数据卡 + 五种成品数据卡 (blank_keycard 模板, 成品卡带 useCount) |
| `machines/printer_analyzer.json` | 两台机器声明: `"ui": "psui"` + panel |
| `ui/part_printer.psui` / `ui/item_analyzer.psui` | 面板定义 (结构恒定铁律同组装台) |
| `events/printer/01_data.pss` | 打印机数值表 (液量/耗电/卡→部件映射/部件液耗) |
| `events/printer/02_printer.pss` | 打印机状态机 + 隔夜结算 (打印供电优先) |
| `events/analyzer.pss` | 分析仪状态机 + 隔夜析出 |
| `events/cells.pss` | 老枪 sell_items 追加: 打印机/分析仪/空白卡×2 + 收购五种数据卡(声望+2/张); 老鬼加售空白卡 |

# 仿制证书 (v0.7.0, 需要 PSApi.Events ≥ v1.7.0 + PSApi.Items ≥ v0.8.2)

## 玩法 (用户裁定)

- **一张管所有枪械**: desc 里的枪械字段 (Pistol / Auto Pistol / SMG / Rifle / Bolt Sniper / Semi Sniper / H1)
  是展示/设定, 不做逐枪匹配。
- **开张前放上展示柜 (商品展示区)** → 当天卖任何 WEAPON 类型物品**不写治安档案**
  (`WEAPON_TRAFFICKING` 枪械违禁品记录与 `FENCING` 赃物记录同免; 其他物品照常)。
  开张后再放入当天不生效 (与原版许可证 HandlePermit 开卷帘门扫描同构)。
- **只管当天新记录**, 历史治安档案不动。
- **有效期 7 天** (v0.9.0 用户拍板, 原 3 天; 借原生 UseCountHelper: `useCount: 7`): 每晚**所有**证书 -1
  (随身背包/后仓/展示柜都扣), 归零自动销毁; tooltip 显示 剩余/7, 价格随剩余天数折算
  (满 300 = 底价 90 + 次价 30×7)。
- **展示柜防卖**: 脚本顶层 `shop.block_sale` 注册, 任何客户都不买证书 (否则会被路人当商品买走)。
- **获取渠道 (v0.9.0)**: ① 格雷(老鬼)货架 7 天基础款 (v0.10.0 起走 sell_pool 权重 8, 原 chance 0.15→0.08 已废); ② 伪造师费边对话售卖
  (v0.11.0 起固定价: 全套 300 块=随机 3-30 天款+3 件货, 现金不够选项不显示); ③ 朱利安见面礼 10 天款 (仅首次接受)。
  衰减按 id 扫描与次数上限无关 → 各天数款共用 permit.pss 逻辑。

## 技术路径

- 治安档案唯一写入口 = `SecData.CheckSoldContraband` (柜台议价 PerformOnSoldCheck 与展示柜
  自动售货 OnItemsSold 都走它) → `CrimeExemptPatch` Prefix: 豁免开 + `IsGameItemType("WEAPON")` → 跳过。
- 防卖 = `PlayerStore.CanSellThisItem` Prefix + 禁售 id 注册表 (`SaleBlockPatch`)。
- 展示柜枚举 = `EmporiumEntry.Instance.showcaseElement.childItems` (ItemsFacade.ShowcaseItems)。
- 新脚本 API: `crime.exempt_guns(bool)` / `shop.showcase_items()` / `shop.block_sale(id)` /
  `shop.unblock_sale(id)` / `items.find_all(id)`。

## 文件 (v0.7.0 新增)

| 文件 | 内容 |
|---|---|
| `items/gunworks_permit.json` | 仿制证书 (business_permit 模板, v0.9.0 起 useCount=7, 满值 300=90+30×7) |
| `events/permit.pss` | shop_opened 扫展示柜设豁免 / day_wake 复位 + 全场证书 -1 / 顶层禁售注册 |

# 素材合并 + 枪械零件 (v0.8.0)

## 素材合并 (物品编辑器 temp 素材)

- 29 张手绘图标进 `icons/`; 全部 25 个已有物品显式化 `shape`/`icon`/`tags`/`contraband`
  (mcb_weapon_gun1 不动)。`template` 全部保留作基底: 显式 shape/icon 覆盖克隆结果
  (ItemStore: shape 显式优先、icon 自定义键优先), tags 与模板类型合并 (merge 模式),
  contraband 显式等级走官方 InitContrabandItem; 文案/useCount/value 保持原值。
- 枪械显式违禁等级: 手枪 low / 自动手枪·冲锋枪 mid / 步枪 high / 两种狙击 critical
  (与仿制证书免记录体系配套 — 没证书卖枪会写治安档案)。

## 枪械零件 (组装台主材, 每次组装消耗 1 个)

| 物品 | 售价 | 用于 | 获取 |
|---|---|---|---|
| `gunworks:pistol_parts` 手枪零件 | 65 | 手枪/自动手枪 | 老枪 (0.6) |
| `gunworks:smg_parts` 冲锋枪零件 | 100 | 冲锋枪 | 老枪 (0.5) |
| `gunworks:rifle_parts` 步枪零件 | 130 | 步枪 | 老枪 (0.35) / 老鬼 (0.2) |
| `gunworks:sniper_parts` 狙击枪零件 | 185 | 两种狙击枪 | 老枪 (0.25) / 老鬼 (0.15) |

- 定价锚点 (部件合计+零件 ≈ 枪售价 55%~78%, 入门枪薄利走量、狙击厚利):
  手枪 195/260 (利 65) · 自动手枪 265/340 (利 75) · 冲锋枪 330/460 (利 130) ·
  步枪 420/560 (利 140) · 栓动狙击 395/620 (利 225) · 半自动狙击 495/720 (利 225)。
  组装台一晚一把, 零件是唯一现金主材 (部件走打印体系自产)。
- 组装台面板 (1,2) 空单元改为零件槽 s5 —— **元素树变更, 旧存档组装台读档串槽**
  (取回物品重买一台即可, 全是测试物品)。

# v0.9.0 大扩充 (NPC 英译名/皮肤/分段对话 · 证书天数体系 · 博士上架 · 货架流通)

> 依赖 Phase1 能力 (Events v1.9.0 / Items v0.9.1): dialogues texts 多段链、auto_leave、
> sprite 指定/FNV-1a 固定、`items.give(id,count,uses)`、`inject.sell_shelf/doctor` 的 `{uses=N}`。

## NPC 英译名 + 皮肤 + 对话

- 16 个 NPC 全部英译名 (对照表见上文映射节), id 不变, 绰号保留为对话"道上叫法"。
- 每个 NPC 指定 `sprite` (全部取自 `UserData/probe/` 实测皮肤键); 不写时由 Events v1.9.0
  FNV-1a 按 id 从同前缀池固定。
- 长对话全部拆 `texts` 多段链 (寒暄→正题→细节, 原版 NextDialogue 节奏); choices 挂链尾。
- 收购对话模糊表述全部改为具体物品名 (如"铅管、战斗匕首""壁垒霰弹枪、维和者冲锋枪")。
- 剧情网: 革命军借玩家店做武装中转 (组装台体系=自产线); 格雷中立洗货两头收钱;
  费边是格雷证书货源+朱利安旧识, 治安部通缉在案; 墨菲两头下注; 切尼是治安部钉子。
- 马洛夫 sell_items 加原版配件弹药 (7 条, 0.12~0.45); 格雷加原版赃物 (5 条, 0.08~0.25)
  + 仿制证书基础款 (0.15)。

## 引导员见面礼 (guide.pss 接受分支, 幂等仅首次)

枪械组装台×1 + `gunworks:permit_forged` 10 天款×1 (`items.give` uses=10) + rand(3,4) 次
从 9 种 part_* 随机送 1。选项 next 文案同步告知。

## 证书天数体系

- 基础证书 `useCount: 3→7`, 满价保持 300 (底价 90 + 次价 30×7, 旧 60+80×3)。
- 衰减逻辑零改动 (permit.pss 按 id 扫描 `items.use` 扣 1, 与次数上限无关)。
- 费边售卖 (v0.9.0 原版: 天数×45): 加权掷天数 (60% → 3-9 天, 40% → 10-30 天);
  **v0.11.0 起改固定价双选项** (见文末 v0.11.0 节), 掷天数逻辑保留在全套选项里。
- 费边调度: 每周一重掷本周行程, 随机 1-2 天 `npc.schedule` 进城。

## 博士机器上架 (distribution.pss)

`inject.doctor` 注册三台机器, 每天 `day_wake` 按 `inject.list()` 现查 idx (channel+id 匹配,
对注册顺序零假设) → `inject.tune(idx, {enabled})` + `inject.reset_tracking()`:

| 机器 | 起售天数 (rel_day) | 每日上架概率 |
|---|---|---|
| gw_bench 组装台 | ≥4 | 40% |
| part_printer 打印机 | ≥8 | 35% |
| item_analyzer 分析仪 | ≥12 | 35% |

## 原版 NPC 货架流通 (inject.sell_shelf)

摆货时机 v0.9.1 起对齐原版: 客户 main 对话链末行播完时摆 (与原版摆货 endAction 同帧),
不再到店即摆; OpenUI 兜底不变, 每客户一次拖走不补。

| 物品 | count (v0.10.0) | chance (v0.10.0) | 备注 |
|---|---|---|---|
| part_barrel_s / part_grip_s | 1-3 | 0.10 | 部件小件 |
| part_mag | 1-3 | 0.09 | |
| part_barrel_m / part_receiver_s | 1-3 | 0.08 | |
| datacard_barrel / datacard_grip | 1 | 0.03 / 0.02 | `{uses=5}` 满卡, 仍全场最低 |
| datacard_blank | 1 | 0.05 | 打印原料 |
| pistol_parts / smg_parts | 1-3 | 0.08 / 0.06 | 零件散货 |
| gw_pistol / gw_smg | 1 | 0.03 | 违禁品稀有好货 |
| handmade_pistol / pipe_weapon / combat_knife | 1 | 0.04 / 0.05 / 0.04 | 原版(巢火手枪/铅管/战斗匕首) |
| revolver / shotgun | 1 | 0.03 / 0.02 | 原版(左轮/壁垒霰弹枪) |
| silencer / match_barrel / mag_10mm | 1 | 0.03 / 0.03 / 0.04 | 原版配件 |

# v0.10.0 Phase4 售卖体系大修 (需 Events v1.10.0)

- **回归修复**: Phase3 把脚本 NPC 摆货挪到对话末行时判定要求链尾带 endAction, 而同批 auto_leave
  收窄到 DIALOGUE intent → SELL 型自定义 NPC 链尾无 endAction → 永不摆货。v1.10.0 脚本 NPC 链尾
  判定去掉 endAction 要求(链尾=话说完即可, 归属指针走查已够; 全局 sell_shelf 仍要求 endAction
  以对齐原版摆货帧), OpenUI 兜底保留, `_stockedSessionKey` 两路共享不互斥。
- **sell_shelf 数量区间**: count 支持 `"1-3"`(上限 5, 每客户实例独立掷); inject.list 行新增
  `count_max` 字段。
- **概率回调中间档**: 见上表 (v0.9.1 的 0.04-0.06 太低, v0.9.0 的 0.10-0.15 太高; 成品数据卡仍最低)。
- **inject.loot_pool 进原版掷骰池**: 解放步枪→makeshiftWeapon 0.03; 零件→material
  (pistol 0.04/smg 0.03/rifle 0.03/sniper 0.02); 空白卡→material 0.02。成品数据卡(次数消耗品)/
  机器(高价值设备)/证书(剧情伪证)不进池。消费方=拾荒客/小贼等 SELL 客户工厂+远征。
- **马洛夫/格雷售卖池化** (sell_pool + sell_count): 加权不放回抽种, 每种 1-2 件;
  权重=抽选概率, 与 sell_items 的 chance(每条独立判定)语义不同, 二者混用(先摆 sell_items 再补齐)。
  马洛夫 sell_count "4-6", 19 候选(模组货高权重: 零件/部件/空白卡/两机器/解放步枪/printed_gun;
  原版配件弹药低权重 7 条); 格雷 sell_count "3-5", 13 候选(证书 8/空白卡 6/模组枪与赃物)。
  其余 12 个细胞是小贩不是商户, 不动。
- **需求文本 round2**: 马洛夫弹药台词具体化(「阻铁」误写→调校扳机组件, 弹匣点名 .22/10毫米);
  其余"枪械零件"=gun_part 官方中文名, 已是具体物品名不改。

## 部件 tags 补齐 (v0.9.0)

`gw_semi_sniper` 补 `[FIREARM,WEAPON]`; `part_barrel_l`/`part_bolt` 补 `[GUN_MOD,MATERIAL]`
(对照同文件其他枪/部件; 任务单原写 [MATERIAL], 按 sibling 一致性含 GUN_MOD)。
mcb_weapon_gun1.json 不动 (既定红线)。

# v0.9.1 Phase3 修复 (需 Events v1.9.1 / Items v0.9.2)

- **见面礼改 `items.give_counter`** (guide.pss): 组装台+10 天证+随机 3~4 部件摆**门口柜台**归玩家
  (贴原版送礼路径, 不再入背包); "接受"回复补"货给你放门口柜台上了"指引; 不在对局/摆柜失败记日志。
- **摆货时机**: sell_shelf 与脚本 NPC sell_items 均改对话链末行播完时摆 (见上节)。
- **auto_leave 收窄**: 纯聊天"说完就走"仅 DIALOGUE intent 客户生效, 交易客户走原版交割离开,
  修"话没说完就走"。
- **刷货概率下调**: sell_shelf 部件 0.04-0.06 / 零件 0.03-0.05 / 模组枪 0.02 / 成品数据卡 0.01-0.015
  (全场最低); 格雷证书 0.15→0.08; 博士 40/35/35% 不动。
- **choices 回复核查**: guide 3 选项/forger 2 选项 next 均齐 (费边"买证书"回复两结局兼容措辞保持);
  cells.pss 14 NPC 纯文本对话无 choices 无需补。

# v0.11.0 Phase5 收购/价格/校验大修 (需 Events v1.11.0 / Items v0.9.3)

> 背景: 游戏 9-18 版本更新后旧物品参考全部过期 (items_catalog.md 9-4 / IsilDump 9-9 /
> game_templates.json 9-20), 唯一新实证 `UserData/probe/loottables_232456.txt` (9-21)。
> Events v1.11.0 起所有摆货/注入执行点对无效物品 id 跳过+WARNING (不再静默),
> 本包各 id 均有运行时校验兜底。

## 老猫·影 (cat_shadow.pss, 自 example_hello 迁入)

- example_hello e4_demo.pss 的演示 NPC「老猫·影」正式落户本包 (id `pss_demo_fence` → `cat_shadow`),
  **对话原文一字不动**; e4 侧代码注释保留作语法参考, 不再注册 (example_hello v0.4.1)。
- shadyMerchant 模板 / 黑市阵营 / sellnbuy / 现金 8000 / 收 `printed_gun`;
  schedule 每 2 天 80% 概率 + bm 加权池权重 5 双通道; can_spawn = 黑市声望非负。
- 货改 `sell_pool` 27 种加权不放回抽 6-10 种: 9 部件 + 4 零件 + 6 数据卡 + 6 组装枪 +
  仿制证书 + common_ore; **rare_ore 注释暂缓** (9-18 更新后存活性待 F11 全量 dump 确认)。
- 与格雷的 sell_pool 有意部分重叠 (销赃商串货, 两人到货日不同)。

## 费边固定价双选项 (forger.pss)

- 旧「天数×45」改两个明码选项: **证件全套 300 块** = 证书 (60% 3-9 天 / 40% 10-30 天,
  掷天数逻辑保留) + 随机 3 件货; **只要货 150 块** = 随机 3 件 (9 部件 权3-4 /
  5 成品数据卡 权2 uses=5 / 空白卡 权2, 加权不放回=拒绝采样实现)。
- **钱不够不显示**: Events v1.11.0 新键 `choices[].cond` (谓词函数, falsy=不显示);
  cond 隐藏会使运行时选项下标前移 → dialogue_choice 结算按 运行时索引+现金 双重判定。
- 扣款 `player.add_cash(-N)`; 三档: ≥300 全显 / 150-299 隐全套 / <150 只剩「再逛逛」。

## 模组物品收购全覆盖 (cells.pss, buy_pool+buy_count)

Events v1.11.0 新键: `buy_pool`+`buy_count` (与 sell_pool/sell_count 同构, 加权不放回掷 N 种,
与 buying_ids 必收合并; 与 sell 侧重叠报错)。收购价 = 原版折算 (不动)。
台词: 5 家 main 首候选末段加「今天还收: {buy_list}」(新插值, 点名 buy_pool 当日掷中种显示名;
必收项台词原本写死, 不复读)。

| 收购方 | buy_count | buy_pool 覆盖 |
|---|---|---|
| 邓肯 cell_electrician (电修铺) | 1-2 | 4 种枪械零件 + 空白数据卡 |
| 裘德 cell_junker (拾荒) | 2-3 | 9 种枪械部件 |
| 马洛夫 cell_gunsmith (枪匠) | 1 | 低档组装枪 gw_pistol/gw_autopistol/gw_smg (翻新) |
| 格雷 cell_fence (销赃) | 1-2 | 高档组装枪 gw_rifle/gw_bolt_sniper/gw_semi_sniper (洗大货) |
| 瑞特 cell_rat (快车道) | 1 | 仿制证书 + 解放步枪 mcb_weapon_gun1 |

- 五种成品数据卡 = 马洛夫 buying_ids 原有必收 (不动)。覆盖核对: 9 部件/4 零件/6 数据卡/
  6 组装枪/证书/空白卡/解放步枪 每个至少一个收购方, 分散不扎堆。
- **瑞特 sell_items 移除解放步枪** (改由其收购 — buy/sell 同 id 不允许重叠)。
- 切尼 (不收违禁品人设)/诺曼/拜伦不加; CELL_REP 声望表不动 (收购激励走台词+原版价格)。
- 自定义 NPC 价格 (Events v1.13.0, 9-18 实证): 全部显式声明 `price = {sell_single = 0.9,
  sell_bulk = 0.8, buy = 1.05}` = 单买九折(tooltip「顾客折扣」)/批发八折(原版整桌批发按钮)/
  收购 +5%(「供应商加价」特性, 叠在原版零售加价之上)。v1.13.0 起框架缺省中立 1.0/1.0/1.0,
  且克隆模板后自动白板化(清模板摆货回调/收购特性清单/sellPriceModifier)——奸商模板的
  违禁品压价(0元购)等原版私有行为不再泄漏进自定义 NPC。
