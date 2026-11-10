using LLMTray.Core.Chat;
using MdBlock = LLMTray.Core.Chat.Block;
using MdRun = LLMTray.Core.Chat.Run;
using LLMTray.Core.Runtime;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace LLMTray.App;

/// <summary>
/// The chat, laid out like the Mac app's chat window: chats on the left
/// (new, temporary, search, by day), the conversation on the right (your
/// messages on the right, answers on the left with their thought process
/// folded, copy and numbers), Regenerate and tok/s above the composer.
/// </summary>
sealed partial class ChatWindow : Window
{
    readonly ChatClient chat;
    readonly ChatStore store;
    readonly ListView chats = new() { SelectionMode = ListViewSelectionMode.Single };
    readonly TextBox search = new() { PlaceholderText = L.T("Search chats") };
    readonly ToggleButton temporary = new() { Content = new FontIcon { Glyph = "", FontSize = 14 } };
    readonly TextBlock title = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox model = new() { MinWidth = 220, PlaceholderText = L.T("Choose a model") };
    readonly Microsoft.UI.Xaml.Shapes.Ellipse dot = Ui.Dot(Ui.Gray, 8);
    readonly TextBlock status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ProgressRing loading = new() { Width = 12, Height = 12, IsActive = false };
    /// <summary>Under the model picker: its size and where it runs, or why it isn't running.</summary>
    readonly TextBlock facts = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly Button startStop = new();
    readonly ColumnDefinition sideColumn = new() { Width = new GridLength(260) };
    UIElement? sideFrame;
    /// <summary>When it last lost the front: a click on the tray icon takes it first.</summary>
    DateTime deactivatedAt;
    bool foreground;
    readonly ScrollViewer scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel messages = new() { Spacing = 14, MaxWidth = 860, Padding = new Thickness(24, 16, 24, 16) };
    readonly Button regenerate = new() { Content = L.T("Regenerate"), Visibility = Visibility.Collapsed };
    readonly Button compact = new() { Content = L.T("Compact"), Visibility = Visibility.Collapsed };
    readonly TextBlock stats = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox input = new()
    {
        PlaceholderText = L.T("Message…"),
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxHeight = 220,
        MinHeight = 40,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    /// <summary>Thinking effort (adr/0011): shown for a model whose template thinks.</summary>
    readonly DropDownButton thinking = new();
    readonly Button send = new();
    bool rendering;
    /// <summary>Keep the newest in view: on after sending, off when the user scrolls up.</summary>
    bool follow = true;
    /// <summary>Thought process blocks the user opened, by message, kept across redraws.</summary>
    readonly HashSet<ChatMessage> openThoughts = [];
    bool renderQueued;
    int renderedCount = -1;
    int renderedEpoch = -1;
    int attachEpoch;

    public ChatWindow()
    {
        // The app's window (adr/0014): the chat.
        Title = "LLMTray";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        store = App.Current.Chats;
        chat = new ChatClient(App.Current.Local, new Uri($"http://127.0.0.1:{Core.AppInfo.ProxyPort}/"), store);
        chat.Changed += () => DispatcherQueue.TryEnqueue(RenderSoon);
        store.Changed += _ => DispatcherQueue.TryEnqueue(RenderChats);

        Content = Layout();
        SetUpPictures((UIElement)Content);
        Size(1100, 760);

        search.TextChanged += (_, _) => RenderChats();
        chats.SelectionChanged += (_, _) =>
        {
            if (rendering || chats.SelectedItem is not ListViewItem { Tag: ChatSummary summary }) return;
            if (summary.Id != chat.Id) chat.Load(summary.Id);
            input.Focus(FocusState.Programmatic);
        };
        ToolTipService.SetToolTip(temporary, L.T("Temporary chat: not saved"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(temporary, L.T("Temporary chat"));
        temporary.Click += (_, _) => chat.New(temporary.IsChecked == true);
        model.SelectionChanged += (_, _) =>
        {
            if (rendering) return;
            RenderStatus();
            RenderAttachNote();
            RenderThinking();
        };
        regenerate.Click += (_, _) => _ = chat.RegenerateAsync(Options());
        ToolTipService.SetToolTip(compact, L.T("Replace the middle of this chat with a summary the model writes, to save context"));
        compact.Click += (_, _) => _ = CompactAsync(retry: false);
        send.Click += (_, _) => SendOrStop();
        input.PreviewKeyDown += (_, e) =>
        {
            // Enter sends; Shift+Enter is a new line.
            if (e.Key != VirtualKey.Enter) return;
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shift) return;
            e.Handled = true;
            SendOrStop();
        };
        ToolTipService.SetToolTip(thinking, L.T("Thinking effort: how long the model may think before it answers"));
        var efforts = new MenuFlyout();
        foreach (var effort in Enum.GetValues<ThinkingEffort>())
        {
            var item = new MenuFlyoutItem { Text = EffortName(effort) };
            item.Click += (_, _) =>
            {
                App.Current.UpdateSettings(App.Current.Settings with { ThinkingEffort = effort });
                RenderThinking();
            };
            efforts.Items.Add(item);
        }
        thinking.Flyout = efforts;
        App.Current.HostChanged += _ => { RenderModels(); RenderStatus(); RenderThinking(); };
        App.Current.ModelsChanged += RenderStatus;
        App.Current.ModelsChanged += () => { RenderModels(); RenderThinking(); };
        App.Current.ThinkingStylesChanged += RenderThinking;
        // Closing the chat keeps LLMTray in the notification area; with no icon there, nothing
        // would bring it back, so then it quits.
        AppWindow.Closing += (sender, args) =>
        {
            if (App.Current.Quitting) return;
            args.Cancel = true;
            if (App.Current.HasTrayIcon) sender.Hide();
            else _ = App.Current.QuitAsync();
        };

        // Keys: Ctrl+N a new chat, Ctrl+F search, Esc stops an answer.
        AddKey(VirtualKey.N, VirtualKeyModifiers.Control, NewChat);
        AddKey(VirtualKey.F, VirtualKeyModifiers.Control, () => search.Focus(FocusState.Keyboard));
        // Ctrl+, Settings, as on the Mac (⌘,).
        AddKey((VirtualKey)0xBC, VirtualKeyModifiers.Control, () => App.Current.ShowSettings());
        AddKey(VirtualKey.Escape, VirtualKeyModifiers.None, () => { if (chat.Busy) chat.Stop(); });
        Activated += (_, e) =>
        {
            foreground = e.WindowActivationState != WindowActivationState.Deactivated;
            if (foreground) input.Focus(FocusState.Programmatic);
            else
            {
                deactivatedAt = DateTime.UtcNow;
                // Lost to the taskbar (the tray icon's click), not to another app.
                deactivatedToTaskbar = ForegroundIsTaskbar();
            }
        };
        if (Content is FrameworkElement root)
        {
            // Colours are picked per theme: draw everything again on a switch.
            root.ActualThemeChanged += (_, _) =>
            {
                renderedEpoch = -1;
                Render();
                RenderChats();
            };
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, L.T("Search chats"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chats, L.T("Chats"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(stats, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        RenderModels();
        RenderStatus();
        RenderThinking();
        RenderChats();
        Render();
    }

    static string EffortName(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Off => L.T("Off"),
        ThinkingEffort.Low => L.T("Low"),
        ThinkingEffort.Medium => L.T("Medium"),
        _ => L.T("High"),
    };

    /// <summary>The effort picker: only for a model that thinks; its label says the current effort.</summary>
    void RenderThinking()
    {
        var app = App.Current;
        var picked = (model.SelectedItem as ComboBoxItem)?.Tag as string;
        var entry = picked is null ? app.Host.State.Model : app.Library.Current.Resolve(picked);
        var style = entry is null ? ThinkingStyle.None : app.ThinkingStyleOf(entry);
        thinking.Visibility = style == ThinkingStyle.None ? Visibility.Collapsed : Visibility.Visible;
        var label = L.F("Thinking effort: {0}", EffortName(app.Settings.ThinkingEffort));
        thinking.Content = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(thinking, label);
    }

    void AddKey(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        (Content as UIElement)?.KeyboardAccelerators.Add(accelerator);
    }

    public void NewChat()
    {
        temporary.IsChecked = false;
        chat.New();
        input.Focus(FocusState.Programmatic);
    }

    ChatOptions Options()
    {
        var picked = (model.SelectedItem as ComboBoxItem)?.Tag as string;
        var app = App.Current;
        // The model the answer comes from: the picked one, or whichever is loaded.
        var entry = picked is null ? app.Host.State.Model : app.Library.Current.Resolve(picked);
        return new ChatOptions(picked, Profile: app.Profiles.ResolvedFor(entry?.FullPath), Tools: app.Tools,
            Effort: app.Settings.ThinkingEffort, Style: entry is null ? ThinkingStyle.None : app.ThinkingStyleOf(entry),
            Mcp: app.Mcp, Approve: AskAsync);
    }

    /// <summary>
    /// Before an MCP call (adr/0012): the server, the tool and its arguments
    /// in full; Allow, Allow for This Chat (not after outside text) or Deny.
    /// No default button: Enter mustn't approve.
    /// </summary>
    Task<McpApproval> AskAsync(McpApprovalRequest request, CancellationToken stopped)
    {
        var done = new TaskCompletionSource<McpApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (stopped.IsCancellationRequested)
                {
                    done.TrySetResult(McpApproval.Deny);
                    return;
                }
                Activate();
                var body = new StackPanel { Spacing = 8, MaxWidth = 520 };
                body.Children.Add(new TextBlock { Text = L.F("{0} wants to use {1}.", request.Server, request.Tool), TextWrapping = TextWrapping.Wrap });
                body.Children.Add(new TextBox
                {
                    Text = request.Arguments,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    FontFamily = Ui.Mono,
                    MaxHeight = 220,
                });
                if (request.ReadOnly == true) body.Children.Add(Ui.Caption(L.T("The server says this tool only reads.")));
                body.Children.Add(Ui.Caption(L.T("The server may do more than these arguments show.")));
                if (request.AfterOutsideText)
                {
                    body.Children.Add(Ui.Caption(L.T("Proposed right after reading outside text: check it."), Ui.Orange));
                }
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = L.T("Allow this tool call?"),
                    Content = new ScrollViewer { Content = body },
                    PrimaryButtonText = L.T("Allow"),
                    SecondaryButtonText = request.AfterOutsideText ? "" : L.T("Allow for This Chat"),
                    CloseButtonText = L.T("Deny"),
                    DefaultButton = ContentDialogButton.None,
                };
                // Stop closes the question with the turn: no dialog is left behind to block the next one.
                using var closing = stopped.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
                var result = await dialog.ShowAsync();
                done.TrySetResult(result switch
                {
                    ContentDialogResult.Primary => McpApproval.Allow,
                    ContentDialogResult.Secondary => McpApproval.AllowForThisChat,
                    _ => McpApproval.Deny,
                });
            }
            catch (Exception)
            {
                // Can't ask (another dialog open): not allowed.
                done.TrySetResult(McpApproval.Deny);
            }
        }))
        {
            // The window is closing: nobody to ask, so not allowed.
            done.TrySetResult(McpApproval.Deny);
        }
        return done.Task;
    }

    /// <summary>Compacts the chat; with <paramref name="retry"/>, the question that didn't fit is asked again after.</summary>
    async Task CompactAsync(bool retry)
    {
        var options = Options();
        // The chat and question this is for: the retry happens only if they're still on screen.
        var epoch = chat.Epoch;
        var question = chat.Messages.LastOrDefault(m => m.Role == "user");
        var problem = await chat.CompactAsync(options);
        if (problem is not null)
        {
            DispatcherQueue.TryEnqueue(() => stats.Text = L.T(problem));
            return;
        }
        if (!retry || chat.Epoch != epoch || chat.Messages.LastOrDefault(m => m.Role == "user") != question) return;
        // Only now, with the summary in: the failed answer goes and the question is asked again.
        if (chat.Messages.LastOrDefault() is { Role: "assistant", ContextFull: true }) chat.DropFailedAnswer();
        await chat.RegenerateAsync(options);
    }

    void SendOrStop()
    {
        if (chat.Busy)
        {
            chat.Stop();
            return;
        }
        // While the chat is being compacted nothing is sent: the draft stays.
        if (chat.Compacting) return;
        var text = input.Text;
        if (string.IsNullOrWhiteSpace(text) && pending.Count == 0) return;
        input.Text = "";
        var images = pending.ToList();
        pending.Clear();
        RenderAttachments();
        follow = true;
        _ = chat.SendAsync(text, Options(), images);
    }

    // MARK: Layout

    UIElement Layout()
    {
        // Sidebar
        var newChat = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        newChat.Content = Ui.Horizontal(new FontIcon { Glyph = "", FontSize = 14 }, new TextBlock { Text = L.T("New chat") });
        newChat.Click += (_, _) => NewChat();
        var top = new Grid { ColumnSpacing = 6 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(newChat);
        Grid.SetColumn(temporary, 1);
        top.Children.Add(temporary);
        var sidebar = new Grid { Padding = new Thickness(10, 12, 10, 10), RowSpacing = 8 };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sidebar.Children.Add(top);
        Grid.SetRow(search, 1);
        sidebar.Children.Add(search);
        Grid.SetRow(chats, 2);
        sidebar.Children.Add(chats);
        sideFrame = new Border { Child = sidebar, Background = Ui.Resource<Brush>("LayerFillColorDefaultBrush", Ui.CardBackground), BorderBrush = Ui.Stroke, BorderThickness = new Thickness(0, 0, 1, 0) };

        // Header (adr/0014, Mac ChatHeaderView): the chat and the server on one line,
        // the model and what to do with it on the next, the notices under them.
        var sideToggle = Ui.IconButton("\uE700", L.T("Show or hide the chats"), ToggleSidebar);
        var settings = Ui.IconButton("\uE713", L.T("Settings"), () => App.Current.ShowSettings());
        var server = Ui.Horizontal(dot, status, loading);
        server.Spacing = 6;
        ToolTipService.SetToolTip(server, L.T("Server log"));
        var serverButton = new Button { Content = server, Style = Ui.Resource<Style>("SubtleButtonStyle", null!), Padding = new Thickness(8, 4, 8, 4) };
        serverButton.Click += (_, _) => App.Current.ShowLog();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(serverButton, L.T("Server log"));
        var first = new Grid { ColumnSpacing = 6 };
        first.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        first.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        first.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        first.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        first.Children.Add(sideToggle);
        Grid.SetColumn(title, 1);
        first.Children.Add(title);
        Grid.SetColumn(serverButton, 2);
        first.Children.Add(serverButton);
        Grid.SetColumn(settings, 3);
        first.Children.Add(settings);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(model, L.T("Model"));
        var getModels = Ui.IconButton("\uE896", L.T("Get models…"), () => _ = GetModelsAsync());
        startStop.Style = Ui.Resource<Style>("SubtleButtonStyle", null!);
        startStop.Click += (_, _) => StartOrStop();
        facts.Foreground = Ui.Secondary;
        var second = new Grid { ColumnSpacing = 8 };
        second.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        second.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        second.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        second.Children.Add(model);
        Grid.SetColumn(facts, 1);
        second.Children.Add(facts);
        var actions = Ui.Horizontal(getModels, startStop);
        actions.Spacing = 2;
        Grid.SetColumn(actions, 2);
        second.Children.Add(actions);

        var header = new StackPanel { Padding = new Thickness(12, 8, 12, 8), Spacing = 6 };
        header.Children.Add(first);
        header.Children.Add(second);
        var notices = new Notices(updates: true).Panel;
        notices.Margin = new Thickness(16, 0, 16, 4);
        header.Children.Add(notices);

        scroller.Content = messages;
        // Scrolling up to read stops following the answer; back at the end follows again.
        scroller.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate) follow = scroller.VerticalOffset >= scroller.ScrollableHeight - 40;
        };

        // Above the composer: Regenerate and the numbers.
        var bar = new Grid { Padding = new Thickness(20, 4, 20, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        regenerate.Style = Ui.Resource<Style>("SubtleButtonStyle", regenerate.Style);
        compact.Style = regenerate.Style;
        bar.Children.Add(Ui.Horizontal(regenerate, compact));
        stats.Foreground = Ui.Secondary;
        Grid.SetColumn(stats, 1);
        bar.Children.Add(stats);

        var composer = new Grid { Padding = new Thickness(16, 6, 16, 14), ColumnSpacing = 8 };
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, L.T("Message"));
        composer.Children.Add(attach);
        // Pictures waiting to be sent sit above the message box.
        var box = new StackPanel { Spacing = 6 };
        box.Children.Add(attachments);
        box.Children.Add(attachNote);
        box.Children.Add(input);
        Grid.SetColumn(box, 1);
        composer.Children.Add(box);
        thinking.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(thinking, 2);
        composer.Children.Add(thinking);
        send.VerticalAlignment = VerticalAlignment.Bottom;
        send.Style = Ui.Resource<Style>("AccentButtonStyle", send.Style);
        Grid.SetColumn(send, 3);
        composer.Children.Add(send);

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.Children.Add(header);
        Grid.SetRow(scroller, 1);
        main.Children.Add(scroller);
        Grid.SetRow(bar, 2);
        main.Children.Add(bar);
        Grid.SetRow(composer, 3);
        main.Children.Add(composer);

        var root = new Grid();
        root.ColumnDefinitions.Add(sideColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(sideFrame);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        return root;
    }

    // MARK: Rendering

    void RenderSoon()
    {
        if (renderQueued) return;
        renderQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            renderQueued = false;
            Render();
        });
    }

