using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using KeepPassword.Core.Security;

namespace KeepPassword.App.Views;

public sealed class BreachWindow : Window
{
    public BreachWindow(IReadOnlyList<BreachFinding> findings)
    {
        Title = "公开泄露检查";
        Width = 660;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var mainPanel = new StackPanel { Spacing = 16 };

        // Header
        var header = new StackPanel { Spacing = 8 };
        var title = new TextBlock
        {
            Text = "密码公开泄露检查报告",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold
        };
        title.Classes.Add("h1");
        header.Children.Add(title);

        // Privacy banner
        var privacyBox = new Border();
        privacyBox.Classes.Add("audit-card");
        var privacyText = new TextBlock
        {
            Text = BreachChecker.PrivacyNotice,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        privacyText.Classes.Add("muted");
        privacyBox.Child = privacyText;
        header.Children.Add(privacyBox);

        mainPanel.Children.Add(header);

        if (findings.Count == 0)
        {
            var successCard = new Border();
            successCard.Classes.Add("audit-card");

            var successStack = new StackPanel { Spacing = 8 };
            var successTitle = new TextBlock
            {
                Text = "未在已知泄露库中发现匹配",
                FontSize = 16,
                FontWeight = FontWeight.SemiBold
            };
            successTitle.Classes.Add("success");
            var successDesc = new TextBlock
            {
                Text = "经 k-Anonymity 安全查询，已检查的所有条目密码均未在公开泄露数据中发现。",
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
                Text = $"⚠️ 警报：发现 {findings.Count} 个密码已在公开数据中泄露，建议立即前往更换：",
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
                var domainText = string.IsNullOrEmpty(finding.Domain) ? "" : " · " + finding.Domain;
                var nameBlock = new TextBlock
                {
                    Text = finding.Name + domainText,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 14
                };
                var adviceBlock = new TextBlock
                {
                    Text = finding.Advice,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5
                };
                adviceBlock.Classes.Add("muted");

                itemStack.Children.Add(nameBlock);
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
