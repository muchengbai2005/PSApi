using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MelonLoader;

namespace PSApi.Events.Raid
{
    /// <summary>
    /// 搜打撤场景 HUD 布局 — v1.31.0 三层覆写 (用户拍板) / v1.48.8 第四层用户覆写:
    ///   框架代码默认 (POCO 初始化器) &lt; 包级 packs/&lt;pack&gt;/scenes/layout.json
    ///   &lt; 场景级 packs/&lt;pack&gt;/scenes/&lt;id&gt;/layout.json
    ///   &lt; 用户覆写 UserData/PSApi/layout_overrides/&lt;pack&gt;/&lt;sceneId&gt;.json (物理文件, 与包形态无关, 最高优先),
    ///   逐区逐键深合并 (后者胜),
    ///   非法值告警并回退下一层的值 (钳制在使用端, ClampLines/ClampGrid/ClampFont/ClampSize/Clamp01)。
    /// 解析纯函数 (MergeLayer/ResolveLayers, 无 Unity/IO 依赖, pss_test 无头可测);
    /// 包文件经 Plugin 接线委托读取 (PackLayoutTextOf/SceneLayoutTextOf/PackLoadOrder/PackDirOf/UserLayoutTextOf)。
    /// 当前活动场景布局缓存 Active: SceneService 进/出场景刷新 (RefreshActive), F10 热重载同径。
    /// 无场景上下文: picker 区 = 代码默认 + 全部包级 layout.json 按包加载序合并 (ResolvePickerLayout, 后者胜)。
    /// M 键写回目标 (WriteBackPathFor): 文件夹包 = 当前场景的场景级 layout.json (只写钉位键 box/ground/container + ground.mode;
    ///   junction 入口写穿落开发主库, 有意为之); 内嵌包 (PackDirOf=null) = 用户覆写层 (v1.48.8, 不再放弃)。
    /// 旧全局文件 UserData/PSApi/raid_layout.json v1.31.0 起废弃 (框架不再读写, 启动检测打迁移提示 Warn)。
    /// 坐标系: x/y = 相对屏幕 0-1 (原点左下, 区域中心点; log 区=左下角锚点); w/h/font/spacing = uGUI 像素单位;
    /// w/h 在 ground/container 区 = 格数。box.y = 窗口底边锚点 (底缘贴 y, 自动抬半高完整入屏)。
    /// ground.mode: box=左缘贴物品箱右缘+gap 且底边对齐 (箱缺失回退 x/y 保底), free=永远按 x/y 绝对钉位
    /// (M 键写回时自动置 free)。rt 区 (v1.31.0): 实时动作战斗 UI 全部几何/颜色/键位配置。
    /// </summary>
    internal sealed class RaidLayout
    {
        [JsonPropertyName("_readme")] public string Readme { get; set; } =
            "搜打撤场景 HUD 布局 (PSApi.Events v1.31.0)。四层覆写 (v1.48.8): 代码默认 < 包级 scenes/layout.json " +
            "< 场景级 scenes/<id>/layout.json < 用户覆写 UserData/PSApi/layout_overrides/<pack>/<sceneId>.json " +
            "(物理文件, 与包形态无关, 内嵌 DLL 包的 M 写回落这里), 逐键深合并, 缺字段回退下一层。改完保存, 游戏内按 F10 热重载。" +
            "x/y=相对屏幕 0-1 (原点左下, 区域中心点; log=左下角锚点); w/h/font/spacing=像素; " +
            "w/h 在 ground/container 区=格数。box.y=窗口底边锚点。ground.mode: box=贴物品箱右缘+gap 底边对齐 " +
            "(箱缺失回退 x/y 保底), free=永远按 x/y 绝对钉位 (场景内按 M 写回拖好的图元窗位置时自动置 free)。" +
            "title=无互动物场景左上角标题面板 (x/y=左上角屏比)。canvas_order: 0=自动 (图元窗层级-1 探测), " +
            "非 0=强制。rt=实时动作战斗 UI (top/enemy_hp/arena/hitbox/crosshair/warn/arc/bottom/flee/throw/" +
            "switch/status/keys), 键位=KeyCode 名 (非法回退默认)。pocket=口袋兜底窗 (box_follow 失败时的随身网格, " +
            "y=窗口底边锚点同 box, w/h=格数)。picker 双栏: list_w/detail_w/detail_font/rows。删掉某个字段=该字段回退下一层。";

