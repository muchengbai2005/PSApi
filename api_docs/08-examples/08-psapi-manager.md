# 08 · psapi_manager 带读：动态面板的官方样板

> F6 管理面板本体就是一个内容包：3 个文件、无物品、无 NPC，却是全书
> **PSUI 动态面板**的权威示例——静态骨架 + on_build 命令式填充 +
> ui.rebuild 签名式重建 + 1Hz 刷新。做"浏览器/列表/仪表盘"类面板，
> 照它抄就对了。
>
> 事实来源：`UserData/PSApi/packs/psapi_manager/`（v1.0.0 磁盘实况）；
> PSUI 动态构建 API 见 [06 PSUI · 动态构建](../06-psui/README.md)。

## 一、三份文件、两种区

```text
pack.json           id=psapi_manager, "系统包, 删除后 F6 面板不可用"
ui/manager.psui     静态骨架: 固定元素 + 三个空的 scroll 容器
events/manager.pss  build_panel() 动态填充 + 十几个交互回调 + tick 刷新
```

psui 里的关键设计——**静态区与动态区分离**：

```text
window manager:
  label status_lbl / attract_lbl / last_action     ← 静态区: set_text 原地更新
  row: 刷新 / 补货 / 重置注入记录 按钮组
  dropdown dd_cat + input inp_search + 搜索按钮    ← 静态区: 交互控件
  scroll browse_list: (height: 300)               ← 动态区①: 物品浏览器
  scroll inject_list: (height: 140)               ← 动态区②: 注入注册
  label cur_client                                 ← 静态区(多行文本)
  scroll queue_list: (height: 180)                ← 动态区③: 客户队列
```

三个 `scroll` 容器在 psui 里**是空的**——内容由脚本的 `on_build` 回调
在构建期命令式填充。这是与 gunworks 组装台（纯静态骨架）相反的另一个
极端：**结构每次重建**，所以不存在"结构恒定/串槽"问题，代价是重建瞬间
可能闪动——于是有了签名式刷新（第四节）。

## 二、on_build：构建期命令式填充

```pss
func build_panel():
    refresh_texts()
    fill_browser()
    fill_inject()
    fill_client()
    fill_queue()
```

`on_build: build_panel` 挂在 window 上，每次面板构建（打开或 rebuild）
时执行。填充一个动态区是严格的 begin/end 配对：

```pss
func fill_browser():
    var r = items.browse(g_cat, g_applied, g_page, PAGE_SIZE)
    g_page = r.page
    ui.build_begin("browse_list")            # 开始往这个容器里塞
    if r.total == 0:
        ui.build_label("(无匹配物品) 换个分类或搜索词试试")
    else:
        for it in r.items:
            var val = ""
            if it.value > 0:
                val = " ¥{it.value}"
            ui.build_row()                   # row 容器同样 begin/end
            ui.build_label("{it.name} [{cat_label(it.cat)}]{val} · {it.id}")
            ui.build_button("拿取", "on_take", {arg = it.id,
                tooltip = "发 1 件到背包(后仓)"})
            ui.build_end()
    ui.build_end()
    ui.set_text("page_info", "  第 {r.page + 1}/{r.pages} 页 · {r.total} 项  ")
```

三个可抄的细节：

1. **`{arg = ...}` 闭包参数**：build_button 的第三参把上下文（物品 id、
   inject idx、队列客户指针）绑给回调，回调里 `a.arg` 取回——动态生成的
   一排按钮可以共用一个 handler。
   arg 可以是 dict：`{arg = {id = e.id, count = e.count}}`，回调里
   `a.arg.id` / `a.arg.count` 拆开用。
2. **服务端分页**：`items.browse(分类, 搜索词, 页, 页大小)` 返回
   `{items, page, pages, total}`——浏览器不一次拉全量，翻页按钮只改
   `g_page` 再 rebuild。
3. **状态全在包级 var**：`g_cat / g_search / g_applied / g_page` 是顶层
   变量，rebuild 不丢（重建的是 UI，不是脚本状态）。**搜索的草稿/应用
   分离**（`g_search` 输入即存、`g_applied` 点搜索才生效）让重建不会
   把半截输入吞掉。

## 三、交互回调：面板动作直接调 API

