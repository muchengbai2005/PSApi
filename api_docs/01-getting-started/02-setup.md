# 02 · 环境准备与安装验证

> 阅读时间约 5 分钟。读完你会确认：PS-API 在你的游戏里正常加载，知道每个目录是干什么的，
> 并掌握两个调试快捷键的配置方法。

## 前置条件

| 条件 | 检查方法 |
|---|---|
| 《Probably Stolen Demo》（playtest 版） | 游戏目录即本说明所在目录 |
| MelonLoader 已安装 | 游戏根目录存在 `MelonLoader/` 文件夹，启动游戏会先弹控制台黑窗 |
| PSApi 模组已装 | `Mods/` 下存在 `PSApi.dll`（v2.0.0 起单组件；旧的 `PSApi.Items.dll` + `PSApi.Events.dll` 两件套已合并退役，勿并存） |

> 本指南假定你在标准的 PS-API 开发环境中工作（MelonLoader + PSApi 已就位）。
> 若 `Mods/` 下缺 PSApi 模组，请先从模组分发处获取并放入，再继续本篇。

## 验证 PS-API 正常加载

启动游戏，观察 MelonLoader 控制台（或 `MelonLoader/Logs/<日期时间>.log`），
应出现类似下面的行：

```text
[PSApi] [psapi] PSApi v2.0.8 loaded (统一宿主: items+events)
[PSApi] [psapi] rescan(init): 3 pack(s), 36 item def(s), 5 quality(ies), errors=0
[PSApi] [psapi] [items] 模块初始化完成 (PSApi v2.0.8). packs=3 items=36 qualities=5
[PSApi] [psapi] rescan(init): 3 pack(s), 20 pss file(s), 27 handler(s), 2 scene(s), 0 compile error(s)
[PSApi] [psapi] [events] 模块初始化完成 (PSApi v2.0.8). packs=3
[PSApi] [psapi] event bus selftest hits=12 (expect 12) sceneSubs=1
```

要点：

- `packs=N`：扫描到的内容包数量（`UserData/PSApi/packs/` 下的有效包 + `Mods/` 里的
  `PSPack.*.dll` 内嵌包合并去重后的数量）。
- `errors=0` / `0 compile error(s)`：解析与脚本编译全绿。非 0 时，
  详情已写入 `UserData/PSApi/logs/pack_errors_<时间戳>.log`，打开逐条修即可。
- `event bus selftest hits=12`：事件总线自检，恒为 12（1+10+1 语义验证），
  少了说明环境异常。

> **从旧两件套升级**：v2.0.0 起 `PSApi.Events.dll` + `PSApi.Items.dll` 已合并为单个
> `PSApi.dll`。升级时先删除 Mods/ 里的两个旧 dll 再放新版——并存会弹告警且功能双挂。
> 旧 `[PSApi.Events]` / `[PSApi.Items]` 配置分类里的自定义热键会自动迁移到新的
> `[PSApi]` 分类（仅非默认值才迁移，旧类目留档不删）。

进入任意存档后还会出现：

```text
[PSApi] [psapi] item directory ready via ModHook.OnModItemDirectoryInit; 36 item def(s) registered total
```

表示自定义物品已真正注册进游戏目录——这时 F12 发放、商店购买才可用。
（游戏每局会重建物品目录，切场景/重进对局后可能再出现一条 `via poll` 的同类日志，
属正常重注册。）

## UserData/PSApi 目录导览

PS-API 的全部工作区都在游戏目录的 `UserData/PSApi/` 下：

```text
UserData/PSApi/
├── README.md        ← 官方速览（本文档是其展开版）
├── packs/           ← ★ 内容包根目录，每个子文件夹 = 一个包，你的主战场
├── ui/              ← PS-API 全局 UI 配置（如 button_config.json，配方锁定窗口按钮）
├── state/           ← 存档槽状态（按 <槽位号>/<所属模块>.json 自动管理，勿手改）
└── logs/            ← 错误日志（pack_errors_*.log），排障第一现场
```

| 目录 | 谁读写 | 说明 |
|---|---|---|
| `packs/` | 你写，PSApi 读 | 开发期内容包全部放这里，改文件重启即生效 |
| `state/` | PS-API 自动 | 每个游戏存档槽独立隔离（`state/8/`、`state/global/`…），脚本状态存这里 |
| `logs/` | PS-API 自动 | 内容包解析错误 / 脚本编译错误汇总，有错才生成文件 |
| `ui/` | 你可改 | 少量全局 UI 配置 |

## 两个调试快捷键

PS-API 注册了两个 MelonLoader 配置项，默认值即可用：

| 快捷键 | 作用 | 配置项 |
|---|---|---|
| **F12** | 把**全部**已注册的自定义物品发到玩家后仓（调试神器） | `PSApi` → `GrantHotkey` |
| **F6** | 打开/关闭 PS-API 管理面板（由 `psapi_manager` 内容包提供） | `PSApi` → `ManagerHotkey` |

想改键，编辑 `UserData/MelonPreferences.cfg`（游戏目录下）中对应段落，例如：

```ini
[PSApi]
GrantHotkey = F9
ManagerHotkey = "F7"
```

> `ManagerHotkey` 填 Unity `KeyCode` 名（如 `F6`、`F7`、`Insert`），无法解析时自动回退 F6
> 并在控制台告警。

## 常见安装问题

**启动后完全看不到 `[psapi]` 日志**
→ `Mods/` 下缺 `PSApi.dll`，或被杀软/游戏更新清掉。

**控制台出现 `pack conflict [duplicate]: ...`，主菜单弹"重复加载"窗**
→ 同一个包 id 同时存在文件夹版和 DLL 版（例如 `packs/my_pack/` 与 `Mods/PSPack.my_pack.dll`
并存）。开发期可并存用于对照验证，此时 **DLL 版胜出**；正式环境二选一。

**主菜单弹"缺少前置模组"窗**
→ 某包的 `pack.json` 里 `prerequisites` 声明的前置包未安装。装齐前置或去掉依赖。

**F12 按了没反应，日志说 `nothing granted (not in store yet?)`**
→ 还没进存档，物品目录未就绪。先进入对局再按。

---

下一步：[03 · 5 分钟做出你的第一个包](03-first-pack.md)
