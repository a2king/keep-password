using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace KeepPassword.App.Views.Details;

public partial class LoginDetailView : UserControl
{
    public LoginDetailView() => InitializeComponent();
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LoginDetailContent content)
            content.Owner.PopulateConnectionFields(this.FindControl<StackPanel>("FieldHost")!, content.Source);
    }
}
