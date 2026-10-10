using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KeepPassword.Core.Security;

namespace KeepPassword.App.Views;

public sealed class AuditWindow : Window
{
    public AuditWindow(IReadOnlyList<AuditFinding> findings)
    {
        Title = "密码安全审计";
        Width = 660;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var mainPanel = new StackPanel { Spacing = 16 };

        // Header
        var header = new StackPanel { Spacing = 4 };
        var title = new TextBlock
        {
            Text = "密码安全审计报告",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold
        };
        title.Classes.Add("h1");
        var subtitle = new TextBlock
        {
            Text = "自动检测空密码、弱密码及跨站点重复使用的密码风险。",
            FontSize = 12.5
        };
        subtitle.Classes.Add("muted");
        header.Children.Add(title);
        header.Children.Add(subtitle);
        mainPanel.Children.Add(header);

        if (findings.Count == 0)
        {
            var successCard = new Border();
            successCard.Classes.Add("audit-card");

            var successStack = new StackPanel { Spacing = 8 };
            var successTitle = new TextBlock
            {
                Text = "安全状态良好",
                FontSize = 16,
                FontWeight = FontWeight.SemiBold
            };
            successTitle.Classes.Add("success");
            var successDesc = new TextBlock
            {
                Text = "当前保险库中所有条目均未发现空密码、常见弱密码或跨站重复使用的密码风险。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            };
            successDesc.Classes.Add("muted");
            successStack.Children.Add(successTitle);
            successStack.Children.Add(successDesc);
            successCard.Child = successStack;

            mainPanel.Children.Add(successCard);
        }
        else
        {
            var warningCount = new TextBlock
            {
                Text = $"检测到 {findings.Count} 项安全风险，建议尽快更新：",
                FontWeight = FontWeight.SemiBold,
                FontSize = 13
            };
            warningCount.Classes.Add("danger");
            mainPanel.Children.Add(warningCount);

            var list = new StackPanel { Spacing = 10 };
            foreach (var finding in findings)
            {
                var card = new Border();
                card.Classes.Add("audit-card");

                var itemStack = new StackPanel { Spacing = 4 };
                var nameRow = new DockPanel();
                var domainText = string.IsNullOrEmpty(finding.Domain) ? "" : " · " + finding.Domain;
                var nameBlock = new TextBlock
                {
                    Text = finding.Name + domainText,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 14
                };
                nameRow.Children.Add(nameBlock);

                var adviceBlock = new TextBlock
                {
                    Text = finding.Advice,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5
                };
                adviceBlock.Classes.Add("muted");

                itemStack.Children.Add(nameRow);
                itemStack.Children.Add(adviceBlock);
                card.Child = itemStack;
                list.Children.Add(card);
            }

            mainPanel.Children.Add(list);
        }

        Content = new ScrollViewer
        {
            Margin = new Thickness(24),
            Content = mainPanel
        };
    }
}
