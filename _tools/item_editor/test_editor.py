# -*- coding: utf-8 -*-
"""无头功能测试: 直接驱动 ItemEditor 模型逻辑"""
import json
import os
import sys
import tempfile
import tkinter as tk

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from item_editor import ItemEditor, ImageAsset, IconBinding, CELL
from PIL import Image

app = ItemEditor(smoke=True)
app.root.withdraw()
fails = []


def check(name, cond):
    print(("PASS " if cond else "FAIL ") + name)
    if not cond:
        fails.append(name)


# 1) 创建物品
item = app.create_item([(0, 0), (0, 1)])
check("create 1x2 item", item.grid_size() == (1, 2))

# 2) 防重叠
check("cells_free overlap", not app.cells_free({(0, 0), (1, 0)}))
check("cells_free ignore self", app.cells_free({(0, 0)}, ignore_uid=item.uid))

# 3) 旋转 1x2 -> 2x1
app.selected_items = [item.uid]
app.rotate_selected()
check("rotate 1x2->2x1", item.cells == {(0, 0), (1, 0)} and item.grid_size() == (2, 1))

# 4) 旋转被重叠阻挡
item2 = app.create_item([(2, 0)])
app.rotate_selected()  # item 转回 1x2 -> {(0,0),(0,1)}, 无冲突
check("rotate back", item.cells == {(0, 0), (0, 1)})
# 现在再旋转会撞到 item2? (0,0),(0,1) -> (1,0),(0,0) 不撞 item2(2,0)
app.rotate_selected()
check("rotate no collision", item.cells == {(1, 0), (0, 0)})
item_block = app.create_item([(0, 1)])
app.rotate_selected()  # {(0,0),(1,0)} -> {(0,0),(0,1)} 撞 item_block
check("rotate blocked by overlap", item.cells == {(0, 0), (1, 0)})

# 5) 绑定图片
pil = Image.new("RGBA", (64, 48), (255, 0, 0, 255))
img = ImageAsset("test.png", pil)
img.w_px, img.h_px = 32.0, 48.0
x0, y0, x1, y1 = item.bbox()
w, h = item.grid_size()
img.cx = (x0 + w / 2) * CELL + 8  # 偏移 0.5 格
img.cy = (y0 + h / 2) * CELL
app.images.append(img)
app.do_bind(item.uid, img)
check("bind", item.icon is not None and img.bound_to == item.uid)
check("bind offset dx=0.5", abs(item.icon.dx - 0.5) < 1e-6 and abs(item.icon.dy) < 1e-6)

# 6) 带图标旋转: 偏移 (0.5,0) -> (0,0.5), 图标 +90°, 宽高不变(修复: 不再互换宽高)
old_rot = item.icon.rot
app.selected_items = [item.uid]
# item {(0,0),(1,0)} 旋转 -> {(0,0),(0,1)} 撞 item_block, 先挪走 blocker
item_block.cells = {(5, 5)}
app.rotate_selected()
check("rotate with icon cells", item.cells == {(0, 0), (0, 1)})
check("rotate icon offset", abs(item.icon.dx) < 1e-6 and abs(item.icon.dy - 0.5) < 1e-6)
check("rotate icon rot+90", (item.icon.rot - old_rot) % 360 == 90)
check("icon size unchanged", item.icon.w_px == 32.0 and item.icon.h_px == 48.0)

# 6b) 逆时针旋转: (0,0),(0,1) 1x2 -> 2x1, 偏移 (0,0.5)->(0.5,0), rot-90
app.rotate_selected(ccw=True)
check("ccw rotate cells", item.cells == {(0, 0), (1, 0)})
check("ccw icon offset", abs(item.icon.dx - 0.5) < 1e-6 and abs(item.icon.dy) < 1e-6)
check("ccw icon rot-90", (item.icon.rot - old_rot) % 360 == 0)

# 6c) 拖动中按 R: 旋转后释放不应回滚旋转
item.cells = {(0, 0), (1, 0)}
app.selected_items = [item.uid]
app._drag = {"kind": "item", "uid": item.uid, "start_cell": (0, 0),
             "orig_cells": {(0, 0), (1, 0)}, "delta": (0, 0), "valid": True}
app.rotate_selected()  # 应取消拖动并旋转
check("rotate cancels drag", app._drag is None and item.cells == {(0, 0), (0, 1)})

# 6d) 拖动释放后图片绝对同步(不漂移)
img2_cx_before = img.cx
app._drag = {"kind": "item", "uid": item.uid, "start_cell": (0, 0),
             "orig_cells": {(0, 0), (0, 1)}, "delta": (3, 2), "valid": True,
             "preview": {(3, 2), (3, 3)}}
class E: pass
app.on_left_up(E())
x0, y0, _, _ = item.bbox()
w, h = item.grid_size()
exp_cx = (x0 + w / 2) * CELL + item.icon.dx * CELL
exp_cy = (y0 + h / 2) * CELL + item.icon.dy * CELL
check("drag release absolute sync", abs(img.cx - exp_cx) < 1e-6 and abs(img.cy - exp_cy) < 1e-6)

