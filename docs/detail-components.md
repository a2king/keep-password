# 条目详情组件

详情面板覆盖登录、服务器、数据库，以及三种类型的新建/行内编辑。右侧保持 480px 宽，其他条目类型继续使用原有编辑器。SSH 认证保留独立密钥条目的引用；本轮没有增加私钥文件导入或草稿切换类型。

## 接入

在 `App.axaml` 的 `Application.Styles` 中引入：

```xml
<StyleInclude Source="avares://KeepPassword/Styles/DetailTheme.axaml" />
```

`DetailTheme.axaml` 包含亮暗主题资源，并引入字段、组合控件和标签编辑器的主题。实际程序集名为 `KeepPassword`。

页面使用统一宿主：

```xml
<details:DetailPanelView xmlns:details="using:KeepPassword.App.Views.Details"
                         x:Name="Detail" />
```

主窗口把原有 `MainViewModel` 交给 `SetEditor(view)`，把保存、删除、收藏、关闭、扫码和删除账号事件接回原处理流程。`Refresh(resetState: true)` 用于切换条目；同一条目保存后的刷新使用 `false`，按账号 ID 解析保存后重建的账号对象。`SetEditor(null)` 会释放展示层订阅和敏感展示状态。

业务值仍绑定原来的 `Name`、`Note`、`FieldEditorView.Value`、账号字段及空间/标签操作，不新增持久化字段。新增的 `DetailPresentationViewModel` 仅负责当前账号、页签标题、脏状态和校验提示。

公共字段可以单独复用：

```xml
<d:DetailGroup xmlns:d="using:KeepPassword.App.Controls.Details">
  <StackPanel>
    <d:FieldRow Label="用户名" Value="{Binding UsernameField.Value, Mode=TwoWay}"
                Placeholder="添加用户名" IsMono="True" />
    <d:SecretField Label="密码" Value="{Binding PasswordField.Value, Mode=TwoWay}"
                   Placeholder="添加密码" />
    <d:TotpField Value="{Binding TotpField.Value, Mode=TwoWay}" />
  </StackPanel>
</d:DetailGroup>
```

这里的 DataContext 是现有 `AccountEditorView`。验证码字段的 `Value` / `Secret` 是真实密钥，`Code` / `CopyValue` 才是生成的验证码；时钟刷新绝不回写业务密钥。

## 编辑与状态

- 已有条目默认查看，点击值或编辑图标进入编辑；Enter 接受单行编辑，Esc 撤回该行本次编辑。多行 Enter 换行，Ctrl+Enter 接受。
- 真正持久化仍通过底部保存或 Ctrl+S。原有 `ConfirmDiscardAsync` 保护切换/关闭时未保存的修改。
- 新建时打开字段编辑器并聚焦标题；类型沿用现有新建入口。创建按钮使用原校验规则，不增加端口必填等新约束。
- 密码固定显示十个圆点，切账号、切条目、离开字段、窗口失活或关闭时重新掩码。敏感值没有明文 Tooltip。
- 错误提示带和操作栏预留空间，焦点/编辑描边是覆盖层。切换编辑状态、显隐按钮和错误提示不会改变字段测量尺寸。
- 多账号仅显示当前账号。页签按账号说明、用户名、账号序号依次回退，保留原集合顺序；选择账号不计为内容修改。

## 剪贴板与共享时钟

`ClipboardService` 写入成功后立即返回，按钮和 Toast 无需等待过期任务。敏感值到期后重新读取，只有该任务仍有效且剪贴板仍等于原值才清空；新的复制取消旧任务。`SecretClipboard` 保留原 API，转发到同一个协调器，密码生成页、验证码页和详情复制共用到期状态。

`TotpTicker` 全局只持有一个每秒 `DispatcherTimer`。控件或页面通过 `Subscribe` 获取可释放的订阅；没有可见订阅者时停止。验证码保留现有 6–8 位、实际周期及算法支持，倒计时角度按实际周期计算，不固定除以 30。

## 快捷操作

空值对应的操作隐藏，其余自动均分；账号相关操作使用当前账号。

- 服务器只为 SSH / SFTP 生成不含密码的命令，不启动外部连接。
- 数据库连接串为即时派生结果，不回写模型，也不覆盖旧版本保留下来的自定义字段。支持的格式以 `DetailQuickActionFormatter.cs` 为准；未定义格式的驱动隐藏该项。
- 默认连接串不含密码；Shift+点击才包含密码，使用敏感复制策略并明确提示。URI 用户信息和路径编码，SQL Server / SQLite 使用连接字符串构造器转义。

## 调参位置

| 参数 | 位置 |
|---|---|
| 明暗底色、边框、正文/次要字/强调色 | `Styles/DetailTheme.axaml` 的 ThemeDictionaries |
| 中文字体、等宽字体、基本字号 | `Detail.Font.Ui`、`Detail.Font.Mono`、`Detail.FontSize` |
| 行高、标签宽、值起点 | `Detail.RowMinHeight`、`Detail.LabelWidth`、`Detail.ValueStart`；修改布局时同步 `DetailFieldTheme.axaml` 网格与分割线 |
| 组圆角与组间距 | `DetailGroup` 的 ControlTheme |
| 头部高度和留白 | `DetailCompositeTheme.axaml` 的 `DetailHeader` ControlTheme |
| 字段/Toast 过渡 | `Detail.TransitionDuration` |
| 验证码字号与换码过渡 | `Detail.TotpFontSize`、`Detail.CodeTransitionDuration` |
| 保存条滑入与快捷按钮上浮 | `DetailCompositeTheme.axaml` 对应 ControlTheme |
| 标签外观与编辑区 | `DetailChipsTheme.axaml` |
| 密码固定掩码和按钮反馈时长 | `FieldRow.cs`、`CopyButton.cs` |
| 30 秒剪贴板过期 | 既有 `ExpiringClipboard.DefaultLifetime` |

将 `.no-motion` 加到宿主或祖先可关闭新增过渡。主窗口会跟随现有“动画”设置维护此类；不会影响复制成功、焦点、错误和掩码这些即时状态。

## Avalonia 注意事项

- 当前项目是 Avalonia 11.3.22，含 `Avalonia.Controls.Shapes.Arc`。更旧版本若缺少该 Shape，可改用 `PathGeometry` 的 `ArcSegment`，数据和共享时钟无需变动。
- Avalonia 没有 CSS 相邻兄弟选择器。`DetailGroup` 关联相邻行，隐藏悬停/焦点行上下的分割线。
- 开放的线性 `StreamGeometry` 通过 `Path.Stroke` 绘制；不能直接交给仅填充的 `PathIcon`，否则部分图标不可见。
- 复杂渐变与动态资源并不等于任意控件背后的真实实时折射；此面板优先保证文字清晰和状态稳定。
- 验证通过的自动化测试仍需搭配实际渲染检查；布局在 DPI、字体可用性及不同平台上可能有细微差异。
