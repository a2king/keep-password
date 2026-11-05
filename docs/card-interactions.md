# 卡片交互与复用

客户端目前使用 `Button + ItemsControl` 显示卡片：外层 `VirtualizingStackPanel` 按行虚拟化，每行再用 `UniformGrid` 排列卡片。现有页面已接入 `CardButtonTheme`，保留原来的条目选择、点击事件和 ViewModel；没有引入 CommunityToolkit。

`src/KeepPassword.App/Styles/CardItemStyles.axaml` 同时提供两个主题：

| 主题 | 控件 | 选中状态 |
| --- | --- | --- |
| `CardButtonTheme` | `Button` | `Classes.selected` |
| `CardItemTheme` | `ListBoxItem` | `:selected`，由 `ListBox` 管理 |

默认卡片保持中性表面；未选中悬停时底色不变，只增强边框（蓝色）和阴影，不上浮，避免顶部边框被列表裁掉。选中卡片保留轻微蓝色底色，回到 `translateY=0`，左侧显示强调条；选中且悬停时只轻微加深选中底色。指针光晕跟随鼠标移动，选中时强度减半。边框厚度始终不变，焦点环、强调条和光晕都作为叠层绘制，不挤动正文。

## 应用资源接入

在现有 `App.axaml` 的 `Application.Styles` 中保留 `FluentTheme`，并在其后包含卡片样式。应用程序集名是 `KeepPassword`，URI 不应写成项目目录名 `KeepPassword.App`。

```xml
<Application.Styles>
  <FluentTheme />
  <StyleInclude Source="avares://KeepPassword/Styles/CardItemStyles.axaml" />
</Application.Styles>
```

现有页面中的按钮卡片使用以下主题；`IsSelected` 是当前条目视图已有的属性。

```xml
<Button Theme="{StaticResource CardButtonTheme}"
        Classes="account-card"
        Classes.selected="{Binding IsSelected}"
        HorizontalContentAlignment="Stretch" />
```

## 复用到 ListBox

下面的视图可保存为 `Views/CardListExample.axaml`。它使用现有 `MainViewModel.Entries` 和 `EntryItemView`，由 `ListBox` 负责键盘导航和选择状态。标题、副标题与标签都允许换行，不设置固定内容高度，也不使用省略号截断。副标题使用应用已有的等宽字体资源。

`ListBox` 自身和内置 `ScrollViewer` 都预留至少 12px 空间，避免首尾卡片上浮、阴影和焦点环紧贴裁切边缘；不要再在外面嵌套一个不限高度的 `ScrollViewer`。

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:KeepPassword.App.ViewModels"
             x:Class="KeepPassword.App.Views.CardListExample"
             x:DataType="vm:MainViewModel">
  <UserControl.Styles>
    <Style Selector="ListBox.cards /template/ ScrollViewer#PART_ScrollViewer">
      <Setter Property="Padding" Value="12" />
    </Style>
    <Style Selector="ListBox.cards ListBoxItem">
      <Setter Property="Margin" Value="0,0,0,12" />
    </Style>
  </UserControl.Styles>

  <ListBox Classes="cards"
           ItemsSource="{Binding Entries}"
           ItemContainerTheme="{StaticResource CardItemTheme}"
           SelectionMode="Single"
           Padding="12"
           Background="Transparent"
           BorderThickness="0"
           HorizontalContentAlignment="Stretch"
           ScrollViewer.HorizontalScrollBarVisibility="Disabled"
           ScrollViewer.VerticalScrollBarVisibility="Auto">
    <ListBox.ItemsPanel>
      <ItemsPanelTemplate>
        <VirtualizingStackPanel />
      </ItemsPanelTemplate>
    </ListBox.ItemsPanel>
    <ListBox.ItemTemplate>
      <DataTemplate x:DataType="vm:EntryItemView">
        <Grid ColumnDefinitions="40,12,*">
          <Border Width="40"
                  Height="40"
                  CornerRadius="10"
                  Background="{DynamicResource Kp.SurfaceMuted}"
                  VerticalAlignment="Center">
            <Panel>
              <TextBlock Text="{Binding Initial}"
                         IsVisible="{Binding !HasLogo}"
                         Foreground="{DynamicResource CardAccentBrush}"
                         FontSize="18"
                         FontWeight="SemiBold"
                         HorizontalAlignment="Center"
                         VerticalAlignment="Center" />
              <Image Source="{Binding Logo}"
                     IsVisible="{Binding HasLogo}"
                     Width="24"
                     Height="24"
                     HorizontalAlignment="Center"
                     VerticalAlignment="Center" />
            </Panel>
          </Border>
          <StackPanel Grid.Column="2" Spacing="5">
            <TextBlock Text="{Binding Address}"
                       Foreground="{DynamicResource CardTextBrush}"
                       FontSize="14"
                       FontWeight="SemiBold"
                       TextWrapping="Wrap" />
            <TextBlock Text="{Binding Username}"
                       Foreground="{DynamicResource Kp.TextMuted}"
                       FontFamily="{StaticResource Kp.Font.Mono}"
                       FontSize="12"
                       TextWrapping="Wrap" />
            <ItemsControl ItemsSource="{Binding Tags}">
              <ItemsControl.ItemsPanel>
                <ItemsPanelTemplate>
                  <WrapPanel />
                </ItemsPanelTemplate>
              </ItemsControl.ItemsPanel>
              <ItemsControl.ItemTemplate>
                <DataTemplate x:DataType="x:String">
                  <Border Background="{DynamicResource Kp.AccentSoft}"
                          BorderBrush="{DynamicResource Kp.SelectionBorder}"
                          BorderThickness="1"
                          CornerRadius="6"
                          Padding="6,2"
                          Margin="0,0,6,4">
                    <TextBlock Text="{Binding}"
                               Foreground="{DynamicResource CardAccentBrush}"
                               FontSize="11"
                               TextWrapping="Wrap" />
                  </Border>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
          </StackPanel>
        </Grid>
      </DataTemplate>
    </ListBox.ItemTemplate>
  </ListBox>
