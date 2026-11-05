# 条目详情面板重构计划（已确认）

用户已确认按本计划的推荐范围实施：保留独立 SSH 密钥引用与现有新建类型选择，允许为 AA 对比度调整文字专用颜色。以下记录组件边界与实施依据。

## 1. 已核实的项目现状

- Avalonia **11.3.22**、.NET 8；使用项目自定义 `ViewModelBase`，不是 CommunityToolkit.Mvvm，当前也没有一套可直接绑定的保存/删除 `ICommand`。
- 详情目前集中在 `Views/MainWindow.axaml` 与 `.axaml.cs`，右侧宽度 480px，符合目标 440–520px。保留该宽度和左侧虚拟化列表。
- 编辑数据来自 `MainViewModel` 和 `ItemEditorViews.cs`，已有 `Name`、`Note`、`Kind`、`Accounts`、`PresetFields`、`IsNewItem`、`IsDirty` 等。
- 空间与标签通过 `SpaceChoices` / `TagChoices` 和 `ChooseSpace` / `ToggleTag` / `AddSpaceChoice` / `AddTagChoice` 操作，不存在可直接绑定的公开 `Space` / `Tags` 编辑属性。
- `IsDirty` 已有快照比较，但缺少实时属性通知。当前也没有“当前账号”“字段编辑状态”“字段错误”等展示状态。
- 保存后会重建账号与字段对象，因此不能把旧账号对象作为长期选择标识。
- 服务器账号保存的是 `Auth` 与 `SshKeyId`。私钥、私钥口令属于独立 SSH 密钥条目，并非服务器账号字段。
- 目前主窗口和验证码页各有一个每秒计时器；需要统一为一个共享时钟，不能再为每个验证码控件新增计时器。

## 2. 组件拆分与文件清单

以下都是计划新增的文件，名称不是对现有绑定的假设。路径均相对于项目根目录。

| 文件 | 责任 |
|---|---|
| `src/KeepPassword.App/Styles/DetailTheme.axaml` | 明暗主题资源、字体、尺寸、动画时长和所有详情控件的 `ControlTheme`；颜色统一 `DynamicResource` |
| `src/KeepPassword.App/Controls/Details/DetailGroup.cs` | 14px 分组容器、淡边框、圆角裁剪、组间距 |
| `src/KeepPassword.App/Controls/Details/FieldRow.cs` | 64px 标签、弹性值、操作插槽；查看/编辑/空值/错误状态；键盘交互 |
| `src/KeepPassword.App/Controls/Details/CopyButton.cs` | 32×32 复制按钮、成功勾选、1.5 秒恢复及 Toast 请求 |
| `src/KeepPassword.App/Controls/Details/SecretField.cs` | 固定 10 个圆点、查看/编辑、自动重新掩码 |
| `src/KeepPassword.App/Controls/Details/TotpField.cs` | 等宽分组数字、26px `Arc` 圆环、临界秒提示、整行复制 |
| `src/KeepPassword.App/Controls/Details/ChipsEditor.cs` | 空间单选、标签多选、建议列表、Enter/Esc；复用现有标签操作 |
| `src/KeepPassword.App/Controls/Details/SegmentedControl.cs` | 基于原生选择控件的账号/认证切换、方向键与焦点环 |
| `src/KeepPassword.App/Controls/Details/DetailHeader.cs` | 固定头部、直接编辑标题、类型/空间副标题、收藏/菜单/关闭 |
| `src/KeepPassword.App/Controls/Details/QuickActionBar.cs` | 当前账号的可用快捷操作；隐藏空值项，其余均分 |
| `src/KeepPassword.App/Controls/Details/SaveBar.cs` | 已有条目的脏状态保存条、新建条目的固定创建/取消条 |
| `src/KeepPassword.App/Controls/Details/ToastHost.cs` | 面板顶部居中提示，1.8 秒自动消失；重复操作替换旧提示 |
| `src/KeepPassword.App/Controls/Details/AccountSection.cs` | 仅展示当前账号，账号说明→用户名→账号 N 的页签标题回退，增删确认 |
| `src/KeepPassword.App/Views/Details/DetailPanelView.axaml` 与 `.axaml.cs` | 固定头部、滚动内容、Toast 与底部保存条的统一宿主；键盘/滚动协调 |
| `src/KeepPassword.App/Views/Details/LoginDetailView.axaml` 与 `.axaml.cs` | 网址分组、账号分组、空间/标签/备注分组的拼装 |
| `src/KeepPassword.App/Views/Details/ServerDetailView.axaml` 与 `.axaml.cs` | 协议、主机/端口、当前账号和 SSH 密钥引用的拼装 |
| `src/KeepPassword.App/Views/Details/DatabaseDetailView.axaml` 与 `.axaml.cs` | 数据库类型、对应连接字段、当前账号的拼装；保留 SQLite、OSS 等差异 |
| `src/KeepPassword.App/ViewModels/DetailPresentationViewModel.cs` | 纯展示适配器及三种类型的轻量展示对象，由 `ContentControl` + 类型 `DataTemplate` 选择视图 |
| `src/KeepPassword.App/Services/ClipboardService.cs` | 统一复制入口，复用已有 `SecretClipboard` / `ExpiringClipboard`，协调反馈与到期清除 |
| `src/KeepPassword.App/Services/TotpTicker.cs` | 全局单个 `DispatcherTimer`；可见订阅者存在时运行，无订阅者/锁定时停止 |
| `src/KeepPassword.App/Views/Details/DetailQuickActionFormatter.cs` | 纯字符串格式化：SSH/SFTP 命令及支持的数据库连接串；不读写模型、不启动连接 |
| `docs/detail-components.md` | App 接入、控件用法、参数位置、可访问性及平台限制 |
| `tests/KeepPassword.App.Tests/DetailPanelInteractionTests.cs` | 关键交互、状态重置、尺寸稳定、账号选择及键盘回归 |
| `tests/KeepPassword.App.Tests/DetailServicesTests.cs` | 剪贴板替换保护、共享时钟生命周期和连接串编码的回归 |

