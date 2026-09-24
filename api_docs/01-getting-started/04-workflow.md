# 04 · 开发工作流与排障

> 阅读时间约 8 分钟。这是日常开发中你会反复回来翻的一篇：改完怎么验证、日志去哪看、
> 报错怎么读、以及从"文件夹包"到"可分发 DLL"的最后一公里。

## 核心循环

PS-API 当前没有热重载（源码中 `Rescan` 仅在启动时调用），所以日常节奏是：

```text
   改 packs/<你的包>/ 下的文件
            │
            ▼
      重启游戏（Steam 直接重启即可）
            │
            ▼
   看三处日志（见下）确认加载干净
            │
            ▼
   进存档 → F12 发放 / 游戏内行为验证
            │
      ┌─────┴─────┐
   有错误        一切正常
      │             │
      ▼             ▼
  读 pack_errors   继续改下一个点
  修复后重启
```

> 小技巧：开发期保持存档常开（游戏支持快速重启进档），把"重启 + F12"当成你的
> "编译 + 运行"。

## 三处日志，各看什么

| 位置 | 看什么 |
|---|---|
| **MelonLoader 控制台**（启动时的黑窗） | 实时全部输出，`[psapi]` 前缀 = PS-API 框架，`[pss <包id>]` 前缀 = 你的脚本 `log.*` 输出 |
| `MelonLoader/Logs/<日期_时间>.log` | 同上内容的落盘版，控制台一闪而过时来这里搜 |
| `UserData/PSApi/logs/pack_errors_<时间戳>.log` | **内容包解析错误汇总**（JSON 语法错、字段引用错、脚本编译错），有错才生成 |

### 关键日志行速查

以下日志行均来自 PS-API 源码，含义固定：

| 日志行（节选） | 含义 |
|---|---|
| `PSApi.Items v… loaded. packs=N items=N qualities=N` | 数据面宿主就绪，包/物品/品质计数 |
| `rescan(init): N pack(s), N item def(s), … errors=0` | 数据面解析完成，`errors` 必须为 0 |
| `PSApi.Events … loaded. packs=N` | 逻辑面宿主就绪 |
| `rescan(init): N pack(s), N pss file(s), N handler(s), N compile error(s)` | 脚本编译完成，`compile error(s)` 必须为 0 |
| `event bus selftest hits=12 (expect 12)` | 事件总线自检通过 |
| `item directory ready via …; N item def(s) registered` | 进对局后物品真正注册（F12 此后才可用） |
| `[pss <包id>] …` | 你的脚本 `log.info/warn` 输出 |
| `pack conflict [duplicate-id]` | 同 id 文件夹包 + DLL 并存，DLL 胜出 |
| `pack '<目录>': missing pack.json, skipped` | 缺清单，整包跳过 |
| `pack '<目录>': pack.json missing required 'id'` | 清单缺 id，整包跳过 |
| `pack '<目录>': duplicate id '<id>', skipped` | 文件夹包 id 撞车，目录名排序先者胜 |
| `[<包id>] <文件> parse failed: …` | JSON 解析失败，详情跟在冒号后 |
| `[pss] compile error: <包id>/events/<文件>.pss:<行>:<列>: <消息>` | PSScript 编译错误（词法/语法），该文件整体不加载，其余文件不受影响 |
| `N pack error(s), details: <日志路径>` | 错误已汇总落盘，去该文件逐条修 |

### 建议的启动检查顺序

1. 搜 `rescan`——两个都 `errors=0` / `0 compile error(s)` 才继续。
2. 搜 `pack conflict` / `skipped`——确认没有包被静默丢弃。
3. 搜 `[pss <你的包id>]`——确认脚本顶层日志出现（脚本能跑的最快证据）。

## 调试工具箱

| 工具 | 用法 | 说明 |
|---|---|---|
| **F12 发放** | 进存档后按 | 全部自定义物品进后仓，验证物品定义最快路径 |
| **F6 管理面板** | 任意时刻按 | 打开 `psapi_manager` 包提供的面板（可改 `UserData/MelonPreferences.cfg` 换键） |
| **脚本日志** | `log.info("x={x}")` | 见 [04 PSScript](../04-psscript/README.md)；字符串插值直接把变量打进去 |
| **状态落盘** | 脚本 `state` API | 每存档槽独立，见 [07 进阶 · 存档状态](../07-advanced/README.md) |
| **自检脚本** | 顶层 `log.info` | 在包里放一个 `smoke.pss`，顶层打一行日志——启动即知你的包被加载 |

## 常见故障排查

**改了 JSON/PSS 没生效**
→ 必须重启游戏（无热重载）。确认改的是 `packs/<id>/` 下被加载的那份（同 id 并存时 DLL 胜）。

**JSON 明明是对的却报 parse failed**
→ 检查编码（UTF-8）与真正的语法错误。PS-API 允许 `//` 注释和尾逗号，但**不允许**
多出的引号/括号；错误信息会带系统解析器原因。

**引用别的包的物品不生效 / 找不到**
→ 跨包引用写全 id（`对方包id:物品名`）；引用原版用 `game:裸id`。前置包要在
`pack.json` 的 `prerequisites` 里声明，否则你的包可能先于它加载。

**脚本编译错误定位**
→ 打开 `pack_errors_*.log`，编译错误形如
`<包id>/events/<文件>.pss:<行>:<列>: <消息> (该文件未加载)`，按行:列修。
顶层运行期错误则形如 `[pss] <包id>/events/<文件>.pss:<行> in 顶层: <消息>`，
且**只中断当前文件**，不影响其他 handler。语言细节见 [04 PSScript](../04-psscript/README.md)。

**机器/配方行为怪异**
→ 先确认 `machines/` 与 `recipes/` 的 id 对应关系（机器 `id` 引用 `items/` 里的物品 id），
再检查槽位号（`inputSlot`/`outputSlot` 指原版窗口槽位，详见[03 数据面](../03-items/README.md)）。

**想确认包加载顺序**
→ 文件夹包按**目录名字典序**加载；同 id 先加载者胜。注意：`pack.json` 的
`prerequisites` 只做"缺前置则整包跳过"的检查，**不改变加载顺序**；`loadAfter`
字段目前仅为保留字段，加载器尚未消费（源码核实：仅 DTO 声明，无执行逻辑）。
详见[02 内容包系统](../02-pack/README.md)。

## 开发期 vs 分发期

```text
开发期（改文件即所得）
  packs/<你的包>/    ← 直接改 JSON / .pss / .psui，重启生效
  适合：自己调试、快速迭代

分发期（编译为 DLL）
  _tools/pack_compiler 编译 packs/<你的包>/ → PSPack.<id>.dll
  把 DLL 放进 Mods/（优先级 5，先于 PSApi 宿主自动注册）
  适合：发布给玩家——防误改、版本固定、玩家不需要懂 packs/ 目录
```

编译与分发细节（`--verify` 自检、内嵌资源规则、冲突表现）见
[02 内容包系统 · 分发](../02-pack/README.md)。

## 给 AI 辅助开发者的建议

- 让 AI 生成内容时，**把本篇字段参考的链接喂给它**（03/05/06 章节都有逐字段表），
  可大幅减少编造字段。
- 一次只让它改一个面（先 JSON 后脚本），用启动日志做回归验证。
- `pack_errors_*.log` 的原始报错直接贴回给 AI，定位效率最高。

---

入门篇到此结束。接下来：
- [02 内容包系统](../02-pack/README.md)——pack.json 全字段与加载规则
- [03 数据面 JSON](../03-items/README.md)——物品/机器/配方/品质逐字段