# 6e) 撤销/重做(恢复会重建对象, 需按 uid 重新获取)
app.undo()  # 撤销 6d 的移动
item = app.find_item(item.uid)
check("undo restores cells", item.cells == {(0, 0), (0, 1)})
app.redo()
item = app.find_item(item.uid)
check("redo works", item.cells == {(3, 2), (3, 3)})
app.undo()
item = app.find_item(item.uid)
img = app.find_image(img.uid)  # 图片对象同样会被重建
item_block.cells = set()  # 清理, 避免影响后续导出测试
app2cleanup = app.find_item(item2.uid)
if app2cleanup: app2cleanup.cells = {(8, 8)}

# 7) sync_bound_image 位置
app.sync_bound_image(item)
x0, y0, _, _ = item.bbox()
w, h = item.grid_size()
exp_cx = (x0 + w / 2) * CELL + item.icon.dx * CELL
exp_cy = (y0 + h / 2) * CELL + item.icon.dy * CELL
check("sync image pos", abs(img.cx - exp_cx) < 1e-6 and abs(img.cy - exp_cy) < 1e-6)

# 7b) 模板候选与显示转换
check("templates loaded (live catalog or snapshot)", len(app.templates) > 300)
check("template display roundtrip",
      app._template_from_display(app._template_to_display("scrap_metal")) == "scrap_metal")
check("template display free text", app._template_from_display("custom_tpl_123") == "custom_tpl_123")
check("template display empty", app._template_from_display("") == "")

# 7b2) 标签中文对照: 列表显示带中文, 选中回写仍是英文 id
import item_editor as _iemod
check("tags all have zh", all(t in _iemod.TAG_NAMES_ZH for t in _iemod.GAME_TAGS))
check("tag listbox shows zh",
      any(x.startswith("FIREARM") and "枪械" in x for x in app.tag_list.get(0, tk.END)))
app.tag_list.selection_set(_iemod.GAME_TAGS.index("FIREARM"))
app.selected_items = [item.uid]
app.apply_panel()
check("tag selection roundtrip en id", item.tags == {"FIREARM"})
app.tag_list.selection_clear(0, tk.END)
app.apply_panel()
check("tag deselect clears", item.tags == set())

# 7c) 模板字段(导出/面板)
item.template = "scrap_metal"
item_colon = app.create_item([(10, 10)])
item_colon.item_id = "pk:testgun"

# 8) 导出(PS-API 框架格式: items/<id>.json + icons/<id>.png)
tmp = tempfile.mkdtemp()
import item_editor as ie
orig_dir = ie.filedialog.askdirectory
ie.filedialog.askdirectory = lambda **kw: tmp
try:
    app.export_json()
finally:
    ie.filedialog.askdirectory = orig_dir
export_path = os.path.join(tmp, "items", item.item_id + ".json")
check("export json exists (items/ dir)", os.path.isfile(export_path))
with open(export_path, encoding="utf-8") as f:
    data = json.load(f)
icon_file = os.path.join(tmp, "icons", item.item_id + ".png")
check("export icon exists", os.path.isfile(icon_file))
if os.path.isfile(icon_file):
    with Image.open(icon_file) as ic:
        check("icon size = grid*16", ic.size == (1 * CELL, 2 * CELL))
check("export schema marker", data.get("schema") == "psapi.items/1")
rec = next(i for i in data["items"] if i["id"] == item.item_id)
check("export shape normalized", rec["shape"]["cells"] == [[0, 0], [0, 1]]
      and rec["shape"]["w"] == 1 and rec["shape"]["h"] == 2)
check("export uses desc not description", "desc" in rec and "description" not in rec)
check("export contraband key", rec.get("contraband") == "none" and "contrabandLevel" not in rec)
check("export template", rec.get("template") == "scrap_metal")
check("export icon minimal", rec["icon"] == {"file": item.item_id + ".png"})
# 带冒号 id: JSON 内 id 原样, 文件名取冒号后名部分
colon_path = os.path.join(tmp, "items", "testgun.json")
check("export colon id filename = name part", os.path.isfile(colon_path))
with open(colon_path, encoding="utf-8") as f:
    colon_data = json.load(f)
check("export colon id preserved in json", colon_data["items"][0]["id"] == "pk:testgun")
check("export no template key when empty", "template" not in colon_data["items"][0])
check("export no icon key when unbound", "icon" not in colon_data["items"][0])

# 9) 工程保存/加载
proj_path = os.path.join(tmp, "proj.json")
ie.filedialog.asksaveasfilename = lambda **kw: proj_path
try:
    app.save_project()
finally:
    pass
app.root.destroy()  # 先销毁, 避免多 Tk 实例导致 PhotoImage 冲突(真实使用单实例)
app2 = ItemEditor(smoke=True)
app2.root.withdraw()
ie.filedialog.askopenfilename = lambda **kw: proj_path
app2.load_project()
check("project reload items", len(app2.items) == len(app.items))
bound = [i for i in app2.items if i.icon]
check("project reload binding", len(bound) == 1 and bound[0].icon.image_uid in [im.uid for im in app2.images])
tpl_items = [i for i in app2.items if i.template == "scrap_metal"]
check("project reload template", len(tpl_items) == 1)

app2.root.destroy()
print("\n%d FAIL" % len(fails) if fails else "\nALL PASS")
sys.exit(1 if fails else 0)