通用控件以 `TemplatedControl` / 可模板化的原生控件基类实现，视觉全部由 `ControlTheme` 提供。页面只负责拼装与绑定；视图的 code-behind 只处理焦点、滚动和路由，不承担保存逻辑。

## 3. 现有文件与 VM 的最小改动

### MainViewModel.cs

保留现有属性名、保存/删除流程、模型和持久化格式。计划增加或补齐以下展示接口：

1. **补齐 `IsDirty` 的 `PropertyChanged`**：标题、备注、字段、账号、空间、标签、集合增删及保存/加载/关闭后通知；把本文件中传给账号/节点的空 changed 回调接入通知。继续使用原有 `Snapshot()` 比较，不把倒计时、密码显隐、焦点或账号切换计为修改。
2. **增加只读 `DetailId`**：暴露当前草稿标识，仅供撤销重新加载和展示生命周期识别，不生成新 ID、不改变保存流程。
3. **增加只读校验入口 `GetDetailValidationMessage()`**：从当前编辑数据生成临时快照，复用原有名称检查与 `VaultItemRules.Validate`，返回错误而不保存、不修改会话。展示适配器负责定位对应字段；无法唯一定位的错误显示在保存条中。

不新增 `SaveCommand` / `DeleteCommand`，不引入 MVVM 框架迁移。新建时切换类型的草稿接口不属于上述最小通知修补，见第 7 节。

### ItemEditorViews.cs

- 现有账号/节点已正确调用传入的 changed 回调；回调接线在 `MainViewModel.cs` 修补。本文件不预期需要业务修改，保持原有字段含义和 `ToModel()` 不变。
- 新账号页签文字在展示适配器计算，订阅 `Note` 和 `UsernameField.Value` 更新；不改现有 `DisplayName` 的含义，不改变账号存储顺序。
- 新 `SecretField` 在视图层显示固定掩码，不把掩码写回真实 `Value`。

### DetailPresentationViewModel.cs（新增）

承载 `ActiveAccountIndex`、`SelectedAccountId`、派生的 `CurrentAccount`、账号页签、行编辑状态/进入编辑前的值、标题编辑状态、字段错误、Toast、验证码显示数据与快捷操作的可见性。

字段的真实值仍写回原来的 `Name` / `Note` / `FieldEditorView.Value` / 账号字段；空间、标签继续调用原方法。展示对象通过委托调用主窗口已存在的保存、删除、收藏、生成密码、扫码和确认丢弃流程，不重复实现业务。