        /// <summary>HUD canvas 层级: 0=自动 (图元窗 FloatingWindowCanvas order-1, 运行时探测); 非 0=强制。</summary>
        [JsonPropertyName("canvas_order")] public int CanvasOrder { get; set; } = 0;

        [JsonPropertyName("status")] public StatusZone Status { get; set; } = new StatusZone();
        [JsonPropertyName("action")] public ActionZone Action { get; set; } = new ActionZone();
        [JsonPropertyName("log")] public LogZone Log { get; set; } = new LogZone();
        [JsonPropertyName("poi")] public PoiZone Poi { get; set; } = new PoiZone();
        [JsonPropertyName("picker")] public PickerZone Picker { get; set; } = new PickerZone();
        [JsonPropertyName("title")] public TitleZone Title { get; set; } = new TitleZone();
        [JsonPropertyName("ground")] public GridPin Ground { get; set; } = new GridPin { X = 0.845f, Y = 0.12f, W = 6, H = 6 };
        [JsonPropertyName("container")] public GridPin Container { get; set; } = new GridPin { X = 0.5f, Y = 0.5f, W = 6, H = 3 };
        [JsonPropertyName("box")] public GridPin Box { get; set; } = new GridPin { X = 0.5f, Y = 0.02f };
        /// <summary>v1.37.0: 口袋兜底窗 (box_follow 失败 = 没带物品箱时的独立网格窗, 默认 2x3 贴原箱位,
        /// y = 窗口底边锚点同 box 区; 撤离时内容自动搬到店里称重台)。</summary>
        [JsonPropertyName("pocket")] public GridPin Pocket { get; set; } = new GridPin { X = 0.5f, Y = 0.02f, W = 2, H = 3 };
        [JsonPropertyName("combat")] public CombatZone Combat { get; set; } = new CombatZone();
        [JsonPropertyName("rt")] public RtZone Rt { get; set; } = new RtZone();

        /// <summary>右侧状态栏: HP/体力/饥饿 数值条 + 噪音/深度行 + 姿态按钮 + buff 行。</summary>
        internal sealed class StatusZone
        {
            [JsonPropertyName("x")] public float X { get; set; } = 0.855f;
            [JsonPropertyName("y")] public float Y { get; set; } = 0.58f;
            [JsonPropertyName("w")] public float W { get; set; } = 250f;
            [JsonPropertyName("bar_h")] public float BarH { get; set; } = 16f;
            [JsonPropertyName("font")] public float Font { get; set; } = 18f;
            [JsonPropertyName("btn_h")] public float BtnH { get; set; } = 34f;
            [JsonPropertyName("spacing")] public float Spacing { get; set; } = 8f;
        }

        /// <summary>中下行动按钮: 继续前进 / 撤离 / 原地休息, 横排。</summary>
        internal sealed class ActionZone
        {
            [JsonPropertyName("x")] public float X { get; set; } = 0.5f;
            [JsonPropertyName("y")] public float Y { get; set; } = 0.44f;
            [JsonPropertyName("btn_w")] public float BtnW { get; set; } = 190f;
            [JsonPropertyName("btn_h")] public float BtnH { get; set; } = 42f;
            [JsonPropertyName("spacing")] public float Spacing { get; set; } = 16f;
            [JsonPropertyName("font")] public float Font { get; set; } = 20f;
        }

        /// <summary>信息栏: 滚动日志 (单行高 × lines 行, 一个 TMP 多行文本)。
        /// v1.25.1: x/y 语义改为面板左下角锚点 (默认 0.02/0.02 = 屏幕左下角留 padding)。</summary>
        internal sealed class LogZone
        {
            [JsonPropertyName("x")] public float X { get; set; } = 0.02f;
            [JsonPropertyName("y")] public float Y { get; set; } = 0.02f;
            [JsonPropertyName("w")] public float W { get; set; } = 400f;
            [JsonPropertyName("lines")] public int Lines { get; set; } = 8;
            [JsonPropertyName("line_h")] public float LineH { get; set; } = 24f;
            [JsonPropertyName("font")] public float Font { get; set; } = 17f;
        }

        /// <summary>物资点按钮 (散布场景中心区, 坐标由场景脚本随机)。</summary>
        internal sealed class PoiZone
        {
            [JsonPropertyName("w")] public float W { get; set; } = 170f;
            [JsonPropertyName("h")] public float H { get; set; } = 56f;
            [JsonPropertyName("font")] public float Font { get; set; } = 18f;
        }

