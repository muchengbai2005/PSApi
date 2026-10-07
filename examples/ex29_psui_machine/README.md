# ex29_psui_machine · 示例 29：机器绑定面板（物品 + machines 声明 + 双击开窗）

> **演示知识点**（对应文档 [06-psui/08-machine-panels.md](../../api_docs/06-psui/08-machine-panels.md) ·
> [07-slots.md](../../api_docs/06-psui/07-slots.md) ·
> [03-items/03-machines.md](../../api_docs/03-items/03-machines.md)）：
> 机器三件套 = 机器物品（`items/`）+ 机器声明（`machines/` 里 `ui: "psui"` + `panel`）+ 面板
> （`ui/<panel>.psui`）· 玩家**双击机器物品**原生开窗（脚本不 `ui.open`/不 `bind_key`）·
> 槽内容**随机器存档**（隔夜不丢、关窗不退）· `on_open` 开窗沿回调（机器面板专属）·
> 回调全部带 `machine` 字段（`{elem, machine}` / `{elem, value, machine}`）·
> `machine_*` API 族（`machine_slot_items` / `machine_set_text` / `machine_spawn` /
> `machine_lock` / `machine_clear` / `machine_is_open` / `machine_find_uid`）·
> 输出槽**占位白名单**套路 · `state` 存 uid 隔夜找回机器（句柄不能进 state）·
> **结构恒定铁律**（元素树不得增删，防旧存档串槽）· `items.consume(句柄, count)` 部分消耗。
> 写法参照官方机器面板 `gunworks`（gun_bench 三件套）。

## 文件清单

```text
ex29_psui_machine/
├── pack.json              ← 包清单
├── items/
│   └── workbench.json     ← 机器物品「演示加工台」+ 成品「精工齿轮」(均 test:true)
├── machines/
│   └── workbench.json     ← 机器声明: ui=psui, panel=workbench
├── ui/
│   └── workbench.psui     ← 机器面板 (料仓 grid_slot + 输出槽 slot + 按钮)
└── events/
    └── workbench.pss      ← on_open / 槽位差分 / 加工玩法 / machine_* API / uid 找回
```

## 安装

