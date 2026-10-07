# 08 · 自定义外出场景（搜打撤 / raid）

> PSApi v2.0.0 的场景框架：不打包 Unity 场景，用 **scene.json + pss 脚本 + 布局 JSON**
> 声明一个"夜间外出地点"，挂进原版地图面板，店内黑屏过场进出。
> 事实来源：`_psapi/PSApi/Events/Scenes/`（SceneService / SceneModels / RtCombatService /
> ScriptGridService / ScriptCombatService / SceneLogService）、`_psapi/PSApi/Events/Raid/`
> （RaidCore 公式 / RaidLayout 布局）、`_psapi/PSApi/Events/PsScript/BuiltinsGame.cs`
> （scenes.\*）与 `BuiltinsSceneApi.cs`（combat.\* / grid.\* / scene.log）；
> 实证样板 = examples 场景教学包（[ex31_scene_basic](../../examples/ex31_scene_basic/README.md)
> / [ex32_scene_combat](../../examples/ex32_scene_combat/README.md)）。
> 原版机制背景见 `_docs/开发向/原版夜间外出与场景系统调研.md`。

## 一、30 秒看懂原理

原版夜间外出**不是 Unity 场景切换**——整张游戏只有一个主场景，"外出地点"是同场景内
GameObject 显隐切换 + 黑屏过场。PSApi 场景框架复刻同一模式：

```text
点门（原版条件闸照常：白天/太晚/镇压/重伤/恐惧症）
  → 原版选容器窗口（塑料袋/背包）→ 原版地图面板
  → 你的场景按钮出现在「垃圾场」下方（picker 双栏列表）
  → 点击 → 黑屏 → 场景（包自绘 uGUI / psui 面板 / 默认按钮列）
  → 撤离 → 黑屏回店，外出背包物品自动搬回玩家库存，当晚可睡觉
```

## 二、目录结构：扁平与文件夹双形态

```text
packs/<pack>/scenes/
  old_scene.json            ← 旧式扁平：一文件一场景（仅 M1 静态场景）
  layout.json               ← 包级 HUD 布局覆写（可选，见 §七）
  raid_depot/               ← 新式文件夹：一个场景一个文件夹（推荐）
    scene.json              ← 场景声明（必填）
    layout.json             ← 场景级布局覆写（可选）
    scripts/                ← 场景脚本（可选，同 pss 管线）
      boot.pss
      data.pss
  _raid/                    ← 下划线前缀 = 库目录：不注册为场景（静默跳过），
    scripts/                  公用玩法脚本/资源存放处；脚本照常全局装载且先于
      main.pss                场景脚本执行（v1.37.1，任一目录段以 "_" 开头即库路径）
```

识别规则（SceneService.ScanPacks）：只认 `scenes/<id>.json` 与 `scenes/<id>/scene.json`
两种布局；`layout.json` 与 `_` 开头目录不当场景；其余文件打告警跳过。
`scenes/<id>/ui/` 下 `.psui` 与 `scripts/` 下 `.pss` 由各自管线递归扫描，
**与同包 `events/*.pss` 共享全局环境、无作用域隔离**——脚本靠 `on scene_enter`
事件 payload 的 `event.scene` 判断当前场景。

## 三、scene.json 键全表（v2.0.0 schema）

| 键 | 类型 | 缺省 | 说明 |
|---|---|---|---|
| `id` | 串 | **必填** | 包内唯一；全名 = `包id:场景id` |
| `name` | 串 | =id | 地图按钮与场景标题显示名 |
| `desc` | 串 | — | 描述 |
| `danger` | 整 | 1 | 危险度 1-5，标题显示 ★ |
| `loot_hint` | 串 | 不显示 | 产出提示（picker 右栏详情，v1.37.0） |
| `entry` | 串 | `"map"` | `"map"`=注入地图+进 picker 列表；`"hidden"`=不注入不进列表，但可被 `scenes.enter` 进入（v1.32.0）。归一 trim+小写，非法值必抛 |
| `bg` | 串 | — | 背景图键（预留） |
| `bgm` | 串 | `"dumping_ground"` | 环境音：`dumping_ground`（原版垃圾场同款）/ `commissary` / `inventor` / `store` / `none` |
| `ui` | 串 | 自动按钮列 | 场景窗口 psui 文件名（包 `ui/` 下，不带扩展名，容忍 `.psui` 后缀）；缺省 = 旧式自动网格按钮列 |
| `driver` | 串 | 推断 `script` | `"script"`=纯 pss 驱动（**v1.33.0 起唯一内容驱动**）；`"picker"`=选图场景（见 §四）。归一 trim+小写，非法必抛；picker 只认显式声明 |
| `points` | 数组 | `[]` | 互动物（M1 静态场景用，见下） |

