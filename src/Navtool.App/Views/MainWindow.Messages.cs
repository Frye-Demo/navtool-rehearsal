using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mapsui.Extensions;
using Navtool.App.Models;
using Navtool.App.Services;
using Navtool.App.ViewModels;
using Navtool.Core;

namespace Navtool.App.Views;

public partial class MainWindow
{
    private IReadOnlyList<RoutingMessage> _messages = [];
    private long _messageGeneration = -1;
    private bool _messageRefreshQueued;
    private bool _autoMessagePending;
    private ForecastModel? _messageModel;
    private Control? _messageOpener;
    private bool _copyingMessages;
    private long _messagePresentationVersion;
    private readonly List<(RoutingMessage Message, Button Button, Line Connector)> _endpointButtons = [];

    internal bool IsMessagePopupOpen => this.FindControl<Border>("MessagePopup")!.IsVisible;

    private void ScheduleMessageRefresh()
    {
        if (_messageRefreshQueued) return;
        _messageRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _messageRefreshQueued = false;
            UpdateMessagePresentation();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateMessagePresentation()
    {
        if (DataContext is not MainViewModel model) return;
        var messages = model.CurrentMessages;
        var generationChanged = _messageGeneration != model.MessageGeneration;
        var changed = generationChanged || !_messages.SequenceEqual(messages);
        var hasNewMessages = messages.Any(message => !_messages.Contains(message) ||
            (generationChanged && message.Id is not ("setup" or "preferences")));
        _messageGeneration = model.MessageGeneration;
        _messages = messages;
        if (hasNewMessages) _autoMessagePending = true;
        if (messages.Count == 0) _autoMessagePending = false;
        var severity = messages.Any(message => message.Severity == RoutingMessageSeverity.Error) ? "Error"
            : messages.Any(message => message.Severity == RoutingMessageSeverity.Warning) ? "Warning" : "Info";
        foreach (var name in new[] { "MessagesButton", "FooterMessagesButton" })
        {
            var button = this.FindControl<Button>(name)!;
            button.IsVisible = messages.Count > 0;
            button.Content = $"{severity} - Messages ({messages.Count})";
        }
        if (changed)
        {
            BuildInterruptedEndpointButtons();
            if (messages.Count == 0 || generationChanged) CloseMessages(restoreFocus: false);
            if (IsMessagePopupOpen)
                RenderMessageContents();
        }
        if (!model.IsCalculating && _autoMessagePending)
        {
            _autoMessagePending = false;
            if (!IsMessagePopupOpen)
                OpenMessages(null, null, focus: false);
        }
        UpdateMessagePlacement();
    }

    private void OnMessagesClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        UpdateMessagePresentation();
        OpenMessages(null, sender as Control, focus: true);
    }

    private void OpenMessages(ForecastModel? model, Control? opener, bool focus)
    {
        if (_messages.Count == 0) return;
        CloseRadialMenu();
        _messageModel = model;
        _messageOpener = opener;
        RenderMessageContents();
        this.FindControl<Border>("MessagePopup")!.IsVisible = true;
        this.FindControl<ScrollViewer>("MessageScrollViewer")!.Offset = default;
        UpdateMessagePlacement();
        if (focus) this.FindControl<Button>("CloseMessagesButton")!.Focus();
    }

