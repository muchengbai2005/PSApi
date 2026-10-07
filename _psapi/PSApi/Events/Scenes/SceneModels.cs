using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSApi.Events.Scenes
{
    /// <summary>
    /// 自定义外出场景 (搜打撤) 声明模型 — pack 的 scenes/*.json。
    /// M1 范围: 静态场景 (背景+按钮互动物) + 地图入口注入 + 搜索点/撤离点。
    /// v1.33.0 (波 3, 用户拍板): builtin_raid 驱动退役 — v2 raid 内容键 (chain_length/
    /// poi_table/pois/enemies) 与 raid_inv 死键全部移出 schema (写了按未知键容忍, 不消费);
    /// raid 玩法只剩 scenes/_raid pss 库一套实现 (driver=script + scripts/data.pss 数据表)。
    /// </summary>
    internal sealed class SceneDef
    {
        internal string PackId;          // 加载时填
        internal string File;            // 逻辑相对路径 (日志用)

        [JsonPropertyName("id")] public string ShortId { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("desc")] public string Desc { get; set; }
        [JsonPropertyName("danger")] public int Danger { get; set; } = 1;
        /// <summary>v1.37.0: 产出提示 (picker 双栏改版右栏详情展示; 可选字符串, 缺省 null = 不显示)。</summary>
        [JsonPropertyName("loot_hint")] public string LootHint { get; set; }
        /// <summary>入口方式: "map"(默认, 进地图注入+picker 列表) / "hidden"(v1.32.0 起:
        /// 不注入不进列表, 但可被 scenes.enter 进入 — 修"非 map 场景注册成功但永远进不去"死场景)。
        /// 解析归一 trim+小写, 非法值必抛。</summary>
        [JsonPropertyName("entry")] public string Entry { get; set; } = "map";
        [JsonPropertyName("bg")] public string Bg { get; set; }                  // pack 图标键 (M1 暂不用, 预留)
        /// <summary>场景环境音: dumping_ground(默认, 原版垃圾场同款外出氛围) / commissary / inventor / store / none。</summary>
        [JsonPropertyName("bgm")] public string Bgm { get; set; } = "dumping_ground";
        /// <summary>M1.5: 场景窗口 psui 文件名 (pack ui/ 下, 不带扩展名; 容忍 ".psui" 后缀, 解析时剥掉)。
        /// 缺省 null = 自动网格按钮列 (M1 旧行为, 一字不改)。</summary>
        [JsonPropertyName("ui")] public string Ui { get; set; }
        /// <summary>驱动模式 — script = 纯 pss 驱动, C# 只给场景壳 (进出/暗幕/外出背包) + 能力 API
        /// (v1.33.0 起唯一内容驱动; builtin_raid 已随 C# raid 服务退役删除)。
        /// v1.32.0: picker = 选图场景 (轻量壳: 不黑屏不离店, 藏 mapPanel+选图 UI; 全注册表唯一,
        /// 无包注册时框架内置 psapi:picker; 推断永不产生 picker, 必须显式声明)。
        /// 缺省一律推断 script (SceneJson.InferDriver), DriverInferred=true 表示推断所得。</summary>
        [JsonPropertyName("driver")] public string Driver { get; set; }
        [JsonPropertyName("points")] public List<ScenePointDef> Points { get; set; } = new List<ScenePointDef>();

        internal string FullId => PackId + ":" + ShortId;

        /// <summary>P1: driver 是否推断所得 (Parse 填充; 推断日志/exit 点校验用)。</summary>
        internal bool DriverInferred;

        /// <summary>v1.32.0: picker 场景判定 (选图面板场景化; 全注册表唯一生效, 无包注册时框架内置
        /// psapi:picker)。picker 走轻量壳 — 不黑屏/不离店, 藏 mapPanel+选图 UI, 退出对称恢复。</summary>
        internal bool IsPicker => Driver == "picker";

        /// <summary>v1.32.0: 场景文件夹注册了 scripts/ 子目录 (picker 包自绘判定:
        /// ui 键或 scripts 任一存在 → 默认 C# 渲染不建, 包用 pss 自制)。ScanPacks 填充。</summary>
        internal bool HasScripts;
    }

    internal sealed class SceneLootEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        /// <summary>"1-2" 区间或定值, 默认 1 (SceneJson.ParseCount)。</summary>
        [JsonPropertyName("count")] public string Count { get; set; }
        [JsonPropertyName("weight")] public float Weight { get; set; } = 1f;
        /// <summary>最低深度 (默认 0; 到底了的最终物资点无视此限)。</summary>
        [JsonPropertyName("min_depth")] public int MinDepth { get; set; }
        /// <summary>v1.37.0: 稀有度档 0-4 (common/fine/rare/epic/legend; 两阶段掷签 stage2 分桶键,
        /// 缺省 0 普通档)。pss 侧 rarity 写字符串名 (combat.roll_loot 解析层映射, 非法必抛)。</summary>
        [JsonPropertyName("rarity")] public int Rarity { get; set; }
    }

    internal sealed class ScenePointDef
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        /// <summary>"loot" 搜索点 | "exit" 撤离点。</summary>
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; }
        /// <summary>相对坐标 0-1 (面板内, 原点在左下)。</summary>
        [JsonPropertyName("x")] public float X { get; set; } = 0.5f;
        [JsonPropertyName("y")] public float Y { get; set; } = 0.5f;
        /// <summary>loot 点: 掉落物品 id 池 (等概率随机)。</summary>
        [JsonPropertyName("loot")] public List<string> Loot { get; set; }
        /// <summary>loot 点: 数量, "1-2" 区间或 "3" 定值, 默认 1。</summary>
        [JsonPropertyName("loot_count")] public string LootCount { get; set; }
        /// <summary>loot 点: 可搜索次数, 默认 1。</summary>
        [JsonPropertyName("times")] public int Times { get; set; } = 1;
    }

    internal static class SceneJson
    {
        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        internal static SceneDef Parse(string rel, string text)
        {
            var def = JsonSerializer.Deserialize<SceneDef>(text, Opts);
            if (def == null) throw new Exception(rel + ": 解析结果为空");
            if (string.IsNullOrWhiteSpace(def.ShortId)) throw new Exception(rel + ": 缺 id");
            if (string.IsNullOrWhiteSpace(def.Name)) def.Name = def.ShortId;
            if (def.Ui != null)
            {
                def.Ui = def.Ui.Trim();
                if (def.Ui.Length == 0) def.Ui = null;
                else if (def.Ui.EndsWith(".psui", StringComparison.OrdinalIgnoreCase))
                    def.Ui = def.Ui.Substring(0, def.Ui.Length - ".psui".Length);
            }
            def.File = rel;
            // v1.32.0: entry 双值 — "map"(默认) / "hidden"(不注入不进 picker 列表, 可 scenes.enter 进入)。
            // 归一 trim+小写, 非法值必抛 (照 driver 同款惯例)
            if (def.Entry == null) def.Entry = "map";
            else
            {
                def.Entry = def.Entry.Trim().ToLowerInvariant();
                if (def.Entry.Length == 0) def.Entry = "map";
                else if (def.Entry != "map" && def.Entry != "hidden")
                    throw new Exception(rel + ": entry 仅支持 map/hidden, 实得 '" + def.Entry + "'");
            }
            // P1 (v1.26.0): driver 显式声明优先 (归一 trim+小写, 非法值必抛);
            // 缺省一律推断 script (v1.33.0 波 3: builtin_raid 退役,  raid 内容键不再参与推断),
            // 推断所得 DriverInferred=true, 由 ScanPacks 打日志明示。
            // v1.32.0: 第三驱动 picker (选图场景) — 只认显式声明, 推断永不产生。
            if (def.Driver != null)
            {
                def.Driver = def.Driver.Trim().ToLowerInvariant();
                if (def.Driver.Length == 0) def.Driver = null;
                else if (def.Driver != "script" && def.Driver != "picker")
                    throw new Exception(rel + ": driver 仅支持 script/picker (v1.33.0 起 builtin_raid 已退役), 实得 '" + def.Driver + "'");
            }
            if (def.Driver == null)
            {
                def.Driver = InferDriver(def);
                def.DriverInferred = true;
            }
            if (def.Points == null) def.Points = new List<ScenePointDef>();
            bool hasExit = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in def.Points)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) throw new Exception(rel + ": 互动物缺 id");
                if (!seen.Add(p.Id)) throw new Exception(rel + ": 互动物 id 重复 '" + p.Id + "'");
                if (p.Type != "loot" && p.Type != "exit") throw new Exception(rel + ": 互动物 '" + p.Id + "' type 仅支持 loot/exit (M1)");
                if (p.Type == "exit") hasExit = true;
                if (p.Type == "loot" && (p.Loot == null || p.Loot.Count == 0))
                    throw new Exception(rel + ": 搜索点 '" + p.Id + "' 缺 loot 物品池");
                if (p.Times <= 0) p.Times = 1;
                if (string.IsNullOrWhiteSpace(p.Label)) p.Label = p.Id;
            }
            // P1: 显式 driver=script 由脚本提供撤离 (scenes.leave / psui on_click), 不要求 exit 点;
            // 推断的 legacy 场景照旧必须 (零回归)
            // v1.32.0: picker 场景由框架提供返回 (scenes.back / Esc / 「返回地图」按钮), 同样不要求
            if (!hasExit && !def.IsPicker && !(def.Driver == "script" && !def.DriverInferred))
                throw new Exception(rel + ": 至少需要一个 type=exit 撤离点 (或显式 \"driver\":\"script\" 由脚本提供撤离)");
            return def;
        }

        /// <summary>driver 缺省推断 — v1.33.0 (波 3, builtin_raid 退役): 一律 script
        /// (raid 场景改由 pss 库驱动, scene.json 不再声明 raid 内容键)。纯函数可测。</summary>
        internal static string InferDriver(SceneDef def)
        {
            return "script";
        }

        /// <summary>"WxH" 解析; 失败返回默认值。</summary>
        internal static (int W, int H) ParseSize(string s, int defW, int defH)
        {
            if (!string.IsNullOrWhiteSpace(s))
            {
                var parts = s.ToLowerInvariant().Split('x');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h)
                    && w > 0 && h > 0)
                    return (w, h);
            }
            return (defW, defH);
        }

        /// <summary>"1-2" 区间或 "3" 定值 → 随机数量; 失败返回 1。纯函数可测。</summary>
        internal static int ParseCount(string s, Random rng)
        {
            if (!string.IsNullOrWhiteSpace(s))
            {
                var parts = s.Split('-');
                if (parts.Length == 2
                    && int.TryParse(parts[0].Trim(), out int lo) && int.TryParse(parts[1].Trim(), out int hi)
                    && lo > 0 && hi >= lo)
                    return rng.Next(lo, hi + 1);
                if (int.TryParse(s.Trim(), out int n) && n > 0) return n;
            }
            return 1;
        }
    }

    /// <summary>v1.32.0: 场景导航纯逻辑 (场景栈 + id 解析) — 抽出供无头测试; 运行时栈实例在
    /// SceneService (_navStack)。栈元素 = 场景短 id 或特殊标记 MapToken ("map" = 返回点在商店/地图层)。</summary>
    internal static class SceneNav
    {
        /// <summary>栈特殊标记: 返回点 = 商店/地图层 (从非场景态 scenes.enter 时压入)。</summary>
        internal const string MapToken = "map";

        /// <summary>scenes.enter 压栈标记: 场景内调用 = 当前场景短 id; 非场景态 = "map"。纯函数。</summary>
        internal static string EnterPushToken(string activeShortId)
            => string.IsNullOrEmpty(activeShortId) ? MapToken : activeShortId;

        /// <summary>scenes.back 弹栈: 空栈 → null (= 回店, 等价 scenes.leave 回店路径);
        /// 否则栈顶出栈返回 ("map" 或场景短 id)。</summary>
        internal static string PopBack(List<string> stack)
        {
            if (stack == null || stack.Count == 0) return null;
            string t = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            return t;
        }

        /// <summary>场景 id 解析: 全 id (含 ':') 直查; 否则按短名匹配 — 唯一 = 命中; 多义 =
        /// ambiguous=true 并返回先加载者 (字典序即注册序, 调用方告警); 无匹配 = null。</summary>
        internal static SceneDef Resolve(Dictionary<string, SceneDef> scenes, string id, out bool ambiguous)
        {
            ambiguous = false;
            if (scenes == null || string.IsNullOrWhiteSpace(id)) return null;
            SceneDef def;
            if (scenes.TryGetValue(id, out def)) return def;
            SceneDef first = null;
            foreach (var kv in scenes)
            {
                if (kv.Value == null || kv.Value.ShortId != id) continue;
                if (first == null) { first = kv.Value; continue; }
                ambiguous = true;
                break;
            }
            return first;
        }
    }
}
