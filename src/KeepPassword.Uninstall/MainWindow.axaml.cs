using Avalonia.Controls;
using Avalonia.Interactivity;
using KeepPassword.Core.Install;

namespace KeepPassword.Uninstall;

public partial class MainWindow : Window
{
    private readonly string _installRoot;

    public MainWindow()
    {
        InitializeComponent();
        _installRoot = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        PathText.Text = "安装目录：" + _installRoot;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnUninstall(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        UninstallButton.IsEnabled = false;
        var deleteCache = DeleteCacheBox.IsChecked == true;
        try
        {
            await Task.Run(() => InstallOperations.Uninstall(_installRoot, deleteCache));
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.IsVisible = true;
            UninstallButton.IsEnabled = true;
        }
    }
}