        /// <summary>选图场景面板 (SceneService 独立选图场景)。v1.37.0 双栏改版:
        /// 左栏 ScrollRect 场景列表 (list_w=左栏宽, rows=可见行数) + 右栏详情面板 (detail_w=右栏宽,
        /// detail_font=详情字号); 旧键 w/btn_w/btn_h/font/info_font/title_font/spacing 保留兼容。</summary>
        internal sealed class PickerZone
        {
            [JsonPropertyName("w")] public float W { get; set; } = 460f;
            [JsonPropertyName("btn_w")] public float BtnW { get; set; } = 320f;
            [JsonPropertyName("btn_h")] public float BtnH { get; set; } = 38f;
            [JsonPropertyName("font")] public float Font { get; set; } = 20f;
            [JsonPropertyName("info_font")] public float InfoFont { get; set; } = 15f;
            [JsonPropertyName("title_font")] public float TitleFont { get; set; } = 24f;
            [JsonPropertyName("spacing")] public float Spacing { get; set; } = 8f;
            /// <summary>v1.37.0: 左栏场景列表宽 (像素)。</summary>
            [JsonPropertyName("list_w")] public float ListW { get; set; } = 300f;
            /// <summary>v1.37.0: 右栏详情面板宽 (像素)。</summary>
            [JsonPropertyName("detail_w")] public float DetailW { get; set; } = 320f;
            /// <summary>v1.37.0: 详情正文字号。</summary>
            [JsonPropertyName("detail_font")] public float DetailFont { get; set; } = 16f;
            /// <summary>v1.37.0: 左栏列表可见行数 (定 ScrollRect 视口高)。</summary>
            [JsonPropertyName("rows")] public int Rows { get; set; } = 8;
        }

        /// <summary>v1.30.0: 无互动物场景 (script/自动网格零 points) 的左上角场景标题 uGUI 面板 —
        /// 场景名 (font×1.5) / 危险度★ / 状态行 三行; x/y = 面板左上角屏比 (0-1, 原点左下)。</summary>
        internal sealed class TitleZone
        {
            [JsonPropertyName("x")] public float X { get; set; } = 0.01f;
            [JsonPropertyName("y")] public float Y { get; set; } = 0.97f;
            [JsonPropertyName("font")] public float Font { get; set; } = 16f;
        }

        /// <summary>M3 战斗面板 (v1.25.0): 敌人名/描述/HP 格条 + 攻击/投掷/偷袭/逃跑按钮, 居中模态。</summary>
        internal sealed class CombatZone
        {
            [JsonPropertyName("x")] public float X { get; set; } = 0.5f;
            [JsonPropertyName("y")] public float Y { get; set; } = 0.55f;
            [JsonPropertyName("w")] public float W { get; set; } = 460f;
            [JsonPropertyName("btn_w")] public float BtnW { get; set; } = 100f;
            [JsonPropertyName("btn_h")] public float BtnH { get; set; } = 40f;
            [JsonPropertyName("font")] public float Font { get; set; } = 18f;
            [JsonPropertyName("title_font")] public float TitleFont { get; set; } = 24f;
            [JsonPropertyName("desc_font")] public float DescFont { get; set; } = 15f;
            [JsonPropertyName("spacing")] public float Spacing { get; set; } = 10f;
            [JsonPropertyName("pip_w")] public float PipW { get; set; } = 36f;
            [JsonPropertyName("pip_h")] public float PipH { get; set; } = 14f;
        }

        /// <summary>图元网格窗钉位 (只动位置/显隐, 结构恒定); ground/container 的 w/h = 网格格数;
        /// gap = 地面窗左缘与物品箱右缘间距 (像素, 仅 ground 区用)。
        /// mode (v1.30.0, 仅 ground 区用): box=贴物品箱右缘 (默认, 箱缺失回退 x/y 绝对钉位);
        /// free=永远按 x/y 绝对钉位 (M 键写回拖好的位置时自动置 free)。</summary>
        internal sealed class GridPin
        {
            [JsonPropertyName("x")] public float X { get; set; } = 0.5f;
            [JsonPropertyName("y")] public float Y { get; set; } = 0.5f;
            [JsonPropertyName("w")] public int W { get; set; } = 6;
            [JsonPropertyName("h")] public int H { get; set; } = 2;
            [JsonPropertyName("gap")] public float Gap { get; set; } = 8f;
            [JsonPropertyName("mode")] public string Mode { get; set; } = "box";
        }