切换到另一个条目时：退出所有编辑、全部掩码、选择首个账号、滚动到顶部并清除旧提示；同一条目保存后：按账号 `Id` 重新解析选择，不保留已失效的对象引用。关闭或锁定时解除订阅并清除展示对象持有的敏感值。

### App.axaml / MainWindow.axaml / MainWindow.axaml.cs / TotpView.axaml.cs

- App 引入 `DetailTheme.axaml`，主题资源限定在详情范围内，避免通用键名影响其他页面。
- 主窗口用统一详情宿主接入三种新视图，去掉重复“条目名称”输入框；其他条目类型仍保留现有能力，不因本轮只覆盖三种场景而丢失字段。
- 主窗口复用 `ConfirmDiscardAsync`、`OnSave` 对应保存和同步通知流程、收藏行为、扫码与生成密码入口。收藏对已有条目仍立即保存，不改成 SaveBar 提交后才生效。
- 两处验证码计时器改为订阅 `TotpTicker`，保留原验证码算法；没有可见使用者时停止。

## 4. 布局与交互实现

1. **头部和保存条固定，只有内容滚动。** 保留 480px 面板，长网址/主机使用省略号；非敏感值可完整 Tooltip，密码和私钥不通过 Tooltip 泄露。
2. **按组组织，不逐字段套框。** 标签宽 64px，左内距 16px，标签和值之间 12px，分割线从 92px 开始。外组圆角裁剪，快捷操作条外层留上浮空间。
3. **查看与编辑尺寸一致。** 文本和输入框叠放，保留操作栏宽度；2px 编辑/焦点描边独立覆盖，不占测量空间。单行最小 44px，内部预留错误提示带，错误出现不推移后续行；多行字段使用固定的更大行模板。
4. **相邻分割线联动。** 分组监听当前悬停/键盘聚焦行，同时淡出该行上下分割线；不依赖 Avalonia 不支持的 CSS 相邻兄弟选择器。
5. **默认查看态。** 点击值/编辑按钮进入行内编辑，Enter 接受当前行，Esc 恢复该行进入编辑前的值；失焦提交到草稿，点击保存才持久化。多行输入保留 Enter 换行，Ctrl+Enter 接受该行。
6. **新建态。** 所有可编辑行打开编辑器，标题自动聚焦；固定创建/取消条。Ctrl+S 同样走原保存入口，已知必填不满足时创建按钮置灰；键盘提交仍会验证、定位并滚动到首个错误。
7. **敏感字段。** 默认恰好 10 个圆点；焦点离开整个字段控件、窗口失活、切换账号/条目、关闭/锁定时重新掩码。点击字段内部眼睛或复制按钮不因内部焦点迁移而闪烁。编辑器使用 `PasswordChar`。
8. **复制反馈立即显示。** 写入成功后立即出现勾选和 Toast，敏感值的清除任务在后台等待，不等 30 秒才反馈。到期必须仍等于该次复制值且任务仍有效才清除，后续复制替换旧任务。
9. **TOTP。** 使用项目现有的解析/生成器；圆环按 `剩余秒 / 实际周期 × 360` 计算，保留已有 6–8 位与自定义周期支持。`Arc` 在当前 11.3.22 中可用；数字只在验证码变化时用 120ms 过渡，不每秒对数字做动画。
10. **键盘和减少动效。** Tab 顺序与视觉顺序一致，操作在键盘聚焦时也可见；2px 焦点环。所有新增动画只用 Transitions、120–250ms；沿用现有 `MotionPreferences` / `.no-motion`，关闭上浮、滑入和淡入，不做内容进入动画。

## 5. 主题、字体与图标

- 按附件建立亮暗两套详情主题；背景、边框、强调色、语义色、收藏金色和阴影色全部资源化。
- 正文采用 `Microsoft YaHei UI, Segoe UI`，等宽采用 `Cascadia Mono, Consolas`，都定义为可替换资源。
- 优先复用现有类型图标与 `Icon.*` 资源。现有部分图标为实心，若不能满足 16px、1.5–1.7px 线性要求，则在详情主题中新增局部 `StreamGeometry`：复制、勾选、眼睛/隐藏、更多、关闭、星形、外链、终端、数据库、扫码、生成、导入、加号、删除、展开箭头；不引入新的图标依赖。
- 附件给出的部分浅色小字配色与 WCAG AA 有冲突，例如 `#7B8294` 在白底下不足普通文字的 4.5:1。建议保留材质与装饰色，另加文字专用的次要色/强调色/错误色资源并校验实际背景上的对比度；不让装饰灰承担必读文字。
- 液态玻璃风格继续用于轻量切换胶囊和浮层边缘，详情内容组保持干净稳定，不做大面积流光。