**已退役键**：`raid_inv` 与 v2 raid 内容键（`chain_length` / `poi_table` / `pois` /
`enemies`）随 v1.33.0 `builtin_raid` 驱动退役移出 schema——写了按未知键容忍、不消费。
raid 玩法只剩 `scenes/_raid` pss 库一套实现（数据表在 `scripts/data.pss`，见 §六）。

### points 互动物（M1 静态场景）

```jsonc
{ "id": "shelf1", "type": "loot", "label": "搜刮货架",
  "x": 0.25, "y": 0.6,                    // 相对坐标 0-1，原点左下
  "loot": ["scrap_metal"], "loot_count": "1-3", "times": 2 }
```

- `type`：`"loot"` 搜索点（`loot` 物品 id 池必填，等概率；`loot_count` `"1-3"` 区间或
  `"2"` 定值，默认 1；`times` 可搜次数默认 1，耗尽按钮变灰）| `"exit"` 撤离点。
- 启动期校验（不等到点击）：缺 id / id 重复 / type 非法 / loot 点缺池 → 不注册+警告。
- **exit 要求**：推断所得（未写 driver）的 legacy 场景必须至少一个 exit 点；
  显式 `driver=script` 由脚本自行撤离（`scenes.leave` / psui on_click），picker 由框架
  提供返回（`scenes.back` / Esc /「返回地图」按钮），均不要求 exit 点。

## 四、driver：script 与 picker

- **script**（缺省推断）：C# 只给场景壳——进出/黑屏/外出背包/事件发布 + 能力 API
  （scenes.\* / combat.\* / grid.\* / scene.log）；玩法逻辑全部 pss 自写（或调 `_raid` 库）。
- **picker**（v1.32.0）：选图面板本身场景化。轻量壳——不黑屏不离店，藏 mapPanel +
  选图 UI，退出对称恢复。全注册表唯一生效；无包注册时框架内置 `psapi:picker` 兜底。
  包声明了 `ui` 键或 `scripts/` 目录 = 包自绘，默认 C# 渲染不建。

## 五、scenes.\* API 与场景事件

```text
scenes.enter(id) → bool      进入场景（短名或 "包:id"；场景内=跳转并压栈返回点，
                             非场景态=直入并压 "map"；不存在/不可用 = false）
scenes.back() → bool         返回上一场景（空栈=回店；栈顶 map=回店+重开官方地图）
scenes.leave([reason]) → bool 清栈撤离（黑屏回店；不在场景=no-op false）；
                             psapi.scene.leave 的 reason 取该值（缺省 "script"）
scenes.active() → 串|null    当前场景 FullId
scenes.list() → [dict]       entry==map 场景数组
                             {id,name,danger,desc,loot_hint,locked,lock_reason}
                             （v1.47.0 门禁生效：hide 不列出，锁定 locked=true）
scenes.set_gate(fn | null)   门控（v1.47.0）：fn(场景全id) 返回
                             null/false=可进；"hide" 前缀串=完全隐藏；
                             其他字符串=锁定原因（picker 置灰显示）；null 清除
```

场景 id 解析：全 id（含 `:`）直查；短名匹配——唯一命中即用，多义取先加载者并告警。

事件（payload 公共键 `scene`/`name`/`danger`/`day`）：

| 事件 | 附加键 | 时机 |
|---|---|---|
| `psapi.scene.enter` | — | 进入场景（黑屏后） |
| `psapi.scene.leave` | `reason` | 撤离 |
| `psapi.scene.interact` | `point` / `type` | 互动物点击（M1 内置按钮列与 psui 路由均发布） |
| `psapi.scene.loot` | `point` / `type` / `item` / `count` | 搜索点掉落入外出背包 |

## 六、raid 玩法 = `_raid` pss 库 + 数据表（实证范式）

raid 没有 C# 内容驱动——机制全在包内 `scenes/_raid/scripts/`（hud/loot/combat/bestiary/
main 五件），每张地图文件夹只放三样：`scene.json` + `scripts/data.pss`（数据表）+
`scripts/boot.pss`（入口，on scene_enter/leave 判 id 后调库公共入口
`raid_enter(scene_id, DATA)` / `raid_leave(scene_id)`）。

**加一张新地图只要三步**（boot.pss 开发者样板）：

1. 复制场景文件夹改名（如 `scenes/raid_dock/`）
2. `scene.json` 改 id/name/danger（`driver=script` 不变）
3. `data.pss` 改数据表 + `boot.pss` 里场景 FullId 改成新值

DATA 表 schema（顶层 dict；现行版本）：

