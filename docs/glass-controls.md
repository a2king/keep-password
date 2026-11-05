# 液态玻璃控件：调参与替换清单

分段切换、开关、下拉三类控件统一使用液态玻璃风格。全部是静态渐变 + `BoxShadow`，不使用 `BlurEffect`，没有常驻动画。

## 文件

| 文件 | 内容 |
|---|---|
| `Styles/GlassTheme.axaml` | 入口：亮/暗资源、动效时长与缓动、通用 `glass-trough` / `glass-thumb` / `glass-switch-row` 样式、减少动画规则 |
| `Styles/GlassSegmentedTheme.axaml` | `SegmentedControl` 与选项主题（默认 / `compact` / `wrap`） |
| `Styles/GlassToggleTheme.axaml` | `ToggleSwitch` 默认主题（标准 / `compact`） |
| `Styles/GlassComboTheme.axaml` | `ComboBox` / `ComboBoxItem` 默认主题（标准 / `compact`） |
| `Controls/SegmentedControl.cs` | 继承 `ListBox`；滑块定位、键盘、按压态 |
| `Controls/GlassHighlight.cs` | 玻璃块顶部镜面高光（按比例绘制） |
| `Controls/GlassSpringEasing.cs` | 可调回弹幅度的 BackEaseOut |
| `Controls/GlassPopupReveal.cs` | 下拉面板打开动画触发 |
| `Controls/GlassConverters.cs` | `HasContent`、`BoolToIndex` |

`App.axaml` 只在样式末尾引入 `GlassTheme.axaml`，三类控件按类型自动换皮。

## 调参位置

| 想调什么 | 位置 |
|---|---|
| 玻璃透明度 | `GlassTheme.axaml` → `GlassThumbBrush`（玻璃块）、`GlassTroughBrush`（凹槽）、`GlassPopupBrush`（下拉面板），亮/暗各一份 |
| 高光强度 | `GlassSpecularBrush`（顶部镜面条）；`GlassThumbShadow` 里两条 `inset`（顶部亮线、底部反光） |
| 高光形状 | `GlassHighlight` 的 `HeightRatio`（默认 0.38）、`InsetRatio`（默认 0.12） |
| 阴影 | `GlassThumbShadow`、`GlassThumbShadowCompact`（换行页签）、`GlassPopupShadow`、`GlassTroughShadow`、`ToggleOnShadow` |
| 回弹幅度 | `Glass.SpringEasing` 的 `Overshoot`：1.2 ≈ 越过终点 5%；0 = 无回弹 |
| 动画时长 | `Glass.SlideDuration`（滑块 480ms）、`Glass.PressDuration`（按压 200ms）、`Glass.ColorDuration`、`Glass.TrackColorDuration`（开关底色 300ms）、`Glass.HoverDuration`、`Glass.PopupFadeDuration` / `Glass.PopupMoveDuration`、`Glass.ChevronDuration`、`Glass.ReducedFadeDuration` |
| 强调色与文字 | `GlassAccent`（填充/描边）、`GlassAccentText`（小字号选中文字，已按 WCAG AA 调深/调亮）、`GlassTextSecondary` |
| 分段尺寸 | `GlassSegmentedTheme.axaml` 末尾 `c|SegmentedControl ListBoxItem`（高 34 / compact 26）；凹槽 `Padding`、`CornerRadius` 在控件主题 Setter 和 `^.compact` |
| 开关尺寸 | `GlassToggleTheme.axaml`：`PART_Switch` 58×34、`PART_Thumb` 40×28、开启位移 `translate(12px)`、按住拉长 `Margin -6`；compact 对应 46×28 / 32×22 / 8px / -4 |
| 下拉尺寸 | `GlassComboTheme.axaml`：`MinHeight` 42、`CornerRadius` 14、`MaxDropDownHeight` 320、下拉项 `MinHeight` 38；compact 32 / 10 |

## 替换清单

| 位置 | 旧 | 新 |
|---|---|---|
| `Views/PasswordGeneratorView.axaml` 密码类型 | `LiquidGlassSegments` + 3 个 `Button`（`OnRandom` 等） | `SegmentedControl KindSegments`，`OnKindChanged` 调原 `ApplyKind` |
| `Views/SettingsView.axaml` 外观主题 | 同上（`OnThemeLight` 等） | `SegmentedControl ThemeSegments`，`OnThemeSegmentChanged` 调原 `ApplyTheme` |
| `Views/SettingsView.axaml` 界面动画 | 同上（`OnMotionFull` 等） | `SegmentedControl MotionSegments`，`OnMotionSegmentChanged` 调原 `ApplyMotion` |
| `Views/MainWindow.axaml` 旧编辑器认证方式 | 同上（`OnAccountAuthPassword/Key`） | `SegmentedControl`，`SelectedIndex` 单向绑定 `UseKeyAuth`，`OnAccountAuthChanged` 调原 `SetKeyAuth` |
| `Views/Details/DetailPanelView.axaml.cs` 认证 | `Controls.Details.SegmentedControl` | `Controls.SegmentedControl`（`compact`） |
| `Styles/DetailCompositeTheme.axaml` 账号切换 | 同上 | `Controls.SegmentedControl`（`compact wrap`：放不下就换行，不滚动） |
| `Views/Details/DetailPanelView.axaml.cs` 生成密码弹层 | 4 个 `CheckBox` | 4 个 `ToggleSwitch`（`compact`） |
| 所有 `ToggleSwitch`、`ComboBox` | Fluent 主题 + App.axaml 全局补丁样式 | 玻璃默认主题；详情面板的 3 个下拉加 `compact` |

已删除：`Views/LiquidGlassSegments.cs`、`Controls/Details/SegmentedControl.cs`；App.axaml 中 `liquid-*`、`ComboBox`、`ComboBoxItem`、`ToggleSwitch` 全局样式。

保留不改：侧边栏主导航；`ImportWizardWindow` 每行的导入勾选框；`UnlockView` 的风险确认勾选框（二者是确认语义，不是开关）。

## 已知限制与替代方案

- **没有背景模糊**：Avalonia 11 没有 `backdrop-filter`。下拉面板用高不透明度渐变（亮 ≥ 93%、暗 ≥ 90%）保证文字对比度。
- **下拉关闭不淡出**：`Popup` 在 `IsOpen=false` 时立即销毁弹出窗口，无法再播放过渡；打开动画照常。
- **开关不支持拖动**：Avalonia 要求模板里有 `PART_MovingKnobs`，这里只保留 0 尺寸占位，点击 / Space 切换。
- **字重 650**：微软雅黑只有常规与粗体，选中态用 `SemiBold`，实际渲染为粗体；等宽格子布局下不会抖动。不定宽的账号页签选中不加粗。
- **按住拉长**：用内层 `Margin` 实现而非 `Width`，保证回弹任意中间帧都不越出 3px 边距。
- **暗色玻璃块**：规格 34%→14% 白时，选中蓝字对比度只有 3.3:1，已降到 15%→8%，选中文字用 `#C8DCFF`（≥ 5.4:1；字形顶端碰到高光条处约 4.3:1）。
