# ex28_psui_slots · 示例 28：物品槽位面板（图元树后端）

> **演示知识点**（对应文档 [06-psui/07-slots.md](../../api_docs/06-psui/07-slots.md) ·
> [03-elements.md](../../api_docs/06-psui/03-elements.md) ·
> [05-callbacks.md](../../api_docs/06-psui/05-callbacks.md)）：
> `slot` / `grid_slot` 把整面板切到**图元树后端**（与原生机器界面同源，无"混合后端"）·
> 槽位三件套 `whitelist`（排他白名单）/ `size`（格数非像素）/ `on_change`（轮询差分感知）·
> `strict_footprint`（slot 尺寸校验开关）· `lock_interactions`（锁槽内物品交互）·
> `grid_slot shape` 洞（行优先 01 串）· 图元后端专属控件 `rich_label` / `spacer` ·
> `ui.get_slot_item` / `ui.get_slot_items` 读槽 · 关窗自动退物品（未写 `persistent`）。

## 文件清单

```text
ex28_psui_slots/
├── pack.json              ← 包清单
├── items/
│   └── demo_parts.json    ← 2 个 2x2 测试物品 (test:true → F12 发放)
├── ui/
│   └── depot.psui         ← 1 张槽位面板 (图元树后端)
└── events/
    └── depot.pss          ← 3 个槽位回调 + 读取按钮 + F10 绑定
```

## 安装

把整个 `ex28_psui_slots` 文件夹复制到游戏目录：

```text
<游戏目录>/UserData/PSApi/packs/ex28_psui_slots/
```

## 验证（5 分钟）

1. 启动游戏，控制台应出现：

```text
[PSApi] [psui] bind_key: F10 → ex28_psui_slots:depot
[PSApi] [pss ex28_psui_slots] [ex28_psui_slots] 槽位面板包就绪: F12 发放演示物品 → F10 开/关 depot 面板
```

2. 进任意存档，按 **F12**（调试发放键，一次性发放**全部** PSApi 注册物品）→
   后仓里即有两个测试物品：**演示齿轮**、**演示齿轮核心**（都是 2x2 小件）。
   也可以按 **F6** 开管理面板，在物品浏览器的"测试"分类里单独拿取。
3. 按 **F10** 弹出「槽位仓库」窗口，做四组实验：
   - **白名单**：把「演示齿轮核心」拖向 **s1** → 放不进（s1 白名单只收齿轮）；
     换「演示齿轮」→ 进槽，日志 `[槽位 on_change] s1 (齿轮槽, 严格footprint) → 演示齿轮 x1 [ex28_psui_slots:demo_gear]`。
   - **锁交互**：把「演示齿轮核心」放进 **s2** → 能放能取，但**双击**槽里的核心
     没有任何原生开启反应——`lock_interactions: true` 吞掉了双击/右键开启路径。
   - **shape 洞**：把任意演示物品拖向 **g1** 左上第 2 格 → 压不上去
     （`shape: "010000000000"` 把它挖成了永久洞），挪到别处可正常摆放。
   - **脚本读槽**：点 **读取全部槽位** →

```text
[PSApi] [pss ex28_psui_slots] [ex28_psui_slots] [按钮 on_click] 读取全部槽位:
[PSApi] [pss ex28_psui_slots] [ex28_psui_slots]   s1 → 演示齿轮 x1 [ex28_psui_slots:demo_gear]
[PSApi] [pss ex28_psui_slots] [ex28_psui_slots]   s2 → 演示齿轮核心 x1 [ex28_psui_slots:demo_core]
[PSApi] [pss ex28_psui_slots] [ex28_psui_slots]   g1 → 演示齿轮 x1 [ex28_psui_slots:demo_gear]
```

4. 关窗（X / Esc / F10）→ 槽内物品**自动退回后仓**：本面板没写 `persistent`，
   "关窗不掉东西"是图元面板的默认保护；再开面板时槽是空的。

## 逐文件讲解

### ui/depot.psui —— 一颗槽位换一整套底层

- **后端分派**：面板里出现任意 `slot`/`grid_slot` → 整面板走图元树后端。
  因此本面板**没有** progress/toggle/slider/input/dropdown/scroll——它们在图元
  后端会被警告跳过（ex25 里它们齐全，因为那是 CustomUI 后端）。
- **`slot` vs `grid_slot`**：一件东西不管多大 → `slot`（默认**不验**物品 footprint，
  2x2 底格也收得下 10x3 大枪；写了 `strict_footprint: true` 才校验外接矩形 ≤ 槽格数）；
  多格自由摆、要算格子 → `grid_slot`（严格几何判定：越界/压洞/压占格一律拒）。
- **`shape: "010000000000"`**：行优先逐格状态串，长度必须恰为 `size` 的宽×高
  （4x3 = 12 字符）。`0`=可放、`1`=永久洞（不渲染）、`2`=锁定暗格。
  **必须带引号**——裸写会被解析成数字丢掉前导 0。
- **`rich_label` / `spacer`**：图元后端专属控件（前者带 `color` 的标签，后者按像素
  预留空隙），在 CustomUI 面板里写会被警告跳过——ex25 把它们留给本篇演示。
- 图元后端的 **button 不支持 tooltip**（解析器认识但后端忽略），所以本面板按钮都没写。

### events/depot.pss —— 槽位感知工作流

- **on_change 是轮询差分**，不是原生事件：拖入、取出、脚本消耗都触发。
  `items.consume` 消耗槽内物品后 on_change 会**再触发一次**——所以回调里
  **重新读槽**（`ui.get_slot_item`/`get_slot_items`）而不是信 `ev.value`，
  重读天然幂等（u4_printer 的惯例）。
- **读法分工**：`slot` 用 `ui.get_slot_item`（首件，空槽 = null）；
  `grid_slot` 永远用 `ui.get_slot_items`（句柄列表，空 = 空表）。
- **物品句柄的 `.id`**：包物品保留全名 `ex28_psui_slots:demo_gear`；
  原版物品剥掉 `game:` 前缀变裸 id（如 `scrap_metal`）——脚本比较时两种写法都见得到。
- **关窗退物品**：未写 `persistent` 的图元面板关窗时槽内物品自动退回玩家后仓
  （`ItemsFacade.ReturnToPlayer`），"面板即容器"才需要 `persistent: true` 自管。

### items/demo_parts.json —— 槽里的"货物"

- `template: "scrap_metal"` 克隆原版废金属的目录/形状基线，`shape` 显式 2x2
  覆盖克隆结果——小件方便塞进演示格子。
- `test: true` → 归入物品浏览器的"测试"分类（F12 发放），教学包的标准做法。

## 动手练习

1. 把 g1 的 shape 改成 `"020000000000"`（洞 → 锁定暗格 `2`）——第 2 格从"消失"
   变成"暗色可见"（v1.41.0 的"以后解锁"表达法）。
2. 删掉 s1 的 `strict_footprint: true`，再把 s1 的 `size` 改成 `1x1` → 底格视觉
   变小，但 2x2 的齿轮**照样放得进**——slot 默认不验 footprint 的直接证据。
3. 删掉 s2 的 `lock_interactions: true` → 双击槽里的核心会恢复原生反应
   （对比体会锁交互拦的是什么）。
4. 进阶：给 window 加 `persistent: true` → 关窗物品不再退回；此时物品的生命周期
   归你管——试着用 `state` 记录"寄存了什么"，重开后恢复文案。

## 下一个示例

- [ex29_psui_machine](../ex29_psui_machine/README.md) —— 机器绑定面板：物品 + machines 声明 + 双击开窗
