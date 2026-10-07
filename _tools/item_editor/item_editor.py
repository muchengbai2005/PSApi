# -*- coding: utf-8 -*-
"""
Probably Stolen 物品编辑器 (PS-API 内容包版)
- 36x36 网格编辑物品占格(与游戏 16px/格 对应)
- Ctrl+左键涂抹物品轮廓(必须相邻、不可与其他物品重叠), 松开 Ctrl 生成物品
- 左键选中/拖动物品, 空白处左键拖出框选(多选)
- 右侧面板编辑: 模板(原版物品下拉)/ID/名称/描述/风味/标签(游戏全集多选)/违禁品等级/价值
- 图片导入(按钮/拖放), 自由移动/缩放(XY独立)/旋转, X 键与物品绑定/解绑, 绑定后随物品移动旋转
- R 键旋转物品(连带绑定图片), Delete 删除, Ctrl+滚轮缩放画布, 右键拖动平移
- 导出 JSON + 物品图标 PNG(尺寸=占格*16), JSON 为 PS-API 框架格式
  (desc/contraband/shape{w,h,cells}/icon{file}/template, 见 _api_design/items/07)
  导出后把 items/ 与 icons/ 放入 UserData/PSApi/packs/<你的包>/ 即可被加载
"""
import json
import math
import os
import re
import sys
import tkinter as tk
from tkinter import ttk, filedialog, messagebox, colorchooser

try:
    from PIL import Image, ImageTk
except ImportError:
    print("需要 Pillow: pip install Pillow")
    sys.exit(1)

try:
    from tkinterdnd2 import DND_FILES, TkinterDnD
    HAS_DND = True
except Exception:
    HAS_DND = False

GRID_W = 36
GRID_H = 36
CELL = 16  # 游戏标准: 16px/格
APP_TITLE = "Probably Stolen 物品编辑器"

GAME_TAGS = [
    "ACCESS_CARD", "ALARM_MODULE", "ALCOHOL", "AMMUNITION", "ARMOR", "ART",
    "BASIC_MATERIAL", "BREVAGE", "CHEMICAL_SUPPLIES", "CONTRABAND", "DOCUMENT",
    "ENERGY_CELL", "FARMING_SUPPLIES", "FILTER", "FIREARM", "FOOD", "GUN_MOD",
    "HOUSEHOLD_GOOD", "JUNK", "LOTTERY", "LOWTECH", "LUXURY_ITEM", "MACHINE",
    "MAKESHIFT", "MATERIAL", "MEDICAL", "MEDICAL_TOOL", "MELEE_WEAPON",
    "MIDLINE", "MODDABLE", "MODULE", "MUNITION", "NARCOTIC", "NODE", "POISON",
    "PROCESSED_FOOD", "SEED", "STORAGE", "SUBSTANCE", "SUPPLY_CRATE", "TOOL",
    "TREAT", "WATER", "WATER_PURIFICATION_SUPPLY", "WATER_RATION", "WEAPON",
]

# 标签中文对照(编辑器显示用; 参照游戏物品图鉴/UserData/items_catalog.md 的官中用词)
TAG_NAMES_ZH = {
    "ACCESS_CARD": "门禁卡", "ALARM_MODULE": "警报模块", "ALCOHOL": "酒类",
    "AMMUNITION": "弹药", "ARMOR": "护甲", "ART": "艺术品",
    "BASIC_MATERIAL": "基础材料", "BREVAGE": "饮料(原版拼写如此)",
    "CHEMICAL_SUPPLIES": "化学用品", "CONTRABAND": "违禁品", "DOCUMENT": "文件",
    "ENERGY_CELL": "能量电池", "FARMING_SUPPLIES": "农用品", "FILTER": "滤芯",
    "FIREARM": "枪械", "FOOD": "食物", "GUN_MOD": "枪械配件",
    "HOUSEHOLD_GOOD": "家居用品", "JUNK": "垃圾", "LOTTERY": "彩票",
    "LOWTECH": "低科技", "LUXURY_ITEM": "奢侈品", "MACHINE": "机器",
    "MAKESHIFT": "土制/临时拼凑", "MATERIAL": "材料", "MEDICAL": "医疗用品",
    "MEDICAL_TOOL": "医疗工具", "MELEE_WEAPON": "近战武器",
    "MIDLINE": "中端货", "MODDABLE": "可改装", "MODULE": "机器模块",
    "MUNITION": "军火", "NARCOTIC": "麻醉/毒品", "NODE": "节点",
    "POISON": "毒药", "PROCESSED_FOOD": "加工食品", "SEED": "种子",
    "STORAGE": "容器", "SUBSTANCE": "物质", "SUPPLY_CRATE": "补给箱",
    "TOOL": "工具", "TREAT": "零食点心", "WATER": "水",
    "WATER_PURIFICATION_SUPPLY": "净水用品", "WATER_RATION": "配给水",
    "WEAPON": "武器",
}


def tag_display(tag):
    """列表显示用: 'FIREARM  枪械'; 无对照时只显示英文"""
    zh = TAG_NAMES_ZH.get(tag, "")
    return f"{tag}  {zh}" if zh else tag
CONTRABAND_LEVELS = [("无", "none"), ("低", "low"), ("中", "mid"), ("高", "high"), ("严重", "critical")]
PALETTE = [
    "#e6194b", "#3cb44b", "#4363d8", "#f58231", "#911eb4", "#42d4f4",
    "#f032e6", "#bfef45", "#469990", "#9a6324", "#800000", "#808000",
    "#000075", "#a9a9a9", "#e6beff", "#ffd8b1",
]

_ext_counter = [0]


def _uid(prefix):
    _ext_counter[0] += 1
    return f"{prefix}_{_ext_counter[0]}"


