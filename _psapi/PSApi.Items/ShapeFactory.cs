using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2Cpp;
using MelonLoader;

namespace PSApi.Items
{
    /// <summary>
    /// GridShape 原生工厂: 克隆供体形状后按内存布局改写字段
    /// (布局已逆向: +0x20 int width, +0x24 int height, +0x28 Il2CppStructArray&lt;byte&gt; cells 行主序 0=空 1=占用)。
    /// 移植自 ExtraItems.ShapeFactory; 数组必须常驻防 GC(_keepAlive)。
    /// </summary>
    internal static unsafe class ShapeFactory
    {
        private const int OFF_W = 0x20;
        private const int OFF_H = 0x24;
        private const int OFF_ARR = 0x28;

        private static readonly List<object> _keepAlive = new List<object>();
        private static MelonLogger.Instance _log;

        internal static void Init(MelonLogger.Instance log) { _log = log; }

        internal static GridShape Create(int w, int h, int[][] cells)
        {
            try
            {
                // 供体: 任意小物品的形状, 克隆后替换字段
                GameItem donor = null;
                try { donor = DirectoryMaster.Item("scav_token", true); } catch { }
                if (donor == null || donor.shape == null)
                {
                    PsApi.Warn(_log, "shape donor unavailable");
                    return null;
                }
                GridShape shape = donor.shape.Clone();
                if (shape == null) return null;

                if (w < 1) w = 1;
                if (h < 1) h = 1;
                IntPtr p = shape.Pointer;
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(w * h);
                for (int i = 0; i < arr.Length; i++) arr[i] = 0;
                if (cells != null)
                {
                    foreach (var c in cells)
                    {
                        if (c == null || c.Length < 2) continue;
                        int x = c[0], y = c[1];
                        if (x >= 0 && x < w && y >= 0 && y < h) arr[y * w + x] = 1;
                    }
                }
                _keepAlive.Add(arr); // 防止数组被回收
                Marshal.WriteInt32(p + OFF_W, w);
                Marshal.WriteInt32(p + OFF_H, h);
                Marshal.WriteIntPtr(p + OFF_ARR, arr.Pointer);
                return shape;
            }
            catch (Exception e)
            {
                PsApi.Warn(_log, "ShapeFactory.Create failed: " + e.Message);
                return null;
            }
        }
    }
}
