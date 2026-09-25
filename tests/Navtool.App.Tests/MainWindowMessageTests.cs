using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mapsui.Extensions;
using Navtool.App.Models;
using Navtool.App.Services;
using Navtool.App.ViewModels;
using Navtool.App.Views;
using Navtool.Core;
using Navtool.Infrastructure;

namespace Navtool.App.Tests;

public sealed class MainWindowMessageTests
{
    [AvaloniaTheory]
    [InlineData(ForecastModel.NoaaGfs, "NOAA GFS", "NOAA stopped.", "ECMWF stopped.")]
    [InlineData(ForecastModel.EcmwfIfs, "ECMWF IFS", "ECMWF stopped.", "NOAA stopped.")]
    public async Task Copy_messages_uses_popup_model_scope_and_global_notices(
        ForecastModel selected, string name, string expected, string excluded)
    {
        using var fixture = new MessageWindow();
        var (window, model) = (fixture.Window, fixture.Model);
        model.Map.Navigator.CenterOnAndZoomTo(MapProjection.ToMapPoint(new Coordinate(0, 0)), 10_000);
        model.SetRoutingFailure(ForecastModel.NoaaGfs, 0, "NOAA stopped.", new Coordinate(0, 0));
        model.SetRoutingFailure(ForecastModel.EcmwfIfs, 1, "ECMWF stopped.", new Coordinate(0, 1));
        model.WarningMessage = "Global forecast notice.";
        Dispatcher.UIThread.RunJobs();
        var opener = window.FindControl<Canvas>("InterruptedEndpointLayer")!.Children.OfType<Button>()
            .Single(button => (string?)button.Tag == $"route:{selected}:{(selected == ForecastModel.NoaaGfs ? 0 : 1)}");
        opener.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var copy = window.FindControl<Button>("CopyMessagesButton")!;
        copy.Focus();
        string? copied = null;

        await window.CopyMessagesAsync(text => { copied = text; return Task.CompletedTask; });

        Assert.NotNull(copied);
        Assert.StartsWith($"{name} interrupted", copied);
        Assert.Contains($"{name} / leg {(selected == ForecastModel.NoaaGfs ? 1 : 2)}", copied);
        Assert.Contains(expected, copied);
        Assert.Contains("Notice" + Environment.NewLine + "Warning" + Environment.NewLine + "Global forecast notice.", copied);
        Assert.DoesNotContain(excluded, copied);
        Assert.Contains("Warning - Interrupted", copied);
        Assert.Contains("Incomplete: the dashed paths are not completed routes.", copied);
        Assert.True(window.IsMessagePopupOpen);
        Assert.True(copy.IsFocused);
        Assert.Equal($"{name} interrupted", window.FindControl<TextBlock>("MessagePopupTitle")!.Text);
        Assert.Equal("Messages copied.", window.FindControl<TextBlock>("MessageCopyStatus")!.Text);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "\u001b");
        Assert.False(window.IsMessagePopupOpen);
        Assert.True(opener.IsFocused);
    }

    [AvaloniaFact]
    public async Task Copy_button_includes_all_models_and_collapsed_details_without_resetting_scroll()
    {
        using var fixture = new MessageWindow();
        var (window, model) = (fixture.Window, fixture.Model);
        var summary = string.Join(" ", Enumerable.Repeat("NOAA search stopped.", 120));
        model.SetRoutingFailure(ForecastModel.NoaaGfs, 2,
            summary + " The loaded forecast covers the requested passage.", new Coordinate(0, 0));
        model.SetRoutingFailure(ForecastModel.EcmwfIfs, 0, "ECMWF coverage missing.");
        Dispatcher.UIThread.RunJobs();
        Click(window, "MessagesButton");
        var details = Assert.Single(window.FindControl<StackPanel>("MessageItems")!.GetVisualDescendants().OfType<Expander>());
        Assert.False(details.IsExpanded);
        var scroll = window.FindControl<ScrollViewer>("MessageScrollViewer")!;
        scroll.Offset = new Vector(0, 180);
        Dispatcher.UIThread.RunJobs();
        var offset = scroll.Offset;
        Assert.True(offset.Y > 0);
        Assert.NotNull(window.Clipboard);
        await window.Clipboard!.ClearAsync();

        Click(window, "CopyMessagesButton");
        var copied = await window.Clipboard.GetTextAsync();

        Assert.Equal(string.Join(Environment.NewLine + Environment.NewLine,
            "Routing interrupted",
            "Incomplete: the dashed paths are not completed routes. Recalculate to try again.",
            string.Join(Environment.NewLine, "NOAA GFS / leg 3", "Warning - Interrupted", summary,
                "Technical details", "The loaded forecast covers the requested passage."),
            string.Join(Environment.NewLine, "ECMWF IFS / leg 1", "Error", "ECMWF coverage missing.")), copied);
        Assert.Equal(offset, scroll.Offset);
        Assert.False(details.IsExpanded);
        Assert.True(window.IsMessagePopupOpen);
    }

    [AvaloniaFact]
    public async Task Copy_messages_reports_unavailable_clipboard_and_failure_then_allows_retry()
    {
        using var fixture = new MessageWindow();
        var (window, model) = (fixture.Window, fixture.Model);
        model.WarningMessage = "Original notice.";
        Dispatcher.UIThread.RunJobs();
        Click(window, "MessagesButton");
        var status = window.FindControl<TextBlock>("MessageCopyStatus")!;

        await window.CopyMessagesAsync(null);
        Assert.Equal("Clipboard is unavailable on this platform.", status.Text);
        Assert.Contains("error", status.Classes);
        Assert.True(status.IsEffectivelyVisible);
        await window.CopyMessagesAsync(_ => Task.FromException(new IOException("Clipboard busy.")));
        Assert.Equal("Copying messages failed: Clipboard busy.", status.Text);
        Assert.Contains("error", status.Classes);
        Assert.True(window.IsMessagePopupOpen);
        Assert.True(window.FindControl<Button>("CloseMessagesButton")!.IsFocused);
        Assert.Single(model.CurrentMessages);

        string? copied = null;
        await window.CopyMessagesAsync(text => { copied = text; return Task.CompletedTask; });
        Assert.Equal(string.Join(Environment.NewLine + Environment.NewLine, "Messages",
            string.Join(Environment.NewLine, "Notice", "Warning", "Original notice.")), copied);
        Assert.Equal("Messages copied.", status.Text);
        Assert.DoesNotContain("error", status.Classes);
        Click(window, "CloseMessagesButton");
        Assert.False(window.IsMessagePopupOpen);
        Assert.True(window.FindControl<Button>("MessagesButton")!.IsFocused);
    }

    [AvaloniaFact]
    public async Task Copy_messages_reports_expected_platform_failures_and_allows_retry()
    {
        using var fixture = new MessageWindow();
        var (window, model) = (fixture.Window, fixture.Model);
        model.WarningMessage = "Original notice.";
        Dispatcher.UIThread.RunJobs();
        Click(window, "MessagesButton");
        var status = window.FindControl<TextBlock>("MessageCopyStatus")!;
        Exception[] failures =
        [
            new InvalidOperationException("Clipboard busy."),
            new NotSupportedException("Clipboard not supported."),
            new System.Runtime.InteropServices.ExternalException("Native clipboard failed.")
        ];

        foreach (var failure in failures)
        {
            await window.CopyMessagesAsync(_ => Task.FromException(failure));

            Assert.Equal($"Copying messages failed: {failure.Message}", status.Text);
            Assert.Contains("error", status.Classes);
            Assert.True(window.IsMessagePopupOpen);
            await window.CopyMessagesAsync(_ => Task.CompletedTask);
            Assert.Equal("Messages copied.", status.Text);
            Assert.DoesNotContain("error", status.Classes);
        }
    }

    [AvaloniaFact]
    public async Task Copy_messages_propagates_unexpected_failures_and_releases_copy_guard()
    {
        using var fixture = new MessageWindow();
        var (window, model) = (fixture.Window, fixture.Model);
        model.WarningMessage = "Original notice.";
        Dispatcher.UIThread.RunJobs();
        Click(window, "MessagesButton");
        var failure = new ArgumentException("Unexpected writer defect.");

        var thrown = await Assert.ThrowsAsync<ArgumentException>(() =>
            window.CopyMessagesAsync(_ => Task.FromException(failure)));

        Assert.Same(failure, thrown);
        string? copied = null;
        await window.CopyMessagesAsync(text => { copied = text; return Task.CompletedTask; });
        Assert.NotNull(copied);
        Assert.Contains("Original notice.", copied);
        Assert.Equal("Messages copied.", window.FindControl<TextBlock>("MessageCopyStatus")!.Text);
    }

    [AvaloniaFact]
    public async Task Pending_copy_does_not_overwrite_status_for_a_reopened_popup()
    {
        using var fixture = new MessageWindow();
        var (window, model) = (fixture.Window, fixture.Model);
        model.WarningMessage = "Original notice.";
        Dispatcher.UIThread.RunJobs();
        var completion = new TaskCompletionSource();
        var copying = window.CopyMessagesAsync(_ => completion.Task);
        Assert.Equal("Copying messages...", window.FindControl<TextBlock>("MessageCopyStatus")!.Text);
        Click(window, "CloseMessagesButton");
        Click(window, "MessagesButton");
        completion.SetResult();
        await copying;
        Assert.Equal(string.Empty, window.FindControl<TextBlock>("MessageCopyStatus")!.Text);
        Assert.True(window.IsMessagePopupOpen);
    }

    [AvaloniaFact]
    public void Outcome_opens_after_calculation_and_dismissal_survives_unrelated_updates()
    {
        using var fixture = new MessageWindow();
        var (window, model) = (fixture.Window, fixture.Model);
        model.IsCalculating = true;
        model.WarningMessage = "A newer run is available.";
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsMessagePopupOpen);
        model.IsCalculating = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsMessagePopupOpen);
        Click(window, "CloseMessagesButton");
        model.ProgressFraction = .5;
        model.StatusMessage = "Unrelated status update";
        window.SetPanelOpen(false);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsMessagePopupOpen);
        Assert.Single(model.CurrentMessages);
        model.WarningMessage = "Forecast coverage is incomplete.";
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsMessagePopupOpen);
    }

    [AvaloniaFact]
    public async Task Repeated_explicit_validation_failure_reopens_and_keeps_keyboard_focus()
    {
        using var fixture = new MessageWindow();
        var (window, model) = (fixture.Window, fixture.Model);
        var focused = window.FindControl<Control>("SetStartButton")!;
        focused.Focus();
        await model.CalculateRoutesAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsMessagePopupOpen);
        Assert.True(focused.IsKeyboardFocusWithin);
        Click(window, "CloseMessagesButton");
        Assert.False(window.IsMessagePopupOpen);
        await model.CalculateRoutesAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsMessagePopupOpen);
        Click(window, "CloseMessagesButton");
        var opener = window.FindControl<Button>("MessagesButton")!;
        Click(window, "MessagesButton");
        Assert.True(window.FindControl<Button>("CloseMessagesButton")!.IsFocused);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "\u001b");
        Assert.False(window.IsMessagePopupOpen);
        Assert.True(opener.IsFocused);
    }

    [AvaloniaFact]
    public void New_messages_preserve_open_popup_filter_and_scroll_position()
    {
        using var fixture = new MessageWindow();
        var (window, model) = (fixture.Window, fixture.Model);
        var reason = string.Join(" ", Enumerable.Repeat("Detailed NOAA interruption context.", 200));
        model.SetRoutingFailure(ForecastModel.NoaaGfs, 0, reason, new Coordinate(0, 0));
        Dispatcher.UIThread.RunJobs();
        var button = Assert.Single(window.FindControl<Canvas>("InterruptedEndpointLayer")!.Children.OfType<Button>());
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var scroll = window.FindControl<ScrollViewer>("MessageScrollViewer")!;
        scroll.Offset = new Vector(0, 180);
        Dispatcher.UIThread.RunJobs();
        var offset = scroll.Offset;
        Assert.True(offset.Y > 0);

        model.SetRoutingFailure(ForecastModel.EcmwfIfs, 0, "New ECMWF interruption.", new Coordinate(0, 1));
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsMessagePopupOpen);
        Assert.Equal("NOAA GFS interrupted", window.FindControl<TextBlock>("MessagePopupTitle")!.Text);
        Assert.Equal(offset, scroll.Offset);
        Assert.DoesNotContain(window.FindControl<StackPanel>("MessageItems")!.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "New ECMWF interruption.");
        Click(window, "CloseMessagesButton");
        model.StatusMessage = "Unrelated update";
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsMessagePopupOpen);
    }

    [AvaloniaFact]
    public void Endpoint_click_uses_updated_message_before_the_queued_refresh()
    {
        using var fixture = new MessageWindow();
        var (window, model) = (fixture.Window, fixture.Model);
        model.Map.Navigator.CenterOnAndZoomTo(MapProjection.ToMapPoint(new Coordinate(0, 0)), 10_000);
        model.SetRoutingFailure(ForecastModel.NoaaGfs, 0, "Initial reason.", new Coordinate(0, 0));
        Dispatcher.UIThread.RunJobs();
        var button = Assert.Single(window.FindControl<Canvas>("InterruptedEndpointLayer")!.Children.OfType<Button>());
        Click(window, "CloseMessagesButton");

        model.SetRoutingFailure(ForecastModel.NoaaGfs, 0, "Updated reason.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(window.IsMessagePopupOpen);
        Assert.Equal("NOAA GFS interrupted", window.FindControl<TextBlock>("MessagePopupTitle")!.Text);
        var texts = window.FindControl<StackPanel>("MessageItems")!.GetVisualDescendants().OfType<TextBlock>()
            .Select(text => text.Text).ToArray();
        Assert.Contains("Updated reason.", texts);
        Assert.DoesNotContain("Initial reason.", texts);
        Click(window, "CloseMessagesButton");
        var currentButton = Assert.Single(window.FindControl<Canvas>("InterruptedEndpointLayer")!.Children.OfType<Button>());
        Assert.True(currentButton.IsFocused);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsMessagePopupOpen);
    }

    [AvaloniaFact]
    public async Task Old_endpoint_cannot_reopen_after_a_new_attempt_clears_its_outcome()
    {
        using var fixture = new MessageWindow();
        var (window, model) = (fixture.Window, fixture.Model);
        model.SetRoutingFailure(ForecastModel.NoaaGfs, 1, "Search stopped.", new Coordinate(0, 0));
        Dispatcher.UIThread.RunJobs();
        var oldButton = Assert.Single(window.FindControl<Canvas>("InterruptedEndpointLayer")!.Children.OfType<Button>());
        Click(window, "CloseMessagesButton");
        await model.CalculateRoutesAsync();
        Dispatcher.UIThread.RunJobs();
        Click(window, "CloseMessagesButton");
        oldButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(window.IsMessagePopupOpen);
        Assert.Empty(window.FindControl<Canvas>("InterruptedEndpointLayer")!.Children);
        Assert.DoesNotContain(model.CurrentMessages, message => message.IsInterrupted);
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.KindOfBlue)]
    public void Long_messages_scroll_and_have_one_prominent_copy(AppTheme theme)
    {
        using var fixture = new MessageWindow(theme);
        var (window, model) = (fixture.Window, fixture.Model);
        window.Width = 1040;
        window.Height = 680;
        var reason = string.Join(" ", Enumerable.Repeat("An actionable diagnostic with supporting context.", 160));
        model.ErrorMessage = reason;
        model.WarningMessage = "Distinct notice.\nDistinct notice.";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, model.CurrentMessages.Count);
        var popup = window.FindControl<Border>("MessagePopup")!;
        var map = window.FindControl<Grid>("MapShell")!;
        Assert.True(popup.Bounds.Width <= map.Bounds.Width);
        Assert.True(popup.Bounds.Bottom < map.Bounds.Height);
        var scroll = window.FindControl<ScrollViewer>("MessageScrollViewer")!;
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text == reason);
        Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text == "Distinct notice.");
        var close = window.FindControl<Button>("CloseMessagesButton")!;
        Assert.True(close.IsEffectivelyVisible);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var point = close.TranslatePoint(new Rect(close.Bounds.Size).Center, window)!.Value;
        var target = window.InputHitTest(point);
        Assert.True(target is Visual visual && (visual == close || visual.GetVisualAncestors().Contains(close)),
            $"Close at {point}, bounds {close.Bounds}, popup {popup.Bounds}, window {window.Bounds}, hit {target}");
    }

    [AvaloniaFact]
    public void Coverage_context_is_in_details_but_real_coverage_errors_are_not_hidden()
    {
        using var fixture = new MessageWindow();
        var model = fixture.Model;
        model.SetRoutingFailure(ForecastModel.NoaaGfs, 0,
            "Search work limit reached. The loaded forecast covers the requested passage.", new Coordinate(0, 0));
        var interrupted = Assert.Single(model.CurrentMessages);
        Assert.Equal("Search work limit reached.", interrupted.Summary);
        Assert.Equal("The loaded forecast covers the requested passage.", interrupted.Details);
        model.SetRoutingFailure(ForecastModel.EcmwfIfs, 0, "Forecast does not cover the requested passage.");
        Assert.Contains(model.CurrentMessages, message => message.Summary == "Forecast does not cover the requested passage.");
    }

    [AvaloniaTheory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Overlapping_labels_are_clickable_in_rotated_world_copies_without_placing_endpoints(int worldCopy)
    {
        using var fixture = new MessageWindow();
        var (window, model) = (fixture.Window, fixture.Model);
        var endpoint = new Coordinate(0, 179);
        var projected = MapProjection.ToMapPoint(endpoint);
        model.Map.Navigator.CenterOnAndZoomTo(
            new Mapsui.MPoint(projected.X + worldCopy * MapProjection.WebMercatorWorldWidth, projected.Y), 10_000);
        model.Map.Navigator.RotateTo(30);
        model.SetRoutingFailure(ForecastModel.NoaaGfs, 0, "NOAA stopped.", endpoint);
        model.SetRoutingFailure(ForecastModel.EcmwfIfs, 0, "ECMWF stopped.", endpoint);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var canvas = window.FindControl<Canvas>("InterruptedEndpointLayer")!;
        var labels = canvas.Children.OfType<Button>().ToArray();
        Assert.All(labels, label => Assert.True(label.IsEffectivelyVisible));
        Assert.False(labels[0].Bounds.Intersects(labels[1].Bounds));
        var popup = window.FindControl<Border>("MessagePopup")!;
        Assert.All(labels, label =>
        {
            Assert.False(label.Bounds.Intersects(popup.Bounds));
            Assert.True(new Rect(canvas.Bounds.Size).Contains(label.Bounds), $"Label {label.Bounds}, canvas {canvas.Bounds}");
            var center = label.TranslatePoint(new Rect(label.Bounds.Size).Center, window)!.Value;
            var hit = window.InputHitTest(center) as Visual;
            Assert.True(hit == label || hit?.GetVisualAncestors().Contains(label) is true,
                $"Label {label.Bounds}, center {center}, hit {hit}, popup {popup.Bounds}");
        });
        Click(window, "CloseMessagesButton");
        model.SetStartCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var oldStart = model.Itinerary.Start;
        var point = labels[1].TranslatePoint(new Rect(labels[1].Bounds.Size).Center, window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsMessagePopupOpen);
        Assert.Equal("ECMWF IFS interrupted", window.FindControl<TextBlock>("MessagePopupTitle")!.Text);
        Assert.Equal(oldStart, model.Itinerary.Start);
    }

    private static void Click(MainWindow window, string name)
    {
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class MessageWindow : IDisposable
    {
        public MainViewModel Model { get; } = new(null, null, TimeProvider.System, TimeZoneInfo.Utc,
            new OsmTileOptions(Enabled: false));
        public MainWindow Window { get; }

        public MessageWindow(AppTheme theme = AppTheme.Light)
        {
            var service = AppThemeService.CreateTransient();
            service.Initialize(Application.Current!);
            service.SelectTheme(theme);
            Window = new MainWindow(service) { DataContext = Model };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => Window.Close();
    }
}
