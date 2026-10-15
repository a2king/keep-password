using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;
using KeepPassword.App.Views;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Tests;

public class DetailDrawerTests
{
    [AvaloniaFact]
    public async Task EntryAndCloseButtons_SlideFixedWidthDrawerInBothDirections()
    {
        using var fixture = await DrawerFixture.CreateAsync();
        var listWidthBefore = fixture.List.Bounds.Width;

        fixture.ClickEntry(fixture.First.Id);
        var opening = await fixture.IntermediateFrameAsync(opening: true);

        Assert.InRange(opening, 0.01, fixture.Panel.Width - 0.01);
        Assert.Equal(480, fixture.Host.Bounds.Width, precision: 1);
        Assert.Equal(480, fixture.Panel.Bounds.Width, precision: 1);
        Assert.True(fixture.Host.ClipToBounds);
        await fixture.WaitUntilAsync(() => fixture.IsOpen);
        Assert.True(fixture.List.Bounds.Width < listWidthBefore);
        Assert.True(fixture.Window.View!.HasDetail);

        fixture.ClickClose();
        var closing = await fixture.IntermediateFrameAsync(opening: false);

        Assert.InRange(closing, 0.01, fixture.Panel.Width - 0.01);
        Assert.True(fixture.Window.View.HasDetail, "Keep the current fields until closing has finished.");
        Assert.Equal(480, fixture.Host.Bounds.Width, precision: 1);
        Assert.Equal(480, fixture.Panel.Bounds.Width, precision: 1);
        await fixture.WaitUntilAsync(() => fixture.IsClosed);
        Assert.False(fixture.Window.View.HasDetail);
        Assert.Equal(480, fixture.Host.Width);
        Assert.Equal(listWidthBefore, fixture.List.Bounds.Width, precision: 1);
    }

    [AvaloniaFact]
    public async Task ManyEntriesAndTags_ReflowOnlyOncePerOpenAndClose()
    {
        using var fixture = await DrawerFixture.CreateAsync(entryCount: 197, tagCount: 17);
        Assert.Equal(197, fixture.Window.View!.Entries.Count);
        Assert.Equal(18, fixture.Window.View.TagFilters.Count); // Includes the all-tags filter.

        var opening = await fixture.ObserveReflowAsync(
            () => fixture.ClickEntry(fixture.First.Id), () => fixture.IsOpen);

        Assert.Equal(1, opening.RowRebuilds);
        Assert.Equal(1, CountChanges(opening.TagHeights));
        Assert.Equal(1, CountChanges(opening.ListWidths));
        Assert.True(opening.TagHeights[^1] > opening.TagHeights[0]);
        Assert.All(opening.DrawerWidths, width => Assert.Equal(480, width, precision: 1));

        var closing = await fixture.ObserveReflowAsync(fixture.ClickClose, () => fixture.IsClosed);

        Assert.Equal(1, closing.RowRebuilds);
        Assert.Equal(1, CountChanges(closing.TagHeights));
        Assert.Equal(1, CountChanges(closing.ListWidths));
        Assert.True(closing.TagHeights[^1] < closing.TagHeights[0]);
        Assert.All(closing.DrawerWidths, width => Assert.Equal(480, width, precision: 1));
    }

    [AvaloniaFact]
    public async Task DrawerReflow_KeepsScrolledEntriesAtTheSameVisiblePosition()
    {
        using var fixture = await DrawerFixture.CreateAsync(entryCount: 197, tagCount: 17);
        fixture.List.Offset = new Vector(0, fixture.List.Extent.Height * 0.43);
        await fixture.ObserveFramesAsync(TimeSpan.FromMilliseconds(80));
        var beforeOpening = fixture.FirstVisibleEntry();
        Assert.NotEqual(fixture.First.Id, beforeOpening.Id);
        Assert.True(fixture.List.Offset.Y > 0);

        fixture.ClickEntry(beforeOpening.Id);
        await fixture.WaitUntilAsync(() => fixture.IsOpen);

        fixture.AssertEntryPosition(beforeOpening.Id, beforeOpening.Top);
        var beforeClosing = fixture.FirstVisibleEntry();
        fixture.ClickClose();
        await fixture.WaitUntilAsync(() => fixture.IsClosed);

        // Closing may put preceding entries in the same wider row; the original entry
        // must still be visible at its previous vertical position, not a different record.
        fixture.AssertEntryPosition(beforeClosing.Id, beforeClosing.Top);
    }