| 键 | 必/选 | 说明 |
|---|---|---|
| `name` / `danger` | 必 | 显示名 / 危险度 1-5（入场日志星级） |
| `chain` | 必 | 链长 `"8-9"` 区间或 `"4"` 定值（到底=最终物资点） |
| `poi_table` | 必 | 前进掷签表 `{poi=, combat=, event=, empty=}` 权重任意正数归一化；combat 键概率×0.5 = 归途遇袭基数 |
| `enemies` | 必 | 敌人表（可空表 = 无战斗），条目：`{id,name,desc,hp 直接值(≤10 回退 pip×15), atk "3-6", dodge, armor?(0-100 减伤%, 可被 pen 穿透), band?(0近1中2远), hitbox_scale?(0.5-1.3), warn_mul?(0.7-1.6), tempo?(0.5-1.5 行为间隙系数), swarm?{count,box_scale,spread}(与 shield 互斥), behaviors?=[{type,weight,...}], shield?, charge?, strong?(强敌池 噪音≥6 抽), noise_attract?(默认1), loot=[{id,weight,count?,min_depth?,rarity?}]}` |
| `pois` | 必 | 物资点表 `[{id,name,gate={type},loot=[...]}]`；`gate.type`: none / tool（配 `tools=[物品id]`）/ guard（配 `enemy=敌人id`）/ tool+guard |
| `fights` | 选 | 每局战斗场次目标 `"5-7"` 区间（掷签保底/封顶，v0.40.0） |
| `activate` | 选 | 点位池随机激活数（缺省全激活，v0.39.2） |
| `rare_pois` | 选 | 稀有物资点表 `[{chance, poi}]`，独立掷出现率追加进场 |
| `atk_mul` | 选 | 地图攻击乘区（缺省 1.0，与 depth 乘区叠乘；引擎取开战时深度快照 hp/atk ×(1+0.10×depth) 钳 0-8） |
| `rarity` | 选 | 稀有度分布覆写 `{common=‰, fine=‰, rare=‰, epic=‰, legend=‰}`（缺省按 danger 全局表；物资点掷签走稀有度两阶段：先按档掷签再档内加权） |
| `local_pct` | 选 | 普通点本地池占比覆写（缺省 0.65） |
| `loot_count` | 选 | 件数分布覆写 `{normal/gated/final/rare=[[件数,权重]...]}` |
| `box_id` / `ground_title` / `empty_texts` / `hud` | 选 | 跟随物品箱机器 id（默认 travel_box）/ 地面窗标题 / 空事件文本表 / HUD 布局覆写子表 |

`behaviors` 行为卡组（加权出牌）：`strafe`(speed/amplitude/noise 相位噪声 0-4) /
`shrink` / `hide` / `shield` / `charge` / `dash`(distance/count) / `zigzag`(speed/
turn_interval/bound) / `circle`(speed/rx/ry) / `leap`(height) / `feint`(cancel_chance)。
`shield={dots,reduction,moving_ratio,teleport_ratio,red_window}`；
`charge={duration,stagger_mul,heavy_mul}`。

### combat.\* 能力 API（C# 公式层，RaidCore 薄封装）

- **常量/档位**：`combat.consts()`、`hp_tier(hp)`（0健康~4晕倒）、`tier_name`、
  `posture_index("run|walk|crouch|prone")`、`major_wound()`
- **回合制公式**：`advance_cost`、`rest_hunger_cost`、`search_value_mul`、
  `hit_chance`（钳 5-95）、`roll_damage`（×姿态×档位×0.85-1.15）、`jam_chance`、
  `flee_chance`（钳 0-95）、`unarmed_power`、`armor_eff(armor%,pen)`、
  `band_*`（距离带）、`melee_*`、`weapon_info`、`consume_ammo`、`throw_item`
- **掷签**：`roll_encounter({poi,combat,event,empty})`、`combat_rate`、`roll_chain`、
  `roll_loot(pool, depth[, ignore_min_depth, value_mul, min_items, max_items, rarity])`
- **实时战斗（rt）**：`rt_start(cfg, {on_end})` / `rt_active()` + 公式件
  `rt_damage`（×0.9-1.1）、`fire_cd(60/rpm)`、`jitter_radius`、`warn_duration`
  （1.2s/近战 0.96）、`enemy_interval`（2.2-3.5s）、`dodge_tier`（|dt| 边界
  0.15/0.4 → 完美/擦伤/全伤）、`dodge_damage_mul`（0/0.4/1）、`gunstock_atk`（×0.4）、
  `rt_enemy_hp`/`rt_enemy_hp_value`、`shield_*`、`expose_*`、`shrink_scale`、
  `charge_stagger_threshold`、`dot_hits_for_size`、`weaken_mul`

`combat.rt_start(cfg, opts)` 配置键：`{id?, name?, hp(必, 直接 HP 值; ≤10 回退旧
pip×15), atk?("3-6"), strong?, posture_sneak?, player_hp?, flee?(成功率% 0-95),
armor?(0-100), band?(0近1中2远 默认1), tempo?(0.5-1.5 默认1), behaviors?(行为卡组),
shield?, charge?, on_hp_change?(pss 函数收 {hp,max})}`；`opts.on_end` 收
`{result="win|flee|lose", player_hp, sp_cost(战斗时长×0.6 体力结算), enemy_id,
enemy_name}`。会话期间自动锁物品箱/地面窗 + 隐藏光标。

