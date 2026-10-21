#!/usr/bin/env python3
"""
Second pass over a page produced by wpf2avalonia.py: an element's inline <X.Style> with default setters and DataTriggers
becomes classes on the element (Classes.vN bound to the trigger's test) and style selectors collected in <UserControl.Styles>.
Leaves anything it does not recognise for hand editing.

    tools/wpf2avalonia_triggers.py Page.axaml   (rewrites the file)
"""
import re, sys

path = sys.argv[1]
src = open(path, encoding="utf-8").read()
styles, counter = [], [0]

def setters(body, elem, icon):
    out = []
    for m in re.finditer(r'<Setter Property="([\w.]+)" Value="((?:[^"]|\{[^}]*\})*)"\s*/>', body):
        prop, val = m.group(1), m.group(2)
        if prop == "Visibility":
            prop, val = "IsVisible", {"Collapsed": "False", "Visible": "True"}.get(val, val)
        if icon and prop == "Text":
            g = re.search(r'&#x([0-9A-Fa-f]{4});', val)
            prop, val = "Glyph", g.group(1) if g else val
        out.append(f'<Setter Property="{prop}" Value="{val}"/>')
    return "".join(out)

def convert(m):
    elem, attrs, based, body = m.group(1), m.group(2) or "", m.group(3) or "", m.group(4)
    icon = elem == "TextBlock" and "Icon" in based
    counter[0] += 1
    cls = f"st{counter[0]}"
    default = body.split("<Style.Triggers>")[0]
    triggers = re.findall(r'<DataTrigger Binding="\{Binding ([\w.]+)\}" Value="([^"]*)">(.*?)</DataTrigger>', body, re.S)
    tag = "l:Icon" if icon else elem
    if icon:
        attrs = re.sub(r'\s*Style="[^"]*"', "", attrs)
    sel = "l|Icon" if icon else elem
    styles.append(f'    <Style Selector="{sel}.{cls}">{setters(default, elem, icon)}</Style>')
    conds = []
    for i, (path_, value, inner) in enumerate(triggers, 1):
        conds.append(f'Classes.v{i}="{{Binding {path_}, Converter={{StaticResource Eq}}, ConverterParameter={value}}}"')
        styles.append(f'    <Style Selector="{sel}.{cls}.v{i}">{setters(inner, elem, icon)}</Style>')
    return f'<{tag}{attrs} Classes="{cls}" {" ".join(conds)}/>'

pattern = re.compile(r'<(Ellipse|TextBlock)((?:\s+[\w.:]+="[^"]*")*)\s*>\s*<\1\.Style>\s*<Style TargetType="\1"((?: BasedOn="[^"]*")?)>(.*?)</Style>\s*</\1\.Style>\s*</\1>', re.S)
src = pattern.sub(convert, src)

if styles:
    block = "    <UserControl.Styles>\n" + "\n".join(styles) + "\n    </UserControl.Styles>\n"
    if "<UserControl.Styles>" in src:
        src = src.replace("<UserControl.Styles>\n", "<UserControl.Styles>\n" + "\n".join(styles) + "\n", 1)
    else:
        src = re.sub(r'(<UserControl [^>]*>\n)', lambda m: m.group(1) + block, src, count=1)
open(path, "w", encoding="utf-8").write(src)
print(f"{path}: {len(styles)} styles", file=sys.stderr)
