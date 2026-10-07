using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using PSApi.Events.PsScript;
using PSApi.Events.UI;
using PSApi.Items;
using UnityEngine;
using UnityEngine.UI;

namespace PSApi.Events.Scenes
{
    /// <summary>rt 武器解析结果 (ScriptCombatService.ResolveRtWeapon 产物)。
    /// MeleeClass = 近战类 (近战/枪托/枪抡/拳头): 预警加速 + 出手硬直 + 固定挥击 CD;
    /// 枪械射击态才有 Mode (1单发/2连发) / Rate (RPM) / Stab (卡壳+抖动)。
    /// v1.34.0: 六维全消费 (§3.10) — Pen 破甲 / Rng 距离带 / Rad 多目标; Wtype=melee 强制近带。</summary>
    internal sealed class RtWeapon
    {
        internal string Name = "拳头";
        internal bool IsGun;
        internal bool Unarmed;
        internal bool MeleeClass = true;
        internal bool Gunstock;
        internal float Atk;                 // Unarmed = 0 → 每击 2-5 随机
        internal int Stab = 100;
        internal int Mode = 1;
        internal int Rate = 300;
        internal int Pen;                   // w_pen 0-100 (破甲, §3.10.3)
        internal int Rng = 30;              // w_rng (距离带适性, §3.10.2)
        internal int Rad = 1;               // w_rad ≥1 (多目标+判定放宽, §3.10.3)
        internal float Stun;                // v1.36.0: w_stun 麻痹秒数 (钳 0-3, 缺省 0; 仅枪械射击态透出)
        internal string Wtype = "melee";    // MeleeCanReach 判定: 射击态=gun, 其余=melee
        internal GameItem Source;           // v1.43.0: 来源物品 (耐久扣减用; 拳头/回退 = null)
    }

    /// <summary>v1.34.0 cfg behaviors 卡组条目 (§3.10.1): type + weight + 行为级参数 (strafe speed/amplitude 等)。</summary>
    internal sealed class RtBehavior
    {
        internal string Type = "strafe";
        internal float Weight = 1f;
        internal Dictionary<string, object> Params;
    }

    /// <summary>v1.34.0 cfg shield 护盾参数 (解析后已钳制, §3.10.1)。</summary>
    internal sealed class RtShieldCfg
    {
        internal int Dots = 3;
        internal float Reduction = 0.7f;
        internal float MovingRatio = 0.3f;
        internal float TeleportRatio = 0.2f;
        internal float RedWindow = 3f;
    }

    /// <summary>v1.34.0 cfg charge 冲锋参数 (§3.10.1): stagger_mul 乘击退阈值, heavy_mul 乘重击伤害。</summary>
    internal sealed class RtChargeCfg
    {
        internal float Duration = 2f;
        internal float StaggerMul = 1f;
        internal float HeavyMul = 2f;
    }

    /// <summary>v1.36.0 cfg swarm 鼠群参数 (解析后已钳制, M3.7):
    /// 主受击箱隐藏, 生成 count 个子箱 (尺寸=布局基准×box_scale×hitbox_scale) 绕基准点 zigzag 自走 (spread 约束),
    /// 共享 HP 池; 与 shield 互斥 (配了 shield 忽略 swarm)。v1.46.0: +speed 子箱移速 (120-450, 缺省 240 = 旧行为)。</summary>
    internal sealed class RtSwarmCfg
    {
        internal int Count = 3;
        internal float BoxScale = 0.4f;
        internal float Spread = 170f;
        internal float Speed = 240f;                            // v1.46.0: 子箱移速个体化 (旧恒 SwarmSpeedBase)
    }

    /// <summary>v1.45.0 cfg summon 嚎叫者召唤参数 (解析后已钳制, §4.5):
    /// 母体受击箱正常可攻, 每 interval 秒召 1 个小怪受击箱 (同屏 ≤cap), 子箱独立 HP 打死即灭;
    /// 母体攻击命中玩家时每个存活小怪 +child_atk 撕咬。母体死 = 战斗 win = 小怪全灭。</summary>
    internal sealed class RtSummonCfg
    {
        internal float Interval = 4f;
        internal int Cap = 3;
        internal int ChildHp = 10;
        internal float BoxScale = 0.35f;
        internal float Spread = 180f;
        internal int ChildAtk = 2;
    }

    /// <summary>v1.36.0 zigzag 折线游走器 (纯托管 float + System.Random, 零 Unity 依赖, pss_test 无头可测):
    /// 8 向均匀随机变向 (间隔 [turnLo,turnHi] 均匀随机), 偏移钳在基准点 ±bound 矩形内, 触边反弹;
    /// 主受击箱 zigzag 行为与鼠群子箱自走共用 (每实例独立 _rng 序列 = 独立种子)。</summary>
    internal sealed class ZigWalker
    {
        // 8 向单位向量 (对角已归一)
        private static readonly float[] Dir8x = { 1f, 0.7071f, 0f, -0.7071f, -1f, -0.7071f, 0f, 0.7071f };
        private static readonly float[] Dir8y = { 0f, 0.7071f, 1f, 0.7071f, 0f, -0.7071f, -1f, -0.7071f };
        private readonly System.Random _rng;
        private readonly float _speed, _bound, _turnLo, _turnHi;
        private float _dx = 1f, _dy, _nextTurn;
        internal float Ox, Oy;                            // 当前相对基准点偏移 (px)

        internal ZigWalker(System.Random rng, float speed, float bound, float turnLo, float turnHi, bool randStart)
        {
            _rng = rng;
            _speed = speed;
            _bound = bound < 0f ? 0f : bound;
            _turnLo = Math.Min(turnLo, turnHi);
            _turnHi = Math.Max(turnLo, turnHi);
            RollDir();
            _nextTurn = RollTurn();
            if (randStart)
            {
                Ox = ((float)_rng.NextDouble() * 2f - 1f) * _bound;
                Oy = ((float)_rng.NextDouble() * 2f - 1f) * _bound;
            }
        }

        private float RollTurn() => _turnLo + (float)_rng.NextDouble() * (_turnHi - _turnLo);

        private void RollDir()
        {
            int i = _rng.Next(8);
            _dx = Dir8x[i];
            _dy = Dir8y[i];
        }

        /// <summary>推进 dt 秒: 到点变向 (8 向均匀随机) + 匀速移动 + ±bound 矩形钳制触边反弹。</summary>
        internal void Tick(float dt)
        {
            if (dt <= 0f) return;
            _nextTurn -= dt;
            if (_nextTurn <= 0f) { RollDir(); _nextTurn = RollTurn(); }
            float nx = Ox + _dx * _speed * dt;
            float ny = Oy + _dy * _speed * dt;
            if (nx > _bound) { nx = _bound; _dx = -Math.Abs(_dx); }
            else if (nx < -_bound) { nx = -_bound; _dx = Math.Abs(_dx); }
            if (ny > _bound) { ny = _bound; _dy = -Math.Abs(_dy); }
            else if (ny < -_bound) { ny = -_bound; _dy = Math.Abs(_dy); }
            Ox = nx;
            Oy = ny;
        }
    }

    /// <summary>
    /// v1.29.0 实时动作战斗会话服务 — combat.rt_start 的游戏侧后端 (方向 QTE)。
    /// v1.35.0 (2026-09-30 拍板): 经典按钮面板战斗已删, rt 是唯一实现。
    /// 要点:
    /// - 状态机: Idle → Active { 敌节奏器 2.2-3.5s → WARNING(方向, 1.2s/近战0.96s) → RESOLVE(闪避分档) };
    ///   Plugin.OnUpdate 每帧 Tick 驱动 (输入轮询/预警推进/准星跟随+正弦抖动/扇形闪烁+弧环填充)。
    /// - 战斗数学全部 RaidCore 纯函数 (闪避分档/伤害/卡壳/CD/抖动/预警/节奏) — 单一事实源, 无头可测。
    /// - 光标: IncrementMouseFlag 配对 (ISIL 实证: flag&gt;0 → Cursor.visible=false + lockState=Locked)。
    ///   Locked 下 Input.mousePosition 冻结 + uGUI Button 不可点 → 虚拟准星 (Mouse X/Y 轴增量积分,
    ///   轴不可用降级 mousePosition) + 全部点击手动 rect 判定 (RectangleContainsScreenPoint)。
    /// - 战斗锁: 进战斗箱窗+地面窗 Hide, 结束 Show 配对; 全屏暗幕 raycast=true 拦穿透。
    /// - sprite 程序化三件套 (十字准星/扇形/弧环, SetPixels32→Apply→Sprite.Create,
    ///   原版 CustomUIManager.GetHoldRingSprite 范式); 准星可被包级 ui/crosshair.png 覆盖。
    /// - 结束路径 (全部光标配对 Decrement + 箱/地窗 Show): win(敌HP0) / flee(逃跑成功) /
    ///   lose(玩家HP0) / abort(场景撤离, 不回调); OnSceneLeft 仅清引用+光标兜底。
    /// - X 键: 非战斗收/出武器 (收着 = 战斗内只能抡拳头); 战斗中无效 (拍板 2)。
    /// - v1.31.0: UI 全面配置化 — BuildUi 几何/颜色/准星速度/四向闪避+收武器键位全部走当前场景布局
    ///   rt 区 (RaidLayout.Active.Rt, 三层覆写; 开战 SnapshotLayout 截快照, F10 战斗中重建 UI);
    ///   键位 = KeyCode 名 (非法告警回退默认 D/A/S/Space/X)。
    /// - v1.30.0: 玩家 HP 变化实时回调 cfg on_hp_change (状态栏同步, 战斗面板 HP 文字删除);
    ///   结束载荷加 sp_cost (RtSpCost 时长×0.6, pss 扣体力); 底部投掷按钮 (s_throw1-3 实时查槽,
    ///   必中 w_atk×2, 次数扣 1 否则整件消耗, 手动 rect 判定+0.3s 去抖+0.5s 近战 CD 动作锁)。
    /// - v1.34.0 (M3.6 §3.10): 敌人行为状态机 (卡组加权抽 游走/伏身/掩蔽/护盾态/冲锋,
    ///   同种冷却 2 间隔; 破绽=完美闪避触发 overlay) + 交战距离带 (近/中/远, rng 短缺
    ///   ×0.7^n 伤害 ×0.85^n 受击箱) + 破甲 (ArmorEff) + rad 多目标 (80px 内绿点 ≤rad-1,
    ///   判定放宽 +rad×2px); cfg hp 改直接 HP 值 (≤10 回退旧 ×15); cfg 新键
    ///   armor/band/behaviors/shield/charge 全可选; 公式全落 RaidCore 纯函数。
    /// - v1.34.2 (rt 实测修正): ① 近战类 (近战/枪托/拳头) 不再距离锁死 — 吃 BandShortfall 衰减
    ///   (有效带=敌攻击窗口近带 sf 0 全额, 窗口外按真实带 ×0.7^n); ② 弹药提示强化 — 状态行枪械常显
    ///   弹药余量 (无弹红字「无弹! 抡枪托」), 开战有枪无弹大红字警告, 枪托命中日志「你抡起 X (枪托)」;
    ///   ③ 准星轴 3s 自检日志 (排 Mouse X/Y 轴缺失零灵敏度冻结嫌疑) + 开战日志补 crossSpeed/分辨率。
    /// - v1.36.0 (M3.7 战斗体验升级): ① 受击箱个体化 hitbox_scale (钳 0.5-1.3, 与布局基准/行为倍率叠乘)
    ///   + 布局 rt.hitbox 基准 220×240 → 190×210 (全局缩); ② 新行为 5 种 dash 突进/zigzag 折线
    ///   (ZigWalker 8 向游走 ±bound 反弹, 鼠群子箱复用)/circle 椭圆轨道/leap 纵跳 (滞空箱缩)/feint 佯攻
    ///   (钩 StartWarning: 掷 cancel_chance, 中则预警 50% 撤掉+横跳 120-200px+下次攻击提前 0.8-1.4s)
    ///   + strafe 相位噪声 (noise 参数, 缺省 0=旧正弦原样); ③ 麻痹 w_stun (RtWeapon.Stun, 命中后
    ///   节奏器暂停+撤预警+打断冲锋, 状态行「麻痹!」); ④ depth 深度难度 (v1.44.0: 0-8, hp/atk ×(1+0.10×d));
    ///   ⑤ 鼠群 swarm (主箱隐藏, count 子箱 zigzag 自走共享 HP, rad 波及每个 +50%, 被击闪白 0.15s,
    ///   与 shield 互斥); ⑥ warn_mul 预警时长个体化 (0.7-1.6 乘算); 公式全落 RaidCore 纯函数。
    /// - v1.45.0 (gunworks v0.40.0 §4.5/§4.6): ① 嚎叫者 summon (cfg {interval/cap/child_hp/box_scale/
    ///   spread/child_atk}) — 母体受击箱正常可攻, 每 interval 秒召 1 小怪受击箱 (同屏 ≤cap, 独立 HP
    ///   打死即灭, 复用 ZigWalker 自走+闪白基建), 母体攻击命中玩家时每存活小怪 +child_atk 撕咬,
    ///   母体死=win=小怪全清; ② 掠窃者 escape_after (秒, 钳 0-60, 0=off) — Tick 到点
    ///   EndCombat("escape") (新 result 值透传 on_end, 赃物处置由 pss 编排侧负责)。
    /// </summary>
    internal sealed class RtCombatService
    {
        private readonly MelonLogger.Instance _logger;
        private readonly List<object> _pins = new List<object>();
        private readonly UguiBuilder _ugui;
        private readonly System.Random _rng = new System.Random();

        internal SceneService Scenes;                    // Plugin 接线
        internal ScriptGridService Grid;                 // Plugin 接线 (战斗锁)
        internal SceneLogService LogSvc;                 // Plugin 接线 (反馈全走 scene.log)
        internal ScriptCombatService Combat;             // Plugin 接线 (武器解析/弹药)
        internal Func<string, IPackSource> PackSourceOf; // Plugin 接线 (准星 PNG 覆盖)

        private const float CrossSpeedDefault = 10f;       // 虚拟准星速度默认 (布局 rt.crosshair.speed 可改)
        private float _crossSpeed = CrossSpeedDefault;
        // v1.31.0: 四向闪避/收武器键位走布局 rt.keys (KeyCode 名, 非法告警回退默认)
        private readonly KeyCode[] _dodgeKeys = { KeyCode.D, KeyCode.A, KeyCode.S, KeyCode.Space };
        private readonly string[] _dodgeKeyNames = { "D", "A", "S", "Space" };
        private KeyCode _holsterKey = KeyCode.X;
        // v1.31.0: rt 区布局快照 (RtStart/F10 Relayout 时从当前场景布局截取)
        private Raid.RaidLayout.RtZone _rt = new Raid.RaidLayout.RtZone();
        private Color _warnColor = new Color(0.9f, 0.2f, 0.15f, 0.5f);
        private Color _arcColor = new Color(0.95f, 0.85f, 0.25f, 1f);
        private static readonly string[] DirNames = { "左", "右", "上", "下" };
        // 扇形贴图原点在底中朝正上 → 各方向 z 旋转
        private static readonly float[] FanRot = { -90f, 90f, 180f, 0f };
        private static readonly float[] FanRelX = { 0.005f, 0.995f, 0.5f, 0.5f };
        private static readonly float[] FanRelY = { 0.55f, 0.55f, 0.99f, 0.13f };
        // 弧环 Radial360: origin (Bottom=0/Right=1/Top=2/Left=3) + clockwise, 由角向内扫半环
        private static readonly int[] ArcOrigin = { 0, 0, 3, 1 };
        private static readonly bool[] ArcClockwise = { false, true, false, false };

        // ---- 会话状态 ----
        private enum Phase { Idle, Active }
        private Phase _phase = Phase.Idle;
        private string _enemyId = "enemy", _enemyName = "敌人", _atkRange = "3-6";
        private int _enemyHp, _enemyHpMax = 1;
        private int _playerHp = 100, _fleeChance = 50;
        private bool _sneakPending;
        private Interpreter _itp;
        private PsCallable _onEnd;
        private PsCallable _onHpChange;                       // v1.30.0: cfg on_hp_change (HP 变化实时回调)
        private float _combatT0 = -1f;                        // v1.30.0: 开战时刻 (结束 sp_cost 体力结算)

        // ---- 玩家侧 ----
        private RtWeapon _w = new RtWeapon();
        private bool _preferMelee;
        private bool _weaponDrawn = true;                 // X 收/出武器 (跨战斗保持)
        private float _nextFireAt = -1f, _jamLockUntil = -1f;
        private float _meleeCdUntil = -1f, _meleeStunUntil = -1f;
        private Vector2 _crossPos;                        // 虚拟准星屏幕坐标
        private Vector2 _crossJit;                        // v1.34.3: 准星抖动偏移 (命中判定吃视觉位置 = 所见即所得)
        private bool _missDiagDone;                       // v1.34.3: 首次打偏打坐标诊断 (一场一次)
        private bool _axisProbed, _axisOk = true;
        // v1.34.2: 准星轴 3s 自检 (排冻结嫌疑: 轴缺失时 GetAxis 返回 0 不抛异常 → axisOk=true 但准星钉死屏心)
        private float _axisAccum, _axisCheckAt = -1f;
        private bool _axisChecked;

        // ---- 敌攻击调度 ----
        private bool _warnActive;
        private int _warnDir;
        private float _warnT0, _warnDur = 1.2f;
        private float _nextAtkAt;
        private float _lastDodgeAt = -999f;
        private float _warnHeavyMul = 1f;                     // v1.34.0: 冲锋完成后的重击倍率 (本次 RESOLVE 生效)

