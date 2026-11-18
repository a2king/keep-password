using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using KeepPassword.App.Controls.Details;

namespace KeepPassword.App.Tests;

/// <summary>详情底栏的布局约定：删除条目在最左；撤销修改、保存修改相邻（间隔 5px）靠右；三个按钮文字水平、垂直居中。</summary>
internal static class SaveBarLayout
{
    public static void AssertDeleteLeftUndoSaveRight(SaveBar bar)
    {
        var buttons = bar.GetVisualDescendants().OfType<Button>().ToDictionary(button => button.Name!);
        var delete = buttons["PART_Delete"];
        var cancel = buttons["PART_Cancel"];
        var save = buttons["PART_SaveButton"];
        Assert.Equal("删除条目", delete.Content);
        Assert.Equal("撤销修改", cancel.Content);
        Assert.Equal("保存修改", save.Content);

        double Left(Control control) => control.TranslatePoint(default, bar)!.Value.X;
        double Right(Control control) => Left(control) + control.Bounds.Width;
        Assert.True(Right(delete) < bar.Bounds.Width / 2, "删除条目应在左侧");
        Assert.True(Left(cancel) > bar.Bounds.Width / 2, "撤销修改应在右侧");
        Assert.True(Left(cancel) < Left(save), "撤销修改在保存修改左边");
        Assert.Equal(5, Left(save) - Right(cancel), precision: 1);
        Assert.Equal(delete.Bounds.Height, save.Bounds.Height, precision: 1);
        Assert.Equal(cancel.Bounds.Height, save.Bounds.Height, precision: 1);

        foreach (var button in new[] { delete, cancel, save })
        {
            var text = button.GetVisualDescendants().OfType<TextBlock>().Single();
            var origin = text.TranslatePoint(default, button)!.Value;
            Assert.Equal(button.Bounds.Width / 2, origin.X + text.Bounds.Width / 2, precision: 1);
            Assert.Equal(button.Bounds.Height / 2, origin.Y + text.Bounds.Height / 2, precision: 1);
            Assert.True(text.Bounds.Width < button.Bounds.Width - 4, $"{button.Content} 的文字不应撑满按钮");
        }
    }
}
