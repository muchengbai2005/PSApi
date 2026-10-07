using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PSApi
{
    /// <summary>
    /// 分帧执行器: 把重活切到多帧, 避免开店/载入时主线程卡顿尖刺
    /// (教训: 原版模板 dump 同步执行会造成开店卡顿, 必须 ~每帧少量)。
    /// 用法: Enqueue 工作项; 宿主 MelonMod.OnUpdate 每帧调 Drive()。
    /// </summary>
    internal sealed class FrameSlicer
    {
        private readonly Queue<Action> _queue = new Queue<Action>();
        private readonly Stopwatch _sw = new Stopwatch();

        internal double BudgetMs = 1.5;   // 每帧预算
        internal int Pending => _queue.Count;

        internal void Enqueue(Action work) { if (work != null) _queue.Enqueue(work); }

        internal void EnqueueMany(IEnumerable<Action> works)
        {
            if (works == null) return;
            foreach (var w in works) Enqueue(w);
        }

        /// <summary>清空未执行任务(场景切换/重载时)。</summary>
        internal void Clear() => _queue.Clear();

        /// <summary>每帧驱动: 在预算内尽量执行。返回本帧执行数。</summary>
        internal int Drive()
        {
            if (_queue.Count == 0) return 0;
            _sw.Restart();
            int done = 0;
            while (_queue.Count > 0)
            {
                var work = _queue.Dequeue();
                try { work(); }
                catch (Exception e) { MelonLoader.MelonLogger.Error("[psapi] sliced work failed: " + e); }
                done++;
                if (_sw.Elapsed.TotalMilliseconds >= BudgetMs) break;
            }
            return done;
        }
    }
}