        // ---- v1.34.0 行为状态机 (§3.10.1) ----
        private enum BehState { None, Strafe, Shrink, Hide, ShieldDots, ShieldRed, RedDot, Charge, Stagger, Dash, Zigzag, Circle, Leap, Feint }
        private BehState _beh = BehState.None;
        private float _behT0, _behUntil = -1f;                // 当前行为起止 (Charge/Strafe/Shrink/Hide/Stagger 用)
        private float _behNextAt = -1f;                       // 下一次抽行为时刻 (None 期间)
        private readonly Queue<string> _behHist = new Queue<string>();  // 同种冷却: 最近 2 次行为不再连出
        private List<RtBehavior> _deck;                       // cfg behaviors (null/空 = 现状静止)
        private RtShieldCfg _shieldCfg = new RtShieldCfg();
        private RtChargeCfg _chargeCfg = new RtChargeCfg();
        private int _armor;                                   // cfg armor 0-100
        private int _band = 1;                                // 当前距离带 0近1中2远 (行为可改)
        private int _lastBandShown = -1;                      // 敌名旁 近/中/远 变更才重写
        // strafe 参数快照 / hide 掩蔽计时 / charge 累计
        private float _strafeAmp = 80f, _strafeSpeed = 2f;
        private float _chargeAccum;                           // 冲锋期间累计受击 (≥阈值 → stagger)
        private float _exposeUntil = -1f, _weakenUntil = -1f; // 破绽/虚弱 overlay (与主状态独立, 乘算)
        // ---- v1.36.0 M3.7 新增行为/机制字段 ----
        private float _hitboxScale = 1f;                      // cfg hitbox_scale (受击箱个体化, RaidCore.HitboxScale 钳)
        private int _depth;                                   // cfg depth 0-8 (v1.44.0; DepthMul 开战结算 hp/atk)
        private float _warnMul = 1f;                          // cfg warn_mul (预警时长个体化)
        private float _tempo = 1f;                            // v1.37.0: cfg tempo (敌节奏个体化, 只乘行为间隙)
        private float _stunUntil = -1f;                       // 麻痹截止 (w_stun; 期间节奏器暂停+撤预警)
        private RtSwarmCfg _swarmCfg;                         // cfg swarm (null=无; 与 shield 互斥)
        private float _strafeNoise, _strafePhase;             // strafe 相位噪声 (noiseRate, 缺省 0=旧正弦原样)
        // dash/feint 共用横冲位移段 (ease-out; 多段 dash 链式, 完成保持末位置到下个行为)
        private bool _burstActive;
        private float _burstT0, _burstDur;
        private Vector2 _burstFrom, _burstTo;
        private float _dashDist = 200f, _dashDur = 0.3f;      // dash 行为参数快照
        private int _dashSegLeft;                             // dash 剩余段数 (count-1)
        private ZigWalker _zig;                               // zigzag 主箱游走器
        private ZigWalker _ambient;                           // v1.37.0: 空窗微漂移游走器 (低速, ResetBehavior 重建)
        private float _circleSpeed = 2.2f, _circleRx = 150f, _circleRy = 70f;   // circle 参数快照
        private float _leapHeight = 140f;                     // leap 参数快照
        private float _feintCancelChance = 0.35f;             // feint 参数快照
        private float _feintCancelAt = -1f;                   // feint 假动作触发时刻 (预警 50% 处)
        // 鼠群子箱 (UI 引用随 _go 销毁, 游走器/逻辑态保留供 F10 重建; 共享 HP 池, 被击闪白抄 dots FlashUntil)
        private sealed class SwarmBox
        {
            internal RectTransform Rt; internal Image Img;
            internal ZigWalker Walker;
            internal float FlashUntil = -1f;
        }
        private readonly List<SwarmBox> _swarmBoxes = new List<SwarmBox>();
        private float _lastVisualTick = -1f;                  // ApplyHitboxVisual dt 源 (zigzag/相位噪声推进)
        private static Color SwarmBoxColor => new Color(0.55f, 0.45f, 0.35f, 0.30f);
        // ---- v1.45.0 嚎叫者召唤 (§4.5) / 掠窃者逃逸 (§4.6) ----
        private RtSummonCfg _summonCfg;                       // cfg summon (null=无召唤)
        private float _escapeAfter;                           // cfg escape_after 秒 (0=off, 到点 EndCombat("escape"))
        // 召唤小怪受击箱 (独立 HP 打死即灭; 母体受击箱不隐藏, 与鼠群共享 HP 池语义不同)
        private sealed class SummonBox
        {
            internal RectTransform Rt; internal Image Img;
            internal ZigWalker Walker;
            internal float FlashUntil = -1f;
            internal int Hp;
            internal bool Dead;
        }
        private readonly List<SummonBox> _summonBoxes = new List<SummonBox>();
        private float _nextSummonAt = -1f;                    // 下一次召唤时刻
        private static Color SummonBoxColor => new Color(0.50f, 0.30f, 0.38f, 0.30f);
        // 护盾动态目标 (逻辑态与 UI 分离: F10 Relayout 后可按逻辑态重建)
        private sealed class ShieldDot
        {
            internal RectTransform Rt; internal Image Img;
            internal bool Alive = true, Mover, Teleporter;
            internal float Size = 24f, Phase, Speed = 1.2f, NextTeleportAt;
            internal float BaseX, BaseY;                      // 相对受击箱基准中心 (root 锚点坐标)
            internal int HitsLeft = 1;                        // v1.34.1 拍板②: 剩余击数 (生成时按尺寸定, 归零才碎)
            internal float FlashUntil = -1f;                  // 命中未碎闪白截止 (视觉反馈)
        }
        private readonly List<ShieldDot> _dots = new List<ShieldDot>();
        private RectTransform _redDot;
        private float _redUntil = -1f;
        private int _dotsTotal;                               // 「护盾中 剩/总」状态行用
        // 注: 不用 static readonly Color 字段 — 静态构造会触发 UnityEngine.Color.cctor (Il2Cpp),
        //     破坏 pss_test 无头静态解析 (ParseBehaviors 等); 属性每次 new, 无静态初始化
        private static Color DotGreen => new Color(0.30f, 0.85f, 0.35f, 0.95f);
        private static Color DotRed => new Color(0.95f, 0.15f, 0.10f, 0.98f);
        private static Color HitboxNormal => new Color(0.6f, 0.2f, 0.15f, 0.18f);
        private static Color HitboxShield => new Color(0.45f, 0.45f, 0.48f, 0.28f);
        private static Color HitboxWeaken => new Color(0.85f, 0.70f, 0.20f, 0.30f);

        // ---- UI 引用 ----
        private GameObject _go;
        private RectTransform _root;
        private TextMeshProUGUI _nameTmp, _hpTmp, _statusTmp, _switchTmp, _throwTmp;
        private Image _hpFill;
        private RectTransform _hitbox, _crossRt;
        private Image _crossImg, _hitboxImg;
        private readonly Image[] _fans = new Image[4];
        private readonly Image[] _arcs = new Image[4];
        private RectTransform _fleeRt, _switchRt, _throwRt;
        private Image _fleeImg, _switchImg, _throwImg;
        private string _lastStatus;
        // ---- v1.30.0 投掷按钮 (手动 rect 判定, Locked 光标下不走 uGUI Button) ----
        private bool _throwShown;
        private float _throwRefreshAt = -1f;                  // 槽查询节流 0.25s (显隐+剩余件数)
        private float _throwGuardUntil = -1f;                 // 0.3s 去抖 (双派发铁律)
        // v1.34.2 拍板②: 状态行弹药余量缓存 (0.25s 节流照投掷按钮惯例; ResolveWeapon 后强制刷新)
        private int _ammoLeft;
        private float _ammoRefreshAt = -1f;

        // ---- sprite 缓存 (静态: 跨战斗复用, HideAndDontSave) ----
        private static Sprite _spCross, _spFan, _spRing;
        private static readonly List<object> _spKeepAlive = new List<object>();
        private readonly Dictionary<string, Sprite> _crossOverride = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly HashSet<string> _crossTried = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<object> _overrideKeepAlive = new List<object>();

        // ---- 光标配对 ----
        private bool _cursorHeld;

        internal RtCombatService(MelonLogger.Instance logger)
        {
            _logger = logger;
            _ugui = new UguiBuilder(logger, _pins);
        }

        internal bool Active => _phase == Phase.Active;
        internal bool InScene => Scenes != null && Scenes.Active != null;

        // ==================== combat.rt_start 后端 ====================

        /// <summary>cfg: {id?, name?, hp(必, v1.34.0 起=直接 HP 值, ≤10 回退旧 pip×15), atk?, strong?,
        /// posture_sneak?, player_hp?, flee?, armor?(0-100 减伤%, 可被 pen 穿透), band?(0近1中2远, 默认1),
        /// behaviors?(卡组 [{type=strafe|shrink|hide|shield|charge|dash|zigzag|circle|leap|feint|reddot(v1.46.0 暴击窗 {window 1-4s, 缺省2}, 命中 ×2), weight, ...参数}]),
        /// shield?({dots, reduction, moving_ratio, teleport_ratio, red_window}),
        /// charge?({duration, stagger_mul, heavy_mul}),
        /// hitbox_scale?(v1.36.0 受击箱个体化系数, 钳 0.5-1.3, 缺省 1.0),
        /// depth?(v1.36.0 深度难度, int 钳 0-8, 缺省 0: hp/atk 两端 ×(1+0.10×d); v1.44.0 前为 0-5/0.12),
        /// warn_mul?(v1.36.0 预警时长系数, 钳 0.7-1.6, 缺省 1.0),
        /// swarm?(v1.36.0 鼠群 {count 2-4, box_scale 0.3-0.6, spread 100-260, speed 120-450 缺省240(v1.46.0)}, 与 shield 互斥),
        /// summon?(v1.45.0 嚎叫者召唤 {interval 2-8, cap 1-5, child_hp 1-60, box_scale 0.25-0.6,
        ///   spread 100-300, child_atk 0-10} — 子箱独立 HP 打死即灭, 母体可攻),
        /// escape_after?(v1.45.0 掠窃者逃逸秒数, 钳 0-60, 缺省 0 = off; 到点 EndCombat("escape")),
        /// on_hp_change?(pss 函数, 玩家 HP 每次变化收 {hp, max} — v1.30.0 状态栏实时同步)};
        /// onEnd: pss 函数收 {result=win|flee|lose|escape(v1.45.0), player_hp, sp_cost(v1.30.0 体力结算), enemy_id, enemy_name}。</summary>
        internal bool RtStart(Dictionary<string, object> cfg, PsCallable onEnd, Interpreter itp, int line)
        {
            if (!InScene)
            {
                PsApi.Warn(_logger, "[rt] rt_start 不在任何自定义场景里调用, 已忽略");
                return false;
            }
            if (_phase == Phase.Active)
            {
                PsApi.Warn(_logger, "[rt] rt_start 已在实时战斗中, 已忽略");
                return false;
            }
            _enemyId = OptStr(cfg, "id", "enemy");
            _enemyName = OptStr(cfg, "name", "敌人");
            _depth = ParseDepth(CfgVal(cfg, "depth"), line);                     // v1.36.0 (先于 hp/atk 结算)
            float dmul = Raid.RaidCore.DepthMul(_depth);
            _enemyHp = _enemyHpMax = Math.Max(1, (int)Math.Round(
                Raid.RaidCore.RtEnemyHpValue(ReqInt(cfg, "hp", "combat.rt_start", line)) * dmul));
            _atkRange = OptStr(cfg, "atk", "3-6");
            try
            {
                var ar = Raid.RaidCore.ParseChain(_atkRange);
                if (_depth > 0)                                                  // v1.36.0: atk 区间两端同乘 DepthMul (round)
                    _atkRange = Math.Max(1, (int)Math.Round(ar.Lo * dmul)) + "-"
                              + Math.Max(1, (int)Math.Round(ar.Hi * dmul));
            }
            catch (FormatException e) { throw new PsRuntimeError("combat.rt_start 的 atk: " + e.Message, line); }
            _sneakPending = OptBool(cfg, "posture_sneak", false);
            _playerHp = Raid.RaidCore.Clamp(OptInt(cfg, "player_hp", 100), 0, Raid.RaidCore.MaxStat);
            _fleeChance = Raid.RaidCore.Clamp(OptInt(cfg, "flee", 50), 0, 95);
            _itp = itp;
            _onEnd = onEnd;
            _onHpChange = OptCallable(cfg, "on_hp_change", "combat.rt_start", line);   // v1.30.0
            // v1.34.0: 扩展键 (全可选; 类型非法 = PsRuntimeError 带行号, 范围钳制走 RaidCore)
            _armor = ParseArmor(CfgVal(cfg, "armor"), line);
            _band = ParseBand(CfgVal(cfg, "band"), line);
            _deck = ParseBehaviors(CfgVal(cfg, "behaviors"), line);
            _shieldCfg = ParseShieldCfg(CfgVal(cfg, "shield"), line);
            _chargeCfg = ParseChargeCfg(CfgVal(cfg, "charge"), line);
            // v1.36.0 (M3.7): 受击箱个体化 / 预警时长 / 鼠群 (类型非法 = PsRuntimeError 带行号)
            _hitboxScale = ParseHitboxScale(CfgVal(cfg, "hitbox_scale"), line);
            _warnMul = ParseWarnMul(CfgVal(cfg, "warn_mul"), line);
            _tempo = ParseTempo(CfgVal(cfg, "tempo"), line);            // v1.37.0: 敌节奏个体化 (行为间隙 ×tempo)
            _swarmCfg = ParseSwarmCfg(CfgVal(cfg, "swarm"), line);
            if (_swarmCfg != null && CfgVal(cfg, "shield") != null)
            {
                // swarm 与 shield 互斥: 配了 shield 就忽略 swarm
                PsApi.Warn(_logger, "[rt] rt_start cfg 同时配了 shield 与 swarm — 互斥, 已忽略 swarm (" + _enemyId + ")");
                _swarmCfg = null;
            }
            // v1.45.0 (§4.5/§4.6): 嚎叫者召唤 / 掠窃者逃逸计时 (类型非法 = PsRuntimeError 带行号)
            _summonCfg = ParseSummonCfg(CfgVal(cfg, "summon"), line);
            _escapeAfter = ParseEscapeAfter(CfgVal(cfg, "escape_after"), line);

            EnsureSprites();
            SnapshotLayout();                                 // v1.31.0: 截取当前场景布局 rt 区 (几何/颜色/键位/准星速度)
            if (!BuildUi())
            {
                _itp = null; _onEnd = null; _onHpChange = null;
                return false;
            }
            ResolveWeapon();
            // v1.34.2 拍板②: 有枪无弹 (降级枪托) 开战大红字警告 (状态行常显弹药见 UpdateStatus)
            if (_w.Gunstock && _w.IsGun)
            {
                string gname = _w.Name.EndsWith(" (枪托)") ? _w.Name.Substring(0, _w.Name.Length - 5) : _w.Name;
                Log("<color=#FF4444>你的" + gname + "没有弹药! 只能抡枪托 (找弹药或按下方按钮换近战)</color>");
            }
            HoldCursor();
            try { Grid?.BoxShow(false); } catch { }          // 战斗锁: 箱窗/地面窗 Hide
            try { Grid?.SetGroundVisible(false); } catch { }
            _phase = Phase.Active;
            float now = Time.unscaledTime;
            _combatT0 = now;                                  // v1.30.0: sp_cost 计时起点
            _nextFireAt = -1f; _jamLockUntil = -1f; _meleeCdUntil = -1f; _meleeStunUntil = -1f;
            _warnActive = false; _lastDodgeAt = -999f; _warnHeavyMul = 1f;
            _throwRefreshAt = -1f; _throwGuardUntil = -1f; _ammoRefreshAt = -1f;
            _nextAtkAt = now + Raid.RaidCore.EnemyInterval(_rng);
            ResetBehavior(now);                               // v1.34.0: 行为机归零 (v1.37.0: 首次抽行为 = 0~0.3s 后, 开战即动)
            if (_swarmCfg != null) CreateSwarmBoxes(now);     // v1.36.0: 鼠群子箱生成 (主受击箱隐藏)
            if (_summonCfg != null) _nextSummonAt = now + _summonCfg.Interval;   // v1.45.0: 首次召唤计时
            _crossPos = new Vector2(Screen.width / 2f, Screen.height / 2f);
            _crossJit = Vector2.zero; _missDiagDone = false;                     // v1.34.3
            _axisAccum = 0f; _axisChecked = false; _axisCheckAt = now + 3f;   // v1.34.2: 准星轴 3s 自检
            // 遭遇文案由编排侧 (pss) 输出 (强敌/普通变体), 这里只补 rt 专属提示
            if (_sneakPending) Log("你占据先手 — 第一击必中且伤害 ×1.5!");
            if (!_weaponDrawn) Log("你收着武器 — 只能抡拳头 (战斗结束后按 X 拔出)");
            // v1.34.2: 开战日志补 crossSpeed/分辨率 (排准星灵敏度/分辨率嫌疑)
            PsApi.Log(_logger, $"[rt] 实时战斗开始: {_enemyId} hp={_enemyHp} atk={_atkRange} flee={_fleeChance} sneak={_sneakPending} depth={_depth} hitbox_scale={_hitboxScale} crossSpeed={_crossSpeed} screen={Screen.width}x{Screen.height}");
            return true;
        }

        // ==================== 每帧驱动 (Plugin.OnUpdate) ====================

        internal void Tick()
        {
            // X (布局 rt.keys.holster 可改) 收/出武器: 战斗中无效 (拍板 2)
            bool xDown = false;
            try { xDown = Input.GetKeyDown(_holsterKey); } catch { }
            if (xDown && InScene)
            {
                if (_phase == Phase.Active) Log("战斗中无法收/出武器");
                else
                {
                    _weaponDrawn = !_weaponDrawn;
                    Log(_weaponDrawn ? "你拔出了武器" : "你收起了武器 (再按 X 拔出)");
                }
            }
            if (_phase != Phase.Active) return;
            float now = Time.unscaledTime;
            try
            {
                // v1.45.0 掠窃者逃逸 (§4.6): 开战 escape_after 秒到点 → 携赃脱战 (result "escape")
                if (_escapeAfter > 0f && _combatT0 >= 0f && now - _combatT0 >= _escapeAfter)
                {
                    Log(_enemyName + "捞够就跑 — 它转身逃出了战场!");
                    EndCombat("escape");
                    return;
                }
                TickSummon(now);                              // v1.45.0: 嚎叫者召唤计时 (§4.5)
                TickCrosshair(now);
                TickFire(now);
                TickEnemyAttack(now);
                TickBehavior(now);
                TickButtons();
                TickStatus(now);
            }
            catch (Exception e)
            {
                // 不静默吞: 报错 + 安全收尾 (abort 走完整体退出路径, 光标/战斗锁不泄漏)
                PsApi.Err(_logger, $"[rt] Tick 异常, 战斗安全中止: {e}");
                try { EndCombat("abort", false); } catch { }
            }
        }

