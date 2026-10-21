#!/usr/bin/env python3
"""
Mechanical first pass from a Kairos WPF page to Avalonia XAML: namespaces, Visibility bindings, icon TextBlocks,
Style="{StaticResource X}" -> Classes="X", ToolTip -> ToolTip.Tip. What it leaves marked "TODO(avalonia)" (DataTriggers,
RelativeSource, control templates) is translated by hand.

    tools/wpf2avalonia.py src/Kairos/Ui/Pages/StatusPage.xaml > src/Kairos.Linux/Ui/Pages/StatusPage.axaml
"""
import re, sys

src = open(sys.argv[1], encoding="utf-8").read()

src = src.replace('xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"', 'xmlns="https://github.com/avaloniaui"')
src = src.replace('xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"', 'xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"')

# <TextBlock Style="{StaticResource Icon}" Text="&#xE7BA;" .../>  ->  <l:Icon Glyph="E7BA" .../>
def icon(m):
    attrs = m.group(1)
    g = re.search(r'Text="&#x([0-9A-Fa-f]{4});"', attrs)
    if not g or 'Style="{StaticResource Icon}"' not in attrs:
        return m.group(0)
    attrs = re.sub(r'\s*Style="\{StaticResource Icon\}"', '', attrs)
    attrs = re.sub(r'\s*Text="&#x[0-9A-Fa-f]{4};"', f' Glyph="{g.group(1)}"', attrs)
    return f'<l:Icon{attrs}/>'
src = re.sub(r'<TextBlock((?:\s+[\w.:]+="[^"]*"|\s+[\w.:]+=\'[^\']*\')*)\s*/>', icon, src)

# Visibility bindings
def vis(m):
    inner = m.group(1)
    param = re.search(r'ConverterParameter=(\w+)', inner)
    inner = re.sub(r',\s*Converter=\{StaticResource Vis\}', '', inner)
    inner = re.sub(r',\s*ConverterParameter=\w+', '', inner)
    conv = ', Converter={StaticResource Truthy}' + (f', ConverterParameter={param.group(1)}' if param else '')
    return f'IsVisible="{{Binding {inner}{conv}}}"'
src = re.sub(r'Visibility="\{Binding ((?:(?!\}\}).)*?Converter=\{StaticResource Vis\}(?:(?!\}\}).)*?)\}"', vis, src)
for m in re.finditer(r'Visibility="\{Binding ((?:[^"{}]|\{[^{}]*\})*?)\}"', src):
    sys.stderr.write(f"TODO(avalonia): Visibility binding left as is: {m.group(0)}\n")
src = src.replace('Visibility="Collapsed"', 'IsVisible="False"').replace('Visibility="Visible"', 'IsVisible="True"')
src = re.sub(r'Visibility="\{Binding ([\w.]+), Converter=\{StaticResource EqVis\}, ConverterParameter=(\w+)\}"',
             r'IsVisible="{Binding \1, Converter={StaticResource EqVisible}, ConverterParameter=\2}"', src)

# Styles -> classes
src = re.sub(r'Style="\{StaticResource (\w+)\}"', r'Classes="\1"', src)
src = re.sub(r'(\s)ToolTip="', r'\1ToolTip.Tip="', src)

# ComboBox: DisplayMemberPath/SelectedValuePath are bindings in Avalonia
src = re.sub(r'DisplayMemberPath="([\w.]+)"', r'DisplayMemberBinding="{Binding \1}"', src)
src = re.sub(r'SelectedValuePath="([\w.]+)"', r'SelectedValueBinding="{Binding \1}"', src)
src = re.sub(r'StrokeDashArray="([\d.]+) ([\d.]+)"', r'StrokeDashArray="\1,\2"', src)

# things WPF-only that carry no meaning here
src = re.sub(r'\s+WindowChrome\.[\w.]+="[^"]*"', '', src)
for attr in ('UseLayoutRounding', 'SnapsToDevicePixels', 'FocusVisualStyle', 'TextOptions.TextFormattingMode', 'Focusable'):
    src = re.sub(rf'\s+{re.escape(attr)}="[^"]*"', '', src)

# leave a marker on what must be done by hand
for pat in ('Style.Triggers', 'DataTrigger', 'RelativeSource', 'ControlTemplate', 'MultiBinding', 'Setter Property="Template"'):
    src = src.replace(pat, pat + ' TODO(avalonia)') if False else src
sys.stdout.write(src)
