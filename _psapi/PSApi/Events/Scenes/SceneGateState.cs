using System;
using System.Collections.Generic;
using PSApi.Events.PsScript;

namespace PSApi.Events.Scenes
{
    /// <summary>
    /// v1.47.0 场景门禁 (scenes.set_gate 落点) — 纯脚本谓词, 仿 crime.set_filter (CrimeExemptState)。
    ///
    /// 包脚本注册 fn(场景全 id), 返回值三态:
    ///   null / false / 非字符串  → 可进 (picker 正常可选, scenes.enter 放行, scenes.list locked=false)
    ///   "hide" 前缀字符串        → 完全隐藏 (picker 条目剔除, scenes.list 不列, scenes.enter 拒绝)
    ///   其他字符串               → 锁定原因 (picker 灰字不可点+原因展示, scenes.enter 拒绝, scenes.list locked=true + lock_reason)
    ///
    /// 消费点: SceneService.OpenPicker (BuildPickEntries 的 lockRule 实参) / EnterScene (硬门禁) /
    /// ListScenes (locked/lock_reason 填充)。可调对象不持久化, 包脚本顶层 (events/*.pss) 每次加载重新注册。
    /// 异常/重入 = 放行 (null) + warn, 不炸游戏。
    /// </summary>
    internal static class SceneGateState
    {
        /// <summary>隐藏哨兵前缀: 门禁返回以它开头的字符串 = 该场景完全不出现在 picker/list。</summary>
        internal const string HidePrefix = "hide";

        /// <summary>门禁谓词 (scenes.set_gate 注册)。</summary>
        internal static PsCallable Fn;
        internal static Interpreter Itp;
        /// <summary>防重入: 门禁求值中再触发场景导航 (脚本里 scenes.enter?) 直接放行。</summary>
        internal static bool Running;

        internal static bool IsHidden(string gateResult)
            => gateResult != null && gateResult.StartsWith(HidePrefix, StringComparison.Ordinal);

        /// <summary>求值门禁: null=可进; "hide*"=隐藏; 其他字符串=锁定原因。异常/重入 = null (放行) + warn。</summary>
        internal static string Eval(string sceneFullId)
        {
            var fn = Fn;
            var itp = Itp;
            if (fn == null || itp == null || Running || string.IsNullOrEmpty(sceneFullId)) return null;
            try
            {
                Running = true;
                itp.BeginRun();
                var ret = itp.CallCallable(fn, new List<object> { sceneFullId }, 0);
                if (ret is string s && !string.IsNullOrWhiteSpace(s)) return s;
                return null;   // null/false/非字符串 = 可进
            }
            catch (Exception e)
            {
                MelonLoader.MelonLogger.Warning($"[scenes] set_gate 门禁求值出错 ({sceneFullId}, 按可进放行): {e.Message}");
                return null;
            }
            finally { Running = false; }
        }
    }
}