        // ---- 准星: 虚拟位置 (Locked 光标下 mousePosition 冻结, 用轴增量积分) + 正弦抖动 ----
        private void TickCrosshair(float now)
        {
            if (!_axisProbed)
            {
                _axisProbed = true;
                try { Input.GetAxis("Mouse X"); }
                catch (Exception e)
                {
                    _axisOk = false;
                    PsApi.Warn(_logger, "[rt] Mouse X/Y 轴不可用, 准星降级为系统光标位置 (Locked 下可能冻结): " + e.Message);
                }
            }
            if (_axisOk)
            {
                float dx = 0f, dy = 0f;
                try { dx = Input.GetAxis("Mouse X"); dy = Input.GetAxis("Mouse Y"); } catch { }
                _axisAccum += Math.Abs(dx) + Math.Abs(dy);   // v1.34.2: 准星轴自检累计
                _crossPos.x = ClampF(_crossPos.x + dx * _crossSpeed, 0f, Screen.width);
                _crossPos.y = ClampF(_crossPos.y + dy * _crossSpeed, 0f, Screen.height);
            }
            else
            {
                try { _crossPos = Input.mousePosition; } catch { }
            }
            // v1.34.2: 准星轴自检 — 开战 3s 到点打累计量; axisOk=true 但零位移 = 轴不存在/零灵敏度, 准星冻结嫌疑
            if (!_axisChecked && now >= _axisCheckAt)
            {
                _axisChecked = true;
                PsApi.Log(_logger, "[rt] 准星轴自检: 3s 累计位移量=" + _axisAccum.ToString("0.00") + " (axisOk=" + (_axisOk ? "true" : "false") + ")");
                if (_axisOk && _axisAccum < 0.01f)
                    PsApi.Warn(_logger, "[rt] Mouse X/Y 轴疑似不存在或零灵敏度, 准星冻结! 请反馈此日志");
            }
            if (_crossRt is null) return;
            float jr = Raid.RaidCore.JitterRadius(_w.Stab);
            float jx = (float)Math.Sin(now * 2.7f) * jr;
            float jy = (float)Math.Sin(now * 3.4f + 1.7f) * jr;
            _crossJit = new Vector2(jx, jy);
            var rect = _root.rect;
            _crossRt.anchoredPosition = new Vector2(
                _crossPos.x - rect.width / 2f + jx,
                _crossPos.y - rect.height / 2f + jy);
        }

        // ---- 射击/按钮输入 (Locked 下 uGUI Button 不可点, 全手动 rect 判定) ----
        private void TickFire(float now)
        {
            bool down = false, held = false;
            try { down = Input.GetMouseButtonDown(0); held = Input.GetMouseButton(0); } catch { }
            if (!down && !held) return;
            if (down)
            {
                if (PointerIn(_fleeRt)) { FleeClick(); return; }
                if (_throwShown && PointerIn(_throwRt)) { ThrowClick(now); return; }
                if (PointerIn(_switchRt)) { SwitchClick(); return; }
            }
            if (now < _jamLockUntil) return;
            bool want;
            if (_w.MeleeClass) want = down && now >= _meleeCdUntil;
            else if (_w.Mode == 2) want = held && now >= _nextFireAt;
            else want = down && now >= _nextFireAt;
            if (want) TryFire(now);
        }

        private void TryFire(float now)
        {
            // 每发重解析 (弹药打空 → 自动落枪托; X 收武器状态战斗内不变)
            ResolveWeapon();
            bool sneak = _sneakPending;
            _sneakPending = false;
            if (_w.MeleeClass)
            {
                _meleeCdUntil = now + Raid.RaidCore.MeleeCd;
                _meleeStunUntil = now + Raid.RaidCore.MeleeStun;
            }
            else
            {
                _nextFireAt = now + Raid.RaidCore.FireCd(_w.Rate);
                float jam = Raid.RaidCore.JamChance(_w.Stab);
                // v1.48.7 (拍板): 破损 (ps_broken=1) 武器开火 25% 卡壳 (与 stab 卡壳取大;
                //   卡壳=该发打不出, 耗 1 发弹药不扣耐久 — 与既有 stab 卡壳同一路径同一日志)
                if (WeaponDurability.IsBroken(_w.Source)) jam = Math.Max(jam, Raid.RaidCore.BrokenJamChance);
                if (_rng.NextDouble() * 100.0 < jam)
                {
                    try { Combat?.ConsumeAmmo(null); } catch { }
                    _jamLockUntil = now + Raid.RaidCore.JamLock;
                    Log("你的" + _w.Name + "卡壳了! 上膛中…");
                    UpdateStatus(true);
                    return;
                }
                try { Combat?.ConsumeAmmo(null); } catch { }
            }
            // v1.43.0 阶段三: 组装枪耐久 — 实际开火/挥击一次扣 ps_wear (卡壳未射出不扣; 命中与否都扣;
            //   投掷不走此路径; 无 ps_dur_max 的武器零开销)。本次扣到破损 → 战斗日志提示
            if (WeaponDurability.OnUse(_w.Source, _logger) == 2)
                Log("你的" + _w.Name + "耐久耗尽, 已破损! 伤害大减 (拖同材料修理物修补)");
            // v1.45.0 嚎叫者召唤小怪 (§4.5): 子箱优先判定 — 独立 HP 打死即灭, 命中消耗本次射击
            if (_summonBoxes.Count > 0)
            {
                float cpad = _w.Rad * Raid.RaidCore.RadPadPx;
                SummonBox cHit = null;
                foreach (var b in _summonBoxes)
                    if (!b.Dead && !(b.Rt is null) && PointerInPad(b.Rt, cpad)) { cHit = b; break; }
                if (cHit != null)
                {
                    float catk = _w.Unarmed ? _rng.Next(Raid.RaidCore.UnarmedAtkLo, Raid.RaidCore.UnarmedAtkHi + 1) : _w.Atk;
                    int cdmg = Raid.RaidCore.RollRtDamage(catk, _rng);
                    cHit.Hp -= cdmg;
                    cHit.FlashUntil = now + 0.15f;
                    try { if (!(cHit.Img is null)) cHit.Img.color = Color.white; } catch { }
                    if (cHit.Hp <= 0)
                    {
                        cHit.Dead = true;
                        try { if (!(cHit.Rt is null)) cHit.Rt.gameObject.SetActive(false); } catch { }
                        Log("你的" + _w.Name + "击碎了小怪的箱子 — 它瘫在地上不动了");
                    }
                    else Log("你的" + _w.Name + "击中小怪 — " + cdmg + " 点伤害");
                    return;
                }
            }
            // v1.36.0 鼠群 (M3.7): 主受击箱隐藏, 命中 = 任一子箱 (任一中=命中, 共享 HP 池)
            bool swarm = _swarmBoxes.Count > 0;
            SwarmBox swarmHit = null;
            bool hit;
            if (swarm)
            {
                hit = sneak;
                float spad = _w.Rad * Raid.RaidCore.RadPadPx;
                foreach (var b in _swarmBoxes)
                    if (!(b.Rt is null) && PointerInPad(b.Rt, spad)) { swarmHit = b; hit = true; break; }
            }
            else hit = sneak || PointerInPad(_hitbox, _w.Rad * Raid.RaidCore.RadPadPx);
            // v1.34.0 护盾态 (§3.10.1): 红点 > 绿点 (rad 多目标) > 受击箱 (灰免, 命中=0 伤害+无效提示)
            if (_beh == BehState.ShieldRed)
            {
                if (!(_redDot is null) && PointerInPad(_redDot, _w.Rad * Raid.RaidCore.RadPadPx)) { BreakShield(now); return; }
                if (hit) { Log("无效 — " + _enemyName + "的护盾挡住了这一击 (打红点破盾!)"); return; }
                LogMiss();
                return;
            }
            if (_beh == BehState.ShieldDots)
            {
                var dot = DotAt(_w.Rad * Raid.RaidCore.RadPadPx);
                if (dot != null) { HitDots(dot, now); return; }
                if (hit) { Log("无效 — " + _enemyName + "的护盾挡住了这一击 (先打碎绿点!)"); return; }
                LogMiss();
                return;
            }
            if (hit && _beh == BehState.Hide) { Log(_enemyName + "隐匿中, 无法命中!"); return; }
            // v1.46.0 reddot 暴击窗 (M3.6 推广): 命中红点 = 暴击 ×2 并关窗; 打身体照常 (窗不挡枪, 超时无惩罚)
            if (_beh == BehState.RedDot && !(_redDot is null) && PointerInPad(_redDot, _w.Rad * Raid.RaidCore.RadPadPx))
            {
                float catk = _w.Unarmed ? _rng.Next(Raid.RaidCore.UnarmedAtkLo, Raid.RaidCore.UnarmedAtkHi + 1) : _w.Atk;
                int cdmg = Raid.RaidCore.RollRtDamage(catk, _rng);
                cdmg = Math.Max(1, (int)Math.Round(cdmg * Raid.RaidCore.RedDotCritMul()));
                int csf = Raid.RaidCore.BandShortfall(_w.Rng, _w.MeleeClass ? Raid.RaidCore.MeleeEffectiveBand(_warnActive, _band) : _band);
                cdmg = ApplyEnemyDamage(cdmg, csf, _w.Pen, now);
                _enemyHp = Math.Max(0, _enemyHp - cdmg);
                UpdateEnemyHp();
                _beh = BehState.None;
                _redUntil = -1f;
                HideRedUi();
                _behNextAt = now + BehGap();
                Log("暴击! 红点命中 — " + cdmg + " 点伤害 (×" + Raid.RaidCore.RedDotCritMul().ToString("0") + ")");
                UpdateStatus(true);
                if (_enemyHp <= 0) EndCombat("win");
                return;
            }
            if (!hit)
            {
                LogMiss();
                return;
            }
            // v1.34.2 拍板① (实测修正): 近战类 (近战/枪托/拳头) 不再距离锁死 — 与枪械同款吃射程短缺衰减
            //   (同一 BandShortfall 纯函数); 有效带仍走 MeleeEffectiveBand: 敌 WARNING→RESOLVE 攻击窗口全程
            //   视为近带 = sf 0 全额 (抓时机反击), 窗口外按真实带衰减 (rng1 对中带 ×0.70 / 远带 ×0.49);
            //   窗口=_warnActive (TickFire 先于 TickEnemyAttack 跑, RESOLVE 当帧仍 true, 两阶段全覆盖);
            //   MeleeCanReach 近战分支退役, 仅剩投掷限近/中带在用 (ThrowClick)
            int meleeBand = Raid.RaidCore.MeleeEffectiveBand(_warnActive, _band);
            float atk = _w.Unarmed ? _rng.Next(Raid.RaidCore.UnarmedAtkLo, Raid.RaidCore.UnarmedAtkHi + 1) : _w.Atk;
            int dmg = Raid.RaidCore.RollRtDamage(atk, _rng);
            if (sneak) dmg = Math.Max(1, (int)Math.Round(dmg * Raid.RaidCore.SneakMul));
            // v1.34.0: 短缺 ×0.7^n + 破甲 ×(1−ArmorEff) + 虚弱/破绽承伤倍率
            // v1.34.2: 近战类 sf 按有效带算 (窗口内近带=0 全额, 窗口外短缺衰减), 不再恒 0
            int sf = Raid.RaidCore.BandShortfall(_w.Rng, _w.MeleeClass ? meleeBand : _band);
            dmg = ApplyEnemyDamage(dmg, sf, _w.Pen, now);
            // v1.36.0 鼠群 rad 波及: 命中点 80px 内额外子箱 ≤rad-1 个, 每个追加 50% (SwarmBonusMul);
            // 被击子箱闪白 0.15s (抄 dots FlashUntil 机制)
            int swarmExtra = 0;
            if (swarm)
            {
                if (!_w.MeleeClass && _w.Rad > 1)
                    foreach (var b in _swarmBoxes)
                    {
                        if (swarmExtra >= _w.Rad - 1) break;
                        if (ReferenceEquals(b, swarmHit) || b.Rt is null) continue;
                        if (WithinRadius(b.Rt, Raid.RaidCore.RadMultiRadius)) swarmExtra++;
                    }
                if (swarmExtra > 0)
                    dmg = Math.Max(1, (int)Math.Round(dmg * Raid.RaidCore.SwarmBonusMul(swarmExtra)));
                if (swarmHit != null)
                {
                    swarmHit.FlashUntil = now + 0.15f;
                    try { if (!(swarmHit.Img is null)) swarmHit.Img.color = Color.white; } catch { }
                }
            }
            _enemyHp = Math.Max(0, _enemyHp - dmg);
            UpdateEnemyHp();
            // v1.34.2 拍板②: 枪托命中日志区分前缀 (玩家明确知道在抡枪托)
            string verb = _w.Gunstock ? "你抡起" : "你的";
            string spreadNote = swarmExtra > 0 ? " (波及 " + swarmExtra + " 只, ×" + Raid.RaidCore.SwarmBonusMul(swarmExtra).ToString("0.0") + ")" : "";
            if (sneak) Log("偷袭得手! " + verb + _w.Name + "命中" + _enemyName + " — " + dmg + " 点伤害" + spreadNote);
            else Log(verb + _w.Name + "命中" + _enemyName + " — " + dmg + " 点伤害" + (sf > 0 ? " (距离短缺 ×" + Raid.RaidCore.BandDamageMul(sf).ToString("0.00") + ")" : "") + spreadNote);
            if (_enemyHp <= 0)
            {
                // 胜利文案由编排侧 (pss on_end) 输出 — 与旧按钮战斗 raid_victory 同一条, 防双写
                EndCombat("win");
                return;
            }
            // v1.36.0 麻痹 (M3.7, NBT w_stun): 命中后敌节奏器暂停 stun 秒 + 撤进行中预警 + 打断冲锋
            // (冲锋/硬直中也生效 — 电枪克冲锋定位); 状态行「麻痹!」见 EnemyStateText
            if (_w.Stun > 0f)
            {
                float stun = Raid.RaidCore.StunSeconds(_w.Stun);
                _stunUntil = now + stun;
                _nextAtkAt = Math.Max(_nextAtkAt, _stunUntil);
                if (_warnActive) CancelWarning();
                bool brokeCharge = _beh == BehState.Charge;
                if (brokeCharge)
                {
                    _beh = BehState.None;
                    _chargeAccum = 0f;
                    _behNextAt = Math.Max(now + 0.5f, _stunUntil);
                }
                Log(_enemyName + "被电麻了! 麻痹 " + stun.ToString("0.0") + " 秒" + (brokeCharge ? " — 冲锋被打断!" : ""));
                UpdateStatus(true);
            }
            // v1.34.0 冲锋击退: 冲锋期间累计受击 ≥ 阈值 → 取消冲锋+硬直 1s (节奏器暂停)
            if (_beh == BehState.Charge)
            {
                _chargeAccum += dmg;
                int thr = Math.Max(1, (int)Math.Round(Raid.RaidCore.ChargeStaggerThreshold(_enemyHpMax) * _chargeCfg.StaggerMul));
                if (_chargeAccum >= thr) StaggerEnemy(now);
            }
        }

        // ---- 敌攻击: 节奏器 → 四向预警 (扇形闪 3 下 + 弧环填充) → 闪避分档结算 ----
        private void TickEnemyAttack(float now)
        {
            // v1.36.0: 麻痹期间节奏器暂停 (进行中的预警被吞掉)
            if (now < _stunUntil)
            {
                if (_warnActive) CancelWarning();
                return;
            }
            // v1.34.0: 冲锋/硬直期间节奏器暂停 (进行中的预警被吞掉); 护盾态照跑 (拍板 ④ 高压)
            if (_beh == BehState.Charge || _beh == BehState.Stagger)
            {
                if (_warnActive) CancelWarning();
                return;
            }
            if (!_warnActive)
            {
                if (now >= _nextAtkAt) StartWarning(now);
                return;
            }
            // v1.36.0 feint 佯攻: 预警播到 50% 撤预警 + 横跳 + 下次攻击提前
            if (_feintCancelAt > 0f && now >= _feintCancelAt) { FeintWhiff(now); return; }
            // 闪避键轮询 (硬直中无效)
            bool pressed = false;
            try { pressed = Input.GetKeyDown(_dodgeKeys[_warnDir]); } catch { }
            if (pressed)
            {
                if (now < _meleeStunUntil) Log("硬直中, 无法闪避!");
                else _lastDodgeAt = now;
            }
            float p = (now - _warnT0) / _warnDur;
            if (p >= 1f) { ResolveAttack(now); return; }
            var fan = _fans[_warnDir];
            if (!(fan is null))
            {
                float phase = p * 3f;                                     // 闪 3 下 (亮度在配置色 alpha 与其 1/4 间切换)
                bool on = phase - (float)Math.Floor(phase) < 0.6;
                var c = _warnColor; c.a = on ? _warnColor.a : _warnColor.a * 0.24f;
                try { fan.color = c; } catch { }
            }
            var arc = _arcs[_warnDir];
            if (!(arc is null)) try { arc.fillAmount = p * 0.5f; } catch { }   // 弧环由角向内扫半环
        }

        private void StartWarning(float now)
        {
            _warnDir = _rng.Next(4);
            _warnT0 = now;
            // v1.36.0: 预警时长个体化 (warn_mul 乘算, 近战 ÷1.25 规则保留)
            _warnDur = Raid.RaidCore.WarnDuration(_w.MeleeClass) * _warnMul;
            _warnActive = true;
            _lastDodgeAt = -999f;
            // v1.36.0 feint 佯攻: 行为生效期间下一次预警掷 cancel_chance, 中则播到 50% 时假动作
            _feintCancelAt = -1f;
            if (_beh == BehState.Feint && _rng.NextDouble() < _feintCancelChance)
                _feintCancelAt = _warnT0 + _warnDur * 0.5f;
            SetDirActive(_warnDir, true);
            var arc = _arcs[_warnDir];
            if (!(arc is null)) try { arc.fillAmount = 0f; } catch { }
            Log(_enemyName + "从" + DirNames[_warnDir] + "侧扑来! (按 " + DodgeKeyName(_warnDir) + " 闪避)");
        }

        /// <summary>v1.36.0 feint 假动作: 撤预警 + 立即横跳 120-200px (复用 dash 位移段) +
        /// 下次攻击提前 (0.8~1.4s 随机) + 结束佯攻行为。</summary>
        private void FeintWhiff(float now)
        {
            CancelWarning();                                    // 内部清 _feintCancelAt
            StartFeintHop(now);
            _beh = BehState.None;
            _behNextAt = now + BehGap();   // v1.37.0: 行为间隙 ×tempo (个体化)
            _nextAtkAt = now + Raid.RaidCore.FeintNextAttackDelay(_rng);
            Log(_enemyName + "佯攻落空! 它虚晃一枪横跳到了一旁");
            UpdateStatus(true);
        }

        /// <summary>闪避键显示名 (布局配置值; Space 显示「空格」)。</summary>
        private string DodgeKeyName(int dir)
        {
            var n = _dodgeKeyNames[Raid.RaidCore.Clamp(dir, 0, 3)];
            return string.Equals(n, "Space", StringComparison.OrdinalIgnoreCase) ? "空格" : n;
        }

