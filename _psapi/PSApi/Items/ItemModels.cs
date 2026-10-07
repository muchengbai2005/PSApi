using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSApi.Items
{
    /// <summary>
    /// 物品内容文件 DTO: packs/&lt;pack&gt;/items/*.json → { "items": [ ItemDefJson ] }。
    /// 未知字段忽略; 注释与尾逗号允许(PackScanner 同款 JsonOpts)。
    /// </summary>
    internal sealed class ItemFile
    {
        [JsonPropertyName("items")] public List<ItemDefJson> Items { get; set; }
    }

    internal sealed class ItemDefJson
    {
        // ---- 身份 ----
        [JsonPropertyName("id")] public string Id { get; set; }                    // 必填; 建议 "pack:name", 原样写入游戏目录
        [JsonPropertyName("directory")] public string Directory { get; set; }      // 目录类名; 空=按 tags 映射, 再空=Misc
        [JsonPropertyName("template")] public string Template { get; set; }        // 克隆模板 id; 空=目录第一个

        // ---- 文本 ----
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("desc")] public string Desc { get; set; }
        [JsonPropertyName("flavor")] public string Flavor { get; set; }

        // ---- 数值 ----
        [JsonPropertyName("value")] public int Value { get; set; }
        [JsonPropertyName("stack")] public int Stack { get; set; }                 // unitCount; 0/1=默认

        // ---- 外观 ----
        [JsonPropertyName("icon")] public JsonElement? Icon { get; set; }          // "name" | "pack:name" | { "file": "x.png" }
        [JsonPropertyName("shape")] public ShapeJson Shape { get; set; }

        // ---- 类型/标签 ----
        [JsonPropertyName("types")] public List<string> Types { get; set; }        // itemTypes(SetGameItemType)
        [JsonPropertyName("typesMode")] public string TypesMode { get; set; }      // merge(默认)|replace
        [JsonPropertyName("tags")] public List<string> Tags { get; set; }          // types 别名: 既映射目录也作 types (mcb_* 包在用)

        // ---- 品质 ----
        [JsonPropertyName("qualities")] public List<string> Qualities { get; set; }        // 可用品质层 id 列表
        [JsonPropertyName("defaultQuality")] public string DefaultQuality { get; set; }    // 出厂品质; 空=qualities[0]

        // ---- 机制 ----
        [JsonPropertyName("contraband")] public string Contraband { get; set; }            // none|low|mid|high|critical
        [JsonPropertyName("test")] public bool Test { get; set; }                    // v0.8.0: F6 物品浏览器"测试"分类 (CatalogLabels 第 29 类, 只含标记物品)
        [JsonPropertyName("machine")] public string Machine { get; set; }                  // 机器引用(P2 兼容字段); 非空=启用机器配方/原生工厂推断

        // ---- 次数 (v0.8.1: 原生 UseCountHelper; tooltip 自动显示 x/上限, 归零销毁, 次数存 tag 随存档) ----
        [JsonPropertyName("useCount")] public int UseCount { get; set; }             // 最大使用次数; 0=不启用
        [JsonPropertyName("useBaseValue")] public int UseBaseValue { get; set; }     // useAutoValue 底价(次数归零时的残值)
        [JsonPropertyName("useValuePerUse")] public int UseValuePerUse { get; set; } // >0 启用按剩余次数定价: 价值=底价+次价×剩余


        internal bool WantsMachineRecipes => !string.IsNullOrEmpty(Machine);
    }

    internal sealed class ShapeJson
    {
        [JsonPropertyName("w")] public int W { get; set; }
        [JsonPropertyName("h")] public int H { get; set; }
        [JsonPropertyName("cells")] public int[][] Cells { get; set; }
    }

    /// <summary>品质层文件 DTO: packs/&lt;pack&gt;/qualities/*.json → { "qualities": [ ... ] }。</summary>
    internal sealed class QualityFile
    {
        [JsonPropertyName("qualities")] public List<QualityDefJson> Qualities { get; set; }
    }

    internal sealed class QualityDefJson
    {
        [JsonPropertyName("id")] public string Id { get; set; }               // "pack:q_xxx"
        [JsonPropertyName("tag")] public string Tag { get; set; }             // tag 模式: TagSystem 标签; feature 模式可空
        [JsonPropertyName("display")] public string Display { get; set; }     // 显示名
        [JsonPropertyName("namePrefix")] public string NamePrefix { get; set; } // v1.1(名称前缀)
        [JsonPropertyName("priceMul")] public float PriceMul { get; set; } = 1f;
        [JsonPropertyName("tier")] public int Tier { get; set; }
        // 原版对齐(items/08 v2): mode=feature 时用原版 ItemFeature 管线(标签打印机/安检识别/tooltip 全原生支持)
        [JsonPropertyName("mode")] public string Mode { get; set; }           // "tag"(默认)|"feature"
        [JsonPropertyName("category")] public string Category { get; set; }   // feature 模式的特性类别(原版常量如 CATEGORY_CHEMICAL_PURITY, 或自建 CATEGORY_XXX); 空=display
        [JsonPropertyName("categoryDisplay")] public string CategoryDisplay { get; set; } // 自建类别的中文标题(打印机界面显示; 经 StringToDisplayString 注入)
    }

    /// <summary>解析后的完整物品定义(注册期产物, 含来源包与解析好的图标 key)。</summary>
    internal sealed class ItemDef
    {
        internal string Id;
        internal string PackId;
        internal string Directory;
        internal string Template;
        internal string Name, Desc, Flavor;
        internal int Value;
        internal int Stack;
        internal string IconKey;          // IconService 的完整 key(<pack>:<name>); null=用模板图标
        internal int ShapeW, ShapeH;
        internal int[][] ShapeCells;      // null=克隆模板形状
        internal List<string> Types;
        internal bool TypesReplace;
        internal List<string> Qualities;
        internal string DefaultQuality;
        internal int ContrabandLevel;     // 0=无
        internal bool CloneRecipes;       // machine 字段非空 → 无 JSON 配方时保留模板兼容行为
        internal bool Test;               // v0.8.0: F6 物品浏览器"测试"分类标记 (ItemsFacade.Catalog 第 29 类)
        internal int UseCount;            // v0.8.1: 使用次数上限 (>0 启用 UseCountHelper，归零行为见 BuildItem)
        internal int UseBaseValue;        // v0.8.1: 按次数折算售价时的基础值 (valuePerUse>0 才生效)
        internal int UseValuePerUse;      // v0.8.1: 每次使用附加的价值
    }
}