        // ==================== v1.31.0: rt 实时动作战斗 UI 区 (默认值 = 原 RtCombatService 写死值) ====================

        /// <summary>rt 战斗 UI 布局: 敌情板/血条/互动区/受击箱/准星/预警扇形/弧环/底部板/逃跑/投掷/切换/状态行/键位。</summary>
        internal sealed class RtZone
        {
            [JsonPropertyName("top")] public RtBox Top { get; set; } = new RtBox { X = 0.5f, Y = 0.90f, W = 520f, H = 74f };
            [JsonPropertyName("enemy_hp")] public RtSize EnemyHp { get; set; } = new RtSize { W = 504f, H = 18f };
            [JsonPropertyName("arena")] public RtSize Arena { get; set; } = new RtSize { W = 660f, H = 420f };
            [JsonPropertyName("hitbox")] public RtBox Hitbox { get; set; } = new RtBox { X = 0.42f, Y = 0.58f, W = 190f, H = 210f };   // v1.36.0: 基准 220×240 → 190×210 (受击箱全局缩, M3.7 拍板)
            [JsonPropertyName("crosshair")] public RtCross Crosshair { get; set; } = new RtCross();
            [JsonPropertyName("warn")] public RtFx Warn { get; set; } = new RtFx { Size = 240f, R = 0.9f, G = 0.2f, B = 0.15f, A = 0.5f };
            [JsonPropertyName("arc")] public RtFx Arc { get; set; } = new RtFx { Size = 260f, R = 0.95f, G = 0.85f, B = 0.25f, A = 1f };
            [JsonPropertyName("bottom")] public RtBox Bottom { get; set; } = new RtBox { X = 0.5f, Y = 0.07f, W = 760f, H = 64f };
            [JsonPropertyName("flee")] public RtBtn Flee { get; set; } = new RtBtn { X = -230f, W = 180f, H = 44f };
            [JsonPropertyName("throw")] public RtBtn Throw { get; set; } = new RtBtn { X = -20f, W = 200f, H = 44f };
            [JsonPropertyName("switch")] public RtBtn Switch { get; set; } = new RtBtn { X = 210f, W = 220f, H = 44f };
            [JsonPropertyName("status")] public RtBox Status { get; set; } = new RtBox { X = 0.5f, Y = 0.135f, W = 420f, H = 24f };
            [JsonPropertyName("keys")] public RtKeys Keys { get; set; } = new RtKeys();
        }

        /// <summary>rt 矩形区: x/y=屏比中心点, w/h=像素。</summary>
        internal sealed class RtBox
        {
            [JsonPropertyName("x")] public float X { get; set; } = 0.5f;
            [JsonPropertyName("y")] public float Y { get; set; } = 0.5f;
            [JsonPropertyName("w")] public float W { get; set; } = 100f;
            [JsonPropertyName("h")] public float H { get; set; } = 40f;
        }

        /// <summary>rt 尺寸区 (仅宽高, 像素)。</summary>
        internal sealed class RtSize
        {
            [JsonPropertyName("w")] public float W { get; set; } = 100f;
            [JsonPropertyName("h")] public float H { get; set; } = 40f;
        }

        /// <summary>rt 底部板上的手动按钮: x=相对板中心像素偏移, w/h=像素。</summary>
        internal sealed class RtBtn
        {
            [JsonPropertyName("x")] public float X { get; set; }
            [JsonPropertyName("w")] public float W { get; set; } = 180f;
            [JsonPropertyName("h")] public float H { get; set; } = 44f;
        }

        /// <summary>rt 准星: size=贴图显示尺寸 (像素), speed=虚拟准星移动速度 (原 CrossSpeed=10)。</summary>
        internal sealed class RtCross
        {
            [JsonPropertyName("size")] public float Size { get; set; } = 48f;
            [JsonPropertyName("speed")] public float Speed { get; set; } = 10f;
        }

        /// <summary>rt 预警扇形/弧环: size=贴图显示尺寸 (像素), r/g/b/a=颜色 (0-1)。</summary>
        internal sealed class RtFx
        {
            [JsonPropertyName("size")] public float Size { get; set; } = 240f;
            [JsonPropertyName("r")] public float R { get; set; } = 1f;
            [JsonPropertyName("g")] public float G { get; set; } = 1f;
            [JsonPropertyName("b")] public float B { get; set; } = 1f;
            [JsonPropertyName("a")] public float A { get; set; } = 1f;
        }