## 6. 各场景的实现边界

| 场景 | 计划行为 |
|---|---|
| 登录 | 网址、单个当前账号、验证码、空间/标签/备注；空备注仅保留一行占位 |
| 服务器 | 协议下拉、主机/端口独立复制、认证切换；仅 SSH/SFTP 提供不含密码的连接命令 |
| 数据库 | 使用现有 `Drivers`；保留 SQLite 文件路径、Redis DB 编号、OSS Endpoint/AccessKey 等原字段，不一律套成 MySQL |
| 新建/编辑 | 共用控件；新建全部编辑，已有条目按行进入编辑；密码生成浮层复用现有生成器，扫码复用当前入口 |

端口继续遵循项目既有“为空或等于旧类型默认值时更新默认端口”规则；空端口仍按原校验允许，不新增必填限制，也不新增数据库驱动。

数据库“连接串”属于新增的**派生展示/复制**，不恢复已废弃的 `connection` 模型字段，不覆盖已有自定义连接串。建议首批明确支持 MySQL / PostgreSQL / MongoDB / Redis 的 URI 格式；用户信息与路径正确编码，IPv6 正确加方括号。SQL Server / SQLite 使用各自的连接字符串格式，采用对应转义规则；没有确认标准格式的驱动隐藏此快捷项。普通点击不带密码；Shift+点击生成含密码版本、明确 Toast 并启用 30 秒清除。

## 7. 需要确认的范围差异与推荐方案

### A. 服务器 SSH 密钥

**推荐本轮保留独立密钥引用。** 服务器中显示/选择现有密钥，使用新的行与掩码视觉；不提供会直接修改共享密钥的内嵌编辑器，也不假装存在账号私钥字段。

附件中的“导入私钥文件并关联”目前没有实现。若要加入，需要增加创建独立 SSH 条目、保存并关联、取消/失败回滚等业务流程，应独立授权；直接把私钥塞入服务器账号会改变模型，本轮不推荐。

### B. 新建时切换类型并保留值

**推荐本轮先保留当前新建类型选择方式。** `Kind` 当前只读，`NewItem(kind)` 会新建草稿，无法无损保留已填字段。

若要求这轮同时具备该能力，需要允许增加“仅新建草稿可调用”的切型接口：按 `FieldKeys` 保留兼容字段、标题/空间/标签/备注，账号及不兼容字段明确映射或确认丢弃；已保存条目仍禁止修改类型。这属于草稿行为扩展，不能仅换一个类型 Chip 就声称完成。

### C. 文字对比度

**推荐允许微调文字专用资源**以满足 AA，材质背景和装饰色沿用附件。若所有给定颜色必须逐值固定，则无法同时承诺全部小字达到 AA。

## 8. 实施顺序与验收

1. 确认第 7 节的范围和资源处理方案。
2. 实现主题与 `DetailGroup` / `FieldRow`，先验证布局、明暗主题、焦点环与行内编辑不抖动。
3. 实现复制/Toast/敏感字段/共享时钟，再拼 `AccountSection`、头部、快捷操作与保存条。
4. 接入登录、服务器、数据库视图；最后接入新建和既有保存/扫码/生成密码流程。
5. 验证同一套控件在 440 / 480 / 520px、明暗主题、长文本、空值、多账号、错误、键盘与减少动效下的表现。
6. 回归切条目/账号/关闭/锁定的掩码和状态清理，保存/撤销与同步通知，剪贴板到期保护，单共享时钟及页面离开后的释放。
7. 编译并运行现有与新增的必要测试；使用隔离输出目录，避免覆盖当前运行客户端。另做实际窗口视觉检查，不把 headless 通过等同于视觉验收。

最终交付完整可编译代码、中文关键注释、App/DataTemplate 接入说明，以及圆角/行高/分割线缩进/验证码字号/动画时长的统一调参位置。
