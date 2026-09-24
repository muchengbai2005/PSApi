using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace PSApi.Items
{
    /// <summary>
    /// 图标服务: 包内 icons/*.png 解码为 Sprite, 键为 "&lt;packId&gt;:&lt;name&gt;"(命名空间防冲突)。
    /// 物品的 spriteAtlasPath 写 MarkerAtlas 作标识, spritePath 写完整键;
    /// v0.5.3: 两个解析入口都要挂 ——
    ///   ① GameItemElement.ResolveSpriteByName(name)   ← SetSpriteQuick / ValidatePartialSprite 走这里
    ///   ② RenderHandler.LoadFromAtlas(atlasPath, name) ← GameItemElement..ctor 直接调用, 不经 ①
    /// 只挂 ① 时, 由原生机器工厂/克隆路径新建的元素会用自身 atlas 直查图集 → 自定义图标丢失回落原版切片。
    /// 移植自 ExtraItems.CustomIcons/CustomIconPatch。
    /// </summary>
    internal static class IconService
    {
        internal const string MarkerAtlas = "PSApi/icons";

        private static readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<object> _keepAlive = new List<object>();
        private static MelonLogger.Instance _log;

        internal static void Init(MelonLogger.Instance log) { _log = log; }

        internal static bool Has(string key)
            => key != null && _sprites.ContainsKey(key);

        internal static bool TryGet(string key, out Sprite sprite)
        {
            if (key != null) return _sprites.TryGetValue(key, out sprite);
            sprite = null;
            return false;
        }

        internal static int Count => _sprites.Count;

        /// <summary>诊断: 已加载的全部键(启动日志用, 排查"图标不生效"必看)。</summary>
        internal static string DescribeKeys()
        {
            var keys = new List<string>(_sprites.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(", ", keys);
        }

        /// <summary>加载一个包的 icons/ 目录, 键 = packId:文件名(无扩展名)。返回加载数。</summary>
        internal static int LoadPackIcons(PackInfo pack)
        {
            int n = 0;
            try
            {
                foreach (var rel in pack.Source.ListFiles("icons", ".png"))
                {
                    string name = Path.GetFileNameWithoutExtension(rel);
                    if (LoadOne(pack.Source, rel, pack.Id + ":" + name)) n++;
                }
            }
            catch (Exception e) { PsApi.Warn(_log, $"pack '{pack.Id}' icons load failed: {e.Message}"); }
            return n;
        }

        // 注意: 包图标只注册命名空间键(packId:name), 不造扁平别名。
        // v0.5.3 起 RenderHandler.LoadFromAtlas 是全局拦截点, 扁平键(如 "furnace")
        // 会把同名原版切片全局替掉。

        private static bool LoadOne(IPackSource source, string rel, string key)
        {
            try
            {
                if (_sprites.ContainsKey(key)) return false;
                byte[] png = source.ReadBytes(rel);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (!ImageConversion.LoadImage(tex, png))
                {
                    PsApi.Warn(_log, "icon decode failed: " + rel);
                    UnityEngine.Object.Destroy(tex);
                    return false;
                }
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                sprite.hideFlags = HideFlags.HideAndDontSave;
                _sprites[key] = sprite;
                _keepAlive.Add(sprite);
                _keepAlive.Add(tex);
                return true;
            }
            catch (Exception e) { PsApi.Warn(_log, "icon load failed: " + rel + " " + e.Message); return false; }
        }
    }

    /// <summary>按切片名拦截图标解析, 提供包内自定义 Sprite(对原版切片名无影响)。</summary>
    [HarmonyPatch(typeof(GameItemElement), "ResolveSpriteByName")]
    internal static class IconResolvePatch
    {
        private static bool Prefix(string name, ref Sprite __result)
        {
            if (IconService.TryGet(name, out var sprite))
            {
                __result = sprite;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 图集直查入口(GameItemElement..ctor / 各 PixelElement 都调这里, 不经 ResolveSpriteByName)。
    /// 命中自定义键才接管, 其余原样放行 —— 原版切片名不含 "pack:" 形式, 冲突面为零。
    /// </summary>
    [HarmonyPatch(typeof(RenderHandler), "LoadFromAtlas")]
    internal static class IconAtlasPatch
    {
        private static bool Prefix(string name, ref Sprite __result)
        {
            if (IconService.TryGet(name, out var sprite))
            {
                __result = sprite;
                return false;
            }
            return true;
        }
    }
}
