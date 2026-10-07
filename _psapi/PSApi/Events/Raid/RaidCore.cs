using System;
using System.Collections.Generic;
using PSApi.Events.Scenes;

namespace PSApi.Events.Raid
{
    /// <summary>
    /// M2 搜打撤纯逻辑 (无 Unity 依赖, pss_test 无头可测) — 数值照设计文档 §3.3/§4.7:
    /// HP 四档 (健康/轻伤/受伤/重伤) debuff 倍率、姿态四档 (跑/走/蹲/匍匐) 消耗与噪音、
    /// 前进消耗结算、搜索价值修正、加权 loot 掷签、链长区间解析。
    /// M2 只落实体力消耗倍率与搜索价值数量修正; 遇敌率/武器威力等挂钩 M3 战斗。
    /// </summary>
    internal static class RaidCore
    {
        internal const int MaxStat = 100;
        internal const int MaxNoise = 10;

        /// <summary>姿态: 0跑 1走 2蹲 3匍匐。</summary>
        internal static readonly string[] PostureNames = { "跑动", "行走", "蹲下", "匍匐" };
        internal static readonly string[] PostureIds = { "run", "walk", "crouch", "prone" };
        /// <summary>体力/饥饿消耗倍率 (§3.3 姿态表)。</summary>
        internal static readonly float[] PostureCostMul = { 1.6f, 1.0f, 0.7f, 0.5f };
        /// <summary>每次推进噪音增量 (钳 0-10)。</summary>
        internal static readonly int[] PostureNoise = { 3, 1, 0, -1 };
        /// <summary>搜索价值倍率 (跑 -20% / 蹲 +10% / 匍匐 +25%)。</summary>
        internal static readonly float[] PostureSearchMul = { 0.8f, 1.0f, 1.1f, 1.25f };

        /// <summary>HP 档位: >75=0健康 / 51-75=1轻伤 / 26-50=2受伤 / 1-25=3重伤 / ≤0=4晕倒。</summary>
        internal static int HpTier(int hp)
        {
            if (hp <= 0) return 4;
            if (hp > 75) return 0;
            if (hp > 50) return 1;
            if (hp > 25) return 2;
            return 3;
        }

        internal static readonly string[] TierNames = { "健康", "轻伤", "受伤", "重伤", "晕倒" };
        /// <summary>档位体力消耗倍率 (×1/×1.15/×1.3/×1.5)。</summary>
        internal static readonly float[] TierStaminaMul = { 1f, 1.15f, 1.3f, 1.5f, 1.5f };
        /// <summary>档位搜索价值倍率 (-0%/-10%/-20%/-35%)。</summary>
        internal static readonly float[] TierSearchMul = { 1f, 0.9f, 0.8f, 0.65f, 0.65f };
        /// <summary>档位饥饿消耗倍率 (重伤 ×1.5)。</summary>
        internal static readonly float[] TierHungerMul = { 1f, 1f, 1f, 1.5f, 1.5f };

        /// <summary>搜索价值总倍率 = 档位 × 姿态 × (饥饿≤20 → ×0.85)。</summary>
        internal static float SearchValueMul(int hp, int hunger, int posture)
        {
            float v = TierSearchMul[HpTier(hp)] * PostureSearchMul[ClampPosture(posture)];
            if (hunger <= 20) v *= 0.85f;
            return v;
        }

        /// <summary>前进消耗结算 (§4.7): 体力 8×姿态倍率×档位倍率×(饥饿≤20 → ×1.25);
        /// 饥饿 4×姿态倍率×档位饥饿倍率; 噪音按姿态; 饥饿=0 → 额外 -5 HP。</summary>
        internal static void AdvanceCost(int posture, int hp, int hunger,
            out int staminaCost, out int hungerCost, out int noiseDelta, out int hpCost)
        {
            int tier = HpTier(hp);
            float pm = PostureCostMul[ClampPosture(posture)];
            float sm = 8f * pm * TierStaminaMul[tier];
            if (hunger <= 20) sm *= 1.25f;
            staminaCost = Math.Max(1, (int)Math.Round(sm));
            hungerCost = Math.Max(1, (int)Math.Round(4f * pm * TierHungerMul[tier]));
            noiseDelta = PostureNoise[ClampPosture(posture)];
            hpCost = hunger <= 0 ? 5 : 0;
        }

        /// <summary>原地休息 (力竭): 饥饿照扣 (4×姿态倍率, 不含档位体力相关修正)。</summary>
        internal static int RestHungerCost(int posture)
            => Math.Max(1, (int)Math.Round(4f * PostureCostMul[ClampPosture(posture)]));

        internal const int RestStamina = 15;

        /// <summary>"3-4" 区间或 "4" 定值 → (lo, hi); 缺省 (3,3); 格式坏 → FormatException。纯函数可测。</summary>
        internal static (int Lo, int Hi) ParseChain(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return (3, 3);
            var t = s.Trim();
            var parts = t.Split('-');
            if (parts.Length == 2
                && int.TryParse(parts[0].Trim(), out int lo) && int.TryParse(parts[1].Trim(), out int hi)
                && lo > 0 && hi >= lo)
                return (lo, hi);
            if (int.TryParse(t, out int n) && n > 0) return (n, n);
            throw new FormatException("chain_length 须为 \"3-4\" 区间或 \"4\" 定值, 实为 '" + s + "'");
        }

        internal static int RollChain(string s, Random rng)
        {
            var (lo, hi) = ParseChain(s);
            return rng.Next(lo, hi + 1);
        }

