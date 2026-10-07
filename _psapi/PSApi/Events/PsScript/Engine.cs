using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// PSScript 引擎: 扫 packs/&lt;pack&gt;/events/**/*.pss(v1.4.0 起子目录递归, 路径排序)
    /// 与 scenes/**/scripts/*.pss (P1 v1.26.0, 场景文件夹脚本, 同管线无作用域隔离) → 词法/语法分析 →
    /// 登记顶层 on 块到 EventBus → 执行顶层语句。
    /// 同包多文件共享一个全局环境(后加载可调先加载的函数); 跨包隔离。
    /// 加载分<strong>两阶段</strong>: 全部包全部文件先编译 + 登记 on, 再统一执行顶层语句
    /// (保证 bus.emit 自定义事件的订阅方先就位, 与包/文件顺序无关)。
    /// v1.37.1: 场景文件中库目录 (_ 前缀) 先装载 — 场景 data.pss 顶层可直接调库函数
    /// (OrdinalIgnoreCase 下 '_' 排字母后, 不干预则库函数未定义 bug 实证, 见 HANDOFF 第 80 条)。
    /// 编译期错误: 报 文件:行:列, 该文件整体不加载, 汇总写 PsApi.WriteErrorLog;
    /// 运行期错误: catch + 日志, 不断链(与 EventBus 语义一致)。
    /// </summary>
    internal sealed class PsScriptEngine
    {
        /// <summary>事件名映射表: 下划线写法 → EventBus 事件 id。表外名字按 psapi.&lt;名&gt; 转点订阅(仅警告),
        /// 未来桥接新事件时老脚本自动生效。桥接来源见 GameHooks(官方 ModHook)。
        /// 字符串形式(on "pack:event":)为原样订阅, 不查本表。</summary>
        internal static readonly Dictionary<string, string> EventAliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scene_loaded"] = "psapi.scene.loaded",
            ["game_loaded"] = "psapi.game.loaded",
            ["day_wake"] = "psapi.day.wake",
            ["day_sleep"] = "psapi.day.sleep",
            ["shop_opened"] = "psapi.shop.opened",
            ["shop_closed"] = "psapi.shop.closed",
            ["store_leaving"] = "psapi.store.leaving",
            ["store_returning"] = "psapi.store.returning",
            ["night_services"] = "psapi.night.services",
            ["store_event_started"] = "psapi.store_event.started",
            ["store_event_ended"] = "psapi.store_event.ended",
            ["customer_generated"] = "psapi.customer.generated",
            ["dialogue_choice"] = "psapi.dialogue.choice",
            ["trade_completed"] = "psapi.trade.completed",
            ["tick"] = "psapi.tick",
            // P1 (v1.26.0): 自定义外出场景生命周期 (两种驱动都发; payload 见 SceneService.Publish)
            ["scene_enter"] = "psapi.scene.enter",
            ["scene_leave"] = "psapi.scene.leave",
            ["scene_interact"] = "psapi.scene.interact",
            ["scene_loot"] = "psapi.scene.loot",
        };

        /// <summary>一个已编译待执行的文件。</summary>
        private sealed class LoadedFile
        {
            internal string PackId;
            internal string Rel;
            internal PsProgram Prog;
            internal Interpreter Itp;
        }

        private readonly MelonLogger.Instance _logger;
        private readonly EventBus _bus;
        private readonly StoreEventService _storeEvents;
        private readonly NpcService _npcs;
        private readonly PsUI.PsUiService _ui;
        private readonly InjectService _inject;
        private readonly LootPoolService _loot;
        private readonly Scenes.SceneService _scenes;
        private readonly List<Interpreter> _packInterpreters = new List<Interpreter>();
        private List<PackInfo> _packs;   // v1.17.0: pack.list() 用 (LoadAll 时留存)

        internal int LoadedFileCount { get; private set; }
        internal int HandlerCount { get; private set; }

        internal PsScriptEngine(MelonLogger.Instance logger, EventBus bus, StoreEventService storeEvents = null, NpcService npcs = null, PsUI.PsUiService ui = null, InjectService inject = null, LootPoolService loot = null, Scenes.SceneService scenes = null)
        {
            _logger = logger;
            _bus = bus;
            _storeEvents = storeEvents;
            _npcs = npcs;
            _ui = ui;
            _inject = inject;
            _loot = loot;
            _scenes = scenes;
        }

        /// <summary>加载全部内容包的脚本; 返回编译错误数(0 = 全部干净)。</summary>
        internal int LoadAll(List<PackInfo> packs)
        {
            LoadedFileCount = 0;
            HandlerCount = 0;
            _packs = packs;
            _packInterpreters.Clear();
            var errors = new List<string>();
            var loaded = new List<LoadedFile>();

            // ---- 阶段 0: 扫 ui/*.psui 注册面板 (E6; 纯解析校验, 不建游戏对象) ----
            _ui?.ScanPacks(packs, errors);

            // ---- 阶段 1: 编译全部包全部文件(失败文件隔离) ----
            foreach (var pack in packs)
            {
                // v1.4.0: 子目录递归扫描 (gunworks 模板包 events/bench/*.pss 按功能分目录)
                // P1 (v1.26.0): 场景文件夹脚本 scenes/<id>/scripts/*.pss 同管线全局加载
                //   (任务书明示无作用域隔离 — 与同包 events/*.pss 共享全局环境, 脚本靠
                //   on scene_enter 的 event.scene 判场景)。ListFiles 内部已按 OrdinalIgnoreCase 排序
                var files = pack.Source.HasDir("events") ? pack.Source.ListFiles("events", ".pss") : Array.Empty<string>();
                var sceneFiles = pack.Source.HasDir("scenes") ? pack.Source.ListFiles("scenes", ".pss") : Array.Empty<string>();
                // v1.37.1 (实测修复): 库目录 (_) 文件先装载 — OrdinalIgnoreCase 下 '_' 排在字母后,
                // 不干预则场景 data.pss 顶层先于库函数定义执行 (「未定义变量 raid_enemy_*」实证);
                // OrderBy 稳定排序, 库内/场景内各自保持路径序
                sceneFiles = sceneFiles.OrderBy(f => IsLibraryPath(f) ? 0 : 1).ToArray();
                if (files.Length == 0 && sceneFiles.Length == 0) continue;

                var env = PsBuiltins.CreatePackEnv(_logger, pack.Id, _bus);
                PsBuiltinsGame.Register(env, _logger, pack.Id, _storeEvents, _npcs, _ui, _inject, _loot, _packs, _scenes, _scenes?.SceneUgui, _scenes?.ScriptGrid, _scenes?.ScriptCombat, _scenes?.SceneLog); // E2: state/time/...; E3: store_event; E4: npc; E6: ui; M1: inject + v1.10.0 loot_pool; v1.20.0: scenes; P2 (v1.27.0): ui.panel 场景 uGUI; P3 (v1.28.0): scene.log/grid.*/combat.* (v1.33.0: raid.* builtin 随 builtin_raid 退役删除)
                var itp = new Interpreter(pack.Id) { Global = env };
                _packInterpreters.Add(itp);

                foreach (var file in files.Concat(sceneFiles))
                {
                    // v1.4.0: 相对 events/ 的子路径 (诊断/报错用, 子目录文件带目录前缀)
                    string rel = $"{pack.Id}/{file}";
                    try
                    {
                        var tokens = Lexer.Lex(rel, pack.Source.ReadText(file));
                        loaded.Add(new LoadedFile { PackId = pack.Id, Rel = rel, Prog = Parser.Parse(rel, tokens), Itp = itp });
                        LoadedFileCount++;
                    }
                    catch (PsCompileError ce)
                    {
                        errors.Add(ce.Message + " (该文件未加载)");
                        PsApi.Warn(_logger, $"[pss] compile error: {ce.Message}");
                    }
                    catch (Exception e)
                    {
                        errors.Add($"{rel}: 读取失败: {e.Message}");
                        PsApi.Warn(_logger, $"[pss] read failed: {rel}: {e.Message}");
                    }
                }
            }

            // ---- 阶段 2a: 登记全部 on 块(先于任何顶层执行, 自定义事件订阅先就位) ----
            foreach (var lf in loaded)
                foreach (var on in lf.Prog.Handlers)
                    RegisterHandler(lf.PackId, lf.Rel, on, lf.Itp);

            // ---- 阶段 2b: 统一执行顶层语句(顶层 return = 提前结束本文件) ----
            foreach (var lf in loaded)
            {
                lf.Itp.BeginRun();
                try
                {
                    lf.Itp.ExecBlock(lf.Prog.TopLevel, lf.Itp.Global);
                }
                catch (PsReturn) { }
                catch (PsRuntimeError re)
                {
                    PsApi.Err(_logger, $"[pss] {lf.Rel}:{re.Line} in 顶层: {re.Message}");
                }
                catch (Exception e)
                {
                    PsApi.Err(_logger, $"[pss] {lf.Rel} in 顶层: 内部错误 {e.GetType().Name}: {e.Message}");
                }
            }

            PsApi.WriteErrorLog(_logger, errors);

            // ---- 阶段 3: E6 on_click 绑定启动期校验 (函数缺失 = 警告, 不等到点击才炸) ----
            _ui?.BindAndValidate(_packInterpreters);
            return errors.Count;
        }

        /// <summary>v1.37.1: 库路径判定 (任一目录段以 "_" 开头, 如 scenes/_raid/scripts/x.pss) —
        /// 库文件在场景文件之前装载 (顶层执行序), 供场景脚本顶层直接调库函数。</summary>
        internal static bool IsLibraryPath(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return false;
            foreach (var seg in relPath.Split('/'))
                if (seg.Length > 1 && seg[0] == '_') return true;
            return false;
        }

        /// <summary>on 块 → EventBus 订阅; 标识符事件名走映射(未知仅警告), 字符串形式原样订阅。</summary>
        private void RegisterHandler(string packId, string relFile, OnStmt on, Interpreter itp)
        {
            string eventId;
            if (on.Verbatim)
            {
                eventId = on.EventName; // 字符串形式: 自定义事件, 不查别名表/不加前缀
            }
            else if (!EventAliases.TryGetValue(on.EventName, out eventId))
            {
                eventId = "psapi." + on.EventName.Replace('_', '.');
                PsApi.Warn(_logger, $"[pss] {relFile}:{on.Line}: 未知事件 '{on.EventName}', 按 '{eventId}' 订阅(未来桥接该事件后自动生效)");
            }

            _bus.Subscribe(eventId, payload => RunHandler(packId, relFile, on, itp, payload),
                owner: $"pss:{packId}/{Path.GetFileName(relFile)}");
            HandlerCount++;
        }

        /// <summary>事件触发 → 注入 event 变量执行 handler; 异常隔离不断链。</summary>
        private void RunHandler(string packId, string relFile, OnStmt on, Interpreter itp, object payload)
        {
            itp.BeginRun();
            // event 注入包全局(主线程单线程, 重入时保存/恢复旧值)
            bool hadPrev = itp.Global.TryGet("event", out var prev);
            itp.Global.SetLocal("event", BuildEventDict(payload));
            try
            {
                itp.ExecBlock(on.Body, itp.Global);
            }
            catch (PsReturn) { }
            catch (PsRuntimeError re)
            {
                PsApi.Err(_logger, $"[pss] {relFile}:{re.Line} in {on.EventName}: {re.Message}");
            }
            catch (Exception e)
            {
                PsApi.Err(_logger, $"[pss] {relFile} in {on.EventName}: 内部错误 {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                if (hadPrev) itp.Global.SetLocal("event", prev);
            }
        }

        /// <summary>payload → event dict: string 包成 {value=...}, null 给空 dict, 其余包 {value=...}。</summary>
        private static object BuildEventDict(object payload)
        {
            switch (payload)
            {
                case null:
                    return new Dictionary<string, object>(StringComparer.Ordinal);
                case Dictionary<string, object> d:
                    return d;
                default:
                    return new Dictionary<string, object>(StringComparer.Ordinal) { ["value"] = payload };
            }
        }
    }
}