        private void ResolveAttack(float now)
        {
            _warnActive = false;
            SetDirActive(_warnDir, false);
            float dt = _lastDodgeAt < -100f ? 999f : Math.Abs(now - _lastDodgeAt);
            int tier = Raid.RaidCore.DodgeTier(dt);
            int lo, hi;
            try { var r = Raid.RaidCore.ParseChain(_atkRange); lo = r.Lo; hi = r.Hi; }
            catch { lo = 3; hi = 6; }
            int dmg = _rng.Next(lo, hi + 1);
            if (_warnHeavyMul > 1f) dmg = Math.Max(1, (int)Math.Round(dmg * _warnHeavyMul));   // v1.34.0 冲锋重击
            _warnHeavyMul = 1f;
            if (tier == 0)
            {
                Log("完美闪避! 你毫发无损地躲开了" + _enemyName);
                // v1.34.0 破绽 expose (§3.10.1): 完美闪避触发 1.5s — 受击箱 ×1.3 + 承伤 ×1.5
                _exposeUntil = now + Raid.RaidCore.ExposeDuration;
                Log("破绽! " + _enemyName + "露出破绽 1.5 秒 — 承伤 ×1.5, 受击箱变大");
                UpdateStatus(true);
            }
            else
            {
                int taken = tier == 1
                    ? Math.Max(1, (int)Math.Round(dmg * Raid.RaidCore.DodgeDamageMul(1)))
                    : dmg;
                // v1.45.0 嚎叫者小怪撕咬 (§4.5): 母体攻击命中时每个存活小怪 +child_atk
                int bites = 0;
                if (_summonCfg != null && _summonCfg.ChildAtk > 0)
                {
                    foreach (var b in _summonBoxes) if (!b.Dead) bites++;
                    taken += bites * _summonCfg.ChildAtk;
                }
                _playerHp = Math.Max(0, _playerHp - taken);
                NotifyPlayerHp();
                Log(tier == 1
                    ? "擦伤! " + _enemyName + "的攻击擦中了你 — " + taken + " 点伤害"
                    : _enemyName + "击中了你 — " + taken + " 点伤害"
                    + (bites > 0 ? " (含 " + bites + " 只小怪撕咬 +" + bites * _summonCfg.ChildAtk + ")" : ""));
                if (_playerHp <= 0)
                {
                    Log("你眼前一黑…");
                    EndCombat("lose");
                    return;
                }
            }
            _nextAtkAt = now + Raid.RaidCore.EnemyInterval(_rng);
        }

        // ---- 逃跑 / 武器切换 (手动按钮) ----
        private void FleeClick()
        {
            if (_phase != Phase.Active) return;
            if (_rng.NextDouble() * 100.0 < _fleeChance)
            {
                Log("你甩开" + _enemyName + "逃了出来");
                EndCombat("flee");
                return;
            }
            Log("你没能甩掉" + _enemyName + "!");
            int lo, hi;
            try { var r = Raid.RaidCore.ParseChain(_atkRange); lo = r.Lo; hi = r.Hi; }
            catch { lo = 3; hi = 6; }
            int dmg = _rng.Next(lo, hi + 1);
            _playerHp = Math.Max(0, _playerHp - dmg);
            NotifyPlayerHp();
            Log(_enemyName + "趁机给了你一下 — " + dmg + " 点伤害");
            if (_playerHp <= 0)
            {
                Log("你眼前一黑…");
                EndCombat("lose");
            }
        }

        private void SwitchClick()
        {
            _preferMelee = !_preferMelee;
            ResolveWeapon();
            UpdateSwitchLabel();
            UpdateStatus(true);
            Log("切换武器: " + _w.Name);
        }

        // ---- v1.30.0 投掷 (s_throw1-3 槽; 必中 w_atk×2; 次数物品扣 1 否则整件消耗) ----

        /// <summary>投掷点击: 0.3s 去抖 (双派发铁律) + 0.5s 动作锁 (复用近战 CD: _meleeCdUntil)。
        /// 伤害 = RaidCore.RollRtDamage(RtThrowAtk(w_atk)) = w_atk×2 × 0.9-1.1 必中
        /// (rt 层统一不用姿态/档位修正, 与 TryFire 同规; 基数语义与旧回合制 max(1, w_atk×2) 一致)。</summary>
        private void ThrowClick(float now)
        {
            if (_phase != Phase.Active) return;
            if (now < _throwGuardUntil) return;
            _throwGuardUntil = now + 0.3f;
            if (now < _meleeCdUntil) { Log("动作中, 还来不及投掷"); return; }
            // v1.34.0 (§3.10): 投掷限近/中带 (远带按钮灰显+提示); 隐匿无出手点; 护盾态被弹开 (均不消耗)
            if (!Raid.RaidCore.MeleeCanReach("throw", 0, _band)) { Log("距离太远, 投掷物够不着 (投掷限近/中带)"); return; }
            if (_beh == BehState.Hide) { Log(_enemyName + "隐匿中, 投掷无从出手"); return; }
            if (_beh == BehState.ShieldDots || _beh == BehState.ShieldRed) { Log("投掷物会被护盾弹开 — 先破盾!"); return; }
            GameItem ti = null;
            try { ti = Combat?.FindThrowItem(); } catch { }
            if (ti == null) { Log("投掷槽是空的"); return; }
            string tname = "投掷物";
            try { if (!string.IsNullOrEmpty(ti.name)) tname = ti.name; } catch { }
            float watk = 10f;
            try
            {
                var v = ItemsFacade.GetData(ti, "w_atk");
                if (v is long l) watk = l;
                else if (v is double dd) watk = (float)dd;
            }
            catch { }
            bool consumed = false;
            try
            {
                if (ItemsFacade.UseCountMax(ti) > 0) consumed = ItemsFacade.UseCountUse(ti, 1);
                else consumed = ItemsFacade.ConsumeItem(ti);
            }
            catch (Exception e) { PsApi.Warn(_logger, "[rt] 投掷物消耗失败: " + e.Message); }
            if (!consumed) { Log(tname + "没能扔出去"); return; }
            _meleeCdUntil = now + Raid.RaidCore.MeleeCd;          // 0.5s 动作锁 (近战 CD 复用)
            int dmg = Raid.RaidCore.RollRtDamage(Raid.RaidCore.RtThrowAtk(watk), _rng);
            dmg = ApplyEnemyDamage(dmg, 0, 0, now);               // v1.34.0: 破甲+虚弱/破绽照走 (投掷无 pen)
            _enemyHp = Math.Max(0, _enemyHp - dmg);
            UpdateEnemyHp();
            Log("你掷出" + tname + ", 正中" + _enemyName + " — " + dmg + " 点伤害!");
            _throwRefreshAt = -1f;                                // 立即重查槽 (件数变化/槽空隐藏)
            if (_enemyHp <= 0) EndCombat("win");
        }

        /// <summary>投掷按钮显隐+标签 (节流 0.25s 查槽): s_throw1-3 有投掷物才显示,
        /// 标签带剩余件数 (次数物品=剩余次数, 否则单件 x1)。</summary>
        private void TickThrowButton(float now)
        {
            if (_throwRt is null) return;
            if (now < _throwRefreshAt) return;
            _throwRefreshAt = now + 0.25f;
            GameItem ti = null;
            try { ti = Combat?.FindThrowItem(); } catch { }
            _throwShown = ti != null;
            try { if (_throwRt.gameObject.activeSelf != _throwShown) _throwRt.gameObject.SetActive(_throwShown); } catch { }
            if (!_throwShown || _throwTmp is null) return;
            string tname = "投掷物";
            try { if (!string.IsNullOrEmpty(ti.name)) tname = ti.name; } catch { }
            int left = 1;
            try { if (ItemsFacade.UseCountMax(ti) > 0) left = Math.Max(1, ItemsFacade.UseCountGet(ti)); } catch { }
            try { _throwTmp.text = "投掷 (" + tname + " x" + left + ")"
                + (Raid.RaidCore.MeleeCanReach("throw", 0, _band) ? "" : " 太远"); } catch { }   // v1.34.0: 远带灰显提示
        }

        // ==================== v1.31.0: rt 区布局快照 / 键位解析 / F10 热重载 ====================

        /// <summary>从当前场景布局 (RaidLayout.Active, 进场景/ F10 时已三层解析) 截取 rt 区快照:
        /// 几何/颜色在 BuildUi 逐键读并钳制; 准星速度钳 1-100; 键位经 NormalizeRtKey 归一
        /// (非法告警回退默认)。RtStart 与 Relayout 共用。</summary>
        private void SnapshotLayout()
        {
            Raid.RaidLayout.RtZone rt = null;
            try { rt = Raid.RaidLayout.Active?.Rt; } catch { }
            _rt = rt ?? new Raid.RaidLayout.RtZone();
            _crossSpeed = ClampF(_rt.Crosshair != null ? _rt.Crosshair.Speed : CrossSpeedDefault, 1f, 100f);
            var k = _rt.Keys ?? new Raid.RaidLayout.RtKeys();
            ApplyKey(0, k.DodgeLeft, "D", "dodge_left");
            ApplyKey(1, k.DodgeRight, "A", "dodge_right");
            ApplyKey(2, k.DodgeUp, "S", "dodge_up");
            ApplyKey(3, k.DodgeDown, "Space", "dodge_down");
            var hol = RtKey(k.Holster, "X", "holster");
            KeyCode hk;
            _holsterKey = Enum.TryParse(hol, true, out hk) ? hk : KeyCode.X;
        }

        private void ApplyKey(int dir, string raw, string def, string label)
        {
            var name = RtKey(raw, def, label);
            _dodgeKeyNames[dir] = name;
            KeyCode kc;
            _dodgeKeys[dir] = Enum.TryParse(name, true, out kc) ? kc : KeyCode.Space;
        }

        private string RtKey(string raw, string def, string label)
        {
            var n = NormalizeRtKey(raw, def);
            if (n == def && !string.IsNullOrWhiteSpace(raw)
                && !string.Equals(raw.Trim(), def, StringComparison.OrdinalIgnoreCase))
                PsApi.Warn(_logger, $"[rt] 布局键位 {label} '{raw}' 无法解析 (须 KeyCode 名), 回退默认 {def}");
            return n;
        }

        /// <summary>键位名归一 (纯函数, pss_test 可测): 合法 KeyCode 名 (大小写/空白容忍) → trim 后原名;
        /// 非法/空 → def。</summary>
        internal static string NormalizeRtKey(string raw, string def)
        {
            if (string.IsNullOrWhiteSpace(raw)) return def;
            var t = raw.Trim();
            KeyCode kc;
            return Enum.TryParse(t, true, out kc) ? t : def;
        }

        /// <summary>v1.31.0: F10 布局热重载 (Plugin.OnUpdate) — 战斗中按新 rt 区重截快照并重建 UI
        /// (「战斗面板开着也重建」先例); 不在战斗中 = 下次开战自动用新布局。</summary>
        internal void Relayout()
        {
            if (_phase != Phase.Active) return;
            SnapshotLayout();
            DestroyUi();
            if (!BuildUi())
            {
                PsApi.Warn(_logger, "[rt] F10 重建 UI 失败, 战斗安全中止");
                try { EndCombat("abort", false); } catch { }
                return;
            }
            RespawnShieldUi();                                // v1.34.0: 护盾绿点/红点按逻辑态重建
            RespawnSwarmUi();                                 // v1.36.0: 鼠群子箱按逻辑态重建
            RespawnSummonUi();                                // v1.45.0: 嚎叫者召唤子箱按逻辑态重建
            PsApi.Log(_logger, "[rt] 战斗 UI 已按新布局重建 (F10)");
        }

        private void ResolveWeapon()
        {
            try { _w = Combat?.ResolveRtWeapon(_preferMelee, _weaponDrawn, null) ?? new RtWeapon(); }
            catch { _w = new RtWeapon(); }
            _ammoRefreshAt = -1f;   // v1.34.2: 武器/弹药态变化 (每发重解析/切换/开战) → 状态行弹药强制刷新
        }

        // ---- 按钮悬停提亮 (虚拟准星位置) ----
        private void TickButtons()
        {
            TickThrowButton(Time.unscaledTime);
            HoverTint(_fleeImg, PointerIn(_fleeRt));
            HoverTint(_throwImg, _throwShown && PointerIn(_throwRt));
            HoverTint(_switchImg, PointerIn(_switchRt));
            // v1.34.0: 投掷限近/中带 — 远带时按钮灰显 (点击仍有提示, 见 ThrowClick)
            if (_throwShown && !(_throwImg is null) && !Raid.RaidCore.MeleeCanReach("throw", 0, _band))
                try { _throwImg.color = new Color(0.10f, 0.10f, 0.10f, 0.55f); } catch { }
        }

        private static void HoverTint(Image img, bool hover)
        {
            if (img is null) return;
            var c = hover ? new Color(0.30f, 0.20f, 0.12f, 0.95f) : new Color(0.16f, 0.12f, 0.09f, 0.95f);
            try { if (img.color != c) img.color = c; } catch { }
        }

        private bool PointerIn(RectTransform rt) => CrossIn(rt, 0f);