def _parse_catalog(path):
    """从 UserData/items_catalog.md 表格解析模板候选: [{id, name}]"""
    rows = []
    with open(path, encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if not line.startswith("|"):
                continue
            cells = [c.strip() for c in line.strip("|").split("|")]
            if len(cells) < 4:
                continue
            iid = cells[0]
            if not iid or iid == "ID" or iid.startswith("-"):
                continue
            rows.append({"id": iid, "name": cells[3] or cells[2] or ""})
    rows.sort(key=lambda r: r["id"])
    return rows


def load_template_candidates():
    """模板候选: 优先游戏目录活图鉴, 其次脚本旁快照 game_templates.json"""
    here = os.path.dirname(os.path.abspath(__file__))
    catalog = os.path.normpath(os.path.join(here, "..", "..", "UserData", "items_catalog.md"))
    if os.path.isfile(catalog):
        try:
            rows = _parse_catalog(catalog)
            if rows:
                return rows
        except Exception:
            pass
    snap = os.path.join(here, "game_templates.json")
    if os.path.isfile(snap):
        try:
            with open(snap, encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return []


class ImageAsset:
    """导入的图片, 自由变换; 绑定后跟随物品"""
    def __init__(self, path, pil_image):
        self.uid = _uid("img")
        self.path = path
        self.pil = pil_image  # PIL.Image RGBA 原图
        self.cx = 0.0         # 中心坐标 (zoom-1 像素, 即 1格=16px 的坐标系)
        self.cy = 0.0
        self.w_px = float(pil_image.width)
        self.h_px = float(pil_image.height)
        self.rot = 0.0        # 顺时针角度
        self.bound_to = None  # ItemDef.uid or None

    def to_dict(self):
        return {"uid": self.uid, "path": self.path, "cx": self.cx, "cy": self.cy,
                "w_px": self.w_px, "h_px": self.h_px, "rot": self.rot,
                "bound_to": self.bound_to}


class IconBinding:
    """物品与图片的绑定信息"""
    def __init__(self, image_uid, dx, dy, w_px, h_px, rot):
        self.image_uid = image_uid
        self.dx = dx          # 相对物品包围盒中心的偏移(格)
        self.dy = dy
        self.w_px = w_px      # 绑定时的尺寸(zoom-1 px)
        self.h_px = h_px
        self.rot = rot        # 顺时针角度

    def to_dict(self):
        return {"image_uid": self.image_uid, "dx": self.dx, "dy": self.dy,
                "w_px": self.w_px, "h_px": self.h_px, "rot": self.rot}


class ItemDef:
    def __init__(self, cells, color):
        self.uid = _uid("item")
        self.item_id = f"new_item_{_ext_counter[0]}"
        self.template = ""       # 克隆模板(原版物品 id), 空=框架默认(目录第一个)
        self.name = ""
        self.desc = ""
        self.flavor = ""
        self.tags = set()
        self.contraband = "none"
        self.value = 10
        self.cells = set(cells)  # set[(x,y)]
        self.color = color
        self.icon = None         # IconBinding or None

    def bbox(self):
        xs = [c[0] for c in self.cells]
        ys = [c[1] for c in self.cells]
        return min(xs), min(ys), max(xs), max(ys)

    def norm_cells(self):
        x0, y0, _, _ = self.bbox()
        return sorted((x - x0, y - y0) for x, y in self.cells)

    def grid_size(self):
        x0, y0, x1, y1 = self.bbox()
        return x1 - x0 + 1, y1 - y0 + 1

    def to_dict(self, icon_file=None):
        """工程文件用(编辑器内部格式, 保留图标绑定元数据)"""
        w, h = self.grid_size()
        d = {
            "id": self.item_id,
            "template": self.template,
            "name": self.name,
            "description": self.desc,
            "flavor": self.flavor,
            "tags": sorted(self.tags),
            "contrabandLevel": self.contraband,
            "value": self.value,
            "cells": [list(c) for c in self.norm_cells()],
            "gridW": w,
            "gridH": h,
        }
        if self.icon:
            d["icon"] = {
                "file": icon_file or "",
                "sourceImageUid": self.icon.image_uid,
                "offsetCells": [round(self.icon.dx, 4), round(self.icon.dy, 4)],
                "sizePx": [round(self.icon.w_px, 2), round(self.icon.h_px, 2)],
                "rotation": round(self.icon.rot, 2),
            }
        return d

    def to_export_dict(self, icon_file=None):
        """导出用: PS-API 框架物品格式 (packs/<pack>/items/*.json, 见 _api_design/items/07)"""
        w, h = self.grid_size()
        d = {
            "id": self.item_id,
            "name": self.name,
            "desc": self.desc,
            "flavor": self.flavor,
            "value": self.value,
            "tags": sorted(self.tags),
            "contraband": self.contraband,
            "shape": {
                "w": w,
                "h": h,
                "cells": [list(c) for c in self.norm_cells()],
            },
        }
        if self.template:
            d["template"] = self.template
        if self.icon:
            d["icon"] = {"file": icon_file or ""}
        return d


class ItemEditor:
    def __init__(self, smoke=False):
        self.smoke = smoke
        if HAS_DND and not smoke:
            self.root = TkinterDnD.Tk()
        else:
            self.root = tk.Tk()
        self.root.title(APP_TITLE)
        self.root.geometry("1500x950")

        # 模板候选: [{id, name}], 显示为 "id  中文名"
        self.templates = load_template_candidates()
        self._template_names = {t["id"]: t.get("name", "") for t in self.templates}
        self._template_displays = [
            t["id"] + ("  " + t["name"] if t.get("name") and t["name"] != t["id"] else "")
            for t in self.templates
        ]

        self.zoom = 1.5
        self.items = []              # list[ItemDef]
        self.images = []             # list[ImageAsset]
        self.selected_items = []     # list[uid]
        self.selected_image = None   # uid
        self.bind_mode_item = None   # uid (等待点击图片绑定)
        self.place_image = None      # uid (等待点击画布放置)

        # 交互状态
        self._stroke = None          # ctrl 涂抹: {"cells": set, "bad": set}
        self._drag = None            # 拖动: {"kind":..., ...}
        self._rubber = None
        self._photo_refs = []
        self._updating_panel = False
        self._undo = []
        self._redo = []

        self._build_ui()
        self._bind_events()
        self.redraw()
        self.set_status("就绪: Ctrl+左键涂抹新物品轮廓; 左键拖动选择/移动; 右键平移; Ctrl+滚轮缩放")

    # ---------------- UI ----------------
    def _build_ui(self):
        bar = ttk.Frame(self.root)
        bar.pack(side=tk.TOP, fill=tk.X)
        ttk.Button(bar, text="撤销(Ctrl+Z)", command=self.undo).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="重做(Ctrl+Y)", command=self.redo).pack(side=tk.LEFT, padx=2)
        ttk.Separator(bar, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=4)
        ttk.Button(bar, text="导入图片", command=self.import_images).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="旋转(R)/逆转(Shift+R)", command=self.rotate_selected).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="绑定/解绑(X)", command=self.toggle_bind).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="图片适配物品", command=self.fit_image_to_item).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="预览图标", command=self.preview_icon).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="复制选中", command=self.duplicate_selected).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="删除选中(Del)", command=self.delete_selected).pack(side=tk.LEFT, padx=2)
        ttk.Separator(bar, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=6)
        ttk.Button(bar, text="保存工程", command=self.save_project).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="打开工程", command=self.load_project).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="导出 JSON+图标", command=self.export_json).pack(side=tk.LEFT, padx=2)
        ttk.Button(bar, text="清空全部", command=self.clear_all).pack(side=tk.LEFT, padx=2)

        main = ttk.PanedWindow(self.root, orient=tk.HORIZONTAL)
        main.pack(fill=tk.BOTH, expand=True)

        # 左: 物品列表
        left = ttk.Frame(main, width=180)
        ttk.Label(left, text="物品列表").pack(anchor=tk.W)
        self.item_list = tk.Listbox(left, exportselection=False)
        self.item_list.pack(fill=tk.BOTH, expand=True)
        self.item_list.bind("<<ListboxSelect>>", self.on_list_select)
        main.add(left, weight=0)

        # 中: 画布
        center = ttk.Frame(main)
        self.canvas = tk.Canvas(center, bg="#2b2b2b", highlightthickness=0)
        self.canvas.pack(fill=tk.BOTH, expand=True)
        main.add(center, weight=1)

        # 右: 属性面板
        right = ttk.Frame(main, width=320)
        self._build_panel(right)
        main.add(right, weight=0)

        # 下: 图片条 + 状态栏
        bottom = ttk.Frame(self.root)
        bottom.pack(side=tk.BOTTOM, fill=tk.X)
        ttk.Label(bottom, text="已导入图片(点击后放到画布 / Delete 删除选中图片):").pack(anchor=tk.W)
        strip_frame = ttk.Frame(bottom)
        strip_frame.pack(fill=tk.X)
        self.strip = tk.Canvas(strip_frame, height=64, bg="#3a3a3a", highlightthickness=0)
        self.strip_h = ttk.Scrollbar(strip_frame, orient=tk.HORIZONTAL, command=self.strip.xview)
        self.strip.configure(xscrollcommand=self.strip_h.set)
        self.strip.pack(side=tk.TOP, fill=tk.X)
        self.strip_h.pack(side=tk.BOTTOM, fill=tk.X)
        self.status = ttk.Label(bottom, text="", anchor=tk.W, foreground="#006600")
        self.status.pack(fill=tk.X)

        if HAS_DND:
            try:
                self.root.drop_target_register(DND_FILES)
                self.root.dnd_bind("<<Drop>>", self.on_drop_files)
            except Exception:
                pass

    def _build_panel(self, parent):
        self.panel = ttk.Frame(parent)
        self.panel.pack(fill=tk.BOTH, expand=True, padx=4, pady=4)
        row = 0
        ttk.Label(self.panel, text="物品属性", font=("", 11, "bold")).grid(row=row, column=0, columnspan=2, sticky=tk.W); row += 1

        ttk.Label(self.panel, text="ID").grid(row=row, column=0, sticky=tk.W)
        self.var_id = tk.StringVar()
        e = ttk.Entry(self.panel, textvariable=self.var_id, width=28)
        e.grid(row=row, column=1, sticky=tk.EW); e.bind("<FocusOut>", lambda e: self.apply_panel()); e.bind("<Return>", lambda e: self.apply_panel()); row += 1

        ttk.Label(self.panel, text="模板").grid(row=row, column=0, sticky=tk.W)
        self.var_template = tk.StringVar()
        self.tpl_combo = ttk.Combobox(self.panel, textvariable=self.var_template,
                                      values=self._template_displays, width=26)
        self.tpl_combo.grid(row=row, column=1, sticky=tk.EW)
        self.tpl_combo.bind("<FocusOut>", lambda e: self.apply_panel())
        self.tpl_combo.bind("<Return>", lambda e: self.apply_panel())
        self.tpl_combo.bind("<<ComboboxSelected>>", lambda e: self._on_template_pick())
        self.tpl_combo.bind("<KeyRelease>", lambda e: self._filter_templates())
        row += 1

        ttk.Label(self.panel, text="名称").grid(row=row, column=0, sticky=tk.W)
        self.var_name = tk.StringVar()
        e = ttk.Entry(self.panel, textvariable=self.var_name, width=28)
        e.grid(row=row, column=1, sticky=tk.EW); e.bind("<FocusOut>", lambda e: self.apply_panel()); e.bind("<Return>", lambda e: self.apply_panel()); row += 1

        ttk.Label(self.panel, text="描述").grid(row=row, column=0, sticky=tk.NW)
        self.txt_desc = tk.Text(self.panel, width=28, height=3)
        self.txt_desc.grid(row=row, column=1, sticky=tk.EW); self.txt_desc.bind("<FocusOut>", lambda e: self.apply_panel()); row += 1

        ttk.Label(self.panel, text="风味文本").grid(row=row, column=0, sticky=tk.NW)
        self.txt_flavor = tk.Text(self.panel, width=28, height=2)
        self.txt_flavor.grid(row=row, column=1, sticky=tk.EW); self.txt_flavor.bind("<FocusOut>", lambda e: self.apply_panel()); row += 1

        ttk.Label(self.panel, text="标签(多选)").grid(row=row, column=0, sticky=tk.NW)
        self.tag_list = tk.Listbox(self.panel, selectmode=tk.MULTIPLE, height=10, exportselection=False)
        for t in GAME_TAGS:
            self.tag_list.insert(tk.END, tag_display(t))
        sb = ttk.Scrollbar(self.panel, command=self.tag_list.yview)
        self.tag_list.configure(yscrollcommand=sb.set)
        self.tag_list.grid(row=row, column=1, sticky=tk.EW)
        self.tag_list.bind("<<ListboxSelect>>", lambda e: self.apply_panel())
        row += 1

        ttk.Label(self.panel, text="违禁品等级").grid(row=row, column=0, sticky=tk.W)
        self.var_contra = tk.StringVar(value="无")
        cb = ttk.Combobox(self.panel, textvariable=self.var_contra, values=[n for n, _ in CONTRABAND_LEVELS], state="readonly", width=10)
        cb.grid(row=row, column=1, sticky=tk.W); cb.bind("<<ComboboxSelected>>", lambda e: self.apply_panel()); row += 1

        ttk.Label(self.panel, text="价值").grid(row=row, column=0, sticky=tk.W)
        self.var_value = tk.IntVar(value=10)
        sp = ttk.Spinbox(self.panel, from_=0, to=999999, textvariable=self.var_value, width=10, command=self.apply_panel)
        sp.grid(row=row, column=1, sticky=tk.W); sp.bind("<FocusOut>", lambda e: self.apply_panel()); row += 1

        ttk.Label(self.panel, text="占格").grid(row=row, column=0, sticky=tk.W)
        self.lbl_size = ttk.Label(self.panel, text="-")
        self.lbl_size.grid(row=row, column=1, sticky=tk.W); row += 1

        ttk.Label(self.panel, text="颜色").grid(row=row, column=0, sticky=tk.W)
        cf = ttk.Frame(self.panel)
        self.color_box = tk.Label(cf, width=4, bg="#cccccc", relief=tk.SUNKEN)
        self.color_box.pack(side=tk.LEFT)
        ttk.Button(cf, text="更换", command=self.pick_color).pack(side=tk.LEFT, padx=4)
        cf.grid(row=row, column=1, sticky=tk.W); row += 1

        self.lbl_bind = ttk.Label(self.panel, text="图标: 未绑定")
        self.lbl_bind.grid(row=row, column=0, columnspan=2, sticky=tk.W); row += 1
        self.panel.columnconfigure(1, weight=1)
        self.show_panel_item(None)

    # ---------------- 事件 ----------------
    def _bind_events(self):
        c = self.canvas
        c.bind("<Button-1>", self.on_left_down)
        c.bind("<B1-Motion>", self.on_left_move)
        c.bind("<ButtonRelease-1>", self.on_left_up)
        c.bind("<Button-3>", lambda e: c.scan_mark(e.x, e.y))
        c.bind("<B3-Motion>", lambda e: c.scan_dragto(e.x, e.y, gain=1))
        c.bind("<MouseWheel>", self.on_wheel)
        c.bind("<Control-MouseWheel>", self.on_ctrl_wheel)
        self.root.bind("<KeyRelease-Control_L>", self.on_ctrl_release)
        self.root.bind("<KeyRelease-Control_R>", self.on_ctrl_release)
        self.root.bind("<KeyPress-r>", lambda e: self.rotate_selected(ccw=False))
        self.root.bind("<KeyPress-R>", lambda e: self.rotate_selected(ccw=True))
        self.root.bind("<Control-z>", lambda e: self.undo())
        self.root.bind("<Control-Z>", lambda e: self.undo())
        self.root.bind("<Control-y>", lambda e: self.redo())
        self.root.bind("<Control-Y>", lambda e: self.redo())
        self.root.bind("<KeyPress-x>", lambda e: self.toggle_bind())
        self.root.bind("<KeyPress-X>", lambda e: self.toggle_bind())
        self.root.bind("<Delete>", lambda e: self.delete_selected())
        self.root.bind("<KeyPress-q>", lambda e: self.nudge_rot(-5, e))
        self.root.bind("<KeyPress-e>", lambda e: self.nudge_rot(5, e))
        self.root.bind("<KeyPress-Q>", lambda e: self.nudge_rot(-90, e))
        self.root.bind("<KeyPress-E>", lambda e: self.nudge_rot(90, e))

    # ---------------- 坐标 ----------------
    def cell_px(self):
        return CELL * self.zoom

    def to_cell(self, ex, ey):
        cx = self.canvas.canvasx(ex)
        cy = self.canvas.canvasy(ey)
        return int(cx // self.cell_px()), int(cy // self.cell_px())

    def to_z1(self, ex, ey):
        """画布事件坐标 -> zoom-1 像素坐标(1格=16)"""
        return self.canvas.canvasx(ex) / self.zoom, self.canvas.canvasy(ey) / self.zoom

    # ---------------- 左键 ----------------
    def on_left_down(self, e):
        ctrl = bool(e.state & 0x0004)
        cell = self.to_cell(e.x, e.y)

        if ctrl:
            self.start_stroke(cell)
            return

        # 绑定模式: 点击图片完成绑定
        if self.bind_mode_item:
            img = self.hit_image(e.x, e.y)
            if img:
                self.do_bind(self.bind_mode_item, img)
            else:
                self.set_status("绑定模式: 请点击一张图片(或再按 X 取消)")
            return

        # 放置图片模式
        if self.place_image:
            img = self.find_image(self.place_image)
            if img:
                x, y = self.to_z1(e.x, e.y)
                img.cx, img.cy = x, y
                self.selected_image = img.uid
                self.selected_items = []
                self.place_image = None
                self.set_status(f"图片已放置, 拖动调整位置/滚轮缩放/Q,E旋转; 选中物品后按 X 绑定")
                self.redraw()
            return

        # 命中物品?
        item = self.hit_item(cell)
        if item:
            self.selected_items = [item.uid]
            self.selected_image = None
            self.refresh_list_selection()
            self.show_panel_item(item)
            self._drag = {"kind": "item", "uid": item.uid, "start_cell": cell,
                          "orig_cells": set(item.cells), "delta": (0, 0), "valid": True}
            self.redraw()
            return

        # 命中图片?
        img = self.hit_image(e.x, e.y)
        if img:
            self.selected_image = img.uid
            self.selected_items = []
            self.show_panel_item(None)
            x, y = self.to_z1(e.x, e.y)
            self._drag = {"kind": "image", "uid": img.uid, "off": (img.cx - x, img.cy - y)}
            self.refresh_list_selection()
            self.redraw()
            return

        # 空白: 框选
        self.selected_items = []
        self.selected_image = None
        self.show_panel_item(None)
        self.refresh_list_selection()
        self._rubber = {"x0": e.x, "y0": e.y, "x1": e.x, "y1": e.y}
        self.redraw()

    def on_left_move(self, e):
        if self._stroke is not None:
            self.extend_stroke(self.to_cell(e.x, e.y))
            return
        if self._drag:
            if self._drag["kind"] == "item":
                item = self.find_item(self._drag["uid"])
                if not item:
                    self._drag = None
                    return
                cell = self.to_cell(e.x, e.y)
                dx = cell[0] - self._drag["start_cell"][0]
                dy = cell[1] - self._drag["start_cell"][1]
                if (dx, dy) != self._drag["delta"]:
                    self._drag["delta"] = (dx, dy)
                    new_cells = {(x + dx, y + dy) for x, y in self._drag["orig_cells"]}
                    self._drag["valid"] = self.cells_free(new_cells, ignore_uid=item.uid) and \
                        all(0 <= x < GRID_W and 0 <= y < GRID_H for x, y in new_cells)
                    self._drag["preview"] = new_cells
                    self.redraw()
            elif self._drag["kind"] == "image":
                img = self.find_image(self._drag["uid"])
                if img and img.bound_to is None:
                    x, y = self.to_z1(e.x, e.y)
                    img.cx = x + self._drag["off"][0]
                    img.cy = y + self._drag["off"][1]
                    self.redraw()
            return
        if self._rubber is not None:
            self._rubber["x1"], self._rubber["y1"] = e.x, e.y
            self.redraw()

    def on_left_up(self, e):
        if self._drag and self._drag["kind"] == "item":
            item = self.find_item(self._drag["uid"])
            if item and "preview" in self._drag:
                if self._drag["valid"]:
                    if self._drag["delta"] != (0, 0):
                        self.push_undo()
                    item.cells = self._drag["preview"]
                    # 绑定图片用绝对位置同步(物品包围盒+偏移推导), 永不漂移
                    if item.icon:
                        self.sync_bound_image(item)
                    self.set_status(f"移动 {item.item_id} -> {item.bbox()[:2]}")
                else:
                    self.set_status("移动被取消: 与其他物品重叠或越界")
            self._drag = None
            self.show_panel_item(item)
            self.redraw()
            return
        self._drag = None
        if self._rubber is not None:
            r = self._rubber
            self._rubber = None
            x0, y0 = self.to_z1(min(r["x0"], r["x1"]), min(r["y0"], r["y1"]))
            x1, y1 = self.to_z1(max(r["x0"], r["x1"]), max(r["y0"], r["y1"]))
            c0 = (int(x0 // CELL), int(y0 // CELL))
            c1 = (int(x1 // CELL), int(y1 // CELL))
            self.selected_items = [it.uid for it in self.items
                                   if any(c0[0] <= x <= c1[0] and c0[1] <= y <= c1[1] for x, y in it.cells)]
            self.refresh_list_selection()
            if len(self.selected_items) == 1:
                self.show_panel_item(self.find_item(self.selected_items[0]))
            elif len(self.selected_items) > 1:
                self.show_panel_multi(len(self.selected_items))
            self.redraw()

    # ---------------- Ctrl 涂抹 ----------------
    def start_stroke(self, cell):
        if not (0 <= cell[0] < GRID_W and 0 <= cell[1] < GRID_H):
            self._stroke = None
            return
        self._stroke = {"cells": set(), "bad": set()}
        self.extend_stroke(cell)

    def extend_stroke(self, cell):
        s = self._stroke
        if s is None:
            return
        x, y = cell
        if not (0 <= x < GRID_W and 0 <= y < GRID_H):
            return
        if cell in s["cells"]:
            return
        if not self.cell_free(cell):
            s["bad"].add(cell)
            self.redraw()
            return
        # 相邻规则: 第一格任意, 之后必须与已选格相邻
        if s["cells"]:
            adj = any(abs(x - cx) + abs(y - cy) == 1 for cx, cy in s["cells"])
            if not adj:
                s["bad"].add(cell)
                self.redraw()
                return
        s["cells"].add(cell)
        self.redraw()

    def on_ctrl_release(self, e):
        if self._stroke is None:
            return
        s = self._stroke
        self._stroke = None
        if s["cells"]:
            self.push_undo()
            item = self.create_item(sorted(s["cells"]))
            self.selected_items = [item.uid]
            self.selected_image = None
            self.refresh_list_selection()
            self.show_panel_item(item)
            self.set_status(f"新物品 {item.item_id}: {len(item.cells)} 格, 请在右侧编辑属性")
        self.redraw()

    # ---------------- 查询 ----------------
    def find_item(self, uid):
        return next((i for i in self.items if i.uid == uid), None)

    def find_image(self, uid):
        return next((i for i in self.images if i.uid == uid), None)

    def cell_free(self, cell):
        return not any(cell in it.cells for it in self.items)

    def cells_free(self, cells, ignore_uid=None):
        occ = set()
        for it in self.items:
            if it.uid != ignore_uid:
                occ |= it.cells
        return not (cells & occ)

    def hit_item(self, cell):
        for it in reversed(self.items):
            if cell in it.cells:
                return it
        return None

    def hit_image(self, ex, ey):
        x, y = self.to_z1(ex, ey)
        for img in reversed(self.images):
            dx, dy = x - img.cx, y - img.cy
            rad = math.radians(img.rot)
            rx = dx * math.cos(rad) + dy * math.sin(rad)
            ry = -dx * math.sin(rad) + dy * math.cos(rad)
            if abs(rx) <= img.w_px / 2 and abs(ry) <= img.h_px / 2:
                return img
        return None

    # ---------------- 物品操作 ----------------
    def create_item(self, cells):
        color = PALETTE[len(self.items) % len(PALETTE)]
        item = ItemDef(cells, color)
        self.items.append(item)
        self.refresh_item_list()
        return item

    def rotate_selected(self, ccw=False):
        if len(self.selected_items) != 1:
            self.set_status("请选中一个物品再旋转")
            return
        item = self.find_item(self.selected_items[0])
        if not item:
            return
        # 拖动进行中先取消(还原到拖动前), 防止拖动释放时覆盖旋转结果
        if self._drag:
            dragged = self.find_item(self._drag.get("uid"))
            if dragged is not None:
                dragged.cells = self._drag["orig_cells"]
            self._drag = None
        times = 3 if ccw else 1  # 逆时针 = 顺时针 x3
        cells = set(item.cells)
        x0, y0, x1, y1 = item.bbox()
        w = x1 - x0 + 1
        h = y1 - y0 + 1
        for _ in range(times):
            # 顺时针 90°: (x,y)->(h-1-y,x), 包围盒宽高互换
            cells = {(x0 + (h - 1 - (y - y0)), y0 + (x - x0)) for x, y in cells}
            w, h = h, w
        if not (self.cells_free(cells, ignore_uid=item.uid) and
                all(0 <= x < GRID_W and 0 <= y < GRID_H for x, y in cells)):
            self.set_status("旋转被取消: 会与其他物品重叠或越界")
            return
        self.push_undo()
        item.cells = cells
        # 绑定图片联动: 只旋转偏移与角度(图片宽高不变, 旋转 90° 自带方向)
        if item.icon:
            img = self.find_image(item.icon.image_uid)
            if img:
                for _ in range(times):
                    item.icon.dx, item.icon.dy = -item.icon.dy, item.icon.dx
                item.icon.rot = (item.icon.rot + 90 * times) % 360
                self.sync_bound_image(item)
        self.show_panel_item(item)
        w2, h2 = item.grid_size()
        self.set_status(f"{item.item_id} {'逆时针' if ccw else '顺时针'}旋转后占格 {w2}x{h2}")
        self.redraw()

    def duplicate_selected(self):
        if not self.selected_items:
            return
        self.push_undo()
        new_sel = []
        for uid in list(self.selected_items):
            src = self.find_item(uid)
            if not src:
                continue
            x0, y0, _, _ = src.bbox()
            w, h = src.grid_size()
            placed = None
            for oy in range(0, GRID_H):
                cand = {(x + 0, y + oy) for x, y in src.cells}
                if self.cells_free(cand) and all(0 <= x < GRID_W and 0 <= y < GRID_H for x, y in cand):
                    placed = cand
                    break
            if not placed:
                continue
            item = ItemDef(placed, PALETTE[len(self.items) % len(PALETTE)])
            item.item_id = src.item_id + "_copy"
            item.template = src.template
            item.name = src.name
            item.desc = src.desc
            item.flavor = src.flavor
            item.tags = set(src.tags)
            item.contraband = src.contraband
            item.value = src.value
            if src.icon:
                img = self.find_image(src.icon.image_uid)
                if img:
                    dup_img = ImageAsset(img.path, img.pil.copy())
                    dup_img.w_px, dup_img.h_px, dup_img.rot = src.icon.w_px, src.icon.h_px, src.icon.rot
                    self.images.append(dup_img)
                    item.icon = IconBinding(dup_img.uid, src.icon.dx, src.icon.dy,
                                            src.icon.w_px, src.icon.h_px, src.icon.rot)
                    dup_img.bound_to = item.uid
                    self.sync_bound_image(item)
            self.items.append(item)
            new_sel.append(item.uid)
        self.selected_items = new_sel
        self.refresh_item_list()
        self.refresh_list_selection()
        self.refresh_strip()
        self.redraw()
        self.set_status(f"已复制 {len(new_sel)} 个物品")

    def delete_selected(self):
        if not self.selected_image and not self.selected_items:
            return
        self.push_undo()
        if self.selected_image:
            img = self.find_image(self.selected_image)
            if img:
                for it in self.items:
                    if it.icon and it.icon.image_uid == img.uid:
                        it.icon = None
                self.images.remove(img)
            self.selected_image = None
        if self.selected_items:
            for uid in list(self.selected_items):
                it = self.find_item(uid)
                if it:
                    if it.icon:
                        img = self.find_image(it.icon.image_uid)
                        if img:
                            img.bound_to = None
                    self.items.remove(it)
            self.selected_items = []
        self.show_panel_item(None)
        self.refresh_item_list()
        self.refresh_strip()
        self.redraw()
        self.set_status("已删除选中")

    def clear_all(self):
        if not messagebox.askyesno("确认", "清空全部物品和图片?"):
            return
        self.push_undo()
        self.items = []
        self.images = []
        self.selected_items = []
        self.selected_image = None
        self.show_panel_item(None)
        self.refresh_item_list()
        self.refresh_strip()
        self.redraw()

    def pick_color(self):
        if len(self.selected_items) != 1:
            return
        item = self.find_item(self.selected_items[0])
        if not item:
            return
        c = colorchooser.askcolor(item.color)
        if c and c[1]:
            item.color = c[1]
            self.color_box.configure(bg=item.color)
            self.redraw()

    # ---------------- 绑定 ----------------
    def toggle_bind(self):
        if self.bind_mode_item:
            self.bind_mode_item = None
            self.set_status("已退出绑定模式")
            return
        if len(self.selected_items) != 1:
            self.set_status("请先选中一个物品, 再按 X 进入绑定模式")
            return
        item = self.find_item(self.selected_items[0])
        if not item:
            return
        if item.icon:
            # 解绑
            self.push_undo()
            img = self.find_image(item.icon.image_uid)
            if img:
                img.bound_to = None
                # 解绑后图片留在当前位置, 保持尺寸与旋转
                img.w_px, img.h_px, img.rot = item.icon.w_px, item.icon.h_px, item.icon.rot
                self.sync_bound_image(item, unbind=True)
            item.icon = None
            self.show_panel_item(item)
            self.redraw()
            self.set_status("已解绑, 可自由调整图片; 再按 X 重新进入绑定模式")
            return
        self.bind_mode_item = item.uid
        self.set_status("绑定模式: 点击一张图片完成绑定 (再按 X 取消)")

    def do_bind(self, item_uid, img):
        item = self.find_item(item_uid)
        if not item:
            self.bind_mode_item = None
            return
        self.push_undo()
        x0, y0, x1, y1 = item.bbox()
        w, h = item.grid_size()
        bcx = (x0 + w / 2.0) * CELL
        bcy = (y0 + h / 2.0) * CELL
        dx = (img.cx - bcx) / CELL
        dy = (img.cy - bcy) / CELL
        item.icon = IconBinding(img.uid, dx, dy, img.w_px, img.h_px, img.rot)
        img.bound_to = item.uid
        self.bind_mode_item = None
        self.show_panel_item(item)
        self.redraw()
        self.set_status(f"已绑定: {item.item_id} <-> 图片 (偏移 {dx:.2f},{dy:.2f} 格)")

    def sync_bound_image(self, item, unbind=False):
        """按绑定信息把图片摆到正确位置"""
        if not item.icon:
            return
        img = self.find_image(item.icon.image_uid)
        if not img:
            return
        x0, y0, _, _ = item.bbox()
        w, h = item.grid_size()
        bcx = (x0 + w / 2.0) * CELL
        bcy = (y0 + h / 2.0) * CELL
        img.cx = bcx + item.icon.dx * CELL
        img.cy = bcy + item.icon.dy * CELL
        if not unbind:
            img.w_px, img.h_px, img.rot = item.icon.w_px, item.icon.h_px, item.icon.rot

    # ---------------- 撤销/重做 ----------------
    def _make_snapshot(self):
        items = []
        for it in self.items:
            items.append({
                "uid": it.uid, "item_id": it.item_id, "template": it.template,
                "name": it.name, "desc": it.desc,
                "flavor": it.flavor, "tags": set(it.tags), "contraband": it.contraband,
                "value": it.value, "color": it.color, "cells": set(it.cells),
                "icon": None if not it.icon else IconBinding(
                    it.icon.image_uid, it.icon.dx, it.icon.dy,
                    it.icon.w_px, it.icon.h_px, it.icon.rot),
            })
        images = []
        for im in self.images:
            images.append({"uid": im.uid, "path": im.path, "pil": im.pil,
                           "cx": im.cx, "cy": im.cy, "w_px": im.w_px, "h_px": im.h_px,
                           "rot": im.rot, "bound_to": im.bound_to})
        return {"items": items, "images": images,
                "selected_items": list(self.selected_items),
                "selected_image": self.selected_image}

    def _restore_snapshot(self, snap):
        self.items = []
        for d in snap["items"]:
            it = ItemDef(d["cells"], d["color"])
            it.uid = d["uid"]
            it.item_id = d["item_id"]
            it.template = d.get("template", "")
            it.name, it.desc, it.flavor = d["name"], d["desc"], d["flavor"]
            it.tags = set(d["tags"])
            it.contraband = d["contraband"]
            it.value = d["value"]
            it.icon = d["icon"]
            self.items.append(it)
        self.images = []
        for d in snap["images"]:
            im = ImageAsset(d["path"], d["pil"])
            im.uid = d["uid"]
            im.cx, im.cy = d["cx"], d["cy"]
            im.w_px, im.h_px, im.rot, im.bound_to = d["w_px"], d["h_px"], d["rot"], d["bound_to"]
            self.images.append(im)
        self.selected_items = list(snap["selected_items"])
        self.selected_image = snap["selected_image"]
        self.bind_mode_item = None
        self._drag = None
        self._stroke = None
        self._rubber = None
        self.refresh_item_list()
        self.refresh_list_selection()
        self.refresh_strip()
        if len(self.selected_items) == 1:
            self.show_panel_item(self.find_item(self.selected_items[0]))
        else:
            self.show_panel_item(None)
        self.redraw()

    def push_undo(self):
        self._undo.append(self._make_snapshot())
        if len(self._undo) > 30:
            self._undo.pop(0)
        self._redo.clear()

    def undo(self):
        if not self._undo:
            self.set_status("没有可撤销的操作")
            return
        self._redo.append(self._make_snapshot())
        self._restore_snapshot(self._undo.pop())
        self.set_status(f"已撤销 (剩余 {len(self._undo)} 步)")

    def redo(self):
        if not self._redo:
            self.set_status("没有可重做的操作")
            return
        self._undo.append(self._make_snapshot())
        self._restore_snapshot(self._redo.pop())
        self.set_status("已重做")

    # ---------------- 便捷功能 ----------------
    def fit_image_to_item(self):
        """把选中图片缩放并居中到选中物品的占格(绑定前的快速耦合)"""
        if not self.selected_image:
            self.set_status("请先选中一张图片")
            return
        img = self.find_image(self.selected_image)
        if not img or img.bound_to is not None:
            self.set_status("图片已绑定, 先按 X 解绑再调整")
            return
        if len(self.selected_items) != 1:
            self.set_status("请再选中一个物品(作为目标占格)")
            return
        item = self.find_item(self.selected_items[0])
        if not item:
            return
        self.push_undo()
        x0, y0, _, _ = item.bbox()
        w, h = item.grid_size()
        img.w_px, img.h_px = float(w * CELL), float(h * CELL)
        img.cx, img.cy = (x0 + w / 2.0) * CELL, (y0 + h / 2.0) * CELL
        img.rot = 0.0
        self.redraw()
        self.set_status(f"图片已适配 {item.item_id} 占格 ({w}x{h}), 按 X 绑定")

    def preview_icon(self):
        """预览导出图标效果"""
        if len(self.selected_items) != 1:
            self.set_status("请先选中一个物品")
            return
        item = self.find_item(self.selected_items[0])
        if not item or not item.icon:
            self.set_status("该物品未绑定图片, 无法预览图标")
            return
        img = self.find_image(item.icon.image_uid)
        if not img:
            return
        import tempfile
        tmp = os.path.join(tempfile.gettempdir(), "icon_preview.png")
        self.render_icon(item, img, tmp)
        top = tk.Toplevel(self.root)
        top.title(f"图标预览: {item.item_id}")
        w, h = item.grid_size()
        scale = max(1, min(8, 256 // max(w * CELL, h * CELL, 1)))
        pil = Image.open(tmp).resize((w * CELL * scale, h * CELL * scale), Image.NEAREST)
        photo = ImageTk.PhotoImage(pil)
        lbl = tk.Label(top, image=photo, bg="#2b2b2b")
        lbl.image = photo
        lbl.pack(padx=8, pady=8)
        ttk.Label(top, text=f"导出尺寸: {w * CELL}x{h * CELL}px (放大 {scale}x 显示)").pack(pady=4)

    # ---------------- 图片 ----------------
    def import_images(self):
        paths = filedialog.askopenfilenames(
            title="选择图片",
            filetypes=[("图片", "*.png *.jpg *.jpeg *.gif *.bmp *.webp")])
        for p in paths:
            self.add_image(p)

    def on_drop_files(self, e):
        for p in self.root.tk.splitlist(e.data):
            p = p.strip("{}")
            if os.path.isfile(p) and p.lower().endswith((".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp")):
                self.add_image(p)

    def add_image(self, path):
        try:
            pil = Image.open(path).convert("RGBA")
        except Exception as ex:
            messagebox.showerror("导入失败", f"{path}\n{ex}")
            return
        self.push_undo()
        img = ImageAsset(path, pil)
        # 默认缩到合理大小(不超过 6 格宽)
        if img.w_px > 6 * CELL:
            ratio = 6 * CELL / img.w_px
            img.w_px *= ratio
            img.h_px *= ratio
        img.cx = (GRID_W * CELL) / 2
        img.cy = (GRID_H * CELL) / 2
        self.images.append(img)
        self.place_image = img.uid
        self.refresh_strip()
        self.set_status(f"已导入 {os.path.basename(path)}, 点击画布放置")

    def nudge_rot(self, deg, event):
        if self.selected_image:
            img = self.find_image(self.selected_image)
            if img and img.bound_to is None:
                img.rot = (img.rot + deg) % 360
                self.redraw()
                self.set_status(f"图片旋转 {img.rot:.0f}°")

    def on_wheel(self, e):
        if self.selected_image:
            img = self.find_image(self.selected_image)
            if img and img.bound_to is None:
                factor = 1.1 if e.delta > 0 else 1 / 1.1
                shift = bool(e.state & 0x0001)
                alt = bool(e.state & 0x20000)
                if shift:
                    img.w_px = max(2, img.w_px * factor)
                elif alt:
                    img.h_px = max(2, img.h_px * factor)
                else:
                    img.w_px = max(2, img.w_px * factor)
                    img.h_px = max(2, img.h_px * factor)
                self.redraw()
                return
        self.on_ctrl_wheel(e)

    def on_ctrl_wheel(self, e):
        old = self.zoom
        self.zoom = max(0.4, min(5.0, self.zoom * (1.1 if e.delta > 0 else 1 / 1.1)))
        if abs(self.zoom - old) > 1e-6:
            self.redraw()

    # ---------------- 面板 ----------------
    def show_panel_item(self, item):
        self._updating_panel = True
        try:
            if item is None:
                for v in (self.var_id, self.var_name, self.var_template):
                    v.set("")
                self.tpl_combo.configure(values=self._template_displays)
                self.txt_desc.delete("1.0", tk.END)
                self.txt_flavor.delete("1.0", tk.END)
                self.tag_list.selection_clear(0, tk.END)
                self.var_contra.set("无")
                self.var_value.set(10)
                self.lbl_size.configure(text="-")
                self.color_box.configure(bg="#cccccc")
                self.lbl_bind.configure(text="图标: 未绑定")
                return
            self.var_id.set(item.item_id)
            self.var_template.set(self._template_to_display(item.template))
            self.tpl_combo.configure(values=self._template_displays)
            self.var_name.set(item.name)
            self.txt_desc.delete("1.0", tk.END)
            self.txt_desc.insert("1.0", item.desc)
            self.txt_flavor.delete("1.0", tk.END)
            self.txt_flavor.insert("1.0", item.flavor)
            self.tag_list.selection_clear(0, tk.END)
            for i, t in enumerate(GAME_TAGS):
                if t in item.tags:
                    self.tag_list.selection_set(i)
            name = next((n for n, v in CONTRABAND_LEVELS if v == item.contraband), "无")
            self.var_contra.set(name)
            self.var_value.set(item.value)
            w, h = item.grid_size()
            self.lbl_size.configure(text=f"{w} x {h}  (导出图标 {w * CELL}x{h * CELL}px)")
            self.color_box.configure(bg=item.color)
            if item.icon:
                self.lbl_bind.configure(text=f"图标: 已绑定 ({item.icon.image_uid})")
            else:
                self.lbl_bind.configure(text="图标: 未绑定 (选中后按 X 绑定)")
        finally:
            self._updating_panel = False

    def show_panel_multi(self, n):
        self.show_panel_item(None)
        self.lbl_bind.configure(text=f"已多选 {n} 个物品(多选时不编辑属性)")

    # ---------------- 模板下拉 ----------------
    def _template_to_display(self, tpl_id):
        if not tpl_id:
            return ""
        name = self._template_names.get(tpl_id, "")
        return tpl_id + ("  " + name if name and name != tpl_id else "")

    @staticmethod
    def _template_from_display(text):
        text = (text or "").strip()
        return text.split()[0] if text else ""

    def _on_template_pick(self):
        self.tpl_combo.configure(values=self._template_displays)
        self.apply_panel()

    def _filter_templates(self):
        text = self.var_template.get().strip().lower()
        values = self._template_displays if not text else \
            [d for d in self._template_displays if text in d.lower()]
        self.tpl_combo.configure(values=values)

    def apply_panel(self):
        if self._updating_panel or len(self.selected_items) != 1:
            return
        item = self.find_item(self.selected_items[0])
        if not item:
            return
        new_id = self.var_id.get().strip()
        if new_id:
            item.item_id = new_id.replace(" ", "_")
        item.template = self._template_from_display(self.var_template.get())
        item.name = self.var_name.get()
        item.desc = self.txt_desc.get("1.0", tk.END).strip()
        item.flavor = self.txt_flavor.get("1.0", tk.END).strip()
        item.tags = {GAME_TAGS[i] for i in self.tag_list.curselection()}
        item.contraband = next((v for n, v in CONTRABAND_LEVELS if n == self.var_contra.get()), "none")
        try:
            item.value = int(self.var_value.get())
        except Exception:
            pass
        self.refresh_item_list()
        self.redraw()

    # ---------------- 列表/图片条 ----------------
    def refresh_item_list(self):
        self.item_list.delete(0, tk.END)
        for it in self.items:
            w, h = it.grid_size()
            self.item_list.insert(tk.END, f"{it.item_id} ({w}x{h})")

    def refresh_list_selection(self):
        self.item_list.selection_clear(0, tk.END)
        for i, it in enumerate(self.items):
            if it.uid in self.selected_items:
                self.item_list.selection_set(i)

    def on_list_select(self, e):
        sel = [self.items[i].uid for i in self.item_list.curselection() if i < len(self.items)]
        if sel:
            self.selected_items = sel
            self.selected_image = None
            if len(sel) == 1:
                self.show_panel_item(self.find_item(sel[0]))
            else:
                self.show_panel_multi(len(sel))
            self.redraw()

    def refresh_strip(self):
        self.strip.delete("all")
        self._strip_photos = []
        x = 4
        for img in self.images:
            thumb = img.pil.copy()
            thumb.thumbnail((56, 56))
            photo = ImageTk.PhotoImage(thumb)
            self._strip_photos.append(photo)
            self.strip.create_image(x + 28, 32, image=photo, tags=(f"thumb_{img.uid}",))
            self.strip.tag_bind(f"thumb_{img.uid}", "<Button-1>",
                                lambda e, u=img.uid: self.on_thumb_click(u))
            x += 60
        self.strip.configure(scrollregion=(0, 0, x + 4, 64))

    def on_thumb_click(self, uid):
        img = self.find_image(uid)
        if not img:
            return
        self.place_image = uid
        self.selected_image = uid
        self.selected_items = []
        self.set_status("点击画布放置该图片 (拖到想要的位置松开也可以: 点击图片即选中)")
        self.redraw()

    # ---------------- 渲染 ----------------
    def redraw(self):
        c = self.canvas
        c.delete("all")
        self._photo_refs = []
        cpx = self.cell_px()
        W, H = GRID_W * cpx, GRID_H * cpx
        c.configure(scrollregion=(0, 0, W, H))

        # 网格
        for i in range(GRID_W + 1):
            c.create_line(i * cpx, 0, i * cpx, H, fill="#444444")
        for j in range(GRID_H + 1):
            c.create_line(0, j * cpx, W, j * cpx, fill="#444444")

        # 图片(先画未绑定的, 绑定的一会随物品画)
        for img in self.images:
            if img.bound_to is None:
                self.draw_image(img)

        # 物品
        for it in self.items:
            sel = it.uid in self.selected_items
            for x, y in it.cells:
                c.create_rectangle(x * cpx, y * cpx, (x + 1) * cpx, (y + 1) * cpx,
                                   fill=it.color, stipple="gray50",
                                   outline="#ffffff" if sel else it.color,
                                   width=2 if sel else 1)
            if it.icon:
                img = self.find_image(it.icon.image_uid)
                if img:
                    self.draw_image(img)
            x0, y0, x1, y1 = it.bbox()
            c.create_text((x0 + (x1 - x0 + 1) / 2) * cpx, (y0 + (y1 - y0 + 1) / 2) * cpx,
                          text=it.item_id, fill="#ffffff", font=("", max(6, int(7 * self.zoom))),
                          tags=("nolower",))

        # 拖动物品预览
        if self._drag and self._drag.get("kind") == "item" and "preview" in self._drag:
            color = "#00ff00" if self._drag["valid"] else "#ff0000"
            for x, y in self._drag["preview"]:
                c.create_rectangle(x * cpx, y * cpx, (x + 1) * cpx, (y + 1) * cpx,
                                   outline=color, width=2, dash=(3, 2))

        # ctrl 涂抹预览
        if self._stroke:
            for x, y in self._stroke["cells"]:
                c.create_rectangle(x * cpx, y * cpx, (x + 1) * cpx, (y + 1) * cpx,
                                   fill="#00cc66", stipple="gray50", outline="#00ff88", width=2)
            for x, y in self._stroke["bad"]:
                c.create_rectangle(x * cpx, y * cpx, (x + 1) * cpx, (y + 1) * cpx,
                                   outline="#ff3333", width=2)

        # 框选
        if self._rubber:
            r = self._rubber
            c.create_rectangle(self.canvas.canvasx(r["x0"]), self.canvas.canvasy(r["y0"]),
                               self.canvas.canvasx(r["x1"]), self.canvas.canvasy(r["y1"]),
                               outline="#ffcc00", dash=(4, 2))

        # 绑定模式高亮选中物品
        if self.bind_mode_item:
            it = self.find_item(self.bind_mode_item)
            if it:
                for x, y in it.cells:
                    c.create_rectangle(x * cpx, y * cpx, (x + 1) * cpx, (y + 1) * cpx,
                                       outline="#00ffff", width=3)

        # 选中图片框
        if self.selected_image:
            img = self.find_image(self.selected_image)
            if img:
                rad = math.radians(img.rot)
                hw, hh = img.w_px / 2 * self.zoom, img.h_px / 2 * self.zoom
                pts = []
                for dx, dy in ((-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh)):
                    rx = dx * math.cos(rad) - dy * math.sin(rad)
                    ry = dx * math.sin(rad) + dy * math.cos(rad)
                    pts.append(img.cx * self.zoom + rx)
                    pts.append(img.cy * self.zoom + ry)
                c.create_polygon(*pts, outline="#00ffff", fill="", width=2)

    def draw_image(self, img):
        w = max(1, int(img.w_px * self.zoom))
        h = max(1, int(img.h_px * self.zoom))
        try:
            pil = img.pil.resize((w, h), Image.NEAREST if w <= 128 else Image.BILINEAR)
            if img.rot:
                pil = pil.rotate(-img.rot, expand=True, resample=Image.BICUBIC)
            photo = ImageTk.PhotoImage(pil)
            self._photo_refs.append(photo)
            self.canvas.create_image(img.cx * self.zoom, img.cy * self.zoom, image=photo)
        except Exception:
            pass

    # ---------------- 工程/导出 ----------------
    def save_project(self):
        path = filedialog.asksaveasfilename(defaultextension=".json",
                                            filetypes=[["工程", "*.json"]],
                                            initialfile="item_project.json")
        if not path:
            return
        data = {
            "editor": "ps-item-editor", "version": 1,
            "items": [self._item_project_dict(it) for it in self.items],
            "images": [img.to_dict() for img in self.images],
        }
        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        self.set_status(f"工程已保存: {path}")

    def _item_project_dict(self, it):
        d = it.to_dict()
        d["color"] = it.color
        d["cells"] = [list(c) for c in sorted(it.cells)]
        return d

    def load_project(self):
        path = filedialog.askopenfilename(filetypes=[["工程", "*.json"]])
        if not path:
            return
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
        except Exception as ex:
            messagebox.showerror("打开失败", str(ex))
            return
        self.items = []
        self.images = []
        self.selected_items = []
        self.selected_image = None
        base = os.path.dirname(path)
        uid_map = {}
        for im in data.get("images", []):
            p = im.get("path", "")
            if p and not os.path.isabs(p):
                p = os.path.join(base, p)
            try:
                pil = Image.open(p).convert("RGBA") if p and os.path.isfile(p) else Image.new("RGBA", (32, 32), (255, 0, 255, 255))
            except Exception:
                pil = Image.new("RGBA", (32, 32), (255, 0, 255, 255))
            img = ImageAsset(p, pil)
            img.cx, img.cy = im.get("cx", 0), im.get("cy", 0)
            img.w_px, img.h_px = im.get("w_px", 32), im.get("h_px", 32)
            img.rot = im.get("rot", 0)
            img.bound_to = im.get("bound_to")
            uid_map[im.get("uid")] = img.uid
            self.images.append(img)
        for itd in data.get("items", []):
            item = ItemDef([tuple(c) for c in itd.get("cells", [])], itd.get("color", PALETTE[0]))
            item.item_id = itd.get("id", item.item_id)
            item.template = itd.get("template", "")
            item.name = itd.get("name", "")
            item.desc = itd.get("description", "")
            item.flavor = itd.get("flavor", "")
            item.tags = set(itd.get("tags", []))
            item.contraband = itd.get("contrabandLevel", "none")
            item.value = itd.get("value", 10)
            ic = itd.get("icon")
            if ic:
                old_uid = ic.get("image_uid") or ic.get("sourceImageUid")
                if old_uid in uid_map:
                    item.icon = IconBinding(uid_map[old_uid], ic.get("dx", ic.get("offsetCells", [0, 0])[0] if isinstance(ic.get("offsetCells"), list) else 0),
                                            ic.get("dy", ic.get("offsetCells", [0, 0])[1] if isinstance(ic.get("offsetCells"), list) else 0),
                                            ic.get("w_px", ic.get("sizePx", [32, 32])[0] if isinstance(ic.get("sizePx"), list) else 32),
                                            ic.get("h_px", ic.get("sizePx", [32, 32])[1] if isinstance(ic.get("sizePx"), list) else 32),
                                            ic.get("rot", ic.get("rotation", 0)))
                    img = self.find_image(item.icon.image_uid)
                    if img:
                        img.bound_to = item.uid
            self.items.append(item)
        for it in self.items:
            if it.icon:
                self.sync_bound_image(it)
        self.refresh_item_list()
        self.refresh_strip()
        self.show_panel_item(None)
        self.redraw()
        self.set_status(f"工程已加载: {path}")

    def export_json(self):
        """导出 PS-API 内容包格式: <目录>/items/<id>.json (一物一文件) + <目录>/icons/<id>.png
        字段规范见 _api_design/items/07; 把两个子目录放进 packs/<包>/ 即被框架加载"""
        if not self.items:
            if not self.smoke:
                messagebox.showinfo("导出", "没有物品可导出")
            return
        # 重复 ID 检查
        ids = [it.item_id for it in self.items]
        dupes = sorted({i for i in ids if ids.count(i) > 1})
        if dupes:
            if self.smoke or not messagebox.askyesno("重复 ID 警告",
                                       f"以下物品 ID 重复, 导出后游戏内会冲突:\n{', '.join(dupes)}\n\n仍要导出吗?"):
                if not self.smoke:
                    return
        out_dir = filedialog.askdirectory(title="选择导出目录 (将创建 items/ 和 icons/ 子目录)")
        if not out_dir:
            return
        items_dir = os.path.join(out_dir, "items")
        icon_dir = os.path.join(out_dir, "icons")
        os.makedirs(items_dir, exist_ok=True)
        os.makedirs(icon_dir, exist_ok=True)
        exported = 0
        no_prefix = []
        collisions = []
        used_fnames = {}
        for it in self.items:
            # 文件名取 id 冒号后的名部分(与包惯例一致: 包id:x → x.json),
            # 非法字符转下划线; id 本身原样写入 JSON
            fname = re.sub(r'[<>:"/\\|?*]', "_", it.item_id.rsplit(":", 1)[-1])
            if fname in used_fnames and used_fnames[fname] != it.item_id:
                collisions.append(f"{used_fnames[fname]} 与 {it.item_id} 都导出为 {fname}.json (后者覆盖前者)")
            used_fnames[fname] = it.item_id
            if ":" not in it.item_id:
                no_prefix.append(it.item_id)
            icon_file = None
            if it.icon:
                img = self.find_image(it.icon.image_uid)
                if img:
                    icon_file = f"{fname}.png"
                    self.render_icon(it, img, os.path.join(icon_dir, icon_file))
            data = {"editor": "ps-item-editor", "schema": "psapi.items/1",
                    "items": [it.to_export_dict(icon_file=icon_file)]}
            with open(os.path.join(items_dir, f"{fname}.json"), "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
            exported += 1
        warn_text = ""
        if no_prefix:
            warn_text += (f"\n\n注意: {len(no_prefix)} 个物品 id 没有 '包名:' 前缀 "
                          f"({', '.join(no_prefix[:5])}{'...' if len(no_prefix) > 5 else ''})\n"
                          f"PS-API 规范要求 id 形如 '包名:物品名', 请确认是有意为之")
        if collisions:
            warn_text += "\n\n文件名冲突:\n" + "\n".join(collisions[:5])
        self.set_status(f"已导出 {exported} 个物品: {items_dir} (图标在 {icon_dir})")
        if not self.smoke:
            messagebox.showinfo("导出完成",
                                f"{exported} 个物品已导出:\n物品 JSON → {items_dir}\n图标 PNG → {icon_dir}\n\n"
                                f"把这两个文件夹整体放入游戏 UserData/PSApi/packs/<你的包>/ 即可被 PS-API 加载{warn_text}")

    def render_icon(self, item, img, out_path):
        w, h = item.grid_size()
        W, H = w * CELL, h * CELL
        canvas_img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        # 按绑定时的尺寸缩放(zoom-1 px 即最终像素)
        sw = max(1, int(item.icon.w_px))
        sh = max(1, int(item.icon.h_px))
        pil = img.pil.resize((sw, sh), Image.BILINEAR)
        if item.icon.rot:
            pil = pil.rotate(-item.icon.rot, expand=True, resample=Image.BICUBIC)
        cx = W / 2 + item.icon.dx * CELL
        cy = H / 2 + item.icon.dy * CELL
        canvas_img.paste(pil, (int(cx - pil.width / 2), int(cy - pil.height / 2)), pil)
        canvas_img.save(out_path)

    # ---------------- 其他 ----------------
    def set_status(self, text):
        self.status.configure(text=text)

    def run(self):
        self.root.mainloop()


def main():
    smoke = "--smoke" in sys.argv
    app = ItemEditor(smoke=smoke)
    if smoke:
        app.root.update()
        app.root.update_idletasks()
        print("SMOKE OK")
        app.root.destroy()
        return
    app.run()


if __name__ == "__main__":
    main()