        /// <summary>rt 键位 (KeyCode 名, 大小写容忍; 非法值告警回退默认)。
        /// dodge_*=四向闪避 (左/右/上/下), holster=非战斗收/出武器。</summary>
        internal sealed class RtKeys
        {
            [JsonPropertyName("dodge_left")] public string DodgeLeft { get; set; } = "D";
            [JsonPropertyName("dodge_right")] public string DodgeRight { get; set; } = "A";
            [JsonPropertyName("dodge_up")] public string DodgeUp { get; set; } = "S";
            [JsonPropertyName("dodge_down")] public string DodgeDown { get; set; } = "Space";
            [JsonPropertyName("holster")] public string Holster { get; set; } = "X";
        }

        // ==================== 基础解析 (单层; pss_test 可测) ====================

        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };

        /// <summary>纯解析 (pss_test 可测): 失败 = null + error; 缺字段保留默认。</summary>
        internal static RaidLayout Parse(string json, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json)) { error = "空内容"; return null; }
            try
            {
                var lo = JsonSerializer.Deserialize<RaidLayout>(json, Opts);
                if (lo == null) { error = "解析结果为空"; return null; }
                return lo;
            }
            catch (Exception e) { error = e.Message; return null; }
        }

        /// <summary>序列化默认布局 (文档/测试往返用)。</summary>
        internal static string DefaultText() => JsonSerializer.Serialize(new RaidLayout(), Opts);

        // ==================== v1.31.0: 多层深合并 (纯函数, pss_test 无头可测) ====================

        private static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> PropCache =
            new Dictionary<Type, Dictionary<string, PropertyInfo>>();

        private static Dictionary<string, PropertyInfo> PropsOf(Type t)
        {
            Dictionary<string, PropertyInfo> map;
            lock (PropCache)
            {
                if (PropCache.TryGetValue(t, out map)) return map;
                map = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead || !p.CanWrite) continue;
                    var a = p.GetCustomAttribute<JsonPropertyNameAttribute>();
                    map[a != null ? a.Name : p.Name] = p;
                }
                PropCache[t] = map;
            }
            return map;
        }

        /// <summary>把一层 JSON 逐区逐键合并进 target (就地改)。层解析失败 = 整层跳过 + error;
        /// 单键非法 (类型不符/int 键给小数) = warn + 保留 target 现值 (即下一层的值)。未知键忽略。</summary>
        internal static void MergeLayer(RaidLayout target, string layerJson, string label, Action<string> warn, out string error)
        {
            error = null;
            if (target == null || string.IsNullOrWhiteSpace(layerJson)) return;
            JsonObject layer;
            try
            {
                layer = JsonNode.Parse(layerJson) as JsonObject;
                if (layer == null) { error = "根不是 JSON 对象"; return; }
            }
            catch (Exception e) { error = e.Message; return; }
            string tag = string.IsNullOrEmpty(label) ? "层" : label;
            foreach (var kv in layer)
            {
                if (kv.Key == "_readme") continue;
                PropertyInfo prop;
                if (!PropsOf(typeof(RaidLayout)).TryGetValue(kv.Key, out prop)) continue;   // 未知区忽略
                if (prop.PropertyType == typeof(int))
                {
                    int iv;
                    if (TryInt(kv.Value, out iv)) prop.SetValue(target, iv);
                    else warn?.Invoke($"[layout] {tag}: '{kv.Key}' 非法值 (须整数), 保留下层值");
                    continue;
                }
                var zone = prop.GetValue(target);
                var zj = kv.Value as JsonObject;
                if (zone == null) continue;
                if (zj == null) { warn?.Invoke($"[layout] {tag}: 区 '{kv.Key}' 不是对象, 保留下层值"); continue; }
                MergeZone(zone, zj, tag + "." + kv.Key, warn);
            }
        }

        private static void MergeZone(object zone, JsonObject layer, string path, Action<string> warn)
        {
            var props = PropsOf(zone.GetType());
            foreach (var kv in layer)
            {
                PropertyInfo prop;
                if (!props.TryGetValue(kv.Key, out prop)) continue;   // 未知键忽略 (前向兼容)
                var pt = prop.PropertyType;
                if (pt == typeof(float))
                {
                    double d;
                    if (TryNum(kv.Value, out d)) prop.SetValue(zone, (float)d);
                    else warn?.Invoke($"[layout] {path}.{kv.Key} 非法值 (须数字), 保留下层值");
                }
                else if (pt == typeof(int))
                {
                    int iv;
                    if (TryInt(kv.Value, out iv)) prop.SetValue(zone, iv);
                    else warn?.Invoke($"[layout] {path}.{kv.Key} 非法值 (须整数), 保留下层值");
                }
                else if (pt == typeof(string))
                {
                    string s;
                    if (kv.Value is JsonValue jv && jv.TryGetValue(out s)) prop.SetValue(zone, s);
                    else warn?.Invoke($"[layout] {path}.{kv.Key} 非法值 (须字符串), 保留下层值");
                }
                else if (pt.IsClass)
                {
                    var sub = kv.Value as JsonObject;
                    if (sub == null) { warn?.Invoke($"[layout] {path}.{kv.Key} 非法值 (须对象), 保留下层值"); continue; }
                    var subObj = prop.GetValue(zone);
                    if (subObj == null)
                    {
                        try { subObj = Activator.CreateInstance(pt); prop.SetValue(zone, subObj); }
                        catch { continue; }
                    }
                    MergeZone(subObj, sub, path + "." + kv.Key, warn);
                }
            }
        }

        private static bool TryNum(JsonNode n, out double d)
        {
            d = 0;
            return n is JsonValue jv && jv.TryGetValue(out d);
        }

        private static bool TryInt(JsonNode n, out int i)
        {
            i = 0;
            double d;
            if (!TryNum(n, out d)) return false;
            if (d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue) return false;
            i = (int)d;
            return true;
        }

        /// <summary>多层合并解析: 代码默认 → layers 顺序叠加 (后者胜)。每层解析失败跳过 + warn;
        /// ground.mode 收尾归一 (非法值 warn 回退 box)。绝不返回 null。纯函数。</summary>
        internal static RaidLayout ResolveLayers(IList<string> layers, IList<string> labels, Action<string> warn)
        {
            var lo = new RaidLayout();
            if (layers != null)
                for (int i = 0; i < layers.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(layers[i])) continue;
                    string label = labels != null && i < labels.Count ? labels[i] : "层" + (i + 1);
                    string err;
                    MergeLayer(lo, layers[i], label, warn, out err);
                    if (err != null) warn?.Invoke($"[layout] {label} 解析失败, 整层跳过: {err}");
                }
            var nm = NormalizeGroundMode(lo.Ground.Mode);
            if (!string.Equals(nm, lo.Ground.Mode, StringComparison.Ordinal))
            {
                warn?.Invoke($"[layout] ground.mode '{lo.Ground.Mode}' 无法识别 (可选 box/free), 回退 \"box\"");
                lo.Ground.Mode = nm;
            }
            return lo;
        }

        // ==================== 运行期解析 (包文件经 Plugin 接线委托读取) ====================

        /// <summary>packId → 包级 scenes/layout.json 文本 (无文件/失败 = null)。Plugin 接线。</summary>
        internal static Func<string, string> PackLayoutTextOf = null;
        /// <summary>(packId, sceneId) → 场景级 scenes/&lt;id&gt;/layout.json 文本 (无文件/失败 = null)。Plugin 接线。</summary>
        internal static Func<string, string, string> SceneLayoutTextOf = null;
        /// <summary>有效包 id 按加载序 (picker 区多包合并用)。Plugin 接线。</summary>
        internal static Func<List<string>> PackLoadOrder = null;
        /// <summary>packId → 包物理根目录 (M 写回目标路径用; 内嵌包/未找到 = null)。Plugin 接线。</summary>
        internal static Func<string, string> PackDirOf = null;
        /// <summary>v1.48.8: (packId, sceneId) → 用户覆写层 layout_overrides/&lt;pack&gt;/&lt;sceneId&gt;.json 文本
        /// (物理文件, 与包形态无关; 无文件/失败 = null)。Plugin 接线 ReadUserOverrideText。</summary>
        internal static Func<string, string, string> UserLayoutTextOf = null;

        // ---- v1.48.8: 用户覆写层 (UserData/PSApi/layout_overrides/) ----

        /// <summary>用户覆写层根目录。</summary>
        internal static string UserOverrideRoot => Path.Combine(PsApi.RootDir, "layout_overrides");

        /// <summary>场景级用户覆写文件路径 (packId/sceneId 非空才合法, 调用方保证)。</summary>
        internal static string UserOverridePath(string packId, string sceneId)
            => Path.Combine(UserOverrideRoot, packId, sceneId + ".json");

        /// <summary>读用户覆写层文本 (物理文件; 无文件/失败 = null)。</summary>
        internal static string ReadUserOverrideText(string packId, string sceneId)
        {
            try
            {
                string p = UserOverridePath(packId, sceneId);
                return File.Exists(p) ? File.ReadAllText(p) : null;
            }
            catch { return null; }
        }

        /// <summary>M 写回目标路径决策 (纯函数, pss_test 可测): 文件夹包 = 包内场景级 layout.json
        /// (junction 入口写穿落开发主库, 有意为之); 内嵌包 (packDir=null) = 用户覆写层文件。</summary>
        internal static string WriteBackPathFor(string packId, string sceneId, string packDir)
            => string.IsNullOrEmpty(packDir)
                ? UserOverridePath(packId, sceneId)
                : Path.Combine(packDir, "scenes", sceneId, "layout.json");

        /// <summary>按场景解析: 默认 + 包级 + 场景级 + 用户覆写 (sceneId=null = 默认+包级; v1.48.8 第四层)。纯读取, 每次新解析。</summary>
        internal static RaidLayout Resolve(string packId, string sceneId, Action<string> warn)
        {
            var layers = new List<string>();
            var labels = new List<string>();
            if (!string.IsNullOrEmpty(packId))
            {
                string t = null;
                try { t = PackLayoutTextOf?.Invoke(packId); } catch (Exception e) { warn?.Invoke("[layout] 包级读取异常: " + e.Message); }
                if (t != null) { layers.Add(t); labels.Add("包级 " + packId); }
                if (!string.IsNullOrEmpty(sceneId))
                {
                    string t2 = null;
                    try { t2 = SceneLayoutTextOf?.Invoke(packId, sceneId); } catch (Exception e) { warn?.Invoke("[layout] 场景级读取异常: " + e.Message); }
                    if (t2 != null) { layers.Add(t2); labels.Add("场景级 " + packId + ":" + sceneId); }
                    string t3 = null;
                    try { t3 = UserLayoutTextOf?.Invoke(packId, sceneId); } catch (Exception e) { warn?.Invoke("[layout] 用户覆写读取异常: " + e.Message); }
                    if (t3 != null) { layers.Add(t3); labels.Add("用户覆写 " + packId + ":" + sceneId); }
                }
            }
            return ResolveLayers(layers, labels, warn);
        }

        /// <summary>无场景上下文 (picker 区): 代码默认 + 全部包级 layout.json 按包加载序合并 (后者胜)。</summary>
        internal static RaidLayout ResolvePickerLayout(Action<string> warn)
        {
            var layers = new List<string>();
            var labels = new List<string>();
            List<string> order = null;
            try { order = PackLoadOrder?.Invoke(); } catch { }
            if (order != null)
                foreach (var id in order)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    string t = null;
                    try { t = PackLayoutTextOf?.Invoke(id); } catch { }
                    if (t != null) { layers.Add(t); labels.Add("包级 " + id); }
                }
            return ResolveLayers(layers, labels, warn);
        }

        /// <summary>当前活动场景布局缓存 (进/出场景由 SceneService 刷新; 无场景 = 代码默认)。</summary>
        internal static RaidLayout Active { get; private set; } = new RaidLayout();

        /// <summary>刷新当前活动场景布局 (SceneService 进/出场景 + Plugin F10 调用);
        /// packId=null = 无场景回退代码默认。返回新生效布局 (绝不 null)。</summary>
        internal static RaidLayout RefreshActive(string packId, string sceneId, MelonLogger.Instance logger)
        {
            try
            {
                Active = Resolve(packId, sceneId, w => PsApi.Warn(logger, w));
                if (packId != null)
                    PsApi.Log(logger, $"[layout] 当前场景布局已解析 ({packId}:{sceneId ?? "-"}{(LayersNote(packId, sceneId))})");
            }
            catch (Exception e)
            {
                PsApi.Warn(logger, "[layout] 布局解析异常, 回退默认: " + e.Message);
                Active = new RaidLayout();
            }
            return Active;
        }

        private static string LayersNote(string packId, string sceneId)
        {
            // 诊断: 实际叠了哪几层 (包级/场景级/用户覆写文件存在与否)
            bool pk = false, sc = false, uo = false;
            try { pk = PackLayoutTextOf?.Invoke(packId) != null; } catch { }
            try { sc = !string.IsNullOrEmpty(sceneId) && SceneLayoutTextOf?.Invoke(packId, sceneId) != null; } catch { }
            try { uo = !string.IsNullOrEmpty(sceneId) && UserLayoutTextOf?.Invoke(packId, sceneId) != null; } catch { }
            return ", 层=默认" + (pk ? "+包级" : "") + (sc ? "+场景级" : "") + (uo ? "+用户覆写" : "");
        }

        /// <summary>v1.31.0: 旧全局布局文件 UserData/PSApi/raid_layout.json 已废弃 — 启动检测打迁移提示。</summary>
        internal static void CheckLegacyGlobalFile(MelonLogger.Instance logger)
        {
            try
            {
                string path = Path.Combine(PsApi.RootDir, "raid_layout.json");
                if (File.Exists(path))
                    PsApi.Warn(logger, "[layout] 检测到旧全局布局文件 " + path + " — v1.31.0 起已废弃 (框架不再读取); " +
                        "请把内容迁入包级 packs/<pack>/scenes/layout.json 或场景级 packs/<pack>/scenes/<id>/layout.json");
            }
            catch { }
        }

        // ---- 使用端钳制 (防 JSON 写离谱值炸布局) ----

        internal static int ClampLines(int lines) => lines < 1 ? 1 : lines > 30 ? 30 : lines;
        internal static int ClampGrid(int cells) => cells < 1 ? 1 : cells > 12 ? 12 : cells;
        internal static float ClampFont(float font) => font < 8f ? 8f : font > 48f ? 48f : font;
        /// <summary>几何尺寸钳制 (像素, rt 区等): 1-2000。</summary>
        internal static float ClampSize(float v) => v < 1f ? 1f : v > 2000f ? 2000f : v;
        /// <summary>颜色通道钳制 0-1。</summary>
        internal static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        // ==================== v1.30.0: M 键写回 (纯逻辑, pss_test 无头可测) ====================

        /// <summary>ground.mode 归一: null/空/无法识别 → "box"; 大小写/空白容忍。纯函数。</summary>
        internal static string NormalizeGroundMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return "box";
            var m = mode.Trim().ToLowerInvariant();
            return m == "free" ? "free" : "box";
        }

        /// <summary>屏比钳制 0-1 (M 写回坐标用)。纯函数。</summary>
        internal static float ClampRel(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        /// <summary>M 键写回: 把 sections (区名 → 键值表) 合并进布局 JSON 文本, 其它键原样保留
        /// (JsonNode 只改出现的键); float 值 = 屏比, 钳 0-1 后保留 3 位小数。失败 = null + error。</summary>
        internal static string MergePinsJson(string json, Dictionary<string, Dictionary<string, object>> sections, out string error)
        {
            error = null;
            try
            {
                JsonObject root;
                if (string.IsNullOrWhiteSpace(json)) root = new JsonObject();
                else
                {
                    root = JsonNode.Parse(json) as JsonObject;
                    if (root == null) { error = "布局文件根不是 JSON 对象"; return null; }
                }
                if (sections != null)
                    foreach (var sec in sections)
                    {
                        if (string.IsNullOrEmpty(sec.Key) || sec.Value == null || sec.Value.Count == 0) continue;
                        if (!(root[sec.Key] is JsonObject so))
                        {
                            so = new JsonObject();
                            root[sec.Key] = so;
                        }
                        foreach (var kv in sec.Value)
                        {
                            if (kv.Value == null) continue;
                            switch (kv.Value)
                            {
                                case string s: so[kv.Key] = s; break;
                                case float f: so[kv.Key] = Math.Round((double)ClampRel(f), 3); break;
                                case double d: so[kv.Key] = Math.Round((double)ClampRel((float)d), 3); break;
                                case int i: so[kv.Key] = i; break;
                                case long l: so[kv.Key] = l; break;
                                case bool b: so[kv.Key] = b; break;
                            }
                        }
                    }
                return root.ToJsonString(Opts);
            }
            catch (Exception e) { error = e.Message; return null; }
        }
    }
}
