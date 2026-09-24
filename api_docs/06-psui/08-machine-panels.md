# 06 · 08 机器绑定面板

> 前面所有面板都要脚本 `ui.open` 打开。还有一种更"原生"的形态：
> **机器绑定面板**——面板挂在机器物品上，玩家**双击机器**开窗（与原版机器同交互），
> 槽内物品**随机器存档**（隔夜不丢），关窗不退回。
> 这是 gunworks 组装台/打印机/分析仪的形态，也是 PSUI 的"重型应用"篇。

## 声明：machines/*.json 加两行

在包的 `machines/*.json` 里把机器的 `ui` 设为 `"psui"`，`panel` 指向面板名
（`gunworks/ui/gun_bench.psui` 的真实声明）：

```json
{
  "machines": [
    {
      "id": "gunworks:gw_bench",
      "ui": "psui",
      "panel": "gun_bench"
    }
  ]
}
```

- `panel` 写**短名**（省本包前缀，自动补 `gunworks:`），也可写 `"pack:panel"` 全限定
  引用他包面板。
- 机器物品本体在 `items/*.json` 声明（见 [03 · 03 机器](../03-items/03-machines.md)）；
  `ui: "psui"` 之外还有 `"furnace"`（桥接原版工厂 UI）等路线，`psui` = 完全自定义。
- 面板装配发生在物品创建时：面板文件缺失/装配失败 → 告警并回退为普通物品。

## 交互模型：双击 + on_open

- **开/关窗由原生双击管理**——脚本不 `ui.open`，玩家双击机器物品开，
  再双击或 X/Esc 关。`ui.machine_is_open(machine)` 查当前开没开。
- **每次开窗触发 `on_open`**（window 属性）：窗口从关到开的跳变沿触发，
  参数 `{machine}`。它是机器面板的"刷新时机"——读槽、改文本、更新白名单都在这里做。

```text
window gun_bench:
  title: "枪械组装台"
  persistent: true            # 机器面板天然不退物品, 写上明确意图
  on_open: bench_on_open
  spacing: 3
  # ...
```

```pss
func bench_on_open(ev):
    var m = ev.machine                # 机器物品句柄
    bench_apply(m)                    # 按当前存档状态刷新槽名/白名单/锁/按钮文本
```

## 回调签名：全部带 machine

机器面板的回调比独立面板多一个 `machine` 字段——同一面板定义可能装在
**多台机器**上（玩家买了三台组装台），回调必须知道"是哪台的按钮"：

| 回调 | 参数 |
|---|---|
| 按钮 `on_click` | `{elem, machine}` |
| 槽位 `on_change` | `{elem, value, machine}` |
| window `on_open` | `{machine}` |

```pss
# 组装台左列武器按钮共用一个函数: 靠 elem 区分按钮, machine 区分机器
func bench_pick(ev):
    var weapon = ev.elem                 # "btn_w_pistol" / "btn_w_smg" / ...
    var m = ev.machine
    g_sel[m.uid] = weapon                # 每台机器各自的选中状态 (state/uid 存档)
    bench_apply(m)                       # 重设槽标签/白名单/按钮文本
```

## machine_* API 族

槽位/元素操作都要**显式传机器句柄**（与独立面板的 `ui.get_slot_item` 平行）：

| 函数 | 签名 | 说明 |
|---|---|---|
| 改文本 | `ui.machine_set_text(m, elem_id, text)` | 机器面板的 label/button |
| 读单件 | `ui.machine_slot_item(m, slot_id) → 句柄\|null` | 槽空 = null |
| 读全部 | `ui.machine_slot_items(m, slot_id) → [句柄]` | grid_slot 多物品 |
| 重设白名单 | `ui.machine_set_whitelist(m, slot_id, [id...])` | **覆盖式**；不能为空（清白名单没有意义，禁槽用锁） |
| 直入产出 | `ui.machine_spawn(m, slot_id, item_id[, count=1]) → 句柄\|null` | **不过白名单**（输出槽预览/成品专用） |
| 双锁 | `ui.machine_lock(m, slot_id, locked)` | 插入锁 + 取出锁一起上/解 |
| 清槽销毁 | `ui.machine_clear(m, slot_id)` | 槽内物品**全部销毁**（玩家物品请让玩家自取，别用这个） |
| 开着吗 | `ui.machine_is_open(m) → bool` | |
| 找机器 | `ui.machine_find_uid(uid) → 句柄\|null` | 按存档稳定 uniqueId 找已登记的 psui 机器 |

**输出槽的标准套路**（gunworks 三面板共用）：

```text
slot sout: whitelist: ["gunworks:__reserved_output"], size: 4x2, on_change: bench_output_changed
```

whitelist 填一个**不存在的占位 id**——排他注册后任何手动放入都被拒；
成品由脚本 `ui.machine_spawn` 直入（不过白名单）；成品未取走时
`ui.machine_lock(m, "sout", true)` 双锁防重复产出，玩家取走后解锁。

**隔夜找机器**：句柄不能进 state（存档恢复后失效），存 `uniqueId`——
隔夜事件里 `ui.machine_find_uid(uid)` 恢复机器引用再操作。

## 结构恒定铁律（最重要的一条）

> **面板元素树不得增删**——存档按图节点 BFS 索引记录槽内物品位置，
> 结构变了读档会串槽（部件跑到别的槽去）。

gunworks 在每个面板头部都写着这条，并用血泪教训注释：

```text
# v0.8.0: 网格 (1,2) 空单元改零件槽 s5 —— 面板元素树变了 →
#   旧存档组装台槽位会按 BFS 索引串槽 (都是测试物品, 直接清空重来即可)。
#
# 结构恒定铁律: 本面板的元素树永远不变 (5 部件槽 + 1 零件槽 + 1 输出槽 + 全部 spacer/按钮) ——
# 切武器只改槽名/白名单/锁/按钮文本 (脚本 bench_apply), 不增删元素。
```

**动态布局的正确姿势**：元素树固定，变化全部通过运行时改写：

| 想要的效果 | 做法 |
|---|---|
| 切换模式显示不同内容 | 槽标签/按钮文本 `machine_set_text` 换字 |
| 不同模式收不同物品 | `machine_set_whitelist` 覆盖式重设 |
| 阶段性禁止操作 | `machine_lock` 双锁 |
| 预览/成品 | `machine_spawn` + `machine_clear` |

"常驻多按钮按需启用"（打印机的 btn_p0/p1/p2：非当前模式显示"—"）也比
"动态增删按钮"安全——结构恒定优先于视觉极简。

发版前自查：对比新旧 `.psui` 的元素树（含 spacer！）逐一对应；
**任何**增删都意味着旧存档槽位串位，版本注释里必须写迁移方案
（gunworks 的方案是"取回物品、卖掉重买"）。

## 生命周期小结

```text
物品创建/读档
  │  AttachMachinePanel: 装配图元树 → 挂为机器 contentWindow (不显示)
  ▼
玩家双击机器 ────→ 原生开窗 ──→ on_open {machine} 刷新
  │                                  │  运行期: 按钮回调 {elem, machine}
  │                                  │          槽位差分 {elem, value, machine}
  │                                  │          轮询: 机器失效自动清理登记
  ▼                                  ▼
再次双击/X/Esc ──→ 原生关窗 (槽内容留在机器里, 随存档序列化)
```

机器被卖/毁/场景卸载 → 轮询探测失效（读 identifier），面板登记自动清理，
不泄漏不报错。

---

下一篇：[09 · 实战：三张面板](09-practice.md)