面板不是只读仪表盘——注入条目的开关/调数直接生效：

```pss
func on_tune_dec(a):
    var e = find_entry(a.arg)
    if e != null and e.count > 1:
        inject.tune(a.arg, {count = e.count - 1})
        ui.rebuild()

func on_force(a):
    if inject.force_client(a.arg):
        ui.set_text("last_action", "[队列] 已对该客户补单(买池+货架)")
    else:
        ui.set_text("last_action", "[队列] 客户已离队, 补单失败")
```

注意写法习惯：**操作结果写进 `last_action` 静态标签**（一行操作日志），
比弹窗轻、比 log 直观；每次改完数据就 `ui.rebuild()` 让动态区跟上。
`find_entry(idx)` 在 `inject.list()` 里现查——和 gunworks 的 doctor
计划一样，**永远不假设 idx 稳定**。

## 四、tick 签名式刷新：防闪动的关键

面板 1Hz 刷新状态，但**不能每秒重建**（闪动 + 丢滚动位置）。
manager.pss 的解法是"签名比对"：

```pss
var g_queue_sig = ""            # 上次渲染的队列签名

on tick():
    if not ui.is_open("manager"):
        return                  # 面板没开就什么都不做
    refresh_texts()             # 静态文本: set_text 原地更新, 不重建
    fill_client()               # cur_client 是 set_text + set_pref_size, 同样安全
    if g_show_queue:
        var sig = queue_sig_of(shop.queue())    # 把队列压成一个字符串
        if sig != g_queue_sig:
            g_queue_sig = sig
            ui.rebuild()        # 结构真变了才重建
```

`queue_sig_of` 把队列前 10 人压成 `"12:名字,intent,现金,指针|..."`——
**指针也进签名**，同名人换人也能检出。这是"文本原地刷、结构签名刷"的
两层刷新纪律：

| 数据形态 | 刷新方式 | 代价 |
|---|---|---|
| 纯文本（状态行/当前客户） | `ui.set_text` + `ui.set_pref_size` | 无闪动 |
| 结构（列表条目数变化） | 签名比对 → `ui.rebuild()`（位置自动记忆） | 重建瞬间 |

`ui.rebuild()` 自带窗口位置记忆（pack.json 注释里点名的能力），所以
重建的代价只剩滚动位置与闪动——签名把频次压到"真变化"那一秒。

## 五、面板里能"看"到什么（也是调试工具）

带读最后，列一遍 F6 面板的功能——它同时是你开发任何包时的**调试窗口**：

| 区 | 内容 | 对应 API |
|---|---|---|
| 状态行 | 交租倒计时/队列长度/当前客户 | `shop.status()` |
| 吸引力行 | 商店吸引力 → 常客档位/生成率 | `shop.attract()`（t1/t2/t3 三档阈值） |
| 物品浏览器 | 30 分类 × 搜索 × 分页，含 **"测试"第 29 类**（物品 JSON `test: true`）与"Mod物品"第 16 类 | `items.browse()` |
| 注入注册 | 全部包的 inject.* 条目 + 会话级开关/调数 | `inject.list()` / `inject.tune()` |
| 当前客户 | 现金/预算/阵营/收购清单/携带货物/展示区 | `shop.current_client()` |
| 客户队列 | 前 10 位 + 每人"补单"按钮 | `shop.queue()` / `inject.force_client()` |

开发时的高频用法：**面板开着改包**——重启后 F6 看注入区有没有你的新条目、
浏览器"测试"分类拿测试物品、队列区当场验证 buy_pool 掷中的货。

## 六、抄什么

| 你想要 | 抄这个 |
|---|---|
| 浏览器/列表类面板 | psui 静态骨架 + scroll 容器 + fill_* 三段式 |
| 一排动态按钮各带上下文 | `build_button` 的 `{arg = ...}`（含 dict 形态） |
| 周期刷新不闪眼 | tick + 签名比对 + `ui.rebuild()` |
| 轻量操作反馈 | `last_action` 单行标签模式 |
| 翻页/筛选状态跨重建保持 | 包级 var + 草稿/应用分离 |

---

**本篇完。** 下一篇：[09 · 从 0 到 1](09-from-zero.md)