    void Render()
    {
        if (attachEpoch != chat.Epoch)
        {
            // Another chat (new, loaded, temporary): pictures waiting to go were the last one's.
            attachEpoch = chat.Epoch;
            ClearAttachments();
        }
        title.Text = chat.Temporary ? L.T("Temporary chat") : chat.Title.Length > 0 ? chat.Title : L.T("New chat");
        temporary.IsChecked = chat.Temporary;

        if (chat.Messages.Count == 0)
        {
            messages.Children.Clear();
            renderedCount = 0;
            live = null;
            var empty = new StackPanel { Spacing = 6, Margin = new Thickness(0, 120, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new TextBlock { Text = chat.Temporary ? L.T("A temporary chat") : L.T("Ask anything"), FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(Ui.Caption(chat.Temporary
                ? L.T("Nothing of it is saved: it's gone when you start another chat.")
                : L.T("Answers come from the model on this PC. Your chats stay here.")));
            messages.Children.Add(empty);
        }
        else
        {
            // Only the changed tail is drawn again: the answer being written.
            if (renderedEpoch != chat.Epoch || renderedCount < 0 || renderedCount > chat.Messages.Count || messages.Children.Count != renderedCount)
            {
                messages.Children.Clear();
                renderedCount = 0;
            }
            var from = Math.Max(0, renderedCount - 1);
            var lastMessage = chat.Messages[^1];
            if (renderedCount == chat.Messages.Count && live is { } answer && answer.Message == lastMessage && lastMessage.Streaming && answer.Update())
            {
                // The answer being written grows in place: nothing on screen is made again.
            }
            else
            {
                while (messages.Children.Count > from) messages.Children.RemoveAt(messages.Children.Count - 1);
                live = null;
                for (var i = from; i < chat.Messages.Count; i++)
                {
                    var message = chat.Messages[i];
                    var isLast = i == chat.Messages.Count - 1;
                    // In place while it can show all of it; otherwise drawn whole (an MCP record, an error, a picture).
                    if (isLast && message.Streaming && message.Role == "assistant" && !message.IsSummary
                        && new LiveAnswer(this, message) is var candidate && candidate.Update())
                    {
                        live = candidate;
                        messages.Children.Add(candidate.Root);
                    }
                    else
                    {
                        messages.Children.Add(MessageView(message, isLast));
                    }
                }
            }
            renderedCount = chat.Messages.Count;
            renderedEpoch = chat.Epoch;
        }

        var last = chat.Messages.LastOrDefault();
        regenerate.Visibility = !chat.Busy && !chat.Compacting && last is { Role: "assistant" } ? Visibility.Visible : Visibility.Collapsed;
        compact.Visibility = !chat.Busy && ChatClient.CompactionRange(chat.Messages) is not null ? Visibility.Visible : Visibility.Collapsed;
        compact.IsEnabled = !chat.Compacting;
        compact.Content = chat.Compacting ? L.T("Compacting…") : L.T("Compact");
        stats.Text = last is { Role: "assistant", Stats: { } s } && !last.Streaming && s.TokensPerSecond is double tps
            ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{tps:0.0} tok/s")
            : "";
        send.Content = new FontIcon { Glyph = chat.Busy ? "" : "", FontSize = 14 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(send, chat.Busy ? L.T("Stop") : L.T("Send"));
        ToolTipService.SetToolTip(send, chat.Busy ? L.T("Stop") : L.T("Send (Enter)"));
        if (follow) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => scroller.ChangeView(null, scroller.ScrollableHeight, null, disableAnimation: true));
        RenderChatsSelection();
    }

    UIElement MessageView(ChatMessage message, bool isLast)
    {
        if (message.Role == "user")
        {
            var bubble = new Border
            {
                Background = Ui.Resource<Brush>("AccentFillColorDefaultBrush", Ui.Accent),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 640,
                Child = new TextBlock
                {
                    Text = message.Content,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Foreground = Ui.Resource<Brush>("TextOnAccentFillColorPrimaryBrush", Ui.CardBackground),
                },
            };
            var copyMenu = new MenuFlyout();
            var copyItem = new MenuFlyoutItem { Text = L.T("Copy") };
            copyItem.Click += (_, _) => Copy(message.Content);
            copyMenu.Items.Add(copyItem);
            bubble.ContextFlyout = copyMenu;
            var you = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right };
            you.Children.Add(new TextBlock { Text = L.T("You"), FontSize = 12, Foreground = Ui.Secondary, HorizontalAlignment = HorizontalAlignment.Right });
            if (PicturesView(message, HorizontalAlignment.Right) is { } pictures) you.Children.Add(pictures);
            if (message.Content.Length > 0) you.Children.Add(bubble);
            return you;
        }

        var stack = new StackPanel { Spacing = 6 };
        if (message.IsSummary)
        {
            // Compaction's stand-in for the messages it replaced.
            stack.Children.Add(Ui.Horizontal(new FontIcon { Glyph = "\uE8C6", FontSize = 12, Foreground = Ui.Secondary },
                new TextBlock { Text = L.T("Summary of earlier messages"), FontSize = 12, Foreground = Ui.Secondary }));
            stack.Children.Add(new Border
            {
                Child = new TextBlock { Text = message.Content, TextWrapping = TextWrapping.Wrap, FontStyle = Windows.UI.Text.FontStyle.Italic, IsTextSelectionEnabled = true, Foreground = Ui.Secondary },
                BorderBrush = Ui.Stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
            });
            return stack;
        }
        stack.Children.Add(Who(message));

        if (message.Reasoning.Length > 0)
        {
            var thought = new TextBlock
            {
                Text = message.Reasoning.Trim(),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = Ui.Secondary,
                FontSize = 13,
            };
            var thinkingNow = message.Streaming && message.Content.Length == 0;
            var expander = new Expander
            {
                Header = thinkingNow ? L.T("Thinking…") : L.T("Thought process"),
                Content = thought,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsExpanded = openThoughts.Contains(message),
            };
            expander.Expanding += (_, _) => openThoughts.Add(message);
            expander.Collapsed += (_, _) => openThoughts.Remove(message);
            stack.Children.Add(expander);
        }

        if (message.Content.Length > 0 || (message.Streaming && message.Reasoning.Length == 0))
        {
            var body = new StackPanel { Spacing = 8 };
            if (message.Content.Length == 0)
            {
                body.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left });
            }
            foreach (var block in Markdown.Parse(message.Content)) body.Children.Add(BlockView(block));
            stack.Children.Add(new Border
            {
                Child = body,
                Background = Ui.CardBackground,
                BorderBrush = Ui.Stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
            });
        }

        if (PicturesView(message, HorizontalAlignment.Left) is { } made) stack.Children.Add(made);

        if (message.Activity is { } activity)
        {
            var doing = Ui.Horizontal(new ProgressRing { IsActive = true, Width = 14, Height = 14 }, Ui.Caption(Activity(activity)));
            stack.Children.Add(doing);
        }

        if (message.Error is { } error)
        {
            stack.Children.Add(Ui.Caption(error, Ui.Red));
            // The chat outgrew the model's context: a summary of its middle makes room.
            if (message.ContextFull && isLast && !chat.Busy && ChatClient.CompactionRange(chat.Messages) is not null)
            {
                stack.Children.Add(Ui.Button(L.T("Compact the chat and try again"), () => _ = CompactAsync(retry: true), accent: true));
            }
        }

        if (message.McpCalls.Count > 0) stack.Children.Add(McpCallsView(message));

        if (!message.Streaming && message.Sources.Count > 0)
        {
            // Where the tools' facts came from, with the credit their services ask for.
            var sources = new StackPanel { Spacing = 2 };
            sources.Children.Add(Ui.Caption(L.T("Sources")));
            foreach (var source in message.Sources)
            {
                var line = Ui.Caption(source);
                line.IsTextSelectionEnabled = true;
                sources.Children.Add(line);
            }
            stack.Children.Add(sources);
        }

        if (!message.Streaming && message.Content.Length > 0)
        {
            var copy = new Button { Content = Ui.Horizontal(new FontIcon { Glyph = "", FontSize = 12 }, new TextBlock { Text = L.T("Copy"), FontSize = 12 }) };
            copy.Style = Ui.Resource<Style>("SubtleButtonStyle", copy.Style);
            copy.Click += (_, _) => Copy(message.Content);
            var actions = Ui.Horizontal(copy);
            if (message.Stats is { } s && s.Summary() is { Length: > 0 } summary)
            {
                var info = Ui.Caption(summary);
                info.VerticalAlignment = VerticalAlignment.Center;
                actions.Children.Add(info);
            }
            stack.Children.Add(actions);
        }
        return stack;
    }