        /// <summary>加权 loot 掷签: 从 pool 筛 weight>0 且 (ignoreMinDepth 或 min_depth≤depth),
        /// 掷 minItems..maxItems 件 (每件独立加权), 数量走 ParseCount 再乘搜索价值修正 (四舍五入至少 1)。</summary>
        internal static List<KeyValuePair<string, int>> RollLoot(List<SceneLootEntry> pool, int depth,
            bool ignoreMinDepth, float valueMul, int minItems, int maxItems, Random rng)
        {
            var result = new List<KeyValuePair<string, int>>();
            if (pool == null || pool.Count == 0) return result;
            var cand = new List<SceneLootEntry>();
            float total = 0f;
            foreach (var e in pool)
            {
                if (e == null || e.Weight <= 0) continue;
                if (!ignoreMinDepth && e.MinDepth > depth) continue;
                cand.Add(e);
                total += e.Weight;
            }
            if (cand.Count == 0 || total <= 0f) return result;
            int n = rng.Next(minItems, maxItems + 1);
            for (int i = 0; i < n; i++)
            {
                float r = (float)(rng.NextDouble() * total);
                var pick = cand[cand.Count - 1];
                foreach (var e in cand) { r -= e.Weight; if (r <= 0f) { pick = e; break; } }
                int cnt = SceneJson.ParseCount(pick.Count, rng);
                cnt = Math.Max(1, (int)Math.Round(cnt * valueMul));
                result.Add(new KeyValuePair<string, int>(pick.Id, cnt));
            }
            return result;
        }

        /// <summary>poi_table 加权掷签 (按总和归一; 表空/总和≤0 → "empty")。键不在四键内原样返回。</summary>
        internal static string RollEncounter(Dictionary<string, float> table, Random rng)
        {
            if (table == null || table.Count == 0) return "empty";
            float total = 0f;
            foreach (var kv in table) if (kv.Value > 0) total += kv.Value;
            if (total <= 0f) return "empty";
            float r = (float)(rng.NextDouble() * total);
            string pick = "empty";
            foreach (var kv in table)
            {
                if (kv.Value <= 0) continue;
                r -= kv.Value;
                if (r <= 0f) { pick = kv.Key; break; }
            }
            return pick;
        }

        /// <summary>信息栏日志容量 (M2-UI: 滚动日志保留最近 8 条)。</summary>
        internal const int LogCap = 8;

        /// <summary>信息栏日志追加: 最新在尾, 超容量从头删; 空文本忽略。纯函数可测。</summary>
        internal static void AppendLog(List<string> lines, string text, int cap)
        {
            if (lines == null || string.IsNullOrEmpty(text)) return;
            lines.Add(text);
            while (lines.Count > cap) lines.RemoveAt(0);
        }

        internal static int ClampPosture(int p) => p < 0 ? 0 : p > 3 ? 3 : p;

        internal static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        // ==================== M3 战斗 (v1.25.0, §3.6/§4.7 — 纯函数可无头单测) ====================

        /// <summary>姿态枪械命中修正 (§3.3 枪械修正列: 跑-25/走0/蹲+10/匍匐+20)。</summary>
        internal static readonly int[] PostureGunHit = { -25, 0, 10, 20 };
        /// <summary>姿态威力倍率 (跑 -15% / 匍匐 +5%)。</summary>
        internal static readonly float[] PosturePowerMul = { 0.85f, 1f, 1f, 1.05f };
        /// <summary>姿态遇敌先手修正 (跑-20/走0/蹲+15/匍匐+30; 匍匐不可逃跑)。</summary>
        internal static readonly int[] PostureFirstMod = { -20, 0, 15, 30 };
        /// <summary>HP 档位武器威力倍率 (受伤 -10% / 重伤 -25%, §3.3)。</summary>
        internal static readonly float[] TierPowerMul = { 1f, 1f, 0.9f, 0.75f, 0.75f };

        /// <summary>命中% = 武器ACC×4 + 姿态修正 + buff修正 − 敌dodge, 钳 5-95 (§3.6)。</summary>
        internal static int HitChance(int weaponAcc, int posture, int buffMod, int enemyDodge)
            => Clamp(weaponAcc * 4 + PostureGunHit[ClampPosture(posture)] + buffMod - enemyDodge, 5, 95);

        /// <summary>伤害 = FIREPOWER × 姿态威力 × 档位修正 × (0.85~1.15 随机), 四舍五入至少 1 (§3.6)。</summary>
        internal static int RollDamage(float firepower, int posture, int hp, Random rng)
        {
            float mul = PosturePowerMul[ClampPosture(posture)] * TierPowerMul[HpTier(hp)];
            float rand = 0.85f + (float)rng.NextDouble() * 0.30f;
            return Math.Max(1, (int)Math.Round(firepower * mul * rand));
        }

        /// <summary>卡壳率% = (100−reliability)/8, 钳 0-100 (§4.7)。</summary>
        internal static float JamChance(int reliability) => Clamp(100 - reliability, 0, 100) / 8f;

        /// <summary>逃跑成功率% = 50 + 姿态先手修正 + 体力/10, 钳 0-95 (§3.6/§4.7)。</summary>
        internal static int FleeChance(int posture, int stamina)
            => Clamp(50 + PostureFirstMod[ClampPosture(posture)] + Math.Max(0, stamina) / 10, 0, 95);

        /// <summary>容器动态行数 (拍板 7): 总占格/cols 上取整 + 1 行余量, 至少 1 行。</summary>
        internal static int ContainerRows(int totalCells, int cols)
        {
            if (cols <= 0) cols = 6;
            if (totalCells <= 0) return 1;
            return (int)Math.Ceiling(totalCells / (double)cols) + 1;
        }

        /// <summary>掷签表中 combat 键的归一化概率 (归途遇袭基数 = 此值×0.5; 无 combat 键/表空 = 0)。</summary>
        internal static float CombatRate(Dictionary<string, float> table)
        {
            if (table == null) return 0f;
            float total = 0f, combat = 0f;
            foreach (var kv in table)
            {
                if (kv.Value <= 0) continue;
                total += kv.Value;
                if (kv.Key == "combat") combat = kv.Value;
            }
            return total <= 0f ? 0f : combat / total;
        }

