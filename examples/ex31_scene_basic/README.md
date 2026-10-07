# ex31_scene_basic · 示例 31：自定义外出场景（基础）

> **演示知识点**（对应文档 [07-advanced/08-scenes-raid.md](../../api_docs/07-advanced/08-scenes-raid.md) 前半）：
> `scenes/<id>/scene.json` 场景声明（`driver:"script"` 纯脚本驱动）·
> M1 `points` 互动物（`loot` 搜索点 / `exit` 撤离点）·
> 场景事件四件套 `scene_enter` / `scene_interact` / `scene_loot` / `scene_leave` ·
> 场景脚本与 events 共享全局环境、靠 `event.scene` 判路由 ·
> `scenes.leave` 撤离与外出背包自动回库 · `scene.log` 场景日志区。

## 文件清单

```text
ex31_scene_basic/
├── pack.json
└── scenes/
    └── ex31_outpost/
        ├── scene.json          ← 场景声明: id/name/desc/loot_hint/danger/entry/driver/bg + points
        └── scripts/
            ├── data.pss        ← 常量表 (文案/点位速查) — 数据与入口分离的惯例
            └── boot.pss        ← 事件入口: 进场/互动物/掉落/撤离 四个 handler
```

> 背景图 `icons/bg_outpost.png` 由配套资源提供（scene.json 的 `bg` 键已引用 `bg_outpost`）。

## 安装

把整个 `ex31_scene_basic` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex31_scene_basic/
```

启动日志搜 `[scenes]`，注册数 +1。

## 怎么进场景（地图入口）

1. 进存档，白天在店里**点门口**触发原版夜间外出（条件闸照常：不能太晚/重伤/恐惧症）。
2. 原版选容器窗口（塑料袋/背包）→ 原版地图面板：**「前哨废墟」按钮出现在「垃圾场」下方**
   （`entry:"map"` 注入地图 + 进 picker 列表；`loot_hint` 显示在右栏详情）。
3. 点击 → 黑屏过场 → 进入场景：默认按钮列显示三个互动物（两个搜索点 + 一个撤离点）。

## 验证（进存档后的操作）

1. 进场景瞬间，控制台出现：

```text
[ex31_scene_basic] 进入场景 ex31_scene_basic:ex31_outpost (danger=1, day=1)
```

   场景内日志区（黑屏过场后场景左下）出现两行：`你钻过铁丝网进了前哨…` 与 `拿够东西后…`。
2. 点 **搜刮哨位** —— 物品直接掉进外出背包，控制台出现：

```text
[ex31_scene_basic] 互动物点击: point=lamp_post, type=loot
[ex31_scene_basic] 搜索掉落: lamp_post → scrap_metal x1 (已入外出背包)
```

   再搜一次它就变灰（`times:2` 耗尽）。
3. 点 **从断墙豁口撤离** —— 黑屏回店、外出背包物品自动搬回库存，控制台出现：

```text
[ex31_scene_basic] 互动物点击: point=gate_exit, type=exit
[ex31_scene_basic] 撤离场景, reason=gate_exit
```

## 逐文件讲解

### scenes/ex31_outpost/scene.json —— 一张地图的自我介绍

- **识别规则**：`SceneService` 只认 `scenes/<id>.json`（旧式扁平）与 `scenes/<id>/scene.json`
  （新式文件夹，本例）；`layout.json` 与 `_` 开头目录不当场景。
- `driver:"script"` 显式声明 = 纯 pss 驱动，C# 只给壳（黑屏进出/外出背包/事件发布）。
  不写 `driver` 的 legacy 场景**必须至少一个 exit 点**；显式 script 则由脚本自行撤离。
- `points` 是 M1 静态互动物，启动期就校验（缺 id / id 重复 / type 非法 / loot 缺池 → 不注册+警告，
  错误不用等玩家点击才暴露）：
  - `loot` 点：`loot` 物品池**等概率**抽 1 件、`loot_count` 每次掉几件（区间或定值）、`times` 可搜次数；
  - `exit` 点：本包由 `boot.pss` 的 `scene_interact` 认领并调 `scenes.leave`。
- `bg:"bg_outpost"` 引用背景图键（png 由配套资源放 `icons/` 下）。

### scenes/ex31_outpost/scripts/data.pss —— 数据与入口分离

场景 `scripts/` 下的 `.pss` 走同一条 pss 管线，**与同包 `events/*.pss` 共享全局环境**、
无作用域隔离 —— 所以常量 `EX31_SCENE` / `EX31_TEXTS` 在 boot.pss 里直接可用。
多场景包里所有脚本都会装载，进场景后靠 `event.scene` 判断"现在在哪张图"。

### scenes/ex31_outpost/scripts/boot.pss —— 四个事件 handler

- 每个 handler 第一行都是 `if event.scene != EX31_SCENE: return` —— 多场景共存时的标准路由。
- `scene_loot` 事件里**框架已经替你把物品放进了外出背包**，你只负责记账/演出；
  `scenes.leave` 也替你做完黑屏回店与背包回库，撤离点 handler 只有一行调用。
- `scene.log()` 与 `log.info()` 是两条日志通道：前者给玩家看（场景日志区），后者给你自己看（控制台）。

## 动手练习

1. 给 `supply_crate` 再加一件本包物品（先在 `items/` 定义一个 `test:true` 物品），重启验证 `loot` 池混写本包/原版 id。
2. 把 `lamp_post` 的 `times` 改成 `4`、`loot_count` 改成 `"2-3"`，感受次数耗尽与件数区间。
3. 把 `entry` 改成 `"hidden"` —— 地图上不再出现按钮，只能靠 `scenes.enter("ex31_scene_basic:ex31_outpost")`
   （psconsole 或其他脚本）进入，适合做剧情解锁地图。
4. 在 boot.pss 的 `scene_enter` 里按 `event.day` 写个分支：单日场景只在特定日子开门（配合 ex30 学过的 `state` 可做成一次性剧情图）。

## 下一个示例

- [ex32_scene_combat](../ex32_scene_combat/README.md) —— 场景战斗与 grid：搜打撤循环 + `combat.rt_start` + HUD 布局覆写
