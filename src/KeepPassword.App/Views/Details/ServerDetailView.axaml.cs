using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace KeepPassword.App.Views.Details;

public partial class ServerDetailView : UserControl
{
    public ServerDetailView() => InitializeComponent();
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ServerDetailContent content)
            content.Owner.PopulateConnectionFields(this.FindControl<StackPanel>("FieldHost")!, content.Source);
    }
}
