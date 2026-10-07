using System;
using System.Collections.Generic;
using Il2CppTMPro;
using MelonLoader;
using PSApi.Events.UI;
using UnityEngine;

namespace PSApi.Events.Scenes
{
    /// <summary>
    /// P3 (v1.28.0) 场景信息栏服务 — scene.log(text) 的后端: 左下角滚动日志,
    /// 视觉与 raid 信息栏一致 (同一套 UguiBuilder + RaidLayout log 区数值 +
    /// RaidCore.AppendLog 滚动逻辑), 内容包作者不必用 ui.text 自己拼。
    /// 懒构建: 场景内首次 scene.log 才建 canvas (层级 = 图元窗 canvas-1, 运行时探测);
    /// 场景撤离 (SceneService.DoExit) 自动销毁, Unity 场景卸载只清引用。
    /// </summary>
    internal sealed class SceneLogService
    {
        private readonly MelonLogger.Instance _logger;
        private readonly List<object> _pins = new List<object>();
        private readonly UguiBuilder _ugui;
        private readonly List<string> _lines = new List<string>();

        private GameObject _go;
        private TextMeshProUGUI _tmp;
        private string _sceneId;

        /// <summary>场景服务反向引用 (Plugin 接线, 与其他服务同款; scene.log 取当前场景 id 用)。</summary>
        internal SceneService Scenes;

        internal SceneLogService(MelonLogger.Instance logger)
        {
            _logger = logger;
            _ugui = new UguiBuilder(logger, _pins);
        }

        /// <summary>当前缓冲行数 (诊断/测试用)。</summary>
        internal int LineCount => _lines.Count;

        /// <summary>追加一行 (滚动, 容量走 RaidLayout log.lines, 默认 RaidCore.LogCap=8)。
        /// 场景未给 (sceneId=null) 时只缓冲不建 UI — 事实上 builtin 只从场景内调用。</summary>
        internal void Log(string sceneId, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int cap = Raid.RaidCore.LogCap;
            try { cap = Raid.RaidLayout.ClampLines(Raid.RaidLayout.Active.Log.Lines); } catch { }
            Raid.RaidCore.AppendLog(_lines, text, cap);
            if (sceneId == null) return;
            if (_go is null || _sceneId != sceneId) Build(sceneId);
            Refresh();
        }

        private void Build(string sceneId)
        {
            DestroyGo();
            _sceneId = sceneId;
            var G = Raid.RaidLayout.Active.Log;
            float font = Raid.RaidLayout.ClampFont(G.Font);
            int lines = Raid.RaidLayout.ClampLines(G.Lines);
            int order = _ugui.DetectPixelCanvasOrder(3, "场景信息栏") - 1;
            _go = _ugui.MakeCanvas("PSApiSceneLog", order);
            if (_go is null) { PsApi.Warn(_logger, "[scene-log] canvas 创建失败"); return; }
            var root = _go.transform.TryCast<RectTransform>();   // Il2Cpp 红线: 不用 as
            if (root is null)
            {
                PsApi.Warn(_logger, "[scene-log] canvas RectTransform 获取失败");
                DestroyGo();
                return;
            }
            float pad = 10f;
            float panelW = G.W + 2 * pad;
            float panelH = lines * G.LineH + 2 * pad;
            var panel = _ugui.MakePanel(root, "log", panelW, panelH, new Color(0.07f, 0.07f, 0.10f, 0.88f), false);
            UguiBuilder.AnchorBottomLeft(panel, root, G.X, G.Y);
            _tmp = _ugui.MakeText(panel, "txt", " ", font, new Color(0.78f, 0.78f, 0.78f), TextAlignmentOptions.TopLeft);
            _tmp.rectTransform.sizeDelta = new Vector2(G.W, lines * G.LineH);
            UguiBuilder.SeatTop(_tmp.rectTransform, pad);
        }

        private void Refresh()
        {
            if (_tmp is null) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _lines.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(_lines[i]);
            }
            try { _tmp.text = sb.Length == 0 ? " " : sb.ToString(); } catch { }
        }

        private void DestroyGo()
        {
            try { if (_go is not null) UnityEngine.Object.Destroy(_go); } catch { }
            _go = null;
            _tmp = null;
            _sceneId = null;
        }

        /// <summary>场景撤离 (DoExit): 销毁 UI + 清缓冲 (信息栏内容当天当场有效, 不跨场景带)。</summary>
        internal void OnSceneExit()
        {
            DestroyGo();
            _lines.Clear();
        }

        /// <summary>Unity 场景卸载: 对象已随场景销毁, 只清引用 (缓冲也清 — 回店即失效)。</summary>
        internal void OnSceneLeft()
        {
            _go = null;
            _tmp = null;
            _sceneId = null;
            _lines.Clear();
        }
    }
}