    /// <summary>What each MCP call did, as approved: saved with the answer.</summary>
    static UIElement McpCallsView(ChatMessage message)
    {
        var calls = new StackPanel { Spacing = 2 };
        foreach (var call in message.McpCalls)
        {
            var outcome = call.Outcome switch
            {
                "done" => L.T("done"),
                "failed" => L.T("failed"),
                "denied" => L.T("denied"),
                "unknown" => L.T("outcome unknown"),
                _ => L.T("not sent"),
            };
            var line = Ui.Caption($"{call.Server} · {call.Tool} {call.Arguments} — {outcome}",
                call.Outcome is "done" ? null : call.Outcome == "denied" ? Ui.Gray : Ui.Orange);
            line.IsTextSelectionEnabled = true;
            line.MaxLines = 3;
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            calls.Children.Add(line);
        }
        return calls;
    }

    /// <summary>The app's mark, decoded once for every answer (a new image per answer would blink in).</summary>
    static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? mark;

    static UIElement Who(ChatMessage message) => Ui.Horizontal(
        new Image { Source = mark ??= new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.png"))), Width = 16, Height = 16 },
        new TextBlock { Text = message.Model ?? L.T("Assistant"), FontSize = 12, Foreground = Ui.Secondary });

    LiveAnswer? live;

    /// <summary>
    /// The answer being written, changed in place as it grows (Mac: the
    /// bubble updates, nothing blinks): its name line, thoughts, text,
    /// waiting ring and tool line are made once and kept; only the last
    /// paragraph or two of the text are made again. Anything else showing
    /// up mid-answer (an error, an MCP record, a picture) draws it whole.
    /// </summary>
    sealed class LiveAnswer(ChatWindow window, ChatMessage message)
    {
        public ChatMessage Message { get; } = message;
        UIElement? calls;
        int callsShown;
        public StackPanel Root { get; } = new() { Spacing = 6 };
        readonly UIElement who = Who(message);
        Expander? thought;
        TextBlock? thoughtText;
        Border? card;
        readonly StackPanel body = new() { Spacing = 8 };
        readonly ProgressRing waiting = new() { IsActive = true, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };
        TextBlock? doingText;
        UIElement? doing;
        int blocks;

        /// <summary>False when the answer now needs drawing whole.</summary>
        public bool Update()
        {
            var m = Message;
            // Only what this view draws: an error or a picture needs the whole view.
            if (m.Error is not null || m.Images.Count > 0 || !m.Streaming) return false;
            var parts = new List<UIElement> { who };
            if (m.Reasoning.Length > 0)
            {
                if (thought is null)
                {
                    thoughtText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = Ui.Secondary, FontSize = 13 };
                    thought = new Expander
                    {
                        Content = thoughtText,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        IsExpanded = window.openThoughts.Contains(m),
                    };
                    thought.Expanding += (_, _) => window.openThoughts.Add(m);
                    thought.Collapsed += (_, _) => window.openThoughts.Remove(m);
                }
                var header = m.Content.Length == 0 ? L.T("Thinking…") : L.T("Thought process");
                if (!Equals(thought.Header, header)) thought.Header = header;
                var reasoning = m.Reasoning.Trim();
                if (thoughtText!.Text != reasoning) thoughtText.Text = reasoning;
                parts.Add(thought);
            }
            if (m.Content.Length > 0 || m.Reasoning.Length == 0)
            {
                card ??= new Border
                {
                    Child = body,
                    Background = Ui.CardBackground,
                    BorderBrush = Ui.Stroke,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 10, 14, 10),
                };
                if (m.Content.Length == 0)
                {
                    if (body.Children.Count != 1 || body.Children[0] != waiting)
                    {
                        body.Children.Clear();
                        body.Children.Add(waiting);
                        blocks = 0;
                    }
                }
                else
                {
                    if (body.Children.Count > 0 && body.Children[0] == waiting) body.Children.Clear();
                    // Paragraphs before the last two don't change as the text grows.
                    var parsed = Markdown.Parse(m.Content);
                    var keep = Math.Min(Math.Max(0, blocks - 2), parsed.Count);
                    while (body.Children.Count > keep) body.Children.RemoveAt(body.Children.Count - 1);
                    for (var i = keep; i < parsed.Count; i++) body.Children.Add(BlockView(parsed[i]));
                    blocks = parsed.Count;
                }
                parts.Add(card);
            }
            if (m.Activity is { } activity)
            {
                if (doing is null)
                {
                    doingText = Ui.Caption("");
                    doing = Ui.Horizontal(new ProgressRing { IsActive = true, Width = 14, Height = 14 }, doingText);
                }
                doingText!.Text = Activity(activity);
                parts.Add(doing);
            }
            if (m.McpCalls.Count > 0)
            {
                // The records of MCP calls, made again only when one is added.
                if (calls is null || callsShown != m.McpCalls.Count)
                {
                    calls = McpCallsView(m);
                    callsShown = m.McpCalls.Count;
                }
                parts.Add(calls);
            }
            // Only a part coming or going changes the column, and the others stay where they are
            // (taken out and put back, an open thought would lose its place and selection).
            for (var i = Root.Children.Count - 1; i >= 0; i--)
            {
                if (!parts.Contains(Root.Children[i])) Root.Children.RemoveAt(i);
            }
            for (var i = 0; i < parts.Count; i++)
            {
                if (i < Root.Children.Count && Root.Children[i] == parts[i]) continue;
                Root.Children.Insert(i, parts[i]);
            }
            return true;
        }
    }

    /// <summary>What the answer shows while a tool runs.</summary>
    static string Activity(string tool) => tool.ToLowerInvariant() switch
    {
        "mcp" => L.T("Starting MCP servers…"),
        "approval" => L.T("Waiting for your answer…"),
        "web_search" => L.T("Searching…"),
        "get_weather" => L.T("Checking the weather…"),
        "calculate" => L.T("Calculating…"),
        "convert_currency" => L.T("Checking exchange rates…"),
        "get_country_info" => L.T("Looking up the country…"),
        "get_current_time" => L.T("Checking the time…"),
        _ => L.F("Using {0}…", tool),
    };

    static UIElement BlockView(MdBlock block)
    {
        switch (block)
        {
            case MdBlock.Heading h:
                return Text(h.Runs, size: h.Level switch { 1 => 22, 2 => 19, 3 => 16, _ => 14 }, bold: true);
            case MdBlock.Paragraph p:
                return Text(p.Runs);
            case MdBlock.Quote q:
                return new Border
                {
                    BorderBrush = Ui.Stroke,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 0, 0, 0),
                    Child = Text(q.Runs, secondary: true),
                };
            case MdBlock.List l:
            {
                var list = new StackPanel { Spacing = 3 };
                for (var i = 0; i < l.Items.Count; i++)
                {
                    var row = new Grid { ColumnSpacing = 6 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(new TextBlock { Text = l.Ordered ? $"{l.Start + i}." : "•", HorizontalAlignment = HorizontalAlignment.Right, Foreground = Ui.Secondary });
                    var text = Text(l.Items[i]);
                    Grid.SetColumn(text, 1);
                    row.Children.Add(text);
                    list.Children.Add(row);
                }
                return list;
            }
            case MdBlock.Code c:
            {
                var code = new TextBlock { Text = c.Text, FontFamily = Ui.Mono, FontSize = 13, IsTextSelectionEnabled = true };
                var copy = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
                copy.Style = Ui.Resource<Style>("SubtleButtonStyle", copy.Style);
                ToolTipService.SetToolTip(copy, L.T("Copy the code"));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, L.T("Copy the code"));
                copy.Click += (_, _) => Copy(c.Text);
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.Children.Add(Ui.Caption(c.Language.Length > 0 ? c.Language : "code"));
                grid.Children.Add(copy);
                // A long block scrolls inside itself instead of filling the conversation.
                var scroll = new ScrollViewer
                {
                    Content = code,
                    MaxHeight = 480,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Auto,
                };
                Grid.SetRow(scroll, 1);
                grid.Children.Add(scroll);
                return new Border
                {
                    Child = grid,
                    Background = Ui.Resource<Brush>("SubtleFillColorSecondaryBrush", Ui.CardBackground),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 8),
                };
            }
            case MdBlock.Table t:
            {
                var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
                var columns = Math.Max(t.Header.Count, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Count));
                for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var all = new List<IReadOnlyList<string>> { t.Header };
                all.AddRange(t.Rows);
                for (var r = 0; r < all.Count; r++)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    for (var c = 0; c < all[r].Count && c < columns; c++)
                    {
                        var cell = Text(Markdown.Inline(all[r][c]), bold: r == 0);
                        Grid.SetRow(cell, r);
                        Grid.SetColumn(cell, c);
                        grid.Children.Add(cell);
                    }
                }
                return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto };
            }
            default:
                return new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = Ui.Stroke, Margin = new Thickness(0, 4, 0, 4) };
        }
    }

    static TextBlock Text(IReadOnlyList<MdRun> runs, double size = 14, bool bold = false, bool secondary = false)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = size };
        if (bold) block.FontWeight = FontWeights.SemiBold;
        if (secondary) block.Foreground = Ui.Secondary;
        foreach (var run in runs)
        {
            Inline inline;
            var text = new Microsoft.UI.Xaml.Documents.Run { Text = run.Text };
            if (run.Bold) text.FontWeight = FontWeights.SemiBold;
            if (run.Italic) text.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (run.Strike) text.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
            if (run.Code)
            {
                text.FontFamily = Ui.Mono;
                text.Foreground = Ui.Accent;
            }
            if (run.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                var hyperlink = new Hyperlink { NavigateUri = uri };
                hyperlink.Inlines.Add(text);
                inline = hyperlink;
            }
            else
            {
                inline = text;
            }
            block.Inlines.Add(inline);
        }
        return block;
    }

    void RenderStatus()
    {
        var app = App.Current;
        var host = app.Host.State;
        var (text, color) = Status.Of(host);
        status.Text = text;
        dot.Fill = color;
        loading.IsActive = host.Status == HostStatus.Loading;
        loading.Visibility = loading.IsActive ? Visibility.Visible : Visibility.Collapsed;
        // What's running and where, or why not; the API's address when nothing is.
        var problem = host.Status == HostStatus.Failed ? host.Message : app.ProxyError;
        var running = host.Status is HostStatus.Running or HostStatus.Loading ? host.Model : null;
        var line = new List<string>();
        if (running is not null) line.Add(Ui.Size(running.SizeBytes));
        if (Status.Device(host) is { } device) line.Add(device);
        if (line.Count == 0) line.Add(L.F("API at {0}", Core.AppInfo.ApiBaseUrl));
        facts.Text = problem ?? string.Join(" · ", line);
        facts.Foreground = problem is null ? Ui.Secondary : Ui.Red;
        ToolTipService.SetToolTip(facts, facts.Text);
        // Start the picked model, or stop the one running.
        var stop = running is not null;
        startStop.Content = new FontIcon { Glyph = stop ? "\uE71A" : "\uE768", FontSize = 14 };
        var tip = stop ? L.T("Unload the model") : L.T("Load the model");
        ToolTipService.SetToolTip(startStop, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(startStop, tip);
        startStop.IsEnabled = stop || PickedOrLast() is not null;
    }

    /// <summary>The model in the picker, or the last one loaded.</summary>
    Core.Models.ModelEntry? PickedOrLast()
    {
        var catalog = App.Current.Library.Current;
        var name = (model.SelectedItem as ComboBoxItem)?.Tag as string ?? App.Current.Settings.LastModel;
        return name is null ? null : catalog.Resolve(name);
    }

    void StartOrStop()
    {
        var host = App.Current.Host.State;
        if (host.Status is HostStatus.Running or HostStatus.Loading) _ = App.Current.StopAsync();
        else if (PickedOrLast() is { } entry) _ = App.Current.LoadAsync(entry);
    }

    void ToggleSidebar()
    {
        var shown = sideColumn.Width.Value > 0;
        sideColumn.Width = new GridLength(shown ? 0 : 260);
        if (sideFrame is not null) sideFrame.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>In front now, or was until the click on the tray icon (the taskbar) took the front from it.</summary>
    public bool IsForeground => foreground || (deactivatedToTaskbar && DateTime.UtcNow - deactivatedAt < TimeSpan.FromSeconds(2));

    bool deactivatedToTaskbar;

    static bool ForegroundIsTaskbar()
    {
        var name = new System.Text.StringBuilder(64);
        return GetClassNameW(GetForegroundWindow(), name, name.Capacity) > 0
            && name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder name, int capacity);

    /// <summary>Get models (Mac: the header's download button): the download window over the chat.</summary>
    public async Task GetModelsAsync()
    {
        try
        {
            AppWindow.Show();
            Activate();
            var dialog = new DownloadDialog { XamlRoot = Content.XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog is open: the user finishes that one first.
        }
    }

    void RenderModels()
    {
        rendering = true;
        try
        {
            var picked = (model.SelectedItem as ComboBoxItem)?.Tag as string;
            var loaded = App.Current.Host.State is { Status: HostStatus.Running or HostStatus.Loading, Model: { } m } ? m.Name : null;
            model.Items.Clear();
            foreach (var entry in App.Current.Library.Current.Models)
            {
                model.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.Name });
            }
            var want = picked ?? loaded;
            model.SelectedItem = model.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == want);
            model.PlaceholderText = model.Items.Count == 0 ? L.T("No models yet: Get models (↓)") : L.T("Choose a model");
        }
        finally
        {
            rendering = false;
        }
        // Load follows the picker as it is now.
        RenderStatus();
    }

    void RenderChats()
    {
        rendering = true;
        try
        {
            var query = search.Text.Trim();
            var all = store.List().Where(c => query.Length == 0
                || c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || c.Preview.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
            chats.Items.Clear();
            string? group = null;
            foreach (var c in all)
            {
                var g = Group(c.UpdatedAt);
                if (g != group)
                {
                    group = g;
                    chats.Items.Add(new ListViewItem
                    {
                        Content = new TextBlock { Text = g, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Secondary },
                        IsEnabled = false,
                        MinHeight = 28,
                    });
                }
                var item = new ListViewItem { Content = new TextBlock { Text = c.Title, TextTrimming = TextTrimming.CharacterEllipsis }, Tag = c };
                ToolTipService.SetToolTip(item, c.Preview.Length > 0 ? c.Preview : c.Title);
                var menu = new MenuFlyout();
                var rename = new MenuFlyoutItem { Text = L.T("Rename…") };
                rename.Click += async (_, _) => await RenameAsync(c);
                var delete = new MenuFlyoutItem { Text = L.T("Delete") };
                delete.Click += async (_, _) => await DeleteAsync(c);
                menu.Items.Add(rename);
                menu.Items.Add(delete);
                item.ContextFlyout = menu;
                chats.Items.Add(item);
            }
            if (all.Count == 0)
            {
                chats.Items.Add(new ListViewItem { Content = Ui.Caption(query.Length > 0 ? L.T("No chat matches.") : L.T("Your chats appear here.")), IsEnabled = false });
            }
        }
        finally
        {
            rendering = false;
        }
        RenderChatsSelection();
    }

    void RenderChatsSelection()
    {
        rendering = true;
        try
        {
            chats.SelectedItem = chats.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag is ChatSummary s && s.Id == chat.Id);
        }
        finally
        {
            rendering = false;
        }
    }

    static string Group(DateTimeOffset when)
    {
        var day = when.ToLocalTime().Date;
        var today = DateTime.Today;
        if (day == today) return L.T("Today");
        if (day == today.AddDays(-1)) return L.T("Yesterday");
        if (day > today.AddDays(-7)) return L.T("Previous 7 days");
        if (day > today.AddDays(-30)) return L.T("Previous 30 days");
        return L.T("Older");
    }

    async Task RenameAsync(ChatSummary c)
    {
        var box = new TextBox { Text = c.Title };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = L.T("Rename chat"),
            Content = box,
            PrimaryButtonText = L.T("Rename"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
            if (c.Id == chat.Id) chat.Rename(box.Text);
            else store.Rename(c.Id, box.Text);
        }
        catch (Exception)
        {
            // Another dialog is open: nothing renamed.
        }
    }

    async Task DeleteAsync(ChatSummary c)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = L.F("Delete “{0}”?", c.Title),
            Content = L.T("The chat is removed from this PC."),
            PrimaryButtonText = L.T("Delete"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (c.Id == chat.Id) chat.New();
            store.Delete(c.Id);
        }
        catch (Exception)
        {
        }
    }

    static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    void Size(int width, int height)
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = (int)Math.Min(width * scale, area.Width * 0.95);
        var h = (int)Math.Min(height * scale, area.Height * 0.95);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Min(640 * scale, w);
            presenter.PreferredMinimumHeight = (int)Math.Min(480 * scale, h);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);
}
