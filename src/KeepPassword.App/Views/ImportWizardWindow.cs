using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Import;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public sealed class ImportWizardWindow : Window
{
    private static readonly HashSet<string> SensitiveTargets =
    [
        CsvMapper.FieldPrefix + FieldKeys.Password,
        CsvMapper.FieldPrefix + FieldKeys.Totp,
        CsvMapper.FieldPrefix + FieldKeys.ApiKey,
        CsvMapper.FieldPrefix + FieldKeys.Secret,
        CsvMapper.FieldPrefix + FieldKeys.Token
    ];

    private readonly CsvTable _table;
    private readonly ComboBox _kindBox;
    private readonly Grid _mappingGrid = new() { ColumnDefinitions = new ColumnDefinitions("180,*,260") };
    private readonly List<ComboBox> _targetBoxes = [];
    private readonly List<TextBlock> _samples = [];
    private readonly List<ImportRowView> _rows;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _mappingError = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _importButton;
    private ImportPlan? _plan;
    private bool _suspended;

    public ImportWizardWindow(CsvTable table, string fileName)
    {
        _table = table;
        _rows = Enumerable.Range(0, table.Rows.Count).Select(index => new ImportRowView(index, Recompute)).ToList();
        Title = "导入 CSV";
        Width = 900;
        Height = 760;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var title = new TextBlock { Text = "导入 CSV", FontSize = 20, FontWeight = FontWeight.SemiBold };
        title.Classes.Add("h1");
        var subtitle = Muted($"{fileName} · {table.Headers.Count} 列 · {table.Rows.Count} 行数据。按 1Password 的方式：先选条目类型，再把每一列映射到字段，最后确认要导入的行。");

        _kindBox = new ComboBox
        {
            ItemsSource = CsvMapper.ImportableKinds.Select(VaultItemKinds.DisplayName).ToList(),
            SelectedIndex = 0,
            Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _kindBox.SelectionChanged += (_, _) => RebuildMapping();

        _mappingError.Foreground = Brush("Kp.Danger");
        _importButton = new Button { Content = "导入" };
        _importButton.Classes.Add("accent");
        _importButton.Click += (_, _) =>
        {
            if (_plan is { Valid: > 0 })
            {
                Close(_plan.Entries);
            }
        };
        var cancel = new Button { Content = "取消" };
        cancel.Classes.Add("soft");
        cancel.Click += (_, _) => Close(null);

        var rowList = new ListBox
        {
            ItemsSource = _rows,
            ItemTemplate = new FuncDataTemplate<ImportRowView>((_, _) => RowTemplate()),
            Height = 240
        };

        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(Section("1. 条目类型", "整份文件导入为同一种类型。SSH 密钥只能在条目里手动粘贴，不支持从文件导入。"));
        form.Children.Add(_kindBox);
        form.Children.Add(Section("2. 列映射", "已按列名自动匹配，可逐列调整；不需要的列选「忽略此列」。敏感列的示例值已隐藏。"));
        form.Children.Add(HeaderRow());
        form.Children.Add(_mappingGrid);
        form.Children.Add(_mappingError);
        form.Children.Add(Section("3. 确认行", "取消勾选即可忽略该行；红色提示的行不会导入。"));
        form.Children.Add(rowList);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_importButton);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_summary);

        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(title);
        header.Children.Add(subtitle);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = form });
        Content = root;

        RebuildMapping();
    }

    private VaultItemKind Kind => CsvMapper.ImportableKinds[Math.Max(0, _kindBox.SelectedIndex)];

    private void RebuildMapping()
    {
        _suspended = true;
        _mappingGrid.Children.Clear();
        _mappingGrid.RowDefinitions.Clear();
        _targetBoxes.Clear();
        _samples.Clear();
        var targets = CsvMapper.Targets(Kind);
        var guesses = CsvMapper.GuessAll(_table.Headers, Kind);
        for (var column = 0; column < _table.Headers.Count; column++)
        {
            _mappingGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var name = new TextBlock
            {
                Text = _table.Headers[column].Length == 0 ? $"（第 {column + 1} 列）" : _table.Headers[column],
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 8, 4)
            };
            var sample = Muted("");
            sample.VerticalAlignment = VerticalAlignment.Center;
            sample.TextTrimming = TextTrimming.CharacterEllipsis;
            sample.Margin = new Thickness(0, 4, 8, 4);
            var box = new ComboBox
            {
                ItemsSource = targets,
                ItemTemplate = new FuncDataTemplate<ImportTarget>((target, _) => new TextBlock { Text = target?.Label ?? "" }),
                SelectedItem = targets.First(target => target.Code == guesses[column]),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 4)
            };
            box.SelectionChanged += (_, _) => Recompute();
            Grid.SetRow(name, column);
            Grid.SetRow(sample, column);
            Grid.SetColumn(sample, 1);
            Grid.SetRow(box, column);
            Grid.SetColumn(box, 2);
            _mappingGrid.Children.Add(name);
            _mappingGrid.Children.Add(sample);
            _mappingGrid.Children.Add(box);
            _targetBoxes.Add(box);
            _samples.Add(sample);
        }

        _suspended = false;
        Recompute();
    }

    private void Recompute()
    {
        if (_suspended)
        {
            return;
        }

        var targets = _targetBoxes.Select(box => (box.SelectedItem as ImportTarget)?.Code ?? CsvMapper.Ignore).ToList();
        for (var column = 0; column < _samples.Count; column++)
        {
            _samples[column].Text = Sample(column, SensitiveTargets.Contains(targets[column]));
        }

        var error = CsvMapper.ValidateMapping(_table.Headers, targets);
        _mappingError.Text = error ?? "";
        _mappingError.IsVisible = error is not null;
        if (error is not null)
        {
            _plan = null;
            foreach (var row in _rows)
            {
                row.Update(null);
            }

            _summary.Text = "请先修正列映射。";
            _importButton.IsEnabled = false;
            return;
        }

        var ignored = _rows.Where(row => !row.Include).Select(row => row.Index).ToHashSet();
        _plan = CsvMapper.Build(_table, Kind, targets, ignored);
        var results = _plan.Rows.ToDictionary(row => row.RowNumber - 2);
        foreach (var row in _rows)
        {
            row.Update(results.GetValueOrDefault(row.Index));
        }

        _summary.Text = $"将创建 {_plan.Valid} 条 · 忽略 {_plan.Ignored} 行 · 出错 {_plan.Invalid} 行（出错行不会导入）";
        _importButton.Content = $"导入 {_plan.Valid} 条";
        _importButton.IsEnabled = _plan.Valid > 0;
    }

    private string Sample(int column, bool sensitive)
    {
        var value = _table.Rows.Select(row => column < row.Count ? row[column] : "").FirstOrDefault(text => text.Trim().Length > 0) ?? "";
        if (value.Length == 0)
        {
            return "（空）";
        }

        if (sensitive)
        {
            return "••••••";
        }

        var line = value.ReplaceLineEndings(" ");
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    private static Control RowTemplate()
    {
        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
        check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(ImportRowView.Include)) { Mode = BindingMode.TwoWay });
        var line = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        line.Bind(TextBlock.TextProperty, new Binding(nameof(ImportRowView.Title)));
        var error = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 0, 0) };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(ImportRowView.Error)));
        error.Bind(ForegroundProperty, new Binding(nameof(ImportRowView.ErrorBrush)));
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(line, 1);
        Grid.SetColumn(error, 2);
        panel.Children.Add(check);
        panel.Children.Add(line);
        panel.Children.Add(error);
        return panel;
    }

    private static Control HeaderRow()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*,260") };
        var column = Muted("CSV 列名");
        var sample = Muted("示例值");
        var target = Muted("映射到");
        Grid.SetColumn(sample, 1);
        Grid.SetColumn(target, 2);
        grid.Children.Add(column);
        grid.Children.Add(sample);
        grid.Children.Add(target);
        return grid;
    }

    private static Control Section(string title, string hint)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14 });
        panel.Children.Add(Muted(hint));
        return panel;
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("muted");
        return block;
    }

    private static IBrush? Brush(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true ? value as IBrush : null;

    private sealed class ImportRowView(int index, Action changed) : ViewModelBase
    {
        private bool _include = true;
        private string _title = "";
        private string _error = "";

        public int Index { get; } = index;

        public bool Include
        {
            get => _include;
            set
            {
                if (Set(ref _include, value))
                {
                    changed();
                }
            }
        }

        public string Title
        {
            get => _title;
            private set => Set(ref _title, value);
        }

        public string Error
        {
            get => _error;
            private set => Set(ref _error, value);
        }

        public IBrush? ErrorBrush => _failed ? Brush("Kp.Danger") : Brush("Kp.TextMuted");

        private bool _failed;

        public void Update(ImportRowResult? result)
        {
            var label = $"第 {Index + 2} 行";
            _failed = result?.Error is not null;
            if (result?.Entry is { } entry)
            {
                var detail = new[] { ItemTemplates.Summary(entry), entry.Username }.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
                Title = detail is null ? $"{label}  {entry.Name}" : $"{label}  {entry.Name} · {detail}";
                Error = "可导入";
            }
            else
            {
                Title = label;
                Error = !Include ? "已忽略" : result?.Error ?? "";
            }

            OnPropertyChanged(nameof(ErrorBrush));
        }
    }
}
