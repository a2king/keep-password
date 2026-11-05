using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace KeepPassword.App.Tests;

public class CardInteractionTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HoverAndSelection_KeepCardSizeAndInternalContentPosition(bool listItem)
    {
        using var fixture = await CardFixture.CreateAsync(listItem);
        var initialBounds = fixture.Card.Bounds;
        var initialRootBounds = fixture.Root.Bounds;
        var initialContentBounds = fixture.Content.Bounds;
        var initialContentPosition = fixture.Content.TranslatePoint(default, fixture.Root);
        var restBackground = BrushColor(fixture.Surface.Background);

        fixture.MovePointer(new Point(80, 35));
        await ObserveFramesAsync(fixture.Window, 240);

        Assert.Equal(restBackground, BrushColor(fixture.Surface.Background));
        Assert.Equal(initialBounds, fixture.Card.Bounds);
        Assert.Equal(initialRootBounds, fixture.Root.Bounds);
        Assert.Equal(initialContentBounds, fixture.Content.Bounds);
        Assert.Equal(initialContentPosition, fixture.Content.TranslatePoint(default, fixture.Root));

        fixture.Select();
        await ObserveFramesAsync(fixture.Window, 240);

        // Hover may lift the whole presentation; selected cards must
        // settle at zero without changing the layout or moving content within it.
        Assert.Equal(0, fixture.Root.RenderTransform?.Value.M32 ?? 0, precision: 2);
        Assert.Equal(initialBounds, fixture.Card.Bounds);
        Assert.Equal(initialRootBounds, fixture.Root.Bounds);
        Assert.Equal(initialContentBounds, fixture.Content.Bounds);
        Assert.Equal(initialContentPosition, fixture.Content.TranslatePoint(default, fixture.Root));
        Assert.Equal(1, fixture.Accent.Opacity, precision: 2);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Spotlight_FollowsPointerHalvesForSelectionAndStopsWithReducedMotion(bool listItem)
    {
        using var fixture = await CardFixture.CreateAsync(listItem);
        fixture.MovePointer(new Point(45, 28));
        await ObserveFramesAsync(fixture.Window, 240);
        var brush = Assert.IsType<RadialGradientBrush>(fixture.Spotlight.Background);
        Assert.Equal(1, fixture.Spotlight.Opacity, precision: 2);

        var pointerInWindow = fixture.MovePointer(new Point(215, 65));
        var spotlightOrigin = Assert.IsType<Point>(fixture.Spotlight.TranslatePoint(default, fixture.Window));
        Assert.Same(brush, fixture.Spotlight.Background);
        Assert.Equal(RelativeUnit.Absolute, brush.Center.Unit);
        Assert.Equal(pointerInWindow.X - spotlightOrigin.X, brush.Center.Point.X, precision: 1);
        Assert.Equal(pointerInWindow.Y - spotlightOrigin.Y, brush.Center.Point.Y, precision: 1);
        Assert.Equal(brush.Center, brush.GradientOrigin);

        fixture.Select();
        await ObserveFramesAsync(fixture.Window, 240);
        Assert.Equal(0.5, fixture.Spotlight.Opacity, precision: 2);

        fixture.Window.Classes.Add("no-motion");
        fixture.Window.UpdateLayout();

        // Opting out takes effect while a pointer is already over the card.
        Assert.Equal(0, fixture.Spotlight.Opacity, precision: 2);
        Assert.False(fixture.Spotlight.IsVisible);
        Assert.Equal(0, fixture.Root.RenderTransform?.Value.M32 ?? 0, precision: 2);
        fixture.MovePointer(new Point(120, 45));
        Assert.Equal(0, fixture.Spotlight.Opacity, precision: 2);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThemeChange_UpdatesSurfaceAndSpotlightWithoutRebuildingCard(bool listItem)
    {
        using var fixture = await CardFixture.CreateAsync(listItem);
        var originalRoot = fixture.Root;
        var originalContentPosition = fixture.Content.TranslatePoint(default, fixture.Root);
        var lightBackground = BrushColor(fixture.Surface.Background);
        var brush = Assert.IsType<RadialGradientBrush>(fixture.Spotlight.Background);
        var lightSpotlight = brush.GradientStops[0].Color;

        fixture.Window.RequestedThemeVariant = ThemeVariant.Dark;
        await ObserveFramesAsync(fixture.Window, 240);

        Assert.Same(originalRoot, fixture.Root);
        Assert.Equal(originalContentPosition, fixture.Content.TranslatePoint(default, fixture.Root));
        Assert.NotEqual(lightBackground, BrushColor(fixture.Surface.Background));
        Assert.Equal(Color.Parse("#29292C"), BrushColor(fixture.Surface.Background));
        Assert.NotEqual(lightSpotlight, brush.GradientStops[0].Color);
        Assert.True(fixture.Card.TryFindResource("CardSpotlightColor", fixture.Card.ActualThemeVariant, out var darkSpotlight));
        Assert.Equal(Assert.IsType<Color>(darkSpotlight), brush.GradientStops[0].Color);

        fixture.Window.RequestedThemeVariant = ThemeVariant.Light;
        await ObserveFramesAsync(fixture.Window, 240);
        Assert.Equal(lightBackground, BrushColor(fixture.Surface.Background));
        Assert.Equal(lightSpotlight, brush.GradientStops[0].Color);
    }

    [AvaloniaFact]
    public async Task CardItemTheme_PreservesVirtualizationAndSelectionFor240Entries()
    {
        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(0, 240).ToArray(),
            ItemContainerTheme = FindTheme("CardItemTheme"),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<int>((value, _) => new TextBlock
            {
                Text = $"Entry {value:D3}", Height = 52, VerticalAlignment = VerticalAlignment.Center
            })
        };
        var window = new Window { Width = 560, Height = 380, Content = list };
        try
        {
            window.Show();
            await ObserveFramesAsync(window, 80);
            Assert.InRange(RealizedItems(list), 1, 40);

            list.ScrollIntoView(210);
            list.SelectedIndex = 210;
            await ObserveFramesAsync(window, 240);

            var selected = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(210));
            Assert.True(selected.IsSelected);
            Assert.NotNull(Part<Border>(selected, "PART_Spotlight"));
            Assert.Equal(1, Part<Border>(selected, "PART_SelectionAccent").Opacity, precision: 2);
            Assert.InRange(RealizedItems(list), 1, 40);
            Assert.Equal(240, list.ItemCount);
        }
        finally
        {
            window.Close();
        }
    }

    private static int RealizedItems(Control root) => root.GetVisualDescendants().OfType<ListBoxItem>().Count();

    private static Color BrushColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static ControlTheme FindTheme(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var resource), $"Missing production theme {key}.");
        return Assert.IsType<ControlTheme>(resource);
    }

    private static T Part<T>(Control card, string name) where T : Control =>
        Assert.Single(card.GetVisualDescendants().OfType<T>().Where(control => control.Name == name));

    private static async Task ObserveFramesAsync(Window window, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            await Task.Delay(15);
            window.UpdateLayout();
        } while (clock.ElapsedMilliseconds < milliseconds);
    }

    private sealed class CardFixture : IDisposable
    {
        private CardFixture(bool listItem)
        {
            Card = listItem ? new ListBoxItem() : new Button();
            Card.Theme = FindTheme(listItem ? "CardItemTheme" : "CardButtonTheme");
            Card.Width = 300;
            Card.Height = 100;
            Card.HorizontalAlignment = HorizontalAlignment.Left;
            Card.VerticalAlignment = VerticalAlignment.Top;
            Card.Margin = new Thickness(24);
            Card.Content = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = "Production database", FontSize = 14 },
                    new TextBlock { Text = "username · 192.0.2.10", FontSize = 12 }
                }
            };
            Window = new Window
            {
                Width = 600, Height = 240, RequestedThemeVariant = ThemeVariant.Light, Content = Card
            };
            Window.Show();
            Window.UpdateLayout();
            Window.MouseMove(new Point(560, 200));
        }

        public Window Window { get; }
        public ContentControl Card { get; }
        public Grid Root => Part<Grid>(Card, "PART_CardRoot");
        public Border Surface => Part<Border>(Card, "PART_CardSurface");
        public Border Spotlight => Part<Border>(Card, "PART_Spotlight");
        public Border Accent => Part<Border>(Card, "PART_SelectionAccent");
        public ContentPresenter Content => Part<ContentPresenter>(Card, "PART_ContentPresenter");

        public static async Task<CardFixture> CreateAsync(bool listItem)
        {
            var fixture = new CardFixture(listItem);
            await ObserveFramesAsync(fixture.Window, 40);
            return fixture;
        }

        public Point MovePointer(Point point)
        {
            var windowPoint = Assert.IsType<Point>(Card.TranslatePoint(point, Window));
            Window.MouseMove(windowPoint);
            Window.UpdateLayout();
            return windowPoint;
        }

        public void Select()
        {
            if (Card is ListBoxItem item)
            {
                item.IsSelected = true;
            }
            else
            {
                Card.Classes.Add("selected");
            }
        }

        public void Dispose() => Window.Close();
    }
}
