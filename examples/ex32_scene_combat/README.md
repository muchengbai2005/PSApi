# ex32_scene_combat · 示例 32：场景战斗与 grid（搜打撤）

> **演示知识点**（对应文档 [07-advanced/08-scenes-raid.md](../../api_docs/07-advanced/08-scenes-raid.md) 后半）：
> `combat.rt_start` 实时动作战斗（cfg 公式键全表 · `on_end` 回调载荷 win/flee/lose）·
> 敌人定义放 `data.pss`（与 gunworks `_raid/bestiary.pss` 工厂同构，大幅简化为 1 种敌人）·
> `grid.ground_show / ground_place` 战利品上地 · **搜打撤循环**：进场 → 战斗 → 搜刮（`scene_loot`）→ 撤离 ·
> 场景级 `layout.json` HUD 布局覆写（四层覆写 + F10 热重载 + M 键写回）。

## 文件清单

```text
ex32_scene_combat/
├── pack.json
└── scenes/
    └── ex32_arena/
        ├── scene.json          ← danger:2 战斗场景 (points: 军械架 + 撤离点)
        ├── layout.json         ← 场景级 HUD 布局覆写 (title/box/ground 三区)
        └── scripts/
            ├── data.pss        ← 敌人定义 ARENA_ENEMY (behaviors/charge/loot) + 文案表
            └── boot.pss        ← 开战/结算/掉落/撤离 四段循环
```

> 背景图 `icons/bg_arena.png` 由配套资源提供（scene.json 的 `bg` 键已引用 `bg_arena`）。

## 安装

把整个 `ex32_scene_combat` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex32_scene_combat/
```

## 验证（进存档后的搜打撤循环）

1. **进场**：白天点门口 → 选容器 → 地图面板出现「锈甲竞技场」（★★，loot_hint：金属 · 矿石）。
   点击进入的一瞬间看守战自动开打，控制台出现：

```text
[ex32_scene_combat] 进入场景 ex32_scene_combat:ex32_arena (danger=2) — 看守战开打
```

2. **战斗**：实时方向 QTE —— 敌人红色预警条亮起时按对应方向键闪避（完美/擦伤/全伤三档），
   伺机反击。战胜后场景日志与控制台出现：

```text
[ex32_scene_combat] 战斗胜利: 锈甲看守 倒下, sp_cost=13, 剩余 player_hp=68
[ex32_scene_combat] 互动物点击: point=armory_rack, type=loot
[ex32_scene_combat] 搜索掉落: armory_rack → metal_ingot x1
```

   看守的掉落（2 废金属 + 1 矿石）已躺在**地面窗**里 —— 拖进外出背包。
3. **撤离**：点「从塌方围栏撤离」→ 黑屏回店、外出物品自动回库：

```text
[ex32_scene_combat] 撤离场景, reason=fence_exit, 看守状态: 已打倒=true
```

4. **败局验证**（可选）：故意站着挨打到 HP 归零 —— 你会被直接撤离（教学简化版晕倒链）。
5. **HUD**：进场景后改 `layout.json` 任意键 → 保存 → 按 **F10** 热重载，布局当场生效；
   场景内拖动图元窗后按 **M**，钉位键会写回场景级 layout.json。

## 逐文件讲解

### scenes/ex32_arena/scene.json —— 战斗场景的声明

与 ex31 唯一的结构差异是 `danger:2` 和 points 只留"战后搜刮 + 撤离"两个点 ——
**战斗不占互动物位**，由 `boot.pss` 在 `scene_enter` 里直接 `combat.rt_start`。
物资点设计在开战前也能点（教学简化；练习 2 教你用 `EX32_FIGHT_DONE` 把它门起来）。

### scenes/ex32_arena/scripts/data.pss —— 敌人定义

gunworks 把敌人表集中在 `scenes/_raid/scripts/bestiary.pss`（16 个 `raid_enemy_*` 工厂函数），
本包简化成**一个手写 dict**，字段与工厂返回值同构：`hp` 直接值、`atk` 反击区间、
`band` 距离带、`behaviors` 加权行为卡组、`charge` 冲锋参数、`loot` 胜利掉落。
`combat.rt_start` 之外还有一整套**回合制公式件**（`combat.hit_chance / roll_damage / flee_chance /
armor_eff / roll_loot / rt_damage / dodge_tier...`）——公式全走 C# `RaidCore` 单一事实源，
脚本只编排；完整清单见文档 §六。

### scenes/ex32_arena/scripts/boot.pss —— 四段循环

- **组帧函数 `ex32_enemy_cfg()`**：把 data.pss 的"敌人定义"补上"开战参数"
  （`player_hp` / `posture_sneak` / `flee`）。gunworks 的 `raid_rt_cfg` 是它的完全体
  （含 depth 快照、atk_mul 地图乘区、hitbox_scale/warn_mul/tempo 条件透传）。
- **`ex32_rt_end` 回调**：`result=="win"` → `grid.ground_show()` 幂等开地面窗 → 逐条
  `grid.ground_place(id, count)`，返回值 `res.ground` 是实际放上地面的件数（地面满会截流）。
  `"flee"` 保命留场可再战；`"lose"` 简化为直接撤离（完整晕倒链见 `_raid`）。
- `rt_start` 返回 `false` 时**不留半截战斗状态**，撤离保平安。

### scenes/ex32_arena/layout.json —— HUD 四层覆写的第三层

```text
框架代码默认 < 包级 scenes/layout.json < 场景级 scenes/<id>/layout.json < 用户覆写
```

逐区逐键**深合并、后者胜**；本例只覆写 `title`（居中放标题）/`box`（外出箱钉位）/
`ground`（地面窗 `mode:"free"` 绝对钉位）三区。改完保存按 **F10** 热重载；
场景内拖好窗口按 **M** 会把钉位键写回本文件。全部区键见文档 §七。

## 动手练习

1. 给 `ARENA_ENEMY` 加 `armor = 20`（0-100 减伤%）与 `tempo = 0.8`（行为更频繁），重开一局感受 TTK 变化。
2. 在 `on scene_interact` 里判断 `event.point == "armory_rack" and not EX32_FIGHT_DONE`
   → `scene.log("军械架被看守盯着, 先打赢再说")` 然后直接 `return`，把搜刮点真正门在胜利之后。
3. 给 `behaviors` 换一张卡（`{type="zigzag", weight=70, speed=300, turn_interval=0.5}`），
   体会行为卡组"加权出牌"的的手感差异。
4. 把 `layout.json` 的 `ground.mode` 删掉观察回退，再按 F10 —— 删掉某字段 = 该字段回退下一层。

## 下一个示例

- [ex33_state_deep](../ex33_state_deep/README.md) —— 存档状态深入：开业天数 / 累计收入 / 一次性标志