把整个 `ex29_psui_machine` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex29_psui_machine/
```

## 验证（5 分钟）

1. 启动游戏，控制台的机器体检行应含本机器（`ui=psui`）：

```text
machine audit: ex29_psui_machine:workbench ui=psui panel=workbench ...
```

2. 进任意存档，按 **F12**（调试发放键，一次性发放全部 PSApi 注册物品）→ 后仓获得
   **演示加工台**（也可按 **F6** 在管理面板物品浏览器"测试"分类单独拿）。
3. **双击**后仓里的「演示加工台」→ 原生打开加工面板，日志：

```text
[PSApi] [pss ex29_psui_machine] [ex29_psui_machine] [on_open] 加工台面板打开: 机器 uid = xxxxxxxx
```

4. 把 **2 个废金属**（原版物品）拖进左侧料仓 → 状态行实时更新
   「料仓现有 2 个废金属 · 加工一次消耗 2 个」（槽位 `on_change` 差分触发）。
5. 点 **加工 (耗 2 废金属)** → 日志与状态行：

```text
[PSApi] [pss ex29_psui_machine] [ex29_psui_machine] [加工] 消耗 2 废金属 → items.give(ex29_psui_machine:finished_gear, 1) → 本机累计 1 件
```

   后仓出现 **精工齿轮**（成品 `items.give` 直接给背包）；料仓少 2 个。
   手动拖任何物品向右侧**输出槽** → 放不进（占位白名单 `__reserved_output` 排他）。
6. 点 **输出槽演示** → 精工齿轮 `machine_spawn` 直入输出槽并 `machine_lock` 双锁
   （拿不出来）；再点一次 → `machine_clear` 清槽销毁并解锁。对照日志体会
   spawn / lock / clear 三件套。
7. **持久性**：关窗再开 → 料仓里的废金属还在（随**机器**存档，不退玩家）；
   睡觉过夜 / 存档重读 → 再双击机器，日志
   `[机器面板包就绪: 找回加工台 (uid …) — 累计加工 N 件]`——`state` 存的是 uid，
   句柄由 `ui.machine_find_uid` 现场恢复。

## 逐文件讲解

### machines/workbench.json —— 两行决定它是机器

- 一个物品是不是机器，**只看它在不在 `machines/*.json` 里声明**（不按模板猜）。
  `id` 必须与 `items/*.json` 的物品 id 对上，否则警告 `never built`。
- `ui: "psui"` = 完全自定义窗口路线（另有 `furnace` 等 13 种原版桥接、`custom`
  自装配）；`panel: "workbench"` 是**短名**（自动补本包前缀），必须与
  `ui/workbench.psui` 的 `window workbench:` 严格一致。

### items/workbench.json —— 机器物品本体

- 机器物品就是普通物品 JSON：`directory: "StationMachinery"` 进"站台机械"目录、
  `template: "desequencer"` 提供机器基底、`shape` 显式 3x3 覆盖克隆形状。
- 成品「精工齿轮」也是普通物品（2x2，`value: 150`）——它不进 machines 声明，
  双击没有任何机器行为，只能被加工产出。

### ui/workbench.psui —— 与独立面板同一套语法，多一层铁律

- **`on_open: wb_on_open`** 是 window 属性、机器面板专属：每次"关→开"跳变沿触发
  `{machine}`——它是机器面板的"刷新时机"（读槽、改文本都在这里做）。
- **`persistent: true`** 写上是明确意图：机器面板槽内容随机器存档，**不会**像
  ex28 那样关窗退回玩家。
- **结构恒定铁律**：面板元素树（含 spacer！）永不增删——存档按图节点 BFS 索引
  记录槽内物品位置，改树 = 旧存档串槽。要变的是文本/白名单/锁，全部运行时改写。
- 输出槽 `whitelist: ["ex29_psui_machine:__reserved_output"]` 填一个**不存在的
  占位 id**：排他注册后手动放入全被拒，成品只能由脚本 `machine_spawn` 直入。

### events/workbench.pss —— 机器回调工作流

- **回调签名全家带 machine**：`on_click {elem, machine}`、槽位 `on_change
  {elem, value, machine}`、`on_open {machine}`——同一面板定义可能装在多台机器上，
  状态要用 `machine.uid` 分台隔离（本包 `wb:crafts:<uid>` 每台各记各的）。
- **machine_\* 与 ui.\* 平行**：读槽 `ui.machine_slot_items(m, "s_in")`、改文本
  `ui.machine_set_text(m, "lbl_status", …)`——首参都是机器句柄；用 ex28 的
  `ui.get_slot_items` 读机器面板是读不到的。
- **材料消耗用部分消耗**：`items.consume(句柄, take)`（v1.39.0）从堆里直减
  `take` 个，比"整堆销毁"省料；消耗本身也是槽位变化，会再触发一次 `on_change`
  → `wb_refresh` 重读刷新（幂等，不叠加脏状态）。
- **句柄不能进 state**：读档后句柄失效。存机器的存档稳定 `uid`，读档时
  `ui.machine_find_uid(uid)` 恢复引用，`ui.machine_is_open` 查开合——
  gunworks 组装台 `bench:list` 的同款套路。

## 动手练习

1. 把材料换成别的原版物品：改 `ui/workbench.psui` 料仓 `whitelist`（记得
   `game:` 前缀）+ `events/workbench.pss` 的 `WB_MATERIAL`（句柄 id 是剥前缀的
   裸名）——体会同一个物品的两种 id 写法。
2. 把"成品 give 到背包"改成正式输出槽套路：`wb_craft` 里不 `items.give`，改为
   `ui.machine_spawn(m, "s_out", WB_PRODUCT, 1)` + `machine_lock` 双锁，
   在 `wb_out_changed` 里检测"玩家取走 → 解锁"（参照 gunworks 组装台）。
3. F12 再发一台加工台 → 两台并存：分别加工几件，重开面板看各自状态行与
   `wb:crafts:<uid>` 计数——同面板多机器、按 uid 隔离的直观验证。
4. 进阶：`on_open` 里用 `ui.machine_set_text(m, "lbl_title", "…", "#FF9A3C")`
   给标题上色（v1.38.0 支持可选 `color` 参数）。

## 下一个示例

本包是 PSUI 教学系列（ex25 → ex29）的收官。下一步推荐：

- 文档 [06-psui/09-practice.md](../../api_docs/06-psui/09-practice.md) —— 实战：三张面板带读
- 官方重型应用 `UserData/PSApi/packs/gunworks/`（组装台/打印机/分析仪三张机器面板的完整工业级实现）
