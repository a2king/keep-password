using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace KeepPassword.App.Views.Details;

public partial class DatabaseDetailView : UserControl
{
    public DatabaseDetailView() => InitializeComponent();
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is DatabaseDetailContent content)
            content.Owner.PopulateConnectionFields(this.FindControl<StackPanel>("FieldHost")!, content.Source);
    }
}