        /// <summary>战斗每回合体力消耗 (§4.7)。</summary>
        internal const int CombatStaminaCost = 6;
        /// <summary>空手 atk 区间 (§4.7: 2-5)。</summary>
        internal const int UnarmedAtkLo = 2, UnarmedAtkHi = 5;
        /// <summary>空手命中 60% = ACC 15 ×4 (§4.7)。</summary>
        internal const int UnarmedAcc = 15;

        /// <summary>v1.25.1 (用户拍板): loot count 展开件数钳制 — count=N 拆成 N 件独立物品
        /// (每件 stack=1, 禁堆叠角标), 单条 loot 上限 12 件防恶意包 count:"999" 撑爆容器。</summary>
        internal static int ClampLootCount(int count) => Clamp(count, 1, 12);

        /// <summary>v1.25.2: ctrl+左键快捷转移拦截门控 (QuickTransferPatches 用, 纯函数可无头测)。
        /// 三条件全真才接管: 在 raid + 快捷转移标志 + <b>点击键 release</b> — 纯 ctrl 松开
        /// (isQuickTransfer=true 但 clickRelease=false) 是原版的 flag 复位路径, 必须放行,
        /// 否则按一下 ctrl 就触发一次重定向日志 (v1.25.1 用户实测刷屏根因)。</summary>
        internal static bool QuickTransferShouldIntercept(bool inRaid, bool isQuickTransfer, bool clickRelease)
            => inRaid && isQuickTransfer && clickRelease;

