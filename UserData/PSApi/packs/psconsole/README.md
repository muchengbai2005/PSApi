# PS 指令台（/ 键控制台）· 完整教程

> **v0.2.0 起新增 20 条扩展指令**（crime / raid / gw / power / restock / showcase / attract / goto / bag clear / npc rep），完整用法见仓库文档 `_docs/开发向/psconsole指令手册.md`；本文的通用规则与基础指令依然适用。

仿 Minecraft 指令系统的调试控制台。游戏内按 `/`（Slash 键）开关窗口：上方是大日志区，底部输入框，**回车**或点「执行」按钮提交。指令的前导 `/` 可加可不加（`/give ...` 与 `give ...` 完全等价）。

> 截图位留白：<!-- 此处放窗口截图 -->

---

## 一、通用规则（所有指令通用）

1. **空格分词**：参数之间用空格隔开。`k=v` 键值对的**值里不能再含 `=`，也不能含空格**（想写带空格的描述暂时做不到，用 `_` 或连写代替）。
2. **大小写敏感**：指令名全小写；物品 id、NPC id 照抄注册表大小写。
3. **数字**：支持负数（`cash add -500` 合法）。
4. **对局限制**：大部分指令只在对局（开店存档）内可用；主菜单里执行会提示「不在对局中」。
5. **日志**：保留最近 60 行，每条指令先回显一行 `> 你输入的原文`，再输出结果。
6. **错误不炸游戏**：参数写错只会收到一条【错误】/【用法】提示，放心试。

---

## 二、指令详解

### 1. `help` — 帮助

```
help
```
无参数，列出全部指令的速查用法。忘了语法就先打它。

---

### 2. `give` — 发放物品（最常用）

```
give <物品id> [数量] [k=v ...] [{k=v,...}]
```

| 参数 | 说明 |
| --- | --- |
| `<物品id>` | 必填。原版 id（如 `common_ore`）或模组 id（带包前缀，如 `ex05_item_basic:quality_ingot`） |
| `[数量]` | 可选，默认 1，范围 1~9999 |
| 参数区 | 可选，任意多个。**两种写法可混用**：旧式空格分隔 `k=v`；或 v0.1.1 起的花括号块 `{k=v,k=v,...}` |

**花括号块语法（v0.1.1 新增，推荐）：**

- 整块写在 `{...}` 里，条目用逗号分隔（**中文逗号 `，` 也行**）。
- **引号字符串可含空格**：`name="镇 店 之 宝"`。
- **值可嵌套 `{}` 字典，任意深度**：`tempdata={temp1=1,tempcr={r="s1221"}}`。
- 值的类型自动识别：`"引号串"`=字符串 / 整数 / 小数 / `true`/`false`/`null` / `{...}`=字典 / 其余裸词按字符串。

**内置键（进物品本体属性，只收标量，不能给字典）：**

| 键 | 作用 | 示例 |
| --- | --- | --- |
| `to=` | 生成位置：`inventory`（默认·背包）/ `counter_out`（门口外部柜台）/ `counter_in`（店内柜台） | `to=counter_in` |
| `name=` | 实例自定义命名 | `name=镇店之宝` |
| `desc=` | 自定义短描述 | `desc=枪身刻着编号` |
| `flavor=` | 自定义风味文本 | `flavor=来路不明` |
| `value=` | 单价（数字，覆盖折算价） | `value=888` |
| `uses=` | 使用次数（次数物品，归零销毁） | `uses=9999` |
| `quality=` | 品质 id（须为已注册品质） | `quality=fine` |

**其余任意键自动进自定义数据（NBT）**：数字转数值、`true/false` 转布尔、引号串/裸词按字符串，**嵌套 `{}` 字典也支持**（Events ≥v1.16.2 / Items ≥v0.9.8，走 `j:` JSON 编码持久化，随存档保存）。脚本里用 `item.get_data("键")` 读回，嵌套字典读回是 pss dict，可继续 `.` 取内层。

**示例：**

```
give common_ore 5
→ 已发放 common_ore ×5 → 背包

give ex05_item_basic:quality_ingot 1 {to=counter_in,name="镇店之宝",value=888}
→ 店内柜台上出现一块叫「镇店之宝」、单价 888 的精铸锭

give ex07_item_uses:toolbox 1 uses=9999 tool_scope=rifle
→ 背包里一个 9999 次、带自定义字段 tool_scope="rifle" 的多功能工具箱

give ex05_item_basic:quality_ingot 1 {name="镇 店 之 宝",value=888,tempdata={temp1=1,temp2="tempname",tempcr={r="s1221",g="2sw",b="12sw"}}}
→ 带三层嵌套自定义数据的精铸锭; 回显里会把 data 完整拼出来
```

**注意**：`quality=` 的品质 id 必须已在某个包的品质表里注册，否则发放失败并提示。

---

### 3. `cash` — 现金

```
cash                # 查看当前现金
cash add 500        # 加 500（负数=扣钱）
cash set 9999       # 把现金设定为 9999（自动补差额）
```

输出始终回显最新现金：`现金: 10531`。

---

### 4. `rep` — 派系声望

```
rep                     # 查看全部六个派系
rep ll                  # 查看单个派系
rep ll add 20           # 加 20（负数=扣）
```

派系 id 六选一：`sec`（治安部）/ `rev`（革命军）/ `bm`（黑市）/ `cartel`（卡特尔）/ `ul`（上层）/ `ll`（下层）。
（v0.1.1 起与框架合法值对齐；旧的 `lower_unrest`/`upper_friend` 是错误拼写，会被白名单拒绝。）

---

### 5. `day` — 时间查询

```
day
→ 第 12 天 (rel_day=12, weekday=4)
```