    [AvaloniaFact]
    public async Task SelectingAnotherEntryWhileClosing_ReversesWithoutClearingTheNewEntry()
    {
        using var fixture = await DrawerFixture.CreateAsync();
        fixture.ClickEntry(fixture.First.Id);
        await fixture.WaitUntilAsync(() => fixture.IsOpen);
        fixture.ClickClose();
        await fixture.IntermediateFrameAsync(opening: false);
        var positionBeforeReversal = fixture.DrawerOffset;

        fixture.ClickEntry(fixture.Second.Id);

        Assert.InRange(fixture.DrawerOffset, positionBeforeReversal - 0.01, positionBeforeReversal + 0.01);
        Assert.Equal(fixture.Second.Name, fixture.Window.View!.Name);
        await fixture.WaitUntilAsync(() => fixture.IsOpen);
        // Give the superseded close continuation time to run as well.
        await fixture.ObserveFramesAsync(TimeSpan.FromMilliseconds(350));
        Assert.True(fixture.Host.IsVisible);
        Assert.True(fixture.Window.View.HasDetail);
        Assert.Equal(fixture.Second.Name, fixture.Window.View.Name);
        Assert.Equal("second-test-password", Assert.Single(fixture.Window.View.Accounts).PasswordField.Value);
    }

    [AvaloniaFact]
    public async Task SwitchingOpenEntries_KeepsDetailAndListStationaryIncludingRapidReselection()
    {
        using var fixture = await DrawerFixture.CreateAsync();
        fixture.ClickEntry(fixture.First.Id);
        await fixture.WaitUntilAsync(() => fixture.IsOpen);
        var initial = fixture.CaptureDetailFrame();
        var frames = new List<DetailFrame>();
        var rowRebuilds = 0;
        var view = fixture.Window.View!;
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainViewModel.EntryRows))
            {
                rowRebuilds++;
            }
        }

        view.PropertyChanged += OnChanged;
        try
        {
            fixture.ClickEntry(fixture.Second.Id);
            Assert.Equal(fixture.Second.Name, view.Name);
            Assert.Equal("second-test-password", Assert.Single(view.Accounts).PasswordField.Value);
            frames.Add(fixture.CaptureDetailFrame());
            await fixture.ObserveFramesAsync(TimeSpan.FromMilliseconds(45),
                () => frames.Add(fixture.CaptureDetailFrame()));

            // Reselect before the content transition has finished. Cancellation must
            // not move the whole form, nor let the previous selection win later.
            fixture.ClickEntry(fixture.First.Id);
            frames.Add(fixture.CaptureDetailFrame());
            await fixture.ObserveFramesAsync(TimeSpan.FromMilliseconds(350),
                () => frames.Add(fixture.CaptureDetailFrame()));
        }
        finally
        {
            view.PropertyChanged -= OnChanged;
        }

        Assert.All(frames, frame =>
        {
            // Window-relative coordinates include every ancestor transform. Merely
            // checking Bounds or a particular RenderTransform type misses the shake.
            Assert.Equal(initial.ContentPosition.X, frame.ContentPosition.X, precision: 2);
            Assert.Equal(initial.ContentPosition.Y, frame.ContentPosition.Y, precision: 2);
            Assert.Equal(initial.ContentBounds, frame.ContentBounds);
            Assert.Equal(initial.HostPosition, frame.HostPosition);
            Assert.Equal(initial.HostBounds, frame.HostBounds);
            Assert.Equal(initial.DrawerOffset, frame.DrawerOffset, precision: 2);
            Assert.Equal(initial.ListWidth, frame.ListWidth, precision: 2);
            Assert.Equal(initial.TagHeight, frame.TagHeight, precision: 2);
        });
        Assert.Equal(0, rowRebuilds);
        Assert.True(fixture.IsOpen);
        Assert.True(view.HasDetail);
        Assert.Equal(fixture.First.Name, view.Name);
        Assert.Equal("first-test-password", Assert.Single(view.Accounts).PasswordField.Value);
    }

    [AvaloniaTheory]
    [InlineData(VaultItemKind.Login)]
    [InlineData(VaultItemKind.Cluster)]
    public async Task SwitchingShortAndLongTitles_KeepsScrollerAndLoadedEditorCardsStationary(VaultItemKind kind)
    {
        using var fixture = await DrawerFixture.CreateAsync(customizeEntries: (first, second) =>
        {
            first.Kind = second.Kind = kind;
            first.Name = "Short title";
            second.Name = "Long entry title for the production environment that would otherwise wrap across several lines in the detail header";
            if (kind == VaultItemKind.Cluster)
            {
                first.Nodes = [new ClusterNode { Name = "Test node", Host = "192.0.2.10", Port = "22" }];
                second.Nodes = [new ClusterNode { Name = "Test node", Host = "192.0.2.11", Port = "22" }];
            }
        });
        fixture.ClickEntry(fixture.First.Id);
        await fixture.WaitUntilAsync(() => fixture.IsOpen);
        var initial = fixture.CaptureEditorFrame();
        Assert.Equal(kind == VaultItemKind.Cluster, initial.NodePosition.HasValue);
        var frames = new List<EditorFrame>();

        foreach (var entry in new[] { fixture.Second, fixture.First })
        {
            fixture.ClickEntry(entry.Id);
            fixture.Window.UpdateLayout();
            Assert.Equal(entry.Name, fixture.Title.Text);
            frames.Add(fixture.CaptureEditorFrame());
            await fixture.ObserveFramesAsync(TimeSpan.FromMilliseconds(250),
                () => frames.Add(fixture.CaptureEditorFrame()));
        }

        Assert.All(frames, frame =>
        {
            // A longer heading must not push the scrolling form down. The actual
            // editor cards must also stay put when their replacement views attach.
            Assert.Equal(initial.ScrollerPosition, frame.ScrollerPosition);
            Assert.Equal(initial.ScrollerBounds, frame.ScrollerBounds);
            Assert.Equal(initial.TitleHeight, frame.TitleHeight, precision: 2);
            Assert.Equal(initial.AccountPosition, frame.AccountPosition);
            Assert.Equal(initial.AccountBounds, frame.AccountBounds);
            Assert.Equal(initial.NodePosition, frame.NodePosition);
            Assert.Equal(initial.NodeBounds, frame.NodeBounds);
        });
        Assert.Equal(fixture.First.Name, fixture.Window.View!.Name);
        Assert.Equal("first-test-password", Assert.Single(fixture.Window.View.Accounts).PasswordField.Value);
    }

    [AvaloniaFact]
    public async Task ClosingDrawer_InterceptsPointerHitsInItsRemainingVisibleArea()
    {
        using var fixture = await DrawerFixture.CreateAsync();
        fixture.ClickEntry(fixture.First.Id);
        await fixture.WaitUntilAsync(() => fixture.IsOpen);
        fixture.ClickClose();
        await fixture.IntermediateFrameAsync(opening: false);

        var visibleWidth = fixture.Host.Width - fixture.DrawerOffset;
        var localPoint = new Point(Math.Min(2, visibleWidth / 2), 200);
        var windowPoint = Assert.IsType<Point>(fixture.Host.TranslatePoint(localPoint, fixture.Window));
        Assert.True(new Rect(fixture.Window.ClientSize).Contains(windowPoint),
            "Hit test must target the part of the closing drawer still inside the window.");

        var hit = fixture.Window.InputHitTest(windowPoint);

        Assert.Same(fixture.Host, hit);
        await fixture.WaitUntilAsync(() => fixture.IsClosed);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LockDuringDrawerMotion_HidesAndClearsImmediately(bool whileOpening)
    {
        using var fixture = await DrawerFixture.CreateAsync();
        fixture.ClickEntry(fixture.First.Id);
        if (!whileOpening)
        {
            await fixture.WaitUntilAsync(() => fixture.IsOpen);
            fixture.ClickClose();
        }
        await fixture.IntermediateFrameAsync(opening: whileOpening);
        var previousView = fixture.Window.View!;

        fixture.Window.ShowUnlockScreen();

        // These assertions are synchronous: secrecy must not depend on waiting for an animation.
        Assert.False(fixture.VaultRoot.IsVisible);
        Assert.False(fixture.Host.IsVisible);
        Assert.Equal(480, fixture.Host.Width);
        Assert.Equal(480, fixture.DrawerOffset);
        Assert.Null(fixture.Window.View);
        Assert.False(previousView.HasDetail);
        Assert.Empty(previousView.Accounts);
        await fixture.ObserveFramesAsync(TimeSpan.FromMilliseconds(350));
        Assert.False(fixture.Host.IsVisible);
        Assert.Equal(480, fixture.Host.Width);
        Assert.Equal(480, fixture.DrawerOffset);
        Assert.Null(fixture.Window.View);
    }

    private static int CountChanges(IReadOnlyList<double> values) =>
        values.Zip(values.Skip(1)).Count(pair => Math.Abs(pair.First - pair.Second) > 0.1);

    private sealed record ReflowObservation(
        int RowRebuilds, IReadOnlyList<double> TagHeights,
        IReadOnlyList<double> ListWidths, IReadOnlyList<double> DrawerWidths);

    private sealed record DetailFrame(
        Point ContentPosition, Rect ContentBounds, Point HostPosition, Rect HostBounds,
        double DrawerOffset, double ListWidth, double TagHeight);

    private sealed record EditorFrame(
        Point ScrollerPosition, Rect ScrollerBounds, double TitleHeight,
        Point AccountPosition, Rect AccountBounds, Point? NodePosition, Rect? NodeBounds);

    private sealed class DrawerFixture : IDisposable
    {
        private readonly string _directory;
        private readonly VaultSession _session;

        private DrawerFixture(int entryCount, int tagCount, Action<VaultEntry, VaultEntry>? customizeEntries)
        {
            _directory = Path.Combine(Path.GetTempPath(), "keep-password-ui-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            var store = new VaultStore(KdfProfile.Fast);
            var location = new VaultLocation(Path.Combine(_directory, "settings.json"), _directory);
            _session = store.CreateSession(location.VaultFile, "UI test account", "test master password", "test short key");
            First = new VaultEntry
            {
                Name = "First test entry",
                Accounts = [new VaultAccount { Username = "first-user", Password = "first-test-password" }]
            };
            Second = new VaultEntry
            {
                Name = "Second test entry",
                Accounts = [new VaultAccount { Username = "second-user", Password = "second-test-password" }]
            };
            customizeEntries?.Invoke(First, Second);
            if (tagCount > 0)
            {
                First.Tags = ["Tag 00"];
                Second.Tags = ["Tag 01"];
            }
            _session.Upsert(First);
            _session.Upsert(Second);
            for (var index = 2; index < entryCount; index++)
            {
                _session.Upsert(new VaultEntry
                {
                    Name = $"Test entry {index:D3}",
                    Tags = tagCount > 0 ? [$"Tag {index % tagCount:D2}"] : [],
                    Accounts = [new VaultAccount { Username = $"user-{index:D3}", Password = "test-only-password" }]
                });
            }
            _session.Save();
            Window = new MainWindow(store, location, platformAutofillSupported: false)
            {
                Width = 1360,
                Height = 800
            };
            Window.Show();
            Window.ShowVault(_session);
            Host = Window.FindControl<Grid>("DetailHost")!;
            Panel = Window.FindControl<Border>("DetailPanel")!;
            Content = Window.FindControl<Grid>("DetailContent")!;
            Scroller = Window.FindControl<ScrollViewer>("DetailScroller")!;
            Title = Window.FindControl<TextBlock>("DetailTitle")!;
            List = Window.FindControl<ScrollViewer>("AccountScroller")!;
            ListPane = Window.FindControl<Grid>("ItemsListPane")!;
            TagFilters = Window.FindControl<ItemsControl>("TagFiltersList")!;
            VaultRoot = Window.FindControl<Grid>("VaultRoot")!;
        }

        public MainWindow Window { get; }
        public Grid Host { get; }
        public Border Panel { get; }
        public Grid Content { get; }
        public ScrollViewer Scroller { get; }
        public TextBlock Title { get; }
        public ScrollViewer List { get; }
        public Grid ListPane { get; }
        public ItemsControl TagFilters { get; }
        public Grid VaultRoot { get; }
        public VaultEntry First { get; }
        public VaultEntry Second { get; }
        public double DrawerOffset => Host.RenderTransform?.Value.M31 ?? 0;
        public bool IsOpen => Host.IsVisible && DrawerOffset == 0
            && ListPane.Opacity == 1 && ListPane.Margin.Right > 20;
        public bool IsClosed => !Host.IsVisible && ListPane.Opacity == 1 && ListPane.Margin.Right == 20;

        public static async Task<DrawerFixture> CreateAsync(int entryCount = 2, int tagCount = 0,
            Action<VaultEntry, VaultEntry>? customizeEntries = null)
        {
            Assert.True(Motion.IsEnabled,
                "The isolated test app must use full motion even when Windows client-area animations are disabled.");
            var fixture = new DrawerFixture(entryCount, tagCount, customizeEntries);
            try
            {
                await fixture.WaitUntilAsync(() => fixture.VaultRoot.Opacity == 1 && fixture.List.Bounds.Width > 0);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public void ClickEntry(Guid id)
        {
            Window.UpdateLayout();
            var button = Window.GetVisualDescendants().OfType<Button>()
                .Single(control => control.Classes.Contains("account-card") && control.Tag is Guid tag && tag == id);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        public void ClickClose()
        {
            var button = Panel.GetVisualDescendants().OfType<Button>()
                .Single(control => control.Content is string text && text == "关闭");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        public (Guid Id, double Top) FirstVisibleEntry()
        {
            var visible = List.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("account-card") && button.Tag is Guid)
                .Select(button => (Button: button, Position: button.TranslatePoint(default, List)))
                .Where(item => item.Position is { } point && point.Y + item.Button.Bounds.Height > 0
                    && point.Y < List.Viewport.Height)
                .OrderBy(item => item.Position!.Value.Y)
                .ThenBy(item => item.Position!.Value.X)
                .First();
            return ((Guid)visible.Button.Tag!, visible.Position!.Value.Y);
        }

        public void AssertEntryPosition(Guid id, double expectedTop)
        {
            var card = List.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Classes.Contains("account-card") && button.Tag is Guid tag && tag == id);
            var point = Assert.IsType<Point>(card.TranslatePoint(default, List));
            Assert.InRange(point.Y, expectedTop - 1, expectedTop + 1);
            Assert.True(point.Y + card.Bounds.Height > 0 && point.Y < List.Viewport.Height,
                "The same entry should remain visible after the column count changes.");
        }

        public DetailFrame CaptureDetailFrame() => new(
            Assert.IsType<Point>(Content.TranslatePoint(new Point(), Window)), Content.Bounds,
            Assert.IsType<Point>(Host.TranslatePoint(new Point(), Window)), Host.Bounds,
            DrawerOffset, List.Bounds.Width, TagFilters.Bounds.Height);

        public EditorFrame CaptureEditorFrame()
        {
            // Locate each data template's outer card, excluding nested text box
            // borders that inherit the same editor view as their DataContext.
            var editorCards = Scroller.GetVisualDescendants().OfType<Border>()
                .Where(border => border.DataContext is AccountEditorView or NodeEditorView)
                .Where(border => !border.GetVisualAncestors().OfType<Border>()
                    .Any(parent => ReferenceEquals(parent.DataContext, border.DataContext)))
                .ToArray();
            var account = Assert.Single(editorCards.Where(card => card.DataContext is AccountEditorView));
            var node = editorCards.SingleOrDefault(card => card.DataContext is NodeEditorView);
            return new EditorFrame(
                Assert.IsType<Point>(Scroller.TranslatePoint(new Point(), Window)), Scroller.Bounds, Title.Bounds.Height,
                Assert.IsType<Point>(account.TranslatePoint(new Point(), Window)), account.Bounds,
                node?.TranslatePoint(new Point(), Window), node?.Bounds);
        }

        public async Task<double> IntermediateFrameAsync(bool opening)
        {
            var positions = new List<double>();
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(2))
            {
                await NextFrameAsync();
                var position = DrawerOffset;
                positions.Add(position);
                if (Host.IsVisible && position > 0.01 && position < Panel.Width - 0.01)
                {
                    return position;
                }
                if ((opening && position <= 0) || (!opening && position >= Panel.Width))
                {
                    break;
                }
            }
            Assert.Fail($"No intermediate slide frame while {(opening ? "opening" : "closing")}. Positions: {string.Join(", ", positions.Select(x => x.ToString("F1")))}");
            return 0;
        }

        public async Task<ReflowObservation> ObserveReflowAsync(Action action, Func<bool> completed)
        {
            var rowRebuilds = 0;
            var tagHeights = new List<double> { TagFilters.Bounds.Height };
            var listWidths = new List<double> { List.Bounds.Width };
            var drawerWidths = new List<double>();
            var view = Window.View!;
            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(MainViewModel.EntryRows))
                {
                    rowRebuilds++;
                }
            }

            view.PropertyChanged += OnChanged;
            try
            {
                action();
                await WaitUntilAsync(() =>
                {
                    tagHeights.Add(TagFilters.Bounds.Height);
                    listWidths.Add(List.Bounds.Width);
                    if (Host.IsVisible)
                    {
                        drawerWidths.Add(Host.Bounds.Width);
                    }
                    return completed();
                });
            }
            finally
            {
                view.PropertyChanged -= OnChanged;
            }

            return new ReflowObservation(rowRebuilds, tagHeights, listWidths, drawerWidths);
        }

        public async Task WaitUntilAsync(Func<bool> predicate)
        {
            var clock = Stopwatch.StartNew();
            do
            {
                await NextFrameAsync();
                if (predicate())
                {
                    return;
                }
            } while (clock.Elapsed < TimeSpan.FromSeconds(3));
            Assert.Fail($"UI did not reach its expected state. Drawer X: {DrawerOffset:F1}; visible: {Host.IsVisible}; list opacity: {ListPane.Opacity:F2}; list margin: {ListPane.Margin.Right:F1}.");
        }

        public async Task ObserveFramesAsync(TimeSpan duration, Action? observe = null)
        {
            var clock = Stopwatch.StartNew();
            do
            {
                await NextFrameAsync();
                observe?.Invoke();
            } while (clock.Elapsed < duration);
        }

        private async Task NextFrameAsync()
        {
            await Task.Delay(15);
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            Window.ShowUnlockScreen();
            Window.PrepareClose();
            Window.Close();
            _session.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