</UserControl>
```

配套代码只接收已有 ViewModel，不需要新增命令框架。示例中的选择反馈由 `ListBoxItem` 自动触发；如果需要选择后打开详情，可在宿主中将 `SelectionChanged` 接回现有的条目选择方法。

```csharp
using Avalonia.Controls;
using KeepPassword.App.ViewModels;

namespace KeepPassword.App.Views;

public partial class CardListExample : UserControl
{
    public CardListExample(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
```

上面的 `VirtualizingStackPanel` 是单列虚拟化。标签区域的普通 `WrapPanel` 只负责少量标签换行，不提供虚拟化。真正的二维卡片流需要支持虚拟化的二维面板；当前主页面采用“虚拟化行 + 行内 UniformGrid”的方案，不应直接换成普通 `WrapPanel` 后宣称仍有虚拟化。

## 调参位置

| 参数 | 当前值 | 修改位置 |
| --- | --- | --- |
| 静止与悬停阴影 | `CardShadowRest` / `CardShadowHover` | `CardItemStyles.axaml` 的 Light / Dark 主题资源 |
| 光晕半径 | 140px | `SpotlightBehavior.Radius`，默认值在 `CardSpotlightBehavior.cs` |
| 常规过渡 | 180ms | `CardTransitionDuration` |
| 光晕淡入淡出 | 150ms | `CardSpotlightTransitionDuration` |
| 选中强调条 | 3px，卡片高度约 60% | `PART_SelectionAccent.Width` 和外层 `RowDefinitions="2*,6*,2*"` |
| 悬停堆叠顺序 | 未选中 10，选中 1，静止 0 | 两个主题的 `ZIndex` 状态选择器 |

卡片表面、文字、边框、强调色和光晕颜色都通过主题资源动态读取。视觉颜色使用 `DynamicResource`，光晕行为通过资源观察绑定 `CardSpotlightColor`；切换明暗主题时不必重建卡片。

调整样式时保持以下约束：

- `TransformOperations` 的 `translate` 长度必须带单位，例如 `translate(0px, -2px)`。非零长度省略 `px` 可能在运行时解析失败。
- 需要插值的 `CardShadowRest` 和 `CardShadowHover` 应保留相同的 `BoxShadows` 数量；如果从一层阴影改成两层，两种状态都应保留两层。
- `:selected:pointerover`（按钮对应 `.selected:pointerover`）应比普通悬停状态优先。已选中的卡片维持 `translateY=0`，避免被通用悬停规则再次上浮。
- 行为依赖当前控件模板里的 `Border#PART_Spotlight`。替换模板时保留该元素，并让它跟随卡片圆角裁切、`IsHitTestVisible="False"`。
- 光晕的 `Center`、`GradientOrigin` 和半径都采用绝对坐标；不要改成相对百分比后继续传入像素位置。行为复用每张卡片的 brush，指针移动只更新中心，不创建持续运行的动画循环。
- 主窗口根据动画偏好同步 `no-motion` 类，关闭上浮、光晕和相关过渡。新增窗口也应同步相同偏好，不能只隐藏光晕后仍保留上浮。

液态切换与下拉控件使用渐变、高光、透明度和阴影模拟折射；它们并不是对任意应用内背景做实时 backdrop blur。

## 一键关闭

只关闭某张按钮卡片的光晕，保留边框、阴影和选中状态：

```xml
<Button xmlns="https://github.com/avaloniaui"
        xmlns:views="using:KeepPassword.App.Views"
        Theme="{StaticResource CardButtonTheme}"
        views:SpotlightBehavior.IsEnabled="False"
        Content="关闭指针光晕的卡片" />
```

在容器上添加 `no-motion`，即可关闭其卡片和液态控件的动态效果：

```xml
<Window xmlns="https://github.com/avaloniaui"
        Classes="no-motion">
  <ListBox ItemContainerTheme="{StaticResource CardItemTheme}"
           Padding="12" />
</Window>
```

窗口同步用户动画偏好时，使用同样的类切换即可：

```csharp
if (KeepPassword.App.Services.MotionPreferences.IsEnabled)
    window.Classes.Remove("no-motion");
else
    window.Classes.Add("no-motion");
```
