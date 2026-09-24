# example_hello — PS-API 示例包

> PSApi 自测/示范用内容包, 覆盖当前全部已落地能力: 原版机器复用、自定义窗口机器、
> 多夜进度配方、品质层、包内图标。新包从复制本包起步 (见上级 README)。

## 包内子目录约定

| 子目录 | 内容 | 消费方 |
|---|---|---|
| `pack.json` | 包清单: id/name/version/authors/gameVersions | 两模块加载器 |
| `items/` | 物品定义 (模板克隆/形状/图标/品质) | PSApi.Items |
| `machines/` | 机器声明; `"ui"` 字段: `furnace`/`desequencer` = 原版复用, `custom` = 自定义窗口 | PSApi.Items |
| `recipes/` | 配方 (inputs/outputs; 机器级 `progress` 块 = 多夜加工) | PSApi.Items |
| `qualities/` | 品质层定义 (tag 模式 + feature 模式) | PSApi.Items |
| `icons/` | 包内图标 png, 物品 JSON 按文件名引用 | PSApi.Items |
| `events/` | PSScript 脚本 (.pss, 逻辑面), E1 语言内核已生效 | PSApi.Events |

## 当前内容清单

**物品 (`items/`)**

| 文件 | 物品 | 说明 | 游戏内测试 |
|---|---|---|---|
| `example_smelter.json` | `example_hello:example_smelter` 示例熔炉 | furnace 模板, 配 machines/ 桥接原版熔炉工厂 | 发放后放置, 双击开原版熔炉窗 |
| `example_desequencer.json` | `example_hello:example_desequencer` 示例读卡器 | desequencer 模板, 放宽槽位过滤器可放非卡片物品 | 双击开读卡器窗, 槽 3 可放入废金属/矿石 |
| `example_processor.json` | `example_hello:example_processor` 示例加工机 | ui=custom 验证机, 自装配窗口 (v0.6.0), 复用 `mcb_machine_spp.png` 图标 | 双击开自装配 2 列窗口 (输入/产出), 原生拖拽生效 |
| `mcb_machine_spp.json` | `example_hello:mcb_machine_spp` 模组打印机 | 仅物品 (4x3 形状+图标), 机器功能规划走路线 B | 发放后看图标与背包占位形状 |
| `mcb_weapon_gun1.json` | `example_hello:mcb_weapon_gun1` 解放轻步枪 H1 | 6x3 异形物品 + 图标 | 发放后看异形占位与图标 |
| `_placeholder.json` | `example_hello:example_water` 示例净水瓶 | 带品质层的物品示例 | tooltip 品质行 / 名称随品质变化 |

**机器 (`machines/`)**

| 文件 | ui | 说明 | 游戏内测试 |
|---|---|---|---|
| `example_smelter.json` | `furnace` (原版熔炉复用) | 桥接原版工厂, 槽 2 输入/槽 3 输出, 电池供电 | 放电池+废金属, 隔夜看产出 |
| `example_desequencer.json` | `desequencer` (原版读卡器复用) | 双输入槽 2/3, 输出槽 4, 机器级 progress 34/100 = 3 夜 | 放材料后连睡 3 夜看产出与进度 |
| `example_processor.json` | `custom` (自定义窗口) | 自装配 PixelWindow, inputKind=grid + inputSize 3x4/outputSize 2x2, 免电, 白名单排他过滤 | 双击开自定义窗, 拖 2 废金属进输入网格, 1 夜产报纸 |

**配方 (`recipes/`)**

| 文件 | 配方数 | 说明 | 游戏内测试 |
|---|---|---|---|
| `smelter_tests.json` | 1 | 基线回归: 2 废金属 → 1 报纸 | 熔炉槽 2 放 2 废金属, 隔夜槽 3 出报纸 |
| `example_processor_recipes.json` | 1 | 自装配机基线: 2 废金属 → 1 报纸 (无 progress 块 = 1 夜) | 加工机输入区放 2 废金属, 睡 1 夜产出 |
| `example_desequencer_recipes.json` | 7 | 钥匙卡链 5 条 (空白→服务→补给循环) + 多格材料 2 条, 均带 3 夜进度 | 槽 3 放卡/材料连睡 3 夜; 双材料配方用槽 2+3 |

**品质 (`qualities/example_qualities.json`)**: 5 层 (tag 模式 2 层 + feature 模式 3 层,
含自建 category "星辉品级")。测试: 标签打印机下拉出现自定义层级, 改标后名称/价格随
priceMul 变化。

**图标 (`icons/`)**: `mcb_machine_spp.png` / `mcb_weapon_gun1.png`, 被对应物品引用;
example_processor 也复用前者。测试: 物品在背包/商店显示自定义图标。

**事件 (`events/hello.pss`)**: PSScript E1 冒烟示例, 已生效 — 顶层 log.info 演示控制流/数组/字典/插值/标准库,
`on scene_loaded():` handler 打印场景名 (设计见 `_api_design/events/05-PSScript脚本语言.md`)。
测试: 启动日志可见 `[pss example_hello]` 输出, 进场景触发 scene_loaded 行。

**U4 槽位面板 (`ui/u4_printer.psui` + `events/u4_demo.pss`, Events v1.3.0)**: 图元槽位混合窗 =
模组打印机原型。面板含 `grid_slot` → 整面板走图元树后端 (PixelWindow 独立开窗, 不经 CustomUIManager)。
每天开店自动弹出; 拖 2 个废金属进网格 (whitelist 排他过滤, 别的物品放不进), 点"打一份报纸"消耗
2 废金属并发 1 报纸进背包; 槽内容变化 on_change 实时刷新状态行; 关窗槽内物品自动退回后仓。