`day` 是绝对天数，`rel_day` 是开业起第几天（开业日=1），`weekday` 0~6（周五=4，引导员日）。

---

### 6. `npc` — NPC 管理

```
npc list
```
列出全部已注册自定义 NPC：`id [意图] 名字 (schedule: 调度方式)`。意图：`sell`（卖货）/ `buy`（收货）/ `sellnbuy` / `dialogue`（纯剧情）。

```
npc queue
```
今日待客队列：`序号. 名字 [意图] ¥预算`。开店前看一眼今天谁来。

```
npc spawn cell_gunsmith
```
挂起强制生成：下次客户生成时该 NPC 必到店（id 用 `npc list` 查）。回显「已挂起生成」。

```
npc leave
```
请离当前店内客户（走原版 `CurrentClientLeave` 路径，1 秒延迟离场，与「说完就走」同款）。有客户回显「已请离： <名字>」；店内无客户回显「（当前店内无客户可请离）」，不会报错。

---

### 7. `event` — 立即触发商店事件

```
event <事件id>
```

立即激活一个商店事件（不等自然刷新）。id 可以是**内容包经 `store_event.register` 注册的**，也可以是**原版事件池里的**（normal/threat/cosmetic 三池按 id 查找）。

```
event daily_special
→ 已触发商店事件: daily_special
```

id 两头都找不到时回显「【错误】事件 'xxx' 未注册且不在原版事件池」。原版事件 id 清单框架目前不提供枚举，需从游戏数据/反编译查。

---

### 8. `item` — 物品查询

```
item ex05_item_basic:quality_ingot
→ ex05_item_basic:quality_ingot: 精铸锭 · 单价 60 · 持有 是
```

名称 / 基础单价 / 背包是否持有。查未知 id 显示「(未知物品)」。

---

### 9. `state` — 本包存档 KV（调试用）

```
state get 键名            # 读（未设置显示 "(未设置)"）
state set 键名 值         # 写；数字/true/false 自动转型，其余按字符串
```

读写的是 **psconsole 包自己的 state**（随存档持久化，v1.14.2 起与游戏存档事务同步）。给其他包调试 state 请直接改那个包的脚本。

---

### 10. `pack` — 内容包清单（v0.1.2）

```
pack list             # 列出全部已加载内容包: id 版本 [folder|dll] 名称
```

`[dll]` 标记的是编译进 DLL 的内嵌包（与文件夹包同 id 时 DLL 优先）。调试"我的包到底加载没/加载的是哪份"用这条。

---

### 11. `save` — 立即存档（v0.1.2）

```
save                  # 立即触发原版存档 (等同晚上睡觉的存档管线)
```

走的是 `PlayerStore.SaveGame()` 本体，模组状态（state/NBT/NPC 剧情标志）随 v1.14.2 钩子同步落盘，不会出现"游戏存了模组没存"的错位。不在对局时报错不执行。

---

### 12. `echo` / `clear` — 日志工具

```
echo 随便写点啥        # 回显一行文本（测试日志区用）
clear                   # 清空日志区
```

---

## 三、指令缺口分析（候选清单）

> 2026-09-20 拍板落地（Events v1.16.1）：**`npc leave`**（请离当前客户）与 **`event <id>`**（立即触发商店事件）已补做并从本清单移除，见 §二.6 / §二.7。

按"测试当前模组内容"的实用度排序：

**第一梯队（框架 API 基本现成）—— 用户拍板不做，留档备查：**

1. **`save`** — 立即触发游戏存档（调 `PlayerStore.SaveGame`）。v1.14.2 存档同步后，这条是回档测试的关键搭档：白天 `save` → 退菜单 → 重进验证模组状态一致性。
2. **`sleep`** — 立即结束当天/入睡。测隔夜机器（打印机/分析仪/组装台）、证书衰减、NPC 调度翻日，不用真等。
3. **`item edit <uid> k=v ...`** — 编辑**已有**物品的 NBT（名字/价格/次数/自定义字段）。句柄的 `set_name/set_value/set_uses/set_data` 都已就绪，只差指令壳。uid 可配合新子命令 `item list`（列背包物品 uid）使用。
4. **`power`** — 电量查询/修改（`power.get/add` 已存在）。测打印机/分析仪耗电优先级必备。
5. **`machine <id>`** — 机器进度/在产查询（`machine.find/progress/producing` 已存在）。

**第二梯队（需要少量框架工作）—— 剩余候选保留：**

6. **`give ... to=showcase`** — 生成到商品展示区。证书体系测试刚需（现在得手动拖）。需调研展示区放置 API。
7. **`crime list`** — 查询治安档案当前记录（现在有 `crime.commit` 只能写，读要调研 SecData 查询面）。

**锦上添花：**

8. `pack list` — 已加载内容包清单（框架加注册表快照 API）。
9. `reload` — 不重启游戏重载包脚本（Rescan 已有内部方法，热重载风险需评估）。
10. `npc say <id> <文本>` — 让指定 NPC 现场说话（测对话排版）。

## 说明

- 日志保留最近 60 行，随指令执行自动滚动重绘。
- 指令大小写敏感（全小写）。
- 依赖：PSApi.Events ≥ v1.16.2（`ui.bind_key` / input `on_submit` / `npc.list` / `npc.leave_current` / `store_event.trigger` / `items.give` 的 `to` 落点 / 嵌套 data 透传），PSApi.Items ≥ v0.9.8（`counter_in` 落点=店内称重台 `AddDirectToWeightedTable` / `j:` 嵌套数据编码）。
- v0.1.1 起 `to=counter_in` 落点改为**店内称重台**（原版网购送货路径，玩家可见可取）；v0.1.0 落的 `TableMiddle` 是 NPC 自带货容器，客户不在场时不可见。