        /// <summary>v1.34.3: 命中判定统一走 ScreenPointToLocalPointInRectangle (PixelPin 同款, 已证可靠),
        /// 弃 GetWorldCorners/RectangleContainsScreenPoint — 实测准星明明压箱也全目标"打偏了", 疑
        /// Il2Cpp 下世界角点/屏幕点包含判定坐标系不符; 目标点 = 视觉准星 (_crossPos+抖动), 所见即所得。
        /// overlay canvas 相机参数传 null; 判定对 rt 自身 rect (枢轴系) 外扩 pad px, 锚点/嵌套无关。</summary>
        private bool CrossIn(RectTransform rt, float pad)
        {
            if (rt is null) return false;
            try
            {
                Vector2 lp;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, _crossPos + _crossJit, null, out lp))
                    return false;
                var r = rt.rect;
                return lp.x >= r.xMin - pad && lp.x <= r.xMax + pad
                    && lp.y >= r.yMin - pad && lp.y <= r.yMax + pad;
            }
            catch { return false; }
        }

        /// <summary>v1.34.3: 打偏日志 + 首场一次性坐标诊断 (准星屏坐标/受击箱本地中心/尺寸/root 尺寸)。</summary>
        private void LogMiss()
        {
            Log("你的" + _w.Name + "打偏了 (准星不在目标上)");
            if (_missDiagDone) return;
            _missDiagDone = true;
            try
            {
                var rr = _root.rect;
                var hp = _hitbox.anchoredPosition;
                var hs = _hitbox.sizeDelta;
                PsApi.Log(_logger, string.Format(
                    "[rt] 打偏诊断: 准星=({0:0},{1:0}) jit=({2:0.0},{3:0.0}) 箱中心local=({4:0},{5:0}) 箱尺寸=({6:0}x{7:0}) root=({8:0}x{9:0}) beh={10}",
                    _crossPos.x, _crossPos.y, _crossJit.x, _crossJit.y, hp.x, hp.y, hs.x, hs.y, rr.width, rr.height, _beh));
            }
            catch { }
        }

        // ---- 状态行 (敌状态[§3.10] | 卡壳/硬直/武器模式; 变才写) ----
        private void TickStatus(float now) => UpdateStatus(false, now);

        /// <summary>v1.34.0: 敌状态段 (§3.10.1 状态行: 「护盾中 3/4」「虚弱!」「冲锋!!」「破绽!」等)。</summary>
        private string EnemyStateText(float now)
        {
            if (now < _stunUntil) return "麻痹!";                       // v1.36.0: w_stun 麻痹最优先
            if (now < _weakenUntil) return "虚弱!";
            switch (_beh)
            {
                case BehState.ShieldDots: return "护盾中 " + DotsAlive() + "/" + _dotsTotal;
                case BehState.ShieldRed: return "打红点破盾!";
                case BehState.RedDot: return "红点亮起 — 暴击!";   // v1.46.0
                case BehState.Charge: return "冲锋!!";
                case BehState.Stagger: return "硬直!";
                case BehState.Strafe: return "游走";
                case BehState.Shrink: return "伏身";
                case BehState.Hide: return "掩蔽";
                case BehState.Dash: return "突进!";
                case BehState.Zigzag: return "乱窜";
                case BehState.Circle: return "环绕";
                case BehState.Leap: return "纵跳!";
                case BehState.Feint: return "佯攻?";
            }
            if (now < _exposeUntil) return "破绽!";
            return "";
        }

        private void UpdateStatus(bool force, float now = -1f)
        {
            if (_statusTmp is null) return;
            if (now < 0f) now = Time.unscaledTime;
            string st;
            if (now < _jamLockUntil) st = "卡壳! 上膛中 " + (_jamLockUntil - now).ToString("0.0") + "s";
            else if (now < _meleeStunUntil) st = _w.Name + " — 硬直中…";
            else if (_w.MeleeClass) st = _w.Name + " (近战类: 预警更快, 出手后硬直)";
            else st = _w.Name + (_w.Mode == 2 ? " [连发" : " [单发") + " " + _w.Rate + "RPM]";
            // v1.34.2 拍板②: 枪械状态行常显弹药余量 (0.25s 节流缓存); 无弹降级枪托 = 红字「无弹! 抡枪托」
            if (_w.IsGun)
                st += _w.Gunstock ? " | <color=#FF5555>无弹! 抡枪托</color>" : " | 弹药 x" + AmmoLeft(now);
            string est = EnemyStateText(now);
            if (est.Length > 0) st = est + " | " + st;
            if (force || st != _lastStatus)
            {
                _lastStatus = st;
                try { _statusTmp.text = st; } catch { }
            }
        }

        /// <summary>v1.34.2 拍板②: 弹药余量 (0.25s 节流缓存, 照投掷按钮惯例; ResolveWeapon 后强制刷新)。
        /// 余量 = Combat.FindAmmo(null) 命中物品的剩余次数 (UseCountGet, 照投掷余数做法), 无弹 = 0。</summary>
        private int AmmoLeft(float now)
        {
            if (now >= _ammoRefreshAt)
            {
                _ammoRefreshAt = now + 0.25f;
                GameItem ammo = null;
                try { ammo = Combat?.FindAmmo(null); } catch { }
                int left = 0;
                if (ammo != null)
                {
                    left = 1;
                    try { if (ItemsFacade.UseCountMax(ammo) > 0) left = Math.Max(1, ItemsFacade.UseCountGet(ammo)); } catch { }
                }
                _ammoLeft = left;
            }
            return _ammoLeft;
        }

        // ==================== UI 构建 (v1.31.0: 几何/颜色全部走布局 rt 区快照 _rt, 钳制照现有惯例) ====================

        private bool BuildUi()
        {
            int pixelOrder = _ugui.DetectPixelCanvasOrder(3, "rt combat");
            _go = _ugui.MakeCanvas("PSApiRtCombat", pixelOrder + 2);
            if (_go is null) { PsApi.Warn(_logger, "[rt] 战斗 canvas 创建失败"); return false; }
            _root = _go.transform.TryCast<RectTransform>();
            if (_root is null) { PsApi.Warn(_logger, "[rt] 战斗 RectTransform 获取失败"); DestroyUi(); return false; }

            // 模态暗幕 (raycast=true: 拦穿透到 HUD/poi/网格残影)
            var dim = _ugui.MakePanel(_root, "dim", 10f, 10f, new Color(0f, 0f, 0f, 0.45f), true);
            dim.anchorMin = Vector2.zero; dim.anchorMax = Vector2.one;
            dim.offsetMin = Vector2.zero; dim.offsetMax = Vector2.zero;

            // 顶部: 敌名 + 数值血条 (HP 45/45, 弃 pip) — 布局 rt.top / rt.enemy_hp
            float topW = Raid.RaidLayout.ClampSize(_rt.Top.W), topH = Raid.RaidLayout.ClampSize(_rt.Top.H);
            var top = _ugui.MakePanel(_root, "top", topW, topH, new Color(0.10f, 0.08f, 0.08f, 0.92f), false);
            UguiBuilder.AnchorCenter(top, _root, _rt.Top.X, _rt.Top.Y);
            _nameTmp = _ugui.MakeText(top, "name", _enemyName, 22f, new Color(0.95f, 0.55f, 0.45f), TextAlignmentOptions.Center);
            _nameTmp.rectTransform.sizeDelta = new Vector2(topW - 16f, 30f);
            UguiBuilder.SeatTop(_nameTmp.rectTransform, 8f);
            float hpW = Raid.RaidLayout.ClampSize(_rt.EnemyHp.W), hpH = Raid.RaidLayout.ClampSize(_rt.EnemyHp.H);
            _hpFill = _ugui.MakeBar(top, "hp", hpW, hpH, new Color(0.55f, 0.80f, 0.40f));
            var hpBg = _hpFill.rectTransform.parent.TryCast<RectTransform>();
            if (!(hpBg is null)) UguiBuilder.SeatTop(hpBg, 44f);
            _hpTmp = _ugui.MakeText(hpBg ?? top, "hpt", " ", 14f, new Color(0.95f, 0.95f, 0.90f), TextAlignmentOptions.Center);
            Stretch(_hpTmp.rectTransform);

            // 互动区 (布局 rt.arena; 锚定屏幕正中偏上不动) + 受击箱 (布局 rt.hitbox)
            var zone = _ugui.MakePanel(_root, "zone",
                Raid.RaidLayout.ClampSize(_rt.Arena.W), Raid.RaidLayout.ClampSize(_rt.Arena.H),
                new Color(1f, 1f, 1f, 0.03f), false);
            UguiBuilder.AnchorCenter(zone, _root, 0.5f, 0.58f);
            _hitbox = _ugui.MakePanel(_root, "hitbox",
                Raid.RaidLayout.ClampSize(_rt.Hitbox.W), Raid.RaidLayout.ClampSize(_rt.Hitbox.H),
                HitboxNormal, false);
            UguiBuilder.AnchorCenter(_hitbox, _root, _rt.Hitbox.X, _rt.Hitbox.Y);
            _hitboxImg = _hitbox.GetComponent<Image>();
            _lastBandShown = -1;                              // v1.34.0: 敌名旁 近/中/远 首帧必写
            var hbTmp = _ugui.MakeText(_hitbox, "en", _enemyName, 18f, new Color(0.95f, 0.65f, 0.55f), TextAlignmentOptions.Center);
            Stretch(hbTmp.rectTransform);

            // v1.30.0: 战斗面板玩家 HP 文字删除 (状态栏 raid_st.hp 经 on_hp_change 实时同步, 不再双写)

            // 准星 (程序化十字 sprite; 包 ui/crosshair.png 可覆盖; 尺寸布局 rt.crosshair.size)
            float crossSize = Raid.RaidLayout.ClampSize(_rt.Crosshair.Size);
            var crossGo = new GameObject("cross");
            _crossRt = crossGo.AddComponent<RectTransform>();
            _crossRt.SetParent(_root, false);
            _crossRt.sizeDelta = new Vector2(crossSize, crossSize);
            _crossRt.anchorMin = new Vector2(0.5f, 0.5f);
            _crossRt.anchorMax = new Vector2(0.5f, 0.5f);
            _crossRt.pivot = new Vector2(0.5f, 0.5f);
            _crossImg = crossGo.AddComponent<Image>();
            _crossImg.raycastTarget = false;
            Sprite cross = CrosshairSprite(_itp != null ? _itp.PackId : null);
            if (!(cross is null)) _crossImg.sprite = cross;

            // 四向预警: 扇形 (布局 rt.warn 尺寸/颜色, 闪烁) + 弧环 (布局 rt.arc, Radial360 填充)
            float warnSize = Raid.RaidLayout.ClampSize(_rt.Warn.Size);
            float arcSize = Raid.RaidLayout.ClampSize(_rt.Arc.Size);
            _warnColor = new Color(Raid.RaidLayout.Clamp01(_rt.Warn.R), Raid.RaidLayout.Clamp01(_rt.Warn.G),
                Raid.RaidLayout.Clamp01(_rt.Warn.B), Raid.RaidLayout.Clamp01(_rt.Warn.A));
            _arcColor = new Color(Raid.RaidLayout.Clamp01(_rt.Arc.R), Raid.RaidLayout.Clamp01(_rt.Arc.G),
                Raid.RaidLayout.Clamp01(_rt.Arc.B), Raid.RaidLayout.Clamp01(_rt.Arc.A));
            for (int d = 0; d < 4; d++)
            {
                var fanGo = new GameObject("fan" + d);
                var frt = fanGo.AddComponent<RectTransform>();
                frt.SetParent(_root, false);
                frt.sizeDelta = new Vector2(warnSize, warnSize);
                PlaceDir(frt, FanRelX[d], FanRelY[d], FanRot[d], new Vector2(0.5f, 0f));
                var fimg = fanGo.AddComponent<Image>();
                fimg.raycastTarget = false;
                if (!(_spFan is null)) fimg.sprite = _spFan;
                fimg.color = _warnColor;
                _fans[d] = fimg;

                var arcGo = new GameObject("arc" + d);
                var art = arcGo.AddComponent<RectTransform>();
                art.SetParent(_root, false);
                art.sizeDelta = new Vector2(arcSize, arcSize);
                PlaceDir(art, FanRelX[d], FanRelY[d], 0f, new Vector2(0.5f, 0.5f));
                var aimg = arcGo.AddComponent<Image>();
                aimg.raycastTarget = false;
                if (!(_spRing is null)) aimg.sprite = _spRing;
                aimg.color = _arcColor;
                aimg.type = Image.Type.Filled;
                aimg.fillMethod = Image.FillMethod.Radial360;
                aimg.fillOrigin = ArcOrigin[d];
                aimg.fillClockwise = ArcClockwise[d];
                aimg.fillAmount = 0f;
                _arcs[d] = aimg;
                fanGo.SetActive(false);
                arcGo.SetActive(false);
            }

            // 底部 (布局 rt.bottom): 逃跑 rt.flee / 投掷 rt.throw / 武器切换 rt.switch (手动按钮:
            // Locked 光标下 uGUI Button 不可点); 状态行 rt.status 钉板上方
            var bottom = _ugui.MakePanel(_root, "bottom",
                Raid.RaidLayout.ClampSize(_rt.Bottom.W), Raid.RaidLayout.ClampSize(_rt.Bottom.H),
                new Color(0.10f, 0.09f, 0.08f, 0.92f), false);
            UguiBuilder.AnchorCenter(bottom, _root, _rt.Bottom.X, _rt.Bottom.Y);
            _fleeRt = ManualButton(bottom, "flee", "逃跑",
                Raid.RaidLayout.ClampSize(_rt.Flee.W), Raid.RaidLayout.ClampSize(_rt.Flee.H), _rt.Flee.X, out _fleeImg);
            if (_fleeChance <= 0) { try { _fleeRt.gameObject.SetActive(false); } catch { } }   // 匍匐等 0% = 隐藏 (旧面板同规)
            _throwRt = ManualButton(bottom, "throw", "投掷",
                Raid.RaidLayout.ClampSize(_rt.Throw.W), Raid.RaidLayout.ClampSize(_rt.Throw.H), _rt.Throw.X, out _throwImg);
            var tt = _throwRt.GetChild(0);
            _throwTmp = tt is null ? null : tt.GetComponent<TextMeshProUGUI>();
            _throwShown = false;
            try { _throwRt.gameObject.SetActive(false); } catch { }   // 初始隐藏, TickThrowButton 按槽显隐
            _switchRt = ManualButton(bottom, "switch", " ",
                Raid.RaidLayout.ClampSize(_rt.Switch.W), Raid.RaidLayout.ClampSize(_rt.Switch.H), _rt.Switch.X, out _switchImg);
            var st = _switchRt.GetChild(0);
            _switchTmp = st is null ? null : st.GetComponent<TextMeshProUGUI>();
            _statusTmp = _ugui.MakeText(_root, "status", " ", 16f, new Color(0.85f, 0.82f, 0.72f), TextAlignmentOptions.Center);
            _statusTmp.rectTransform.sizeDelta = new Vector2(
                Raid.RaidLayout.ClampSize(_rt.Status.W), Raid.RaidLayout.ClampSize(_rt.Status.H));
            UguiBuilder.AnchorCenter(_statusTmp.rectTransform, _root, _rt.Status.X, _rt.Status.Y);

            UpdateEnemyHp();
            UpdateSwitchLabel();
            _lastStatus = null;
            UpdateStatus(true);
            return true;
        }

        /// <summary>手动按钮 (暗色板 + 文本, raycast 全关; 点击由 TickFire 手动 rect 判定)。</summary>
        private RectTransform ManualButton(RectTransform parent, string name, string label, float w, float h, float x, out Image img)
        {
            var rt = _ugui.MakePanel(parent, name, w, h, new Color(0.16f, 0.12f, 0.09f, 0.95f), false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0f);
            img = rt.GetComponent<Image>();
            var tmp = _ugui.MakeText(rt, "txt", label, 18f, new Color(0.92f, 0.85f, 0.70f), TextAlignmentOptions.Center);
            Stretch(tmp.rectTransform);
            return rt;
        }

        private void PlaceDir(RectTransform rt, float relX, float relY, float rotZ, Vector2 pivot)
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = pivot;
            rt.localEulerAngles = new Vector3(0f, 0f, rotZ);
            var rect = _root.rect;
            rt.anchoredPosition = new Vector2((relX - 0.5f) * rect.width, (relY - 0.5f) * rect.height);
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private void UpdateEnemyHp()
        {
            float pct = _enemyHpMax > 0 ? _enemyHp / (float)_enemyHpMax : 0f;
            UguiBuilder.SetFill(_hpFill, pct);
            if (!(_hpTmp is null)) try { _hpTmp.text = "HP " + _enemyHp + "/" + _enemyHpMax; } catch { }
            if (!(_hpFill is null))
                try { _hpFill.color = pct > 0.5f ? new Color(0.55f, 0.80f, 0.40f) : pct > 0.25f ? new Color(0.90f, 0.60f, 0.20f) : new Color(0.85f, 0.25f, 0.20f); } catch { }
        }

        /// <summary>v1.30.0: 玩家 HP 变化实时回调 (cfg on_hp_change, pss 侧同步状态栏 raid_st.hp;
        /// 战斗面板玩家 HP 文字已删, 不再双写)。回调异常隔离不炸战斗。</summary>
        private void NotifyPlayerHp()
        {
            var itp = _itp; var fn = _onHpChange;
            if (fn == null || itp == null) return;
            try
            {
                itp.BeginRun();
                itp.CallCallable(fn, new List<object>
                {
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["hp"] = (long)_playerHp,
                        ["max"] = (long)Raid.RaidCore.MaxStat,
                    },
                }, 0);
            }
            catch (Exception e) { PsApi.Warn(_logger, "[rt] on_hp_change 回调异常: " + e.Message); }
        }

        private void UpdateSwitchLabel()
        {
            if (_switchTmp is null) return;
            try { _switchTmp.text = "武器: " + (_preferMelee ? "近战" : "枪械") + " (点击切换)"; } catch { }
        }

        private void SetDirActive(int dir, bool on)
        {
            try { if (!(_fans[dir] is null)) _fans[dir].gameObject.SetActive(on); } catch { }
            try { if (!(_arcs[dir] is null)) _arcs[dir].gameObject.SetActive(on); } catch { }
        }

        // ==================== 结束路径 (光标配对: 每条都 ReleaseCursor + 箱/地窗 Show) ====================

        /// <summary>统一结束: 销毁 UI → 还光标 → 还箱/地窗 → 回调 pss on_end (abort 不回调)。
        /// v1.30.0: 载荷加 sp_cost = RtSpCost(战斗时长秒) — rt 战斗体力结算 (旧回合制每回合 -6 的等价)。</summary>
        private void EndCombat(string result, bool callback = true)
        {
            if (_phase != Phase.Active) return;
            _phase = Phase.Idle;
            string eid = _enemyId, ename = _enemyName;
            int php = _playerHp;
            int spCost = _combatT0 >= 0f ? Raid.RaidCore.RtSpCost(Time.unscaledTime - _combatT0) : 0;
            DestroyUi();
            ReleaseCursor();
            ClearBehaviorState();                             // v1.34.0: 行为机/护盾/距离带全清 (退出路径铁律)
            try { Grid?.BoxShow(true); } catch { }
            try { Grid?.SetGroundVisible(true); } catch { }
            PsApi.Log(_logger, $"[rt] 实时战斗结束: {eid} result={result} player_hp={php} sp_cost={spCost}");
            var itp = _itp; var fn = _onEnd;
            _itp = null; _onEnd = null; _onHpChange = null;
            if (!callback || fn == null || itp == null) return;
            try
            {
                itp.BeginRun();
                itp.CallCallable(fn, new List<object>
                {
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["result"] = result,
                        ["player_hp"] = (long)php,
                        ["sp_cost"] = (long)spCost,
                        ["enemy_id"] = eid,
                        ["enemy_name"] = ename,
                    },
                }, 0);
            }
            catch (Exception e) { PsApi.Warn(_logger, $"[rt] on_end 回调异常 ({result}): {e.Message}"); }
        }

        private void DestroyUi()
        {
            try { if (!(_go is null)) UnityEngine.Object.Destroy(_go); } catch { }
            _go = null; _root = null;
            _nameTmp = _hpTmp = _statusTmp = _switchTmp = _throwTmp = null;
            _hpFill = null;
            _hitbox = _crossRt = null;
            _crossImg = _hitboxImg = null;
            for (int d = 0; d < 4; d++) { _fans[d] = null; _arcs[d] = null; }
            _fleeRt = _switchRt = _throwRt = null;
            _fleeImg = _switchImg = _throwImg = null;
            _throwShown = false;
            _lastStatus = null;
            // v1.34.0: 绿点/红点 GameObject 随 _go 销毁 — 只清 UI 引用, 逻辑态 (Alive/总数)
            // 保留给 Relayout RespawnShieldUi 重建; 战斗结束路径由 ClearBehaviorState 全清
            foreach (var dot in _dots) { dot.Rt = null; dot.Img = null; }
            _redDot = null;
            foreach (var b in _swarmBoxes) { b.Rt = null; b.Img = null; }   // v1.36.0: 鼠群子箱同规 (游走器保留)
            foreach (var b in _summonBoxes) { b.Rt = null; b.Img = null; }  // v1.45.0: 召唤子箱同规 (游走器/HP 保留)
        }

        // ==================== 光标配对 (ISIL: flag>0 → Cursor.visible=false + lockState=Locked) ====================

        private void HoldCursor()
        {
            if (_cursorHeld) return;
            try
            {
                var sui = StoreUIManager.Instance;
                if (!(sui is null)) { sui.IncrementMouseFlag(); _cursorHeld = true; }
            }
            catch (Exception e) { PsApi.Warn(_logger, "[rt] IncrementMouseFlag 失败 (光标不隐藏, 战斗继续): " + e.Message); }
        }

        private void ReleaseCursor()
        {
            if (!_cursorHeld) return;
            _cursorHeld = false;
            try
            {
                var sui = StoreUIManager.Instance;
                if (!(sui is null)) sui.DecrementMouseFlag();
            }
            catch (Exception e) { PsApi.Warn(_logger, "[rt] DecrementMouseFlag 失败: " + e.Message); }
        }

        // ==================== 生命周期 ====================

        /// <summary>场景撤离 (DoExit): 战斗中止 (abort, 不回调 — 场景 pss 正在拆), 光标/战斗锁照常归还。</summary>
        internal void OnSceneExit() => EndCombat("abort", false);

        /// <summary>Unity 场景卸载: 对象已随场景销毁, 只清引用 + 光标计数兜底 (计数器在游戏侧, 不随场景销毁)。</summary>
        internal void OnSceneLeft()
        {
            DestroyUi();
            ReleaseCursor();
            ClearBehaviorState();                             // v1.34.0: 行为机兜底全清
            _phase = Phase.Idle;
            _itp = null;
            _onEnd = null;
            _onHpChange = null;
        }

        // ==================== sprite 三件套 (SetPixels32→Apply→Sprite.Create; GetHoldRingSprite 范式) ====================

        /// <summary>FrameSlicer 预热 (Plugin 加载期入队): 三件套 sprite 首次战斗前生成。</summary>
        internal void Prewarm() => EnsureSprites();

        private void EnsureSprites()
        {
            if (!(_spCross is null)) return;
            try
            {
                _spCross = MakeCrossSprite();
                _spFan = MakeFanSprite();
                _spRing = MakeRingSprite();
            }
            catch (Exception e) { PsApi.Err(_logger, "[rt] 程序化 sprite 生成失败: " + e); }
        }

        private static Sprite Bake(int size, Func<int, int, byte> alpha, Vector2 pivot)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = new Color32(255, 255, 255, alpha(x, y));
            tex.SetPixels32(px);
            tex.Apply();
            var sp = Sprite.Create(tex, new Rect(0, 0, size, size), pivot, 100f);
            sp.hideFlags = HideFlags.HideAndDontSave;
            _spKeepAlive.Add(tex);
            _spKeepAlive.Add(sp);
            return sp;
        }

        /// <summary>十字准星 64²: 臂粗 5px, 中心空 6px, 臂长 30px。</summary>
        private static Sprite MakeCrossSprite()
            => Bake(64, (x, y) =>
            {
                float dx = Math.Abs(x - 32f + 0.5f), dy = Math.Abs(y - 32f + 0.5f);
                bool arm = (dx <= 2.5f && dy >= 6f && dy <= 30f) || (dy <= 2.5f && dx >= 6f && dx <= 30f);
                return arm ? (byte)235 : (byte)0;
            }, new Vector2(0.5f, 0.5f));

        /// <summary>扇形 256²: 底中 apex ±55° 楔形朝正上, 角/弧边缘羽化 (PlaceDir 按方向旋转)。</summary>
        private static Sprite MakeFanSprite()
            => Bake(256, (x, y) =>
            {
                float dx = x - 128f + 0.5f, dy = y + 0.5f;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                float ang = (float)(Math.Atan2(dx, dy) * 180.0 / Math.PI);
                if (dist > 254f || Math.Abs(ang) > 55f) return (byte)0;
                float edge = Math.Abs(ang) > 50f ? (55f - Math.Abs(ang)) / 5f : 1f;
                float rad = dist > 216f ? (254f - dist) / 38f : 1f;
                float v = edge * rad;
                if (v < 0f) v = 0f;
                if (v > 1f) v = 1f;
                return (byte)(220f * v);
            }, new Vector2(0.5f, 0f));

        /// <summary>弧环 256²: 外径 122 内径 100 圆环 (Radial360 Filled 用, 必须有 sprite — spriteless 不刷新)。</summary>
        private static Sprite MakeRingSprite()
            => Bake(256, (x, y) =>
            {
                float dx = x - 128f + 0.5f, dy = y - 128f + 0.5f;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (dist > 122f || dist < 100f) return (byte)0;
                float vo = dist > 119f ? (122f - dist) / 3f : 1f;
                float vi = dist < 103f ? (dist - 100f) / 3f : 1f;
                float v = vo < vi ? vo : vi;
                if (v < 0f) v = 0f;
                return (byte)(255f * v);
            }, new Vector2(0.5f, 0.5f));

        /// <summary>准星 sprite: 包级 ui/crosshair.png 覆盖 (每包缓存; 无覆盖/解码失败 = 程序化十字)。</summary>
        private Sprite CrosshairSprite(string packId)
        {
            if (string.IsNullOrEmpty(packId) || PackSourceOf == null) return _spCross;
            if (_crossOverride.TryGetValue(packId, out var cached)) return cached;
            if (_crossTried.Contains(packId)) return _spCross;
            _crossTried.Add(packId);
            try
            {
                var src = PackSourceOf(packId);
                if (src == null || !src.HasFile("ui/crosshair.png")) return _spCross;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                if (!ImageConversion.LoadImage(tex, src.ReadBytes("ui/crosshair.png")))
                {
                    UnityEngine.Object.Destroy(tex);
                    PsApi.Warn(_logger, "[rt] 包准星解码失败: " + packId + "/ui/crosshair.png, 用程序化十字");
                    return _spCross;
                }
                var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                sp.hideFlags = HideFlags.HideAndDontSave;
                _overrideKeepAlive.Add(tex);
                _overrideKeepAlive.Add(sp);
                _crossOverride[packId] = sp;
                PsApi.Log(_logger, "[rt] 准星用包覆盖: " + packId + "/ui/crosshair.png");
                return sp;
            }
            catch (Exception e) { PsApi.Warn(_logger, "[rt] 包准星加载失败: " + e.Message); return _spCross; }
        }

        // ==================== v1.34.0 行为状态机 (§3.10.1 卡组加权抽行为 + 受击箱变换 + 护盾动态目标) ====================

        /// <summary>开战归零: 行为机/护盾/overlay 全清; v1.37.0: 首次抽行为 = 0~0.3s 后 (开战即动,
        /// 原为一个完整敌节奏 2.2-3.5s 开局站桩), ambient 空窗漂移游走器重建。</summary>
        private void ResetBehavior(float now)
        {
            _dots.Clear();
            _redDot = null; _redUntil = -1f; _dotsTotal = 0;
            _beh = BehState.None;
            _behHist.Clear();
            _behUntil = -1f;
            _behNextAt = now + Raid.RaidCore.FirstBehaviorDelay(_rng);
            _chargeAccum = 0f;
            _exposeUntil = _weakenUntil = -1f;
            _warnHeavyMul = 1f;
            _lastBandShown = -1;
            // v1.36.0: M3.7 新增态归零 (burst/zig/circle/leap/feint/strafe 噪声/麻痹/鼠群游走器)
            _burstActive = false;
            _dashSegLeft = 0;
            _zig = null;
            _feintCancelAt = -1f;
            _strafeNoise = 0f; _strafePhase = 0f;
            _stunUntil = -1f;
            _swarmBoxes.Clear();
            _summonBoxes.Clear();                             // v1.45.0: 召唤子箱全清 (母体死=win 时同归)
            _nextSummonAt = -1f;
            _lastVisualTick = -1f;
            // v1.37.0: 空窗微漂移游走器 (低速 ±40px; randStart 开局即有偏移, 间隙 >0.6s 才推进)
            _ambient = new ZigWalker(_rng, Raid.RaidCore.AmbientSpeed, Raid.RaidCore.AmbientBound,
                Raid.RaidCore.AmbientTurnLo, Raid.RaidCore.AmbientTurnHi, true);
        }

        /// <summary>v1.37.0: 行为间隙 = 敌节奏 ×tempo (cfg tempo 个体化; 攻击节奏 _nextAtkAt 不动)。</summary>
        private float BehGap() => Raid.RaidCore.EnemyInterval(_rng) * _tempo;

        /// <summary>结束路径全清 (EndCombat/OnSceneLeft): 含 cfg 态 (deck/armor/band/hitbox_scale/depth/
        /// warn_mul/swarm/summon/escape_after), 下次 RtStart 重解析。</summary>
        private void ClearBehaviorState()
        {
            ResetBehavior(Time.unscaledTime);
            _deck = null;
            _armor = 0;
            _band = 1;
            _shieldCfg = new RtShieldCfg();
            _chargeCfg = new RtChargeCfg();
            _hitboxScale = 1f;
            _depth = 0;
            _warnMul = 1f;
            _tempo = 1f;
            _swarmCfg = null;
            _summonCfg = null;
            _escapeAfter = 0f;
        }

        private void TickBehavior(float now)
        {
            // 1) 定时状态推进 (行为 = 受击箱的定时变换状态)
            switch (_beh)
            {
                case BehState.Strafe:
                case BehState.Shrink:
                    if (now >= _behUntil) EndBehavior(now);
                    break;
                case BehState.Hide:
                    if (now >= _behUntil)
                    {
                        _band = Raid.RaidCore.ClampBand(_band - 1);        // 掩蔽拉远还原
                        EndBehavior(now);
                        Log(_enemyName + "现形了 — 距离带还原");
                    }
                    break;
                case BehState.Charge:
                    if (now >= _behUntil) ChargeComplete(now);
                    break;
                case BehState.Stagger:
                    if (now >= _behUntil)
                    {
                        _beh = BehState.None;
                        _nextAtkAt = now + Raid.RaidCore.EnemyInterval(_rng);   // 节奏器恢复
                        _behNextAt = now + BehGap();   // v1.37.0: 行为间隙 ×tempo (个体化)
                    }
                    break;
                case BehState.ShieldDots:
                    TickDots(now);
                    break;
                case BehState.ShieldRed:
                    if (now >= _redUntil) EndShieldTimeout(now);
                    break;
                case BehState.RedDot:                                    // v1.46.0: 暴击窗超时消散 (无惩罚)
                    if (now >= _redUntil) EndRedDotTimeout(now);
                    break;
                // ---- v1.36.0 M3.7 新行为 ----
                case BehState.Dash:
                    // 段完成 → 还有段就立即接下一段 (新方向/距离掷签), 段尽结束行为
                    if (_burstActive && now >= _burstT0 + _burstDur)
                    {
                        if (_dashSegLeft > 0) { _dashSegLeft--; StartDashBurst(now); }
                        else EndBehavior(now);
                    }
                    break;
                case BehState.Zigzag:
                case BehState.Circle:
                case BehState.Leap:
                case BehState.Feint:
                    if (now >= _behUntil) EndBehavior(now);
                    break;
            }
            // 2) 非行为期间按敌节奏加权抽卡 (同种冷却 2 间隔防连出; 无卡组 = 现状静止)
            if (_beh == BehState.None && _deck != null && _deck.Count > 0 && now >= _behNextAt)
                DrawBehavior(now);
            // 3) 受击箱变换应用 (移动/缩放/隐藏/变色 — 命中判定走同一 RectTransform, 天然同步)
            ApplyHitboxVisual(now);
            TickBandLabel();
        }

        private void EndBehavior(float now)
        {
            if (_beh == BehState.Dash) _burstActive = false;    // v1.36.0: dash 结束位移回基准 (与 strafe 结束回中正弦同规)
            _beh = BehState.None;
            _behNextAt = now + BehGap();   // v1.37.0: 行为间隙 ×tempo (个体化)
        }

        private void DrawBehavior(float now)
        {
            var cand = new List<RtBehavior>();
            foreach (var b in _deck) if (b.Weight > 0f && !_behHist.Contains(b.Type)) cand.Add(b);
            if (cand.Count == 0) foreach (var b in _deck) if (b.Weight > 0f) cand.Add(b);   // 全冷却 → 放开
            if (cand.Count == 0) { _behNextAt = now + 1f; return; }
            float total = 0f;
            foreach (var b in cand) total += b.Weight;
            float r = (float)(_rng.NextDouble() * total);
            var pick = cand[cand.Count - 1];
            foreach (var b in cand) { r -= b.Weight; if (r <= 0f) { pick = b; break; } }
            _behHist.Enqueue(pick.Type);
            while (_behHist.Count > 2) _behHist.Dequeue();
            _burstActive = false;                                 // v1.36.0: 新行为清旧横冲段 (dash 分支随即重开)
            switch (pick.Type)
            {
                case "strafe":
                    _strafeSpeed = ClampF(BehParam(pick, "speed", 2f), 0.5f, 6f);
                    _strafeAmp = ClampF(BehParam(pick, "amplitude", 80f), 10f, 200f);
                    // v1.36.0: 相位噪声 (缺省 0 = 旧正弦原样; 推荐新配置 1.5 rad/s)
                    _strafeNoise = Raid.RaidCore.StrafeNoiseRate(BehParam(pick, "noise", 0f));
                    _strafePhase = 0f;
                    _beh = BehState.Strafe;
                    _behT0 = now;
                    _behUntil = now + ClampF(BehParam(pick, "duration", 3f), 0.5f, 8f);
                    Log(_enemyName + "开始游走! (受击箱水平往返)");
                    break;
                case "shrink":
                    _beh = BehState.Shrink;
                    _behT0 = now;
                    _behUntil = now + ClampF(BehParam(pick, "duration", Raid.RaidCore.ShrinkDuration), 0.5f, 4f);
                    Log(_enemyName + "伏低了身子! (受击箱 ×0.5)");
                    break;
                case "hide":
                {
                    float dur = BehParam(pick, "duration", 0f);
                    if (dur <= 0f) dur = 1f + (float)_rng.NextDouble();          // 缺省 1-2s
                    _beh = BehState.Hide;
                    _behT0 = now;
                    _behUntil = now + ClampF(dur, 0.5f, 3f);
                    _band = Raid.RaidCore.ClampBand(_band + 1);                  // 距离带临时拉远一档 (结束还原)
                    Log(_enemyName + "隐匿了! 受击箱消失, 距离拉远");
                    break;
                }
                case "shield":
                    EnterShield(now);
                    break;
                case "charge":
                    _beh = BehState.Charge;
                    _behT0 = now;
                    _behUntil = now + ClampF(BehParam(pick, "duration", _chargeCfg.Duration), 0.5f, 5f);
                    _chargeAccum = 0f;
                    Log(_enemyName + "发起冲锋!! (累计伤害 ≥" + Math.Max(1, (int)Math.Round(Raid.RaidCore.ChargeStaggerThreshold(_enemyHpMax) * _chargeCfg.StaggerMul)) + " 可击退)");
                    break;
                // ---- v1.36.0 M3.7 新行为 (参数全走 BehParam 带默认值 + RaidCore 钳制) ----
                case "dash":
                    _dashDist = Raid.RaidCore.DashDistance(BehParam(pick, "distance", 200f));
                    _dashDur = Raid.RaidCore.DashDuration(BehParam(pick, "duration", 0.3f));
                    _dashSegLeft = Raid.RaidCore.Clamp((int)BehParam(pick, "count", 1f), 1, 2) - 1;   // 一次行为 1-2 段
                    _beh = BehState.Dash;
                    _behT0 = now;
                    StartDashBurst(now);
                    Log(_enemyName + "猛然突进! (受击箱高速横移)");
                    break;
                case "zigzag":
                {
                    float turn = Raid.RaidCore.ZigzagTurnInterval(BehParam(pick, "turn_interval", 0.5f));
                    _zig = new ZigWalker(_rng,
                        Raid.RaidCore.ZigzagSpeed(BehParam(pick, "speed", 260f)),
                        Raid.RaidCore.ZigzagBound(BehParam(pick, "bound", 220f)),
                        turn * 0.8f, turn * 1.2f, false);           // 每次变向 ±20% 随机
                    _beh = BehState.Zigzag;
                    _behT0 = now;
                    _behUntil = now + Raid.RaidCore.ZigzagDuration(BehParam(pick, "duration", 2.2f));
                    Log(_enemyName + "开始折线乱窜! (受击箱 8 向随机游走)");
                    break;
                }
                case "circle":
                    _circleSpeed = Raid.RaidCore.CircleSpeed(BehParam(pick, "speed", 2.2f));
                    _circleRx = Raid.RaidCore.CircleRx(BehParam(pick, "rx", 150f));
                    _circleRy = Raid.RaidCore.CircleRy(BehParam(pick, "ry", 70f));
                    _beh = BehState.Circle;
                    _behT0 = now;
                    _behUntil = now + Raid.RaidCore.CircleDuration(BehParam(pick, "duration", 2.5f));
                    Log(_enemyName + "绕着你兜圈! (受击箱椭圆轨道)");
                    break;
                case "leap":
                    _leapHeight = Raid.RaidCore.LeapHeight(BehParam(pick, "height", 140f));
                    _beh = BehState.Leap;
                    _behT0 = now;
                    _behUntil = now + Raid.RaidCore.LeapDuration(BehParam(pick, "duration", 0.8f));
                    Log(_enemyName + "纵身跃起! (滞空受击箱缩小)");
                    break;
                case "feint":
                    _feintCancelChance = Raid.RaidCore.FeintCancelChance(BehParam(pick, "cancel_chance", 0.35f));
                    _beh = BehState.Feint;
                    _behT0 = now;
                    _behUntil = now + Raid.RaidCore.FeintDuration(BehParam(pick, "duration", 3f));
                    Log(_enemyName + "压低重心试探着你… (佯攻: 下一次预警可能是假动作)");
                    break;
                // ---- v1.46.0 reddot 暴击窗 (M3.6 红点机制推广, gunworks v0.43.0) ----
                case "reddot":
                    _beh = BehState.RedDot;
                    _redUntil = now + Raid.RaidCore.RedDotWindow(BehParam(pick, "window", 2f));
                    CreateRedUi();
                    Log(_enemyName + "露出了破绽 — 红点亮起, 命中即暴击 (×" + Raid.RaidCore.RedDotCritMul().ToString("0") + " 伤害)!");
                    break;
            }
            UpdateStatus(true);
        }

        /// <summary>冲锋完成 (未被击退): 距离带拉近一档 + 一次重击 (atk×heavy_mul, 走正常 WARNING/RESOLVE 可闪避)。</summary>
        private void ChargeComplete(float now)
        {
            _beh = BehState.None;
            _band = Raid.RaidCore.ClampBand(_band - 1);
            _behNextAt = now + BehGap();   // v1.37.0: 行为间隙 ×tempo (个体化)
            Log(_enemyName + "冲到了眼前 — 距离拉近, 重击来袭!");
            StartWarning(now);
            _warnHeavyMul = Math.Max(1f, _chargeCfg.HeavyMul);
        }

        /// <summary>冲锋击退: 冲锋取消 + 敌硬直 1s (节奏器暂停, Stagger 结束恢复)。</summary>
        private void StaggerEnemy(float now)
        {
            _beh = BehState.Stagger;
            _behUntil = now + Raid.RaidCore.StaggerDuration;
            if (_warnActive) CancelWarning();
            Log("冲锋被打断! " + _enemyName + "硬直 1 秒!");
            UpdateStatus(true);
        }

        private void CancelWarning()
        {
            _warnActive = false;
            _warnHeavyMul = 1f;
            _feintCancelAt = -1f;                             // v1.36.0: 撤预警连带清 feint 假动作挂起
            SetDirActive(_warnDir, false);
        }

        // ==================== v1.36.0 M3.7: dash/feint 横冲位移段 + 鼠群子箱 ====================

        /// <summary>dash 行为一段: 随机方向 (左右为主, 可加小 y 分量) × 距离 (基准 ±20% 掷签),
        /// ease-out (1-(1-p)²); 多段时 from 接上一段终点。</summary>
        private void StartDashBurst(float now)
        {
            float dist = Raid.RaidCore.DashRollDistance(_dashDist, _rng);
            float sx = _rng.NextDouble() < 0.5 ? -1f : 1f;
            var from = _burstActive ? _burstTo : Vector2.zero;
            _burstFrom = from;
            _burstTo = new Vector2(from.x + sx * dist, from.y + (float)(_rng.NextDouble() - 0.5) * 0.5f * dist);
            _burstT0 = now;
            _burstDur = Math.Max(0.05f, _dashDur);
            _burstActive = true;
        }

        /// <summary>feint 假动作横跳 120-200px (纯水平, 复用 dash 位移段机制)。</summary>
        private void StartFeintHop(float now)
        {
            float dist = Raid.RaidCore.FeintHopDistance(_rng);
            float sx = _rng.NextDouble() < 0.5 ? -1f : 1f;
            var from = _burstActive ? _burstTo : Vector2.zero;
            _burstFrom = from;
            _burstTo = new Vector2(from.x + sx * dist, from.y);
            _burstT0 = now;
            _burstDur = 0.25f;
            _burstActive = true;
        }

        /// <summary>当前横冲偏移 (root 锚点坐标): ease-out 到目标; 段完成后保持末位置
        /// (下个行为抽卡时清零 — 与 strafe 结束回中正弦同规)。</summary>
        private Vector2 BurstOffset(float now)
        {
            if (!_burstActive) return Vector2.zero;
            float p = (now - _burstT0) / _burstDur;
            if (p >= 1f) return _burstTo;
            float e = Raid.RaidCore.DashEaseOut(p);
            return new Vector2(_burstFrom.x + (_burstTo.x - _burstFrom.x) * e,
                               _burstFrom.y + (_burstTo.y - _burstFrom.y) * e);
        }

        /// <summary>鼠群子箱生成 (RtStart, BuildUi 之后): count 个独立 RectTransform (中心锚),
        /// 尺寸 = 布局基准 × box_scale × hitbox_scale; 各自 zigzag 自走 (240±40 px/s, 变向 0.4-0.7s,
        /// spread 约束, 每箱独立种子序列)。主受击箱隐藏由 ApplyHitboxVisual 负责。</summary>
        private void CreateSwarmBoxes(float now)
        {
            _swarmBoxes.Clear();
            if (_swarmCfg == null || _root is null) return;
            for (int i = 0; i < _swarmCfg.Count; i++)
            {
                var b = new SwarmBox();
                float speed = _swarmCfg.Speed                 // v1.46.0: cfg speed (缺省 240 = 旧 SwarmSpeedBase)
                    + ((float)_rng.NextDouble() * 2f - 1f) * Raid.RaidCore.SwarmSpeedJitter;
                b.Walker = new ZigWalker(_rng, speed, _swarmCfg.Spread,
                    Raid.RaidCore.SwarmTurnLo, Raid.RaidCore.SwarmTurnHi, true);
                CreateSwarmBoxUi(b, i);
                _swarmBoxes.Add(b);
            }
            Log(_enemyName + "散开了 — " + _swarmBoxes.Count + " 个目标四处乱窜! (共享血量, 打谁都行)");
        }

        private void CreateSwarmBoxUi(SwarmBox b, int idx)
        {
            if (_root is null || _swarmCfg == null) return;
            float bw = Raid.RaidLayout.ClampSize(_rt.Hitbox.W) * _swarmCfg.BoxScale * _hitboxScale;
            float bh = Raid.RaidLayout.ClampSize(_rt.Hitbox.H) * _swarmCfg.BoxScale * _hitboxScale;
            var rt = _ugui.MakePanel(_root, "rat" + idx, bw, bh, SwarmBoxColor, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            b.Rt = rt;
            b.Img = rt.GetComponent<Image>();
        }

        /// <summary>鼠群子箱逐帧: zigzag 推进 + 位置 = 基准点(含行为位移) + 游走偏移 +
        /// 闪白到期复原 + 隐匿时随主箱隐藏。</summary>
        private void TickSwarmBoxes(float now, float dt, Vector2 basePos, bool hidden)
        {
            foreach (var b in _swarmBoxes)
            {
                if (b.Rt is null) continue;
                b.Walker?.Tick(dt);
                if (b.FlashUntil > 0f && now >= b.FlashUntil)
                {
                    b.FlashUntil = -1f;
                    try { if (!(b.Img is null)) b.Img.color = SwarmBoxColor; } catch { }
                }
                try
                {
                    b.Rt.anchoredPosition = new Vector2(
                        basePos.x + (b.Walker != null ? b.Walker.Ox : 0f),
                        basePos.y + (b.Walker != null ? b.Walker.Oy : 0f));
                    if (b.Rt.gameObject.activeSelf == hidden) b.Rt.gameObject.SetActive(!hidden);
                }
                catch { }
            }
        }

        /// <summary>F10 Relayout 后按逻辑态重建鼠群 UI (游走器保留, 只重建 RectTransform)。</summary>
        private void RespawnSwarmUi()
        {
            if (_swarmCfg == null) return;
            for (int i = 0; i < _swarmBoxes.Count; i++)
                if (_swarmBoxes[i].Rt is null) CreateSwarmBoxUi(_swarmBoxes[i], i);
        }

        // ---- v1.45.0 嚎叫者召唤 (§4.5): 母体可攻, 小怪受击箱独立 HP 打死即灭 ----

        /// <summary>召唤计时 (Tick): 每 interval 秒若存活小怪 <cap 召 1 只 (召唤日志含同屏计数)。</summary>
        private void TickSummon(float now)
        {
            if (_summonCfg == null) return;
            int alive = 0;
            foreach (var b in _summonBoxes) if (!b.Dead) alive++;
            if (alive >= _summonCfg.Cap || now < _nextSummonAt) return;
            _nextSummonAt = now + _summonCfg.Interval;
            var b2 = new SummonBox { Hp = _summonCfg.ChildHp };
            float speed = Raid.RaidCore.SwarmSpeedBase
                + ((float)_rng.NextDouble() * 2f - 1f) * Raid.RaidCore.SwarmSpeedJitter;
            b2.Walker = new ZigWalker(_rng, speed, _summonCfg.Spread,
                Raid.RaidCore.SwarmTurnLo, Raid.RaidCore.SwarmTurnHi, true);
            CreateSummonBoxUi(b2, _summonBoxes.Count);
            _summonBoxes.Add(b2);
            Log(_enemyName + "发出凄厉的嚎叫 — 又一只小怪扑了出来! (场上 " + (alive + 1) + "/" + _summonCfg.Cap + ")");
        }

        private void CreateSummonBoxUi(SummonBox b, int idx)
        {
            if (_root is null || _summonCfg == null) return;
            float bw = Raid.RaidLayout.ClampSize(_rt.Hitbox.W) * _summonCfg.BoxScale * _hitboxScale;
            float bh = Raid.RaidLayout.ClampSize(_rt.Hitbox.H) * _summonCfg.BoxScale * _hitboxScale;
            var rt = _ugui.MakePanel(_root, "sum" + idx, bw, bh, SummonBoxColor, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            b.Rt = rt;
            b.Img = rt.GetComponent<Image>();
        }

        /// <summary>召唤子箱逐帧: zigzag 推进 + 位置 = 基准点(含行为位移) + 游走偏移 + 闪白到期复原;
        /// 隐匿时随主箱隐藏; 死箱跳过 (UI 已 SetActive(false))。</summary>
        private void TickSummonBoxes(float now, float dt, Vector2 basePos, bool hidden)
        {
            foreach (var b in _summonBoxes)
            {
                if (b.Dead || b.Rt is null) continue;
                b.Walker?.Tick(dt);
                if (b.FlashUntil > 0f && now >= b.FlashUntil)
                {
                    b.FlashUntil = -1f;
                    try { if (!(b.Img is null)) b.Img.color = SummonBoxColor; } catch { }
                }
                try
                {
                    b.Rt.anchoredPosition = new Vector2(
                        basePos.x + (b.Walker != null ? b.Walker.Ox : 0f),
                        basePos.y + (b.Walker != null ? b.Walker.Oy : 0f));
                    if (b.Rt.gameObject.activeSelf == hidden) b.Rt.gameObject.SetActive(!hidden);
                }
                catch { }
            }
        }

        /// <summary>F10 Relayout 后按逻辑态重建召唤子箱 UI (游走器/HP 保留, 只重建 RectTransform)。</summary>
        private void RespawnSummonUi()
        {
            if (_summonCfg == null) return;
            for (int i = 0; i < _summonBoxes.Count; i++)
                if (!_summonBoxes[i].Dead && _summonBoxes[i].Rt is null) CreateSummonBoxUi(_summonBoxes[i], i);
        }

        // ---- 护盾态: 绿点 (环带 120-260px, 部分移动/瞬移) → 全清 → 红点 (窗口内击中 = 破态虚弱) ----

        private void EnterShield(float now)
        {
            _beh = BehState.ShieldDots;
            _dots.Clear();
            int n = Raid.RaidCore.ShieldDotCount(_shieldCfg.Dots);
            _dotsTotal = n;
            for (int i = 0; i < n; i++)
            {
                var d = new ShieldDot
                {
                    Size = Raid.RaidCore.ShieldDotSize(14f + (float)_rng.NextDouble() * 26f),
                    Phase = (float)(_rng.NextDouble() * 6.283),
                    Speed = 0.8f + (float)_rng.NextDouble(),
                    NextTeleportAt = now + 1f + (float)_rng.NextDouble() * 0.5f,
                };
                d.Mover = _rng.NextDouble() < _shieldCfg.MovingRatio;
                d.Teleporter = !d.Mover && _rng.NextDouble() < _shieldCfg.TeleportRatio;
                d.HitsLeft = Raid.RaidCore.DotHitsForSize(d.Size);     // v1.34.1 拍板②: 按大小分 1/2/3 击碎
                var p = RingPos();
                d.BaseX = p.x; d.BaseY = p.y;
                CreateDotUi(d);
                _dots.Add(d);
            }
            Log(_enemyName + "撑起了护盾 — 先打碎绿点! (护盾中敌人攻击不停)");
        }

        private void TickDots(float now)
        {
            var hp = HitboxBasePos();
            foreach (var d in _dots)
            {
                if (!d.Alive || d.Rt is null) continue;
                if (d.Teleporter && now >= d.NextTeleportAt)
                {
                    var p = RingPos();
                    d.BaseX = p.x; d.BaseY = p.y;
                    d.NextTeleportAt = now + 1f + (float)_rng.NextDouble() * 0.5f;
                }
                float ox = 0f, oy = 0f;
                if (d.Mover)
                {
                    ox = (float)Math.Sin(now * d.Speed + d.Phase) * 16f;
                    oy = (float)Math.Cos(now * d.Speed * 0.8f + d.Phase) * 10f;
                }
                if (d.FlashUntil > 0f && now >= d.FlashUntil)            // v1.34.1: 命中闪白到期复原
                {
                    d.FlashUntil = -1f;
                    try { if (!(d.Img is null)) d.Img.color = DotGreen; } catch { }
                }
                try
                {
                    d.Rt.anchoredPosition = new Vector2(hp.x + d.BaseX + ox, hp.y + d.BaseY + oy);
                    d.Rt.sizeDelta = new Vector2(d.Size, d.Size);
                }
                catch { }
            }
        }

        /// <summary>绿点命中 (主目标 + rad 多目标: 开枪命中点 80px 内额外 ≤rad-1 个);
        /// v1.34.1 拍板②: 按大小分 1/2/3 击碎 — 每击伤害照扣敌 HP, 击数归零才碎 (未碎闪白反馈);
        /// 伤害 = 武器伤害 ×(1−盾减伤×(1−pen/100)) ×(1−ArmorEff(armor,pen)) (×破绽)。</summary>
        private void HitDots(ShieldDot first, float now)
        {
            float atk = _w.Unarmed ? _rng.Next(Raid.RaidCore.UnarmedAtkLo, Raid.RaidCore.UnarmedAtkHi + 1) : _w.Atk;
            var targets = new List<ShieldDot> { first };
            if (!_w.MeleeClass && _w.Rad > 1)
                foreach (var d in _dots)
                {
                    if (targets.Count >= _w.Rad) break;
                    if (d.Alive && !ReferenceEquals(d, first) && WithinRadius(d.Rt, Raid.RaidCore.RadMultiRadius))
                        targets.Add(d);
                }
            int total = 0, broken = 0;
            foreach (var t in targets)
            {
                int d0 = Raid.RaidCore.RollRtDamage(atk, _rng);
                float mul = (1f - Raid.RaidCore.ArmorEff(_shieldCfg.Reduction * 100f, _w.Pen))
                          * (1f - Raid.RaidCore.ArmorEff(_armor, _w.Pen));
                if (now < _exposeUntil) mul *= Raid.RaidCore.ExposeMul();
                total += Math.Max(1, (int)Math.Round(d0 * mul));
                t.HitsLeft--;
                if (t.HitsLeft <= 0)
                {
                    broken++;
                    t.Alive = false;
                    try { if (!(t.Rt is null)) t.Rt.gameObject.SetActive(false); } catch { }
                }
                else
                {
                    t.FlashUntil = now + 0.15f;                            // 未碎: 闪白一档 (TickDots 到期复原)
                    try { if (!(t.Img is null)) t.Img.color = Color.white; } catch { }
                }
            }
            _enemyHp = Math.Max(0, _enemyHp - total);
            UpdateEnemyHp();
            Log(broken > 0
                ? (broken > 1
                    ? "一击多中! 绿点破碎 ×" + broken + " — 共 " + total + " 点伤害"
                    : "绿点破碎 — " + total + " 点伤害 (剩 " + DotsAlive() + "/" + _dotsTotal + ")")
                : "绿点受击 — " + total + " 点伤害 (未碎, 还需 " + first.HitsLeft + " 击)");
            UpdateStatus(true);
            if (_enemyHp <= 0) { EndCombat("win"); return; }
            if (DotsAlive() == 0) EnterRedPhase(now);
        }

        private void EnterRedPhase(float now)
        {
            _beh = BehState.ShieldRed;
            _redUntil = now + Raid.RaidCore.ShieldRedWindow(_shieldCfg.RedWindow);
            CreateRedUi();
            Log("绿点全碎 — 红点出现! " + Raid.RaidCore.ShieldRedWindow(_shieldCfg.RedWindow).ToString("0.0") + "s 内击中它破盾!");
            UpdateStatus(true);
        }

        /// <summary>击中红点 = 破态: 盾结束 + 虚弱 2.5s (承伤 ×1.5, 受击箱金色)。</summary>
        private void BreakShield(float now)
        {
            _beh = BehState.None;
            _redUntil = -1f;
            HideRedUi();                                        // v1.46.0: 红点面板即隐 (此前残留到战斗结束)
            _behNextAt = now + BehGap();   // v1.37.0: 行为间隙 ×tempo (个体化)
            _weakenUntil = now + Raid.RaidCore.WeakenDuration;
            Log("破态! " + _enemyName + "虚弱 2.5 秒 — 承伤 ×1.5, 全力输出!");
            UpdateStatus(true);
        }

        /// <summary>红点超时未中: 盾结束回普通态, 不虚弱。</summary>
        private void EndShieldTimeout(float now)
        {
            _beh = BehState.None;
            _redUntil = -1f;
            HideRedUi();                                        // v1.46.0: 红点面板即隐 (与「红点消散」日志对齐)
            _behNextAt = now + BehGap();   // v1.37.0: 行为间隙 ×tempo (个体化)
            Log("红点消散 — " + _enemyName + "的护盾重置, 没有虚弱");
            UpdateStatus(true);
        }

        /// <summary>v1.46.0 reddot 暴击窗超时: 红点消散回普通态 (无惩罚)。</summary>
        private void EndRedDotTimeout(float now)
        {
            _beh = BehState.None;
            _redUntil = -1f;
            HideRedUi();
            _behNextAt = now + BehGap();
            Log("红点消散 — " + _enemyName + "收起了破绽");
            UpdateStatus(true);
        }

        /// <summary>红点面板即隐 (逻辑引用一并清; 面板 GameObject 随 _root 回收)。</summary>
        private void HideRedUi()
        {
            try { if (!(_redDot is null)) _redDot.gameObject.SetActive(false); } catch { }
            _redDot = null;
        }

        private ShieldDot DotAt(float pad)
        {
            foreach (var d in _dots)
                if (d.Alive && PointerInPad(d.Rt, pad)) return d;
            return null;
        }

        private int DotsAlive()
        {
            int n = 0;
            foreach (var d in _dots) if (d.Alive) n++;
            return n;
        }

        /// <summary>受击箱基准中心 (root 锚点坐标, 布局 rt.hitbox.x/y 屏比)。</summary>
        private Vector2 HitboxBasePos()
        {
            var rect = _root.rect;
            return new Vector2((_rt.Hitbox.X - 0.5f) * rect.width, (_rt.Hitbox.Y - 0.5f) * rect.height);
        }

        private Vector2 RingPos()
        {
            float ang = (float)(_rng.NextDouble() * Math.PI * 2.0);
            float r = Raid.RaidCore.ShieldRingLo + (float)_rng.NextDouble() * (Raid.RaidCore.ShieldRingHi - Raid.RaidCore.ShieldRingLo);
            return new Vector2((float)Math.Cos(ang) * r, (float)Math.Sin(ang) * r);
        }

        private void CreateDotUi(ShieldDot d)
        {
            if (_root is null) return;
            var rt = _ugui.MakePanel(_root, "dot", d.Size, d.Size, DotGreen, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            d.Rt = rt;
            d.Img = rt.GetComponent<Image>();
        }

        private void CreateRedUi()
        {
            if (_root is null) return;
            var p = RingPos();
            var hp = HitboxBasePos();
            var rt = _ugui.MakePanel(_root, "reddot", Raid.RaidCore.ShieldRedSize, Raid.RaidCore.ShieldRedSize, DotRed, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            try { rt.anchoredPosition = new Vector2(hp.x + p.x, hp.y + p.y); } catch { }
            _redDot = rt;
        }

        /// <summary>F10 Relayout 后按逻辑态重建护盾 UI (Alive 标记保留, 位置重新随机)。</summary>
        private void RespawnShieldUi()
        {
            if (_beh == BehState.ShieldDots)
                foreach (var d in _dots)
                    if (d.Alive) CreateDotUi(d);
            if (_beh == BehState.ShieldRed && _redDot is null)
                CreateRedUi();
            if (_beh == BehState.RedDot && _redDot is null)   // v1.46.0: 暴击窗红点同规重建
                CreateRedUi();
        }

        /// <summary>受击箱变换应用: 布局基准 × cfg hitbox_scale (v1.36.0 个体化) × 行为倍率链 ——
        /// 距离带短缺 ×0.85^n + 行为 (游走正弦+相位噪声/伏身 ×0.5/掩蔽隐藏/冲锋 lerp 向屏心 ×1.6/
        /// zigzag 折线/circle 椭圆轨道/leap 纵跳滞空缩小) + dash/feint 横冲段叠加 + 破绽 ×1.3;
        /// 颜色: 盾灰 / 虚弱金 / 常态。v1.36.0 鼠群: 主箱隐藏, 子箱绕基准点 zigzag 自走。</summary>
        private void ApplyHitboxVisual(float now)
        {
            if (_hitbox is null || _root is null) return;
            float dt = _lastVisualTick < 0f ? 0f : now - _lastVisualTick;    // v1.36.0: zigzag/相位噪声 dt 源
            _lastVisualTick = now;
            int sf = _w.MeleeClass ? 0 : Raid.RaidCore.BandShortfall(_w.Rng, _band);
            float scale = Raid.RaidCore.BandScaleMul(sf) * _hitboxScale;
            Vector2 pos = HitboxBasePos();
            switch (_beh)
            {
                case BehState.None:
                    // v1.37.0: 空窗微漂移 — 偏移常挂 (行为开始即被行为分支覆盖), 仅在剩余间隙
                    // >AmbientMinGap 时推进 (低速 ±40px, 不占卡组不占调度)
                    if (!(_ambient is null))
                    {
                        if (dt > 0f && _behNextAt > now && _behNextAt - now > Raid.RaidCore.AmbientMinGap)
                            _ambient.Tick(dt);
                        pos.x += _ambient.Ox;
                        pos.y += _ambient.Oy;
                    }
                    break;
                case BehState.Strafe:
                    // v1.36.0: 相位随机游走 (每帧 phase += (rng−0.5)×2×noiseRate×dt; 0 = 旧正弦原样)
                    if (_strafeNoise > 0f && dt > 0f)
                        _strafePhase += ((float)_rng.NextDouble() - 0.5f) * 2f * _strafeNoise * dt;
                    pos.x += (float)Math.Sin((now - _behT0) * _strafeSpeed + _strafePhase) * _strafeAmp;
                    break;
                case BehState.Shrink:
                    scale *= Raid.RaidCore.ShrinkScale();
                    break;
                case BehState.Charge:
                {
                    float p = ClampF((now - _behT0) / Math.Max(0.01f, _behUntil - _behT0), 0f, 1f);
                    pos = Vector2.Lerp(pos, Vector2.zero, p);            // 冲向屏幕中心 (root 锚点原点)
                    scale *= 1f + (Raid.RaidCore.ChargeHitboxScale - 1f) * p;
                    break;
                }
                case BehState.Zigzag:                                    // v1.36.0: 折线乱窜 (±bound 反弹)
                    if (_zig != null)
                    {
                        _zig.Tick(dt);
                        pos.x += _zig.Ox;
                        pos.y += _zig.Oy;
                    }
                    break;
                case BehState.Circle:                                    // v1.36.0: 椭圆轨道
                {
                    float t = now - _behT0;
                    pos.x += (float)Math.Sin(t * _circleSpeed) * _circleRx;
                    pos.y += (float)Math.Cos(t * _circleSpeed * 0.9f) * _circleRy;
                    break;
                }
                case BehState.Leap:                                      // v1.36.0: 纵跳 (滞空受击箱缩小)
                {
                    float p = ClampF((now - _behT0) / Math.Max(0.01f, _behUntil - _behT0), 0f, 1f);
                    pos.y += (float)Math.Sin(Math.PI * p) * _leapHeight;
                    scale *= Raid.RaidCore.LeapAirScale(p);
                    break;
                }
            }
            var bo = BurstOffset(now);                                   // v1.36.0: dash/feint 横冲段叠加
            pos.x += bo.x;
            pos.y += bo.y;
            if (now < _exposeUntil) scale *= Raid.RaidCore.ExposeHitboxScale();
            try
            {
                _hitbox.sizeDelta = new Vector2(
                    Raid.RaidLayout.ClampSize(_rt.Hitbox.W) * scale,
                    Raid.RaidLayout.ClampSize(_rt.Hitbox.H) * scale);
                _hitbox.anchoredPosition = pos;
            }
            catch { }
            bool swarm = _swarmBoxes.Count > 0;
            bool hidden = _beh == BehState.Hide || swarm;                // v1.36.0: 鼠群主箱隐藏
            try { if (_hitbox.gameObject.activeSelf == hidden) _hitbox.gameObject.SetActive(!hidden); } catch { }
            if (swarm) TickSwarmBoxes(now, dt, pos, _beh == BehState.Hide);
            if (_summonBoxes.Count > 0) TickSummonBoxes(now, dt, pos, _beh == BehState.Hide);   // v1.45.0: 召唤子箱逐帧
            if (!(_hitboxImg is null))
            {
                Color hc = HitboxNormal;
                if (_beh == BehState.ShieldDots || _beh == BehState.ShieldRed) hc = HitboxShield;
                else if (now < _weakenUntil) hc = HitboxWeaken;
                try { if (_hitboxImg.color != hc) _hitboxImg.color = hc; } catch { }
            }
        }

        /// <summary>敌名旁距离带标注 (§3.10.2: 近/中/远, 变更才重写)。</summary>
        private void TickBandLabel()
        {
            if (_nameTmp is null || _band == _lastBandShown) return;
            _lastBandShown = _band;
            try { _nameTmp.text = _enemyName + " · " + Raid.RaidCore.BandNames[Raid.RaidCore.ClampBand(_band)]; } catch { }
        }

        /// <summary>对敌最终伤害: 短缺 ×0.7^n → 破甲 ×(1−ArmorEff(armor,pen)) → 虚弱 ×1.5 / 破绽 ×1.5 (乘算)。</summary>
        private int ApplyEnemyDamage(int dmg, int shortfall, int pen, float now)
        {
            float mul = Raid.RaidCore.BandDamageMul(shortfall);
            mul *= 1f - Raid.RaidCore.ArmorEff(_armor, pen);
            if (now < _weakenUntil) mul *= Raid.RaidCore.WeakenMul();
            if (now < _exposeUntil) mul *= Raid.RaidCore.ExposeMul();
            return Math.Max(1, (int)Math.Round(dmg * mul));
        }

        // ---- 命中判定辅助 (rad 判定放宽 +rad×2px; 80px 半径多目标) ----

        /// <summary>CrossIn 放宽版: 目标 rect 外扩 pad px (v1.34.0: 所有目标判定 +rad×2px)。</summary>
        private bool PointerInPad(RectTransform rt, float pad) => CrossIn(rt, pad);

        /// <summary>准星命中点 radius px 半径内 (rad 多目标清点, §3.10.3)。
        /// v1.34.3: 弃 rt.position 世界坐标直比, 统一走 WorldToScreenPoint 换屏坐标再比视觉准星。</summary>
        private bool WithinRadius(RectTransform rt, float radius)
        {
            if (rt is null) return false;
            try
            {
                Vector2 sp = RectTransformUtility.WorldToScreenPoint(null, rt.position);
                float dx = sp.x - (_crossPos.x + _crossJit.x), dy = sp.y - (_crossPos.y + _crossJit.y);
                return dx * dx + dy * dy <= radius * radius;
            }
            catch { return false; }
        }

        /// <summary>行为条目的数值参数 (strafe speed/amplitude/duration 等; 缺键/非数字 = def — 类型校验在 ParseBehaviors 只保 type/weight)。</summary>
        private static float BehParam(RtBehavior b, string key, float def)
        {
            object v;
            if (b != null && b.Params != null && b.Params.TryGetValue(key, out v) && v != null)
            {
                if (v is long l) return l;
                if (v is double d) return (float)d;
            }
            return def;
        }

        // ==================== v1.34.0 cfg 扩展键解析 (静态纯解析, pss_test 无头可测; 类型非法 = PsRuntimeError 带行号) ====================

        private static object CfgVal(Dictionary<string, object> cfg, string key)
        {
            object v;
            return cfg != null && cfg.TryGetValue(key, out v) ? v : null;
        }

        private static float StrictNum(object v, string what, int line)
        {
            if (v is long l) return l;
            if (v is double d) return (float)d;
            throw new PsRuntimeError($"combat.rt_start 的 {what} 须为数字, 实为 {PsValues.TypeName(v)}", line);
        }

        private static float OptNum(Dictionary<string, object> d, string key, float def, string what, int line)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            return StrictNum(v, what, line);
        }

        /// <summary>cfg armor: 缺省 0; 数字钳 0-100 (减伤%, 可被 pen 穿透, §3.10.3)。</summary>
        internal static int ParseArmor(object v, int line)
        {
            if (v == null) return 0;
            return Raid.RaidCore.Clamp((int)StrictNum(v, "armor", line), 0, 100);
        }

        /// <summary>cfg band: 缺省 1 (中带); 数字钳 0-2 (0近1中2远, §3.10.2)。</summary>
        internal static int ParseBand(object v, int line)
        {
            if (v == null) return 1;
            return Raid.RaidCore.ClampBand((int)StrictNum(v, "band", line));
        }

        /// <summary>行为类型白名单 (破绽 expose 由完美闪避触发, 不进卡组; v1.36.0 新增 dash/zigzag/circle/leap/feint;
        /// v1.46.0 新增 reddot 暴击窗 — M3.6 红点机制推广, 周期暴露红点, 命中 ×2)。</summary>
        internal static readonly string[] BehaviorTypes = { "strafe", "shrink", "hide", "shield", "charge", "dash", "zigzag", "circle", "leap", "feint", "reddot" };

        /// <summary>cfg behaviors 卡组: 缺省/null/空数组 = null (现状静止); 须为数组 of dict,
        /// 每项 type 必填且在白名单内, weight 数字 ≥0 (缺省 1); 其余键留作行为级参数 (strafe speed/amplitude 等)。</summary>
        internal static List<RtBehavior> ParseBehaviors(object v, int line)
        {
            if (v == null) return null;
            if (v is not List<object> arr)
                throw new PsRuntimeError($"combat.rt_start 的 behaviors 须为数组 (如 [{{type=\"strafe\", weight=2}}]), 实为 {PsValues.TypeName(v)}", line);
            if (arr.Count == 0) return null;
            var deck = new List<RtBehavior>();
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not Dictionary<string, object> d)
                    throw new PsRuntimeError($"combat.rt_start 的 behaviors[{i}] 须为 dict, 实为 {PsValues.TypeName(arr[i])}", line);
                object tv;
                if (!d.TryGetValue("type", out tv) || !(tv is string ts) || string.IsNullOrWhiteSpace(ts))
                    throw new PsRuntimeError($"combat.rt_start 的 behaviors[{i}] 缺 type (strafe|shrink|hide|shield|charge|dash|zigzag|circle|leap|feint|reddot)", line);
                ts = ts.Trim();
                bool known = false;
                foreach (var k in BehaviorTypes) if (k == ts) { known = true; break; }
                if (!known)
                    throw new PsRuntimeError($"combat.rt_start 的 behaviors[{i}].type '{ts}' 未知 (须 strafe|shrink|hide|shield|charge|dash|zigzag|circle|leap|feint|reddot)", line);
                float w = OptNum(d, "weight", 1f, $"behaviors[{i}].weight", line);
                if (w < 0f)
                    throw new PsRuntimeError($"combat.rt_start 的 behaviors[{i}].weight 不能为负", line);
                deck.Add(new RtBehavior { Type = ts, Weight = w, Params = d });
            }
            return deck;
        }

        /// <summary>cfg shield: 缺省默认 (3 绿点/减伤 0.7/移动 0.3/瞬移 0.2/红窗 3s); 全键经 RaidCore 钳制。</summary>
        internal static RtShieldCfg ParseShieldCfg(object v, int line)
        {
            var cfg = new RtShieldCfg();
            if (v == null) return cfg;
            if (v is not Dictionary<string, object> d)
                throw new PsRuntimeError($"combat.rt_start 的 shield 须为 dict, 实为 {PsValues.TypeName(v)}", line);
            cfg.Dots = Raid.RaidCore.ShieldDotCount((int)OptNum(d, "dots", cfg.Dots, "shield.dots", line));
            cfg.Reduction = Raid.RaidCore.ShieldReduction(OptNum(d, "reduction", cfg.Reduction, "shield.reduction", line));
            cfg.MovingRatio = Raid.RaidCore.ShieldRatio(OptNum(d, "moving_ratio", cfg.MovingRatio, "shield.moving_ratio", line));
            cfg.TeleportRatio = Raid.RaidCore.ShieldRatio(OptNum(d, "teleport_ratio", cfg.TeleportRatio, "shield.teleport_ratio", line));
            cfg.RedWindow = Raid.RaidCore.ShieldRedWindow(OptNum(d, "red_window", cfg.RedWindow, "shield.red_window", line));
            return cfg;
        }

        /// <summary>cfg charge: 缺省默认 (时长 2s/击退阈值 ×1/重击 ×2); 钳 duration 0.5-5 / stagger_mul 0.25-4 / heavy_mul 1-4。</summary>
        internal static RtChargeCfg ParseChargeCfg(object v, int line)
        {
            var cfg = new RtChargeCfg();
            if (v == null) return cfg;
            if (v is not Dictionary<string, object> d)
                throw new PsRuntimeError($"combat.rt_start 的 charge 须为 dict, 实为 {PsValues.TypeName(v)}", line);
            cfg.Duration = ClampF(OptNum(d, "duration", cfg.Duration, "charge.duration", line), 0.5f, 5f);
            cfg.StaggerMul = ClampF(OptNum(d, "stagger_mul", cfg.StaggerMul, "charge.stagger_mul", line), 0.25f, 4f);
            cfg.HeavyMul = ClampF(OptNum(d, "heavy_mul", cfg.HeavyMul, "charge.heavy_mul", line), 1f, 4f);
            return cfg;
        }

        // ---- v1.36.0 M3.7 cfg 新键解析 ----

        /// <summary>cfg hitbox_scale: 缺省 1.0; 数字钳 0.5-1.3 (受击箱个体化, 与布局基准/行为倍率叠乘)。</summary>
        internal static float ParseHitboxScale(object v, int line)
        {
            if (v == null) return 1f;
            return Raid.RaidCore.HitboxScale(StrictNum(v, "hitbox_scale", line));
        }

        /// <summary>cfg depth: 缺省 0; int 钳 0-8 (深度难度: hp/atk 两端 ×(1+0.10×d); v1.44.0 前为 0-5/0.12)。</summary>
        internal static int ParseDepth(object v, int line)
        {
            if (v == null) return 0;
            return Raid.RaidCore.ClampDepth((int)StrictNum(v, "depth", line));
        }

        /// <summary>cfg warn_mul: 缺省 1.0; 数字钳 0.7-1.6 (预警时长个体化, 近战 ÷1.25 后乘算)。</summary>
        internal static float ParseWarnMul(object v, int line)
        {
            if (v == null) return 1f;
            return Raid.RaidCore.WarnMul(StrictNum(v, "warn_mul", line));
        }

        /// <summary>v1.37.0: cfg tempo: 缺省 1.0; 数字钳 0.5-1.5 (敌节奏个体化 — 只乘行为间隙, 攻击节奏不动)。</summary>
        internal static float ParseTempo(object v, int line)
        {
            if (v == null) return 1f;
            return Raid.RaidCore.TempoClamped(StrictNum(v, "tempo", line));
        }

        /// <summary>cfg swarm: 缺省/null = null (无鼠群); 须为 dict {count(2-4, 缺省3),
        /// box_scale(0.3-0.6, 缺省0.4), spread(100-260px, 缺省170), speed(120-450, 缺省240, v1.46.0)}; 与 shield 互斥 (RtStart 处 Warn 后忽略)。</summary>
        internal static RtSwarmCfg ParseSwarmCfg(object v, int line)
        {
            if (v == null) return null;
            if (v is not Dictionary<string, object> d)
                throw new PsRuntimeError($"combat.rt_start 的 swarm 须为 dict (如 {{count=3, box_scale=0.4, spread=170}}), 实为 {PsValues.TypeName(v)}", line);
            var cfg = new RtSwarmCfg();
            cfg.Count = Raid.RaidCore.SwarmCount((int)OptNum(d, "count", cfg.Count, "swarm.count", line));
            cfg.BoxScale = Raid.RaidCore.SwarmBoxScale(OptNum(d, "box_scale", cfg.BoxScale, "swarm.box_scale", line));
            cfg.Spread = Raid.RaidCore.SwarmSpread(OptNum(d, "spread", cfg.Spread, "swarm.spread", line));
            cfg.Speed = Raid.RaidCore.SwarmSpeed(OptNum(d, "speed", cfg.Speed, "swarm.speed", line));   // v1.46.0
            return cfg;
        }

        // ---- v1.45.0 (gunworks v0.40.0 §4.5/§4.6) 嚎叫者召唤 / 掠窃者逃逸 cfg 解析 ----

        /// <summary>cfg summon: 缺省/null = null (无召唤); 须为 dict {interval(2-8s, 缺省4),
        /// cap(1-5, 缺省3), child_hp(1-60, 缺省10), box_scale(0.25-0.6, 缺省0.35),
        /// spread(100-300px, 缺省180), child_atk(0-10, 缺省2)} — 子箱独立 HP 打死即灭, 母体可攻。</summary>
        internal static RtSummonCfg ParseSummonCfg(object v, int line)
        {
            if (v == null) return null;
            if (v is not Dictionary<string, object> d)
                throw new PsRuntimeError($"combat.rt_start 的 summon 须为 dict (如 {{interval=4, cap=3, child_hp=10}}), 实为 {PsValues.TypeName(v)}", line);
            var cfg = new RtSummonCfg();
            cfg.Interval = Raid.RaidCore.SummonInterval(OptNum(d, "interval", cfg.Interval, "summon.interval", line));
            cfg.Cap = Raid.RaidCore.SummonCap((int)OptNum(d, "cap", cfg.Cap, "summon.cap", line));
            cfg.ChildHp = Raid.RaidCore.SummonChildHp((int)OptNum(d, "child_hp", cfg.ChildHp, "summon.child_hp", line));
            cfg.BoxScale = Raid.RaidCore.SummonBoxScale(OptNum(d, "box_scale", cfg.BoxScale, "summon.box_scale", line));
            cfg.Spread = Raid.RaidCore.SummonSpread(OptNum(d, "spread", cfg.Spread, "summon.spread", line));
            cfg.ChildAtk = Raid.RaidCore.SummonChildAtk((int)OptNum(d, "child_atk", cfg.ChildAtk, "summon.child_atk", line));
            return cfg;
        }

        /// <summary>cfg escape_after: 缺省 0 = off; 数字钳 0-60 (秒, 到点 EndCombat("escape"))。</summary>
        internal static float ParseEscapeAfter(object v, int line)
        {
            if (v == null) return 0f;
            return Raid.RaidCore.EscapeAfter(StrictNum(v, "escape_after", line));
        }

        // ==================== 反馈/参数辅助 ====================

        private void Log(string text)
        {
            try
            {
                string sid = Scenes?.Active?.FullId;
                if (sid != null) LogSvc?.Log(sid, text);
            }
            catch { }
        }

        private static float ClampF(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

        private static int ReqInt(Dictionary<string, object> cfg, string key, string fn, int line)
        {
            object v;
            if (cfg == null || !cfg.TryGetValue(key, out v) || v == null)
                throw new PsRuntimeError($"{fn} 缺必填键 {key} (数字)", line);
            if (v is long l) return (int)l;
            if (v is double d) return (int)d;
            throw new PsRuntimeError($"{fn} 的 {key} 须为数字, 实为 {PsValues.TypeName(v)}", line);
        }

        private static int OptInt(Dictionary<string, object> cfg, string key, int def)
        {
            object v;
            if (cfg == null || !cfg.TryGetValue(key, out v) || v == null) return def;
            if (v is long l) return (int)l;
            if (v is double d) return (int)d;
            return def;
        }

        private static string OptStr(Dictionary<string, object> cfg, string key, string def)
        {
            object v;
            if (cfg == null || !cfg.TryGetValue(key, out v) || v == null) return def;
            try { var s = PsValues.Fmt(v); return string.IsNullOrEmpty(s) ? def : s; } catch { return def; }
        }

        private static bool OptBool(Dictionary<string, object> cfg, string key, bool def)
        {
            object v;
            if (cfg == null || !cfg.TryGetValue(key, out v) || v == null) return def;
            return PsValues.Truthy(v);
        }

        /// <summary>cfg 可选 pss 函数键 (on_hp_change 等); 存在但非函数 = PsRuntimeError。</summary>
        private static PsCallable OptCallable(Dictionary<string, object> cfg, string key, string fn, int line)
        {
            object v;
            if (cfg == null || !cfg.TryGetValue(key, out v) || v == null) return null;
            if (v is PsCallable c) return c;
            throw new PsRuntimeError($"{fn} 的 {key} 须为 pss 函数, 实为 {PsValues.TypeName(v)}", line);
        }
    }
}