    private void RenderMessageContents()
    {
        _messagePresentationVersion++;
        SetMessageCopyStatus(string.Empty);
        if (_messageModel is { } selected && !_messages.Any(message => message.Model == selected))
            _messageModel = null;
        var visible = GetVisibleMessages();
        var interrupted = visible.Any(message => message.IsInterrupted);
        this.FindControl<TextBlock>("MessagePopupTitle")!.Text = GetMessageTitle(visible);
        this.FindControl<TextBlock>("MessageIncompleteText")!.IsVisible = interrupted;
        var items = this.FindControl<StackPanel>("MessageItems")!;
        items.Children.Clear();
        foreach (var message in visible)
        {
            var item = new StackPanel { Spacing = 4 };
            item.Children.Add(new TextBlock
            {
                Text = message.Heading, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap
            });
            var summary = new TextBlock { Text = message.Summary, TextWrapping = TextWrapping.Wrap };
            if (message.Severity == RoutingMessageSeverity.Error) summary.Classes.Add("error");
            item.Children.Add(summary);
            if (message.Details is { } details)
                item.Children.Add(new Expander
                {
                    Header = "Technical details",
                    Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap }
                });
            items.Children.Add(item);
        }
    }

    private RoutingMessage[] GetVisibleMessages() =>
        _messages.Where(message => _messageModel is null ||
            message.Model is null || message.Model == _messageModel).ToArray();

    private string GetMessageTitle(IReadOnlyList<RoutingMessage> messages) => _messageModel switch
    {
        ForecastModel.NoaaGfs => "NOAA GFS interrupted",
        ForecastModel.EcmwfIfs => "ECMWF IFS interrupted",
        _ => messages.Any(message => message.IsInterrupted) ? "Routing interrupted" : "Messages"
    };

    private async void OnCopyMessagesClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var clipboard = Clipboard;
        await CopyMessagesAsync(clipboard is null ? null : text => clipboard.SetTextAsync(text));
    }

    internal async Task CopyMessagesAsync(Func<string, Task>? writeText)
    {
        if (_copyingMessages || !IsMessagePopupOpen) return;
        if (writeText is null)
        {
            SetMessageCopyStatus("Clipboard is unavailable on this platform.", error: true);
            return;
        }

        var visible = GetVisibleMessages();
        var sections = new List<string> { GetMessageTitle(visible) };
        if (visible.Any(message => message.IsInterrupted))
            sections.Add(this.FindControl<TextBlock>("MessageIncompleteText")!.Text!);
        foreach (var message in visible)
        {
            var lines = new List<string>
            {
                message.Heading,
                message.IsInterrupted ? $"{message.Severity} - Interrupted" : message.Severity.ToString(),
                message.Summary
            };
            if (message.Details is { } details)
            {
                lines.Add("Technical details");
                lines.Add(details);
            }
            sections.Add(string.Join(Environment.NewLine, lines));
        }

        var version = _messagePresentationVersion;
        _copyingMessages = true;
        SetMessageCopyStatus("Copying messages...");
        try
        {
            await writeText(string.Join(Environment.NewLine + Environment.NewLine, sections));
            if (version == _messagePresentationVersion)
                SetMessageCopyStatus("Messages copied.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
            NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            if (version == _messagePresentationVersion)
                SetMessageCopyStatus($"Copying messages failed: {exception.Message}", error: true);
        }
        finally
        {
            _copyingMessages = false;
        }
    }

    private void SetMessageCopyStatus(string text, bool error = false)
    {
        var status = this.FindControl<TextBlock>("MessageCopyStatus")!;
        status.Text = text;
        status.Classes.Set("error", error);
    }

    private void OnCloseMessagesClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseMessages();
    }

    private void CloseMessages(bool restoreFocus = true)
    {
        _messagePresentationVersion++;
        this.FindControl<Border>("MessagePopup")!.IsVisible = false;
        if (restoreFocus)
        {
            if (_messageOpener is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } &&
                _messageOpener.GetVisualRoot() == this)
                _messageOpener.Focus();
            else if (_messages.Count > 0) this.FindControl<Button>("MessagesButton")!.Focus();
            else _mapControl?.Focus();
        }
        UpdateMessagePlacement();
    }

    private void BuildInterruptedEndpointButtons()
    {
        var canvas = this.FindControl<Canvas>("InterruptedEndpointLayer")!;
        canvas.Children.Clear();
        _endpointButtons.Clear();
        foreach (var message in _messages.Where(message => message.IsInterrupted))
        {
            var generation = _messageGeneration;
            var button = new Button
            {
                Content = message.Model == ForecastModel.NoaaGfs ? "NOAA interrupted" : "ECMWF interrupted",
                Width = 166, Height = 44,
                Tag = message.Id
            };
            button.Classes.Add("interrupted-endpoint");
            AutomationProperties.SetName(button, $"{message.Heading} interrupted. Open details");
            button.Click += (_, e) =>
            {
                e.Handled = true;
                if (DataContext is not MainViewModel model || model.MessageGeneration != generation) return;
                var current = model.CurrentMessages.FirstOrDefault(candidate =>
                    candidate.Id == message.Id && candidate.IsInterrupted);
                if (current is null) return;
                UpdateMessagePresentation();
                var opener = _endpointButtons.Find(entry => entry.Message.Id == current.Id).Button ?? button;
                OpenMessages(current.Model, opener, focus: true);
            };
            var connector = new Line
            {
                Stroke = Brushes.Gray, StrokeThickness = 1.5, IsHitTestVisible = false
            };
            canvas.Children.Add(connector);
            canvas.Children.Add(button);
            _endpointButtons.Add((message, button, connector));
        }
    }

    private void UpdateMessagePlacement()
    {
        if (_mapControl is null || DataContext is not MainViewModel model ||
            _mapControl.Bounds.Width < 200 || _mapControl.Bounds.Height < 180) return;
        var width = _mapControl.Bounds.Width;
        var height = _mapControl.Bounds.Height;
        var popup = this.FindControl<Border>("MessagePopup")!;
        popup.Width = Math.Min(420, width - 200);
        popup.MaxHeight = Math.Max(100, height - 100);
        var viewport = model.Map.Navigator.Viewport;
        var anchors = _endpointButtons.Select(entry =>
        {
            var point = viewport.WorldToScreen(MapProjection.ToMapPointNear(entry.Message.Endpoint!.Value, viewport.CenterX));
            return new Point(point.X, point.Y);
        }).ToArray();
        var popupHeight = Math.Min(popup.MaxHeight, popup.Bounds.Height > 0 ? popup.Bounds.Height : 360);
        var left = new Rect(16, 68, popup.Width, popupHeight);
        var right = new Rect(width - 16 - popup.Width, 68, popup.Width, popupHeight);
        popup.HorizontalAlignment = anchors.Count(left.Contains) < anchors.Count(right.Contains)
            ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
        var popupBounds = popup.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Left ? left : right;
        var occupied = new List<Rect>();
        if (popup.IsVisible) occupied.Add(popupBounds);
        var timeline = this.FindControl<Border>("ChartTimeline")!;
        var bottom = timeline.IsVisible && timeline.Bounds.Height > 0
            ? height - timeline.Bounds.Height - 38 : height - 12;
        for (var i = 0; i < _endpointButtons.Count; i++)
        {
            var (_, button, connector) = _endpointButtons[i];
            var anchor = anchors[i];
            var onScreen = double.IsFinite(anchor.X) && double.IsFinite(anchor.Y) &&
                anchor.X >= 0 && anchor.X <= width && anchor.Y >= 0 && anchor.Y <= height;
            button.IsVisible = connector.IsVisible = onScreen;
            if (!onScreen) continue;
            var candidates = new[]
            {
                new Point(anchor.X + 12, anchor.Y - 22), new Point(anchor.X - 178, anchor.Y - 22),
                new Point(anchor.X - 83, anchor.Y + 16), new Point(anchor.X - 83, anchor.Y - 60),
                new Point(12, 72 + i * 52), new Point(width - 178, 72 + i * 52),
                new Point(12, bottom - 44 - i * 52), new Point(width - 178, bottom - 44 - i * 52)
            }.Select(point => new Rect(Math.Clamp(point.X, 12, width - 178),
                Math.Clamp(point.Y, 68, Math.Max(68, bottom - 44)), 166, 44));
            var placement = candidates.OrderBy(rect => occupied.Count(other => other.Intersects(rect)))
                .ThenBy(rect => Math.Pow(rect.Center.X - anchor.X, 2) + Math.Pow(rect.Center.Y - anchor.Y, 2)).First();
            occupied.Add(placement);
            Canvas.SetLeft(button, placement.X);
            Canvas.SetTop(button, placement.Y);
            connector.StartPoint = anchor;
            connector.EndPoint = placement.Center;
        }

    }

    private void OnMessagePopupSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateMessagePlacement();
}