        /// <summary>姿态 id → 索引; 非法 → -1。</summary>
        internal static int PostureIndex(string id)
        {
            if (id == null) return -1;
            for (int i = 0; i < PostureIds.Length; i++)
                if (string.Equals(PostureIds[i], id.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // ==================== v1.29.0 实时动作战斗 (combat.rt_start; 纯函数可无头单测) ====================

        /// <summary>闪避分档边界 (秒): |按键时刻−受击时刻| ≤Perfect = 完美, ≤Graze = 擦伤, 否则全伤。</summary>
        internal const float DodgePerfect = 0.15f, DodgeGraze = 0.40f;

        /// <summary>闪避分档: 0 完美 / 1 擦伤 / 2 全伤。</summary>
        internal static int DodgeTier(float absDt)
            => absDt <= DodgePerfect ? 0 : absDt <= DodgeGraze ? 1 : 2;

        /// <summary>分档伤害倍率: 完美 0 / 擦伤 0.4 / 全伤 1。</summary>
        internal static float DodgeDamageMul(int tier)
            => tier <= 0 ? 0f : tier == 1 ? 0.4f : 1f;

        /// <summary>实时命中伤害 = atk × (0.9~1.1 随机), 四舍五入至少 1 (枪械/近战/枪托通用基数)。</summary>
        internal static int RollRtDamage(float atk, Random rng)
        {
            float rand = 0.9f + (float)rng.NextDouble() * 0.2f;
            return Math.Max(1, (int)Math.Round(atk * rand));
        }

        /// <summary>枪托/枪当近战抡的伤害基数 = w_atk × 0.4, 至少 1。</summary>
        internal static float GunstockAtk(float atk) => Math.Max(1f, atk * 0.4f);

        /// <summary>v1.43.0 阶段三: 破损 (ps_broken=1) 武器伤害倍率 — 不毁枪不禁止开火, 伤害大减仍可应急;
        /// 修理 (gunworks 拖同材料修理物) 回满耐久删 ps_broken 后恢复。</summary>
        internal const float BrokenDamageMul = 0.3f;

        /// <summary>v1.43.0: RtWeapon 解析处单点乘 — 破损 ×0.3 (枪械射击/枪托抡/mode0 抡/近战同源)。</summary>
        internal static float BrokenAtk(float atk, bool broken) => broken ? atk * BrokenDamageMul : atk;

        /// <summary>v1.48.7 低耐久伤害曲线 (拍板): 耐久比例 &lt;30% ×0.85 / &lt;15% ×0.7;
        /// 破损 (ps_broken=1) 维持 ×0.3 优先不叠加; ≥30% 无惩罚。作用于所有带 ps_dur_max 的武器
        /// (RtWeapon 解析单点, 与 BrokenAtk 同位; 无 ps_dur_max 的原版枪 ratio=1.0 不参与)。</summary>
        internal const double LowDurRatio30 = 0.30;
        internal const double LowDurRatio15 = 0.15;
        internal const float LowDurMul30 = 0.85f;
        internal const float LowDurMul15 = 0.7f;

        /// <summary>v1.48.7: 耐久伤害倍率 — broken ×0.3 优先; 否则按比例曲线 (&lt;0.15 → ×0.7, &lt;0.30 → ×0.85, 否则 1.0)。</summary>
        internal static float DurabilityMul(double ratio, bool broken)
            => broken ? BrokenDamageMul : ratio < LowDurRatio15 ? LowDurMul15 : ratio < LowDurRatio30 ? LowDurMul30 : 1f;

        /// <summary>v1.48.7: RtWeapon 解析处单点乘 (BrokenAtk 的低耐久扩展; 枪械射击/枪托抡/mode0 抡/近战同源)。</summary>
        internal static float DurabilityAtk(float atk, double ratio, bool broken) => atk * DurabilityMul(ratio, broken);

        /// <summary>v1.48.7 (拍板): 破损状态开火卡壳概率 % (与 stab 卡壳取大; 卡壳=该发打不出,
        /// 耗 1 发弹药不扣耐久 — 与既有 stab 卡壳路径同规)。近战挥击只吃伤害曲线不卡壳。</summary>
        internal const float BrokenJamChance = 25f;

        /// <summary>偷袭倍率 (蹲/匍匐姿态开战第一击必中 ×1.5)。</summary>
        internal const float SneakMul = 1.5f;

        /// <summary>射击 CD 秒 = 60/rpm (rpm 钳 ≥1; 单发连发同规)。</summary>
        internal static float FireCd(int rpm) => 60f / Math.Max(1, rpm);

        /// <summary>卡壳上膛锁时长 (秒): 期间射击输入无效。</summary>
        internal const float JamLock = 0.8f;
        /// <summary>近战出手硬直 (秒): 期间闪避键无效。</summary>
        internal const float MeleeStun = 0.4f;
        /// <summary>近战出手间隔 (秒)。</summary>
        internal const float MeleeCd = 0.5f;

        /// <summary>准星抖动半径 px = (100−w_stab)/10, 钳 0-10。</summary>
        internal static float JitterRadius(int stab) => Clamp(100 - stab, 0, 100) / 10f;

        /// <summary>敌攻击预警时长 (秒): 基准 1.2; 玩家持近战类 (近战/枪托/拳头) ÷1.25 = 0.96。</summary>
        internal static float WarnDuration(bool meleeClass) => meleeClass ? 1.2f / 1.25f : 1.2f;

        /// <summary>敌攻击节奏 (秒): 2.2~3.5 均匀随机。</summary>
        internal static float EnemyInterval(Random rng) => 2.2f + (float)rng.NextDouble() * 1.3f;

        /// <summary>近战类判定 (预警加速 + 出手硬直适用): 近战武器 / 无弹枪托 / 枪当近战抡 / 空手。</summary>
        internal static bool IsMeleeClass(bool isGun, bool hasAmmo, int mode)
            => !isGun || !hasAmmo || mode == 0;

        /// <summary>实时敌 HP 映射: 旧 pip 格 ×15 (野狗 15 / 拾荒者 30 / 巡逻机器人 45)。</summary>
        internal const int RtHpPerPip = 15;
        internal static int RtEnemyHp(int pips) => Math.Max(1, pips) * RtHpPerPip;

        /// <summary>v1.30.0 实时投掷伤害基数 = w_atk × 2, 至少 1
        /// (与旧回合制 raid_cbt_throw 的 power = max(1, w_atk×2) 同语义; 必中, 伤害掷签走 RollRtDamage)。</summary>
        internal static float RtThrowAtk(float watk) => Math.Max(1f, watk * 2f);

        /// <summary>v1.30.0 rt 战斗体力消耗 = round(战斗时长秒 × 0.6) (每 5 秒 3 点;
        /// 旧回合制每回合 -6 的 rt 等价; 负时长钳 0)。结束经 on_end 载荷 sp_cost 交给 pss 扣 raid_st.stamina。</summary>
        internal static int RtSpCost(float seconds) => Math.Max(0, (int)Math.Round(seconds * 0.6));

        // ==================== v1.34.0 战斗升级 M3.6 波 1 (§3.10: 行为状态机/距离带/六维全消费; 纯函数可无头单测) ====================

        /// <summary>破甲 (§3.10.3): 实际减伤率 = armor%(0-100)/100 × (1 − pen/100), 钳 [0, 0.95]。
        /// 对敌 armor 与绿点盾减伤同式 (绿点传 reduction×100)。</summary>
        internal static float ArmorEff(float armorPct, float pen)
        {
            float eff = armorPct / 100f * (1f - pen / 100f);
            if (eff < 0f) return 0f;
            return eff > 0.95f ? 0.95f : eff;
        }

        /// <summary>距离带名 (0近/1中/2远, §3.10.2 UI 敌名旁标注)。</summary>
        internal static readonly string[] BandNames = { "近", "中", "远" };
        /// <summary>距离带 rng 需求 (§3.10.2 拍板): 近 0 / 中 25 / 远 55。</summary>
        internal static readonly int[] BandRngReqTable = { 0, 25, 55 };
        internal static int ClampBand(int band) => band < 0 ? 0 : band > 2 ? 2 : band;
        internal static int BandRngReq(int band) => BandRngReqTable[ClampBand(band)];

        /// <summary>射程短缺档数 0-2 (§3.10.2): rng ≥ 当前带需求 = 0; 每差一档 +1
        /// (逐级比较相邻低档需求, 如远带 rng30 → 短缺 1, rng10 → 短缺 2)。</summary>
        internal static int BandShortfall(int weaponRng, int band)
        {
            int sf = 0;
            for (int b = ClampBand(band); b > 0; b--)
            {
                if (weaponRng >= BandRngReqTable[b]) break;
                sf++;
            }
            return sf;
        }

        /// <summary>短缺伤害倍率 = 0.7^n (n 钳 0-2, §3.10.2 每差一档 ×0.7)。</summary>
        internal static float BandDamageMul(int shortfall)
        {
            float m = 1f;
            for (int i = 0; i < Clamp(shortfall, 0, 2); i++) m *= 0.7f;
            return m;
        }

        /// <summary>短缺受击箱视觉缩小倍率 = 0.85^n (n 钳 0-2, §3.10.2 更难命中)。</summary>
        internal static float BandScaleMul(int shortfall)
        {
            float m = 1f;
            for (int i = 0; i < Clamp(shortfall, 0, 2); i++) m *= 0.85f;
            return m;
        }

        /// <summary>近战类可达性 (§3.10.1/.2): 近战/枪托 (wtype=melee 或 rng≤2) 只能打近带;
        /// wtype=throw (投掷) 只能打近/中带; 其余 (枪械射击) 全带可达 (伤害走 BandShortfall)。
        /// v1.34.2 拍板① (实测修正): rt 层近战/枪托/拳头不再走本函数锁死 (改吃 BandShortfall 衰减,
        /// 窗口内经 MeleeEffectiveBand=近带全额); 本函数现仅服务投掷限近/中带 (RtCombatService.ThrowClick)
        /// 与 pss combat.melee_can_reach (语义保留不改)。</summary>
        internal static bool MeleeCanReach(string wtype, int rng, int band)
        {
            band = ClampBand(band);
            if (string.Equals(wtype, "throw", StringComparison.Ordinal)) return band <= 1;
            if (string.Equals(wtype, "melee", StringComparison.Ordinal) || rng <= 2) return band == 0;
            return true;
        }

        /// <summary>敌攻击窗口内有效距离带 (§3.10.2 拍板① 2026-09-30): 敌 WARNING→RESOLVE 全程视为近带,
        /// 近战可抓时机反击; v1.34.2 起作近战 BandShortfall 入参 (窗口内=近带=短缺 0 全额),
        /// 枪械短缺伤害/受击箱缩放不受影响。</summary>
        internal static int MeleeEffectiveBand(bool inEnemyAttackWindow, int band)
            => inEnemyAttackWindow ? 0 : band;

        /// <summary>绿点击碎次数 (§3.10.1 拍板② 2026-09-30): 14-22px→1 击 / 23-31px→2 击 / 32-40px→3 击
        /// (边界含端点, 钳 1-3); 每击伤害照扣敌 HP, 归零才碎。</summary>
        internal static int DotHitsForSize(float sizePx)
        {
            if (sizePx <= 22f) return 1;
            if (sizePx <= 31f) return 2;
            return 3;
        }

        /// <summary>冲锋击退阈值 (§3.10.1): 累计伤害 ≥ max(10, 敌HP×0.25) → 冲锋取消+硬直 1s。</summary>
        internal static int ChargeStaggerThreshold(int enemyHp) => Math.Max(10, (int)Math.Round(enemyHp * 0.25));

        /// <summary>护盾绿点数钳 1-6 (§3.10.4 包侧推荐 2-4, 引擎层放宽钳制)。</summary>
        internal static int ShieldDotCount(int dots) => Clamp(dots, 1, 6);
        /// <summary>护盾绿点尺寸钳 14-40px。</summary>
        internal static float ShieldDotSize(float px) => px < 14f ? 14f : px > 40f ? 40f : px;
        /// <summary>护盾减伤率钳 0.5-0.9 (§3.10 推荐 60-80%)。</summary>
        internal static float ShieldReduction(float r) => r < 0.5f ? 0.5f : r > 0.9f ? 0.9f : r;
        /// <summary>红点窗口钳 1.5-5s (§3.10 推荐 3s)。</summary>
        internal static float ShieldRedWindow(float s) => s < 1.5f ? 1.5f : s > 5f ? 5f : s;
        /// <summary>移动/瞬移比例钳 0-1。</summary>
        internal static float ShieldRatio(float r) => r < 0f ? 0f : r > 1f ? 1f : r;
        /// <summary>绿点环带半径 (受击箱中心起 120-260px 随机)。</summary>
        internal const float ShieldRingLo = 120f, ShieldRingHi = 260f;
        /// <summary>红点半径/尺寸 24px。</summary>
        internal const float ShieldRedSize = 24f;

        /// <summary>虚弱承伤倍率 (破态后 2.5s)。</summary>
        internal static float WeakenMul() => 1.5f;
        /// <summary>破绽承伤倍率 (完美闪避触发 1.5s, 与虚弱乘算)。</summary>
        internal static float ExposeMul() => 1.5f;
        /// <summary>破绽受击箱放大倍率。</summary>
        internal static float ExposeHitboxScale() => 1.3f;
        /// <summary>伏身受击箱缩小倍率。</summary>
        internal static float ShrinkScale() => 0.5f;

        /// <summary>v1.46.0 reddot 暴击窗 (M3.6 红点机制推广, gunworks v0.43.0):
        /// 行为卡 {type="reddot", weight, window} — 周期暴露红点, 窗口内命中 = 暴击 ×2 并关窗
        /// (打身体照常不挡枪; 超时消散无惩罚)。与盾红同窗 (1-4s 钳制, 缺省 2s)。</summary>
        internal static float RedDotCritMul() => 2.0f;
        internal static float RedDotWindow(float s) => s < 1f ? 1f : s > 4f ? 4f : s;

        /// <summary>虚弱时长 2.5s / 破绽时长 1.5s / 伏身默认时长 1.5s / 硬直 1s (§3.10.1)。</summary>
        internal const float WeakenDuration = 2.5f, ExposeDuration = 1.5f, ShrinkDuration = 1.5f, StaggerDuration = 1f;
        /// <summary>冲锋受击箱放大上限 (lerp 到 ×1.6)。</summary>
        internal const float ChargeHitboxScale = 1.6f;
        /// <summary>rad 多目标: 命中点半径 80px 内额外绿点 ≤rad-1 个; 所有目标判定矩形放宽 +rad×2px。</summary>
        internal const float RadMultiRadius = 80f, RadPadPx = 2f;

        /// <summary>v1.34.0: cfg hp 语义 = 直接 HP 值; ≤10 视为旧 pip 格 ×15 兼容
        /// (§3.10.4 新敌 HP 20/40/70 均 >10, 旧 pip 上限 3; gunworks 现包经 rt_enemy_hp 已传直接值, 不受影响)。</summary>
        internal static int RtEnemyHpValue(int hp) => hp <= 10 ? RtEnemyHp(hp) : Math.Max(1, hp);

        // ==================== v1.36.0 战斗体验升级 M3.7 (受击箱个体化+全局缩 / 5 新行为+strafe 相位噪声 /
        //   麻痹 w_stun / 深度难度 depth / 鼠群 swarm / 预警时长 warn_mul; 纯函数可无头单测) ====================

        /// <summary>受击箱个体化系数钳 0.5-1.3 (rt_start cfg hitbox_scale, 缺省 1.0;
        /// 最终尺寸 = 布局基准 × 本系数 × 行为倍率链, 叠乘)。</summary>
        internal static float HitboxScale(float s) => s < 0.5f ? 0.5f : s > 1.3f ? 1.3f : s;

        /// <summary>深度难度 (rt_start cfg depth, int 钳 0-8, 缺省 0): 开战敌 hp = round(cfg.hp×DepthMul),
        /// atk 区间两端同乘 (round); DepthMul(d) = 1 + 0.10×d (v1.44.0: cap 5→8、每层 12%→10%, 到底 ×1.8)。</summary>
        internal static int ClampDepth(int d) => Clamp(d, 0, 8);
        internal static float DepthMul(int depth) => 1f + 0.10f * ClampDepth(depth);

        /// <summary>鼠群 rad 波及倍率 (§M3.7): 命中点 80px 内额外子箱 ≤rad-1 个, 每个 +50% 伤害;
        /// extra 钳 ≥0 (SwarmBonusMul(extra) = 1+0.5×extra)。</summary>
        internal static float SwarmBonusMul(int extra) => 1f + 0.5f * Math.Max(0, extra);

        /// <summary>预警时长个体化系数钳 0.7-1.6 (rt_start cfg warn_mul, 缺省 1.0;
        /// WarnDuration 结果 ×warn_mul 乘算, 近战 ÷1.25 规则保留)。</summary>
        internal static float WarnMul(float m) => m < 0.7f ? 0.7f : m > 1.6f ? 1.6f : m;

        /// <summary>麻痹秒数钳 0-3 (NBT w_stun, 缺省 0 = 无麻痹; 命中后敌节奏器暂停+撤预警+打断冲锋)。</summary>
        internal static float StunSeconds(float s) => s < 0f ? 0f : s > 3f ? 3f : s;

        // ---- dash 突进 ----
        /// <summary>dash 时长钳 0.2-0.5s (缺省 0.3)。</summary>
        internal static float DashDuration(float s) => s < 0.2f ? 0.2f : s > 0.5f ? 0.5f : s;
        /// <summary>dash 位移距离钳 100-300px (缺省 200; 每次行为掷 ±20%, 见 DashRollDistance)。</summary>
        internal static float DashDistance(float px) => px < 100f ? 100f : px > 300f ? 300f : px;
        /// <summary>dash/feint 共用 ease-out 曲线: 1-(1-p)² (p 钳 0-1)。</summary>
        internal static float DashEaseOut(float p)
        {
            if (p < 0f) p = 0f; else if (p > 1f) p = 1f;
            float q = 1f - p;
            return 1f - q * q;
        }
        /// <summary>dash 每段实际距离 = 基准 ×(0.8~1.2) (每次行为掷 ±20%)。</summary>
        internal static float DashRollDistance(float baseDist, Random rng)
            => baseDist * (0.8f + (float)rng.NextDouble() * 0.4f);

        // ---- zigzag 折线 ----
        /// <summary>zigzag 持续钳 1-4s (缺省 2.2)。</summary>
        internal static float ZigzagDuration(float s) => s < 1f ? 1f : s > 4f ? 4f : s;
        /// <summary>zigzag 变向间隔钳 0.3-0.8s (缺省 0.5; 每次变向 ±20% 随机, 见 ZigzagRollTurn)。</summary>
        internal static float ZigzagTurnInterval(float s) => s < 0.3f ? 0.3f : s > 0.8f ? 0.8f : s;
        /// <summary>zigzag 速度钳 120-450 px/s (缺省 260)。</summary>
        internal static float ZigzagSpeed(float px) => px < 120f ? 120f : px > 450f ? 450f : px;
        /// <summary>zigzag 偏移约束: 基准点 ±bound 矩形 (钳 100-350px, 缺省 220), 触边反弹。</summary>
        internal static float ZigzagBound(float px) => px < 100f ? 100f : px > 350f ? 350f : px;
        /// <summary>zigzag 每次变向的实际间隔 = 基准 ×(0.8~1.2) (±20% 随机)。</summary>
        internal static float ZigzagRollTurn(float baseTurn, Random rng)
            => baseTurn * (0.8f + (float)rng.NextDouble() * 0.4f);

        // ---- circle 椭圆轨道 ----
        /// <summary>circle 持续钳 1.5-4s (缺省 2.5)。</summary>
        internal static float CircleDuration(float s) => s < 1.5f ? 1.5f : s > 4f ? 4f : s;
        /// <summary>circle 角速度钳 1-4 (缺省 2.2; pos = base + (sin(t×speed)×rx, cos(t×speed×0.9)×ry))。</summary>
        internal static float CircleSpeed(float s) => s < 1f ? 1f : s > 4f ? 4f : s;
        /// <summary>circle 横半径钳 60-260px (缺省 150)。</summary>
        internal static float CircleRx(float px) => px < 60f ? 60f : px > 260f ? 260f : px;
        /// <summary>circle 纵半径钳 30-160px (缺省 70)。</summary>
        internal static float CircleRy(float px) => px < 30f ? 30f : px > 160f ? 160f : px;

        // ---- leap 纵跳 ----
        /// <summary>leap 持续钳 0.5-1.2s (缺省 0.8)。</summary>
        internal static float LeapDuration(float s) => s < 0.5f ? 0.5f : s > 1.2f ? 1.2f : s;
        /// <summary>leap 高度钳 60-220px (缺省 140; y 偏移 = sin(πp)×height)。</summary>
        internal static float LeapHeight(float px) => px < 60f ? 60f : px > 220f ? 220f : px;
        /// <summary>leap 滞空受击箱倍率 = 1−0.4×sin(πp) (p 钳 0-1)。</summary>
        internal static float LeapAirScale(float p)
        {
            if (p < 0f) p = 0f; else if (p > 1f) p = 1f;
            return 1f - 0.4f * (float)Math.Sin(Math.PI * p);
        }

        // ---- feint 佯攻 ----
        /// <summary>feint 持续钳 2-5s (缺省 3)。</summary>
        internal static float FeintDuration(float s) => s < 2f ? 2f : s > 5f ? 5f : s;
        /// <summary>feint 假动作概率钳 0.1-0.6 (缺省 0.35; 中则预警播到 50% 时撤预警+横跳+下次攻击提前)。</summary>
        internal static float FeintCancelChance(float c) => c < 0.1f ? 0.1f : c > 0.6f ? 0.6f : c;
        /// <summary>feint 横跳距离 120-200px 随机 (复用 dash 位移逻辑)。</summary>
        internal static float FeintHopDistance(Random rng) => 120f + (float)rng.NextDouble() * 80f;
        /// <summary>feint 假动作后下次攻击提前: 0.8~1.4s 随机。</summary>
        internal static float FeintNextAttackDelay(Random rng) => 0.8f + (float)rng.NextDouble() * 0.6f;

        // ---- strafe 相位噪声 ----
        /// <summary>strafe 相位噪声率钳 0-4 rad/s (每帧 phase += (rng−0.5)×2×rate×dt;
        /// 不配 = 0 保持旧正弦原样, 推荐新配置 1.5)。</summary>
        internal static float StrafeNoiseRate(float r) => r < 0f ? 0f : r > 4f ? 4f : r;

        // ---- swarm 鼠群 ----
        /// <summary>鼠群子箱数钳 2-4 (缺省 3)。</summary>
        internal static int SwarmCount(int n) => Clamp(n, 2, 4);
        /// <summary>鼠群子箱尺寸系数钳 0.3-0.6 (缺省 0.4; 尺寸 = 布局基准 × box_scale × hitbox_scale)。</summary>
        internal static float SwarmBoxScale(float s) => s < 0.3f ? 0.3f : s > 0.6f ? 0.6f : s;
        /// <summary>鼠群散布半径钳 100-260px (缺省 170; 子箱 zigzag 游走约束)。</summary>
        internal static float SwarmSpread(float px) => px < 100f ? 100f : px > 260f ? 260f : px;
        /// <summary>鼠群子箱游走速度 240±40 px/s。</summary>
        internal const float SwarmSpeedBase = 240f, SwarmSpeedJitter = 40f;
        /// <summary>v1.46.0: swarm cfg speed 子箱移速钳制 (缺省 SwarmSpeedBase 240 = 旧恒值行为)。</summary>
        internal static float SwarmSpeed(float px) => px < 120f ? 120f : px > 450f ? 450f : px;
        /// <summary>鼠群子箱变向间隔 0.4-0.7s 均匀随机。</summary>
        internal const float SwarmTurnLo = 0.4f, SwarmTurnHi = 0.7f;

        // ---- v1.45.0 summon 嚎叫者召唤 / escape_after 掠窃者逃逸 (gunworks v0.40.0 §4.5/§4.6) ----
        /// <summary>召唤间隔钳 2-8s (缺省 4; 每 interval 秒召 1 只小怪)。</summary>
        internal static float SummonInterval(float s) => s < 2f ? 2f : s > 8f ? 8f : s;
        /// <summary>同屏小怪上限钳 1-5 (缺省 3)。</summary>
        internal static int SummonCap(int n) => Clamp(n, 1, 5);
        /// <summary>小怪独立 HP 钳 1-60 (缺省 10; 打死即灭, 不共享母体血量)。</summary>
        internal static int SummonChildHp(int hp) => Clamp(hp, 1, 60);
        /// <summary>小怪受击箱尺寸系数钳 0.25-0.6 (缺省 0.35)。</summary>
        internal static float SummonBoxScale(float s) => s < 0.25f ? 0.25f : s > 0.6f ? 0.6f : s;
        /// <summary>小怪散布半径钳 100-300px (缺省 180)。</summary>
        internal static float SummonSpread(float px) => px < 100f ? 100f : px > 300f ? 300f : px;
        /// <summary>小怪撕咬伤害钳 0-10 (缺省 2; 母体攻击命中玩家时每个存活小怪追加)。</summary>
        internal static int SummonChildAtk(int atk) => Clamp(atk, 0, 10);
        /// <summary>掠窃者逃逸计时钳 0-60s (缺省 0 = off; 到点 EndCombat("escape"))。</summary>
        internal static float EscapeAfter(float s) => s < 0f ? 0f : s > 60f ? 60f : s;

        // ==================== v1.37.0 敌人节奏修复 (开战即动 / 空窗微漂移 / tempo 个体化; 纯函数可无头单测) ====================

        /// <summary>开战首张行为卡延迟 (秒): 0~0.3 均匀随机 — 开战即动
        /// (原为抽一个完整敌节奏 2.2-3.5s, 开局站桩)。</summary>
        internal static float FirstBehaviorDelay(Random rng) => (float)rng.NextDouble() * 0.3f;

        /// <summary>敌节奏个体化系数钳 0.5-1.5 (rt_start cfg tempo, 缺省 1.0;
        /// 只乘行为间隙 — 行为卡组间隔 ×tempo, 攻击节奏 (EnemyInterval 驱动的攻击调度) 不动)。</summary>
        internal static float TempoClamped(float t) => t < 0.5f ? 0.5f : t > 1.5f ? 1.5f : t;

        /// <summary>行为空窗微漂移 (ambient): 行为间隙 >MinGap 秒时受击箱低速漂移 (ZigWalker 低速实例),
        /// 不占卡组不占调度, 新行为开始即覆盖。</summary>
        internal const float AmbientMinGap = 0.6f;
        /// <summary>ambient 漂移速度 px/s。</summary>
        internal const float AmbientSpeed = 60f;
        /// <summary>ambient 漂移偏移约束: 基准点 ±px 矩形。</summary>
        internal const float AmbientBound = 40f;
        /// <summary>ambient 变向间隔 (秒) 均匀随机区间。</summary>
        internal const float AmbientTurnLo = 0.9f, AmbientTurnHi = 1.5f;

        // ==================== v1.37.0 稀有度两阶段掷签 (combat.roll_loot; 纯函数可无头单测) ====================

        /// <summary>稀有度档数: 0 common / 1 fine / 2 rare / 3 epic / 4 legend。</summary>
        internal const int RarityTiers = 5;
        /// <summary>档 → pss 字符串名 (pool 条目 rarity 键写法; 非法必抛, 数字 0-4 容忍钳制)。</summary>
        internal static readonly string[] RarityNames = { "common", "fine", "rare", "epic", "legend" };

        /// <summary>稀有度名 → 档 (0-4); 非法/空 → -1 (必抛判定归解析层)。纯函数。</summary>
        internal static int RarityIndex(string name)
        {
            if (name == null) return -1;
            for (int i = 0; i < RarityNames.Length; i++)
                if (string.Equals(RarityNames[i], name.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>按地图 danger 的默认稀有度分布 (千分比, 索引 = Clamp(danger,1,5)-1;
        /// 行内 = [common, fine, rare, epic, legend]; 2★ 的 3‰ 即 0.3% 传奇)。</summary>
        internal static readonly int[][] RarityTables =
        {
            new[] { 700, 220, 70, 10, 0 },    // 1★
            new[] { 600, 260, 110, 30, 3 },   // 2★
            new[] { 500, 280, 150, 60, 10 },  // 3★
            new[] { 420, 290, 180, 90, 20 },  // 4★
            new[] { 350, 300, 220, 100, 30 }, // 5★
        };

        /// <summary>danger (钳 1-5) → 默认稀有度分布行。</summary>
        internal static int[] RarityTableFor(int danger) => RarityTables[Clamp(danger, 1, 5) - 1];

        /// <summary>稀有度档掷签: perMille 千分比表按总和归一 (长度任意; 空表/总和≤0 → 0 普通档)。</summary>
        internal static int RarityRoll(int[] perMille, Random rng)
        {
            if (perMille == null || perMille.Length == 0) return 0;
            int total = 0;
            foreach (var w in perMille) if (w > 0) total += w;
            if (total <= 0) return 0;
            int r = rng.Next(total);
            for (int i = 0; i < perMille.Length; i++)
            {
                if (perMille[i] <= 0) continue;
                if (r < perMille[i]) return i;
                r -= perMille[i];
            }
            return perMille.Length - 1; // 浮点/舍入兜底 (整型路径不可达)
        }

        /// <summary>档空逐级往下降档: 从 rolled 降到 0, 返回首个有候选的档;
        /// 全空 → -1 (调用方回退单层候选直抽)。</summary>
        internal static int RarityFallbackTier(int rolled, bool[] tierHasCand)
        {
            if (tierHasCand == null || tierHasCand.Length == 0) return -1;
            int t = rolled < 0 ? 0 : rolled >= tierHasCand.Length ? tierHasCand.Length - 1 : rolled;
            for (; t >= 0; t--)
                if (tierHasCand[t]) return t;
            return -1;
        }

        /// <summary>两阶段稀有度掷签 (v1.37.0): rarityPerMille=null → 单层旧语义直转;
        /// 否则每件物品独立 — stage1 按千分比表掷稀有度档, 档空逐级降档 (全空回退全候选直抽),
        /// stage2 档内候选 (weight>0 且 min_depth 过) 按 weight 加权; 数量/件数同单层 RollLoot。</summary>
        internal static List<KeyValuePair<string, int>> RollLoot(List<SceneLootEntry> pool, int depth,
            bool ignoreMinDepth, float valueMul, int minItems, int maxItems, int[] rarityPerMille, Random rng)
        {
            if (rarityPerMille == null)
                return RollLoot(pool, depth, ignoreMinDepth, valueMul, minItems, maxItems, rng);
            var result = new List<KeyValuePair<string, int>>();
            if (pool == null || pool.Count == 0) return result;
            var cand = new List<SceneLootEntry>();
            foreach (var e in pool)
            {
                if (e == null || e.Weight <= 0) continue;
                if (!ignoreMinDepth && e.MinDepth > depth) continue;
                cand.Add(e);
            }
            if (cand.Count == 0) return result;
            var buckets = new List<SceneLootEntry>[RarityTiers];
            var tierHas = new bool[RarityTiers];
            foreach (var e in cand)
            {
                int t = Clamp(e.Rarity, 0, RarityTiers - 1);
                if (buckets[t] == null) buckets[t] = new List<SceneLootEntry>();
                buckets[t].Add(e);
                tierHas[t] = true;
            }
            int n = rng.Next(minItems, maxItems + 1);
            for (int i = 0; i < n; i++)
            {
                int tier = RarityFallbackTier(RarityRoll(rarityPerMille, rng), tierHas);
                var bag = tier >= 0 ? buckets[tier] : cand;   // 全空回退: 全候选直抽 (单层语义)
                float total = 0f;
                foreach (var e in bag) total += e.Weight;
                if (total <= 0f) continue;
                float r = (float)(rng.NextDouble() * total);
                var pick = bag[bag.Count - 1];
                foreach (var e in bag) { r -= e.Weight; if (r <= 0f) { pick = e; break; } }
                int cnt = SceneJson.ParseCount(pick.Count, rng);
                cnt = Math.Max(1, (int)Math.Round(cnt * valueMul));
                result.Add(new KeyValuePair<string, int>(pick.Id, cnt));
            }
            return result;
        }
    }
}