### grid.\* 与 scene.log

`grid.open_container(title, 物品表, [on_close])` / `close_container` / `container_open` /
`ground_place/has/count/clear/show(title)/steal` / `afterhour_place/has/hide` /
`box_follow([machine_id])`（外出箱跟随）/ `box_show/has/slot` / `has_tool`；
`scene.log(text)` 写场景日志区（ SceneLogService，条数上限见 `combat.consts().log_cap`）。

## 七、HUD 布局：四层覆写 + M 写回 + F10 热重载

```text
框架代码默认（RaidLayout POCO 初始化器）
  < 包级   packs/<pack>/scenes/layout.json
  < 场景级 packs/<pack>/scenes/<id>/layout.json
  < 用户覆写 UserData/PSApi/layout_overrides/<pack>/<sceneId>.json   ← 最高优先（v1.48.8）
```

- **逐区逐键深合并**，后者胜；删掉某字段 = 该字段回退下一层；非法值告警并回退下一层
  （钳制在使用端）。用户覆写层是物理文件、与包形态无关——内嵌 DLL 包也能被玩家覆写。
- 无场景上下文（picker）= 代码默认 + 全部包级 layout.json 按包加载序合并。
- **坐标系**：x/y = 相对屏幕 0-1（原点左下，区域中心点；log 区 = 左下角锚点；
  title 区 = 左上角屏比）；w/h/font/spacing = uGUI 像素；w/h 在 ground/container/pocket
  区 = 格数。`box.y` / `pocket.y` = 窗口底边锚点。`ground.mode`：`box`=贴物品箱右缘
  +gap 底边对齐（箱缺失回退 x/y），`free`=永远按 x/y 绝对钉位。
- **区键**：`canvas_order`（0=自动探测图元窗层级-1）/ `status`（HP/体力/饥饿条+噪音
  深度行+姿态按钮）/ `action`（前进/撤离/休息按钮）/ `log` / `poi` / `picker`
  （双栏 list_w/detail_w/detail_font/rows）/ `title` / `ground` / `container` / `box` /
  `pocket`（box_follow 失败时的随身兜底网格，撤离时内容自动搬到店里称重台）/
  `combat` / `rt`（实时战斗 UI 全部几何/颜色/键位：top/enemy_hp/arena/hitbox/crosshair/
  warn/arc/bottom/flee/throw/switch/status/keys，键位 = KeyCode 名，非法回退默认）。
- **M 键写回**（场景内拖好图元窗后按 M）：文件夹包 → 写当前场景的**场景级**
  `layout.json`（只写钉位键 box/ground/container + ground.mode 自动置 free；
  junction 开发模式写穿落开发主库，有意为之）；内嵌包（无物理目录）→ 写**用户覆写层**
  （v1.48.8 起不再放弃）。
- **F10 热重载**：重读当前场景 默认+包级+场景级+用户覆写 四层 → 重建 uGUI HUD
  （picker/标题面板开着也对称重建）。改完 layout.json 保存、游戏内按 F10 即生效。
- 旧全局文件 `UserData/PSApi/raid_layout.json` v1.31.0 起废弃（启动检测打迁移提示）。

## 八、打包与分发

pack_compiler 对 `scenes/` 与包内其余目录一视同仁——递归内嵌进 `pspack/` 资源
（含场景文件夹的 scene.json/layout.json/scripts/\*.pss/ui/\*.psui），编译期引用
**PSApi.dll**（`Private=false`，不随包分发）。自检：

```bash
dotnet run -c Release --project _tools/pack_compiler -- --verify "out\PSPack.my_pack.dll"
```

`--verify` 只读元数据/资源（不加载类型，任何机器可安全执行）：列出 pspack/ 资源数与
总字节、打印 pack.json 的 id/version，**v2.0.0 起附程序集引用表**——合并迁移核验用，
应引用 `PSApi` 而非旧的 `PSApi.Items` / `PSApi.Events`。

编译细节与冲突规则见 [02-pack · 03 编译分发](../02-pack/03-distribution.md)。

## 九、排障

- 启动日志 `[scenes]` 行：注册数、无法识别的文件布局告警、库目录跳过是静默的（正常）。
- 场景脚本编译错误落在 `UserData/PSApi/logs/pack_errors`；场景内运行日志用
  `scene.log()` 落场景日志区。
- `scenes.list()` 在 psconsole 里直查注册表与门禁结果；`scenes.enter("pack:id")`
  可不进地图直接跳进场景调试。
- 通用排障路径见 [06 调试与错误对照](06-debugging.md)。
