# 06 · 04 布局

> PSUI 没有绝对定位、没有锚点——所有排版靠**容器嵌套**：row 横排、column 竖排、
> grid 网格、scroll 滚动。思维模型是"盒子套盒子"，接近 CSS flexbox 的极简版。

## 四种容器

| 容器 | 方向 | 属性 | 子元素行为 |
|---|---|---|---|
| `row` | 横向 | `spacing`（默认 4） | 从左到右排一行 |
| `column` | 纵向 | `spacing`（默认 4） | 从上到下排一列 |
| `grid` | 网格 | `columns`(2) `cell`(64x64) `spacing`(4) | 按行优先填格（CustomUI 后端） |
| `scroll` | 纵向滚动 | `height`(200) | 内容超出高度出现滚动（仅 CustomUI 后端） |

`window` 本身是一个 column：顶层元素（不包容器时）自动竖排。
图元后端下这层"隐式 column"的间距由 window 的 `spacing` 属性控制（默认 0）。

## 组合示例

```text
window:                    # 顶层 = 隐式竖排
  label title:
    text: "—— 回收站 ——"
  row:                     # 第一行: 标签 + 按钮
    spacing: 8
    label status_lbl:
      text: "待处理: 0"
    button refresh_btn:
      text: "刷新"
  column:                  # 第二块: 竖排的槽位组
    spacing: 2
    label:
      text: "金属液"
    progress liquid_bar:
      value: 0.3
  row:                     # 第三块: 底部按钮行
    button ok_btn:
      text: "全部回收"
    button cancel_btn:
      text: "清空"
```

嵌套任意深——`row` 里放 `column` 里再放 `grid` 是常态。[ex29_psui_machine](../../examples/ex29_psui_machine/README.md)
的机器面板就是三列 row 中嵌"标签+槽"的 column：

```text
row:
  spacing: 8
  column:                  # 左列: 武器按钮竖排
    spacing: 2
    button btn_w_pistol: text: "手枪", size: 100x18, font_size: 0.75, on_click: bench_pick
    button btn_w_smg:    text: "冲锋枪", size: 100x18, font_size: 0.75, on_click: bench_pick
  grid:                    # 中部: 2 列部件网格
    columns: 2
    spacing: 3
    # ... 6 组 (column: 标签 + slot)
  column:                  # 右列: 箭头 + 成品槽
    spacer:
      size: 6x40
    label:
      text: "→"
      font_size: 1.2
```

## grid：两种后端行为不同

**CustomUI 后端**：`columns` 列数 + `cell` 单元格尺寸（默认 64x64），子元素依次入格。

**图元树后端**：`columns` 列数（默认 2），子元素**按行优先**入座
（第 i 个孩子 → 第 `i % columns` 列、第 `i / columns` 行），行数自动取整。
`cell` 属性不参与（格尺寸由子元素自己的 size 决定）。

机器面板部件区的坐标注释就是这个语义：

```text
# 部件区网格坐标 (columns: 2, 子元素按行优先入座):
#   (0,0) 机匣 s0   (1,0) 枪管 s1
#   (0,1) 手柄 s2   (1,1) 枪机 s3
#   (0,2) 弹匣 s4   (1,2) 零件 s5
```

## scroll：列表与长内容

`scroll` 给一个固定高度（默认 200）的纵向滚动区，内容超出就滚。
动态列表的标准骨架是"静态 scroll + on_build 填充"（见 [06 动态构建](06-dynamic.md)）：

```text
window manager:
  on_build: build_panel
  # ...
  scroll browse_list:      # 带 id 的空滚动区 — on_build 里 ui.build_begin("browse_list") 定位填充
    height: 300
  scroll inject_list:
    height: 140
```

图元树后端**不支持 scroll**（警告跳过，子树同跳）——槽位面板要做长列表，
用 `grid_slot` 的多格滚动自身承担，或者干脆分页按钮。

## 尺寸语义三态（易混淆，专列一表）

同一个 `size: WxH` 属性，写在不同位置语义不同：

| 写在哪 | 语义 | 例 |
|---|---|---|
| `window` | 窗口整体尺寸（像素） | `size: 420x300` |
| `slot` / `grid_slot` | **格数**（宽 x 高各几格），默认 2x2 / 3x4 | `size: 3x4` = 12 格熔化区 |
| `spacer`（图元） | 预留像素（撑开空单元） | `size: 6x40` = 6 宽 40 高的空位 |
| `label`（图元） | 定宽（只用 W） | `size: 120x0` ≈ 定宽 120 |
| `button`（图元） | 定宽定高（像素） | `size: 100x18` |
| `image` | 显示尺寸（像素，两后端都支持） | `size: 64x64` |
| `grid` 的 `cell` | 单元格尺寸（像素，CustomUI 后端） | `cell: 64x64` |

CustomUI 后端的 label/button 不吃 `size`（属性被接受但忽略）——
要控制它们的占位用 `ui.set_pref_size`（脚本侧，见
[05 · 09 ui](../05-api-reference/09-ui.md)）。

## 排版实务（调参经验）

机器面板头部留一段"调参区"注释，数值全是试出来的——
这本身就是 PSUI 的排版工作流：**声明式骨架 + 注释调参 + 纯包改动热改**：

```text
# ===== 调参区 (数值全是估的, 挤/空就改这里, 纯包改动不用重编译) =====
#   window size: 400x330       窗口整体
#   spacing: 3 / 8 / 2 / 3     顶层竖排 / 主区三列 / 武器列行距 / 部件网格
#   武器按钮: 100x18, font_size 0.75 (字大按钮小; 宽度按最长文字留够)
#   槽标签: font_size 0.65           底部: 0.7   箭头: 1.2
# ==================================================================
```

要点：

1. **按钮宽度按最长文字留够**——图元按钮 `size` 定宽后文字放不下会裁切。
2. **字号倍率错落**：标题 1.0~1.2、槽标签 0.65、状态行 0.7，两三档就够建立层级。
3. **spacer + 箭头符号做视觉引导**：`→` 加 font_size 1.2 是机器面板的"流程感"手法。
4. 改完 `.psui` 不用重启：`ui.rebuild()` 即时生效（窗口位置自动记忆恢复）。

---

下一篇：[05 · 回调与生命周期](05-callbacks.md)
