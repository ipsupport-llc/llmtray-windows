using System.Runtime.InteropServices.WindowsRuntime;
using LLMTray.App.Interop;
using LLMTray.Core.Chat;
using LLMTray.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace LLMTray.App;

/// <summary>
/// Spoken messages for a model that hears (adr/0023, Mac adr/0023): a
/// microphone next to Send. A click records a clip that waits by the field
/// until the message is sent (a second click stops it; Send stops one still
/// going and sends it too); held, the button is a walkie-talkie: released
/// over it, the message goes at once; moved off it first, the recording is
/// dropped. Esc drops a recording; Ctrl+Shift+R starts or stops a clip.
/// At most 30 seconds a clip, eight a message; nothing touches the disk
/// until the chat is saved (a temporary chat's never are).
/// </summary>
sealed partial class ChatWindow
{
    /// <summary>Held longer than this, the microphone is a walkie-talkie.</summary>
    static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(300);

    readonly Button mic = new();
    readonly List<(byte[] Wav, double Seconds)> pendingAudio = [];
    /// <summary>The chat the clips and the recording belong to (its epoch): never sent into another.</summary>
    int audioEpoch = -1;
    readonly Border recordingBar = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock recordingText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly ProgressBar recordingLevel = new() { Width = 80, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
    Microphone? recorder;
    DispatcherTimer? recordingTimer;
    /// <summary>The press that may become a hold: when, and whether it started the recording.</summary>
    DateTime pressedAt;
    bool pressStarted;
    bool holding;
    bool movedOff;
    /// <summary>A pointer press already did what its Click would do.</summary>
    bool swallowClick;
    MediaPlayer? player;
    string? playing;

    void SetUpVoice()
    {
        mic.Content = new FontIcon { Glyph = "", FontSize = 14 };
        mic.Style = Ui.Resource<Style>("SubtleButtonStyle", mic.Style);
        mic.VerticalAlignment = VerticalAlignment.Center;
        mic.Width = 34;
        mic.Height = 34;
        mic.Padding = new Thickness(0);
        mic.CornerRadius = new CornerRadius(17);
        mic.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(mic, L.T("Record a spoken message: click to start and stop, or hold to talk and release to send"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mic, L.T("Record a spoken message"));
        // The button handles the pointer itself: these see its presses anyway.
        // Only the primary button (a mouse's left, a touch, a pen tip): a right-click is no press of the microphone.
        // Not the capture's loss: the button lets the capture go before its release reaches here.
        mic.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => { if (Primary(e)) MicPressed(); }), handledEventsToo: true);
        mic.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(mic).Properties.PointerUpdateKind is Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased or Microsoft.UI.Input.PointerUpdateKind.Other)
            {
                MicReleased(Inside(e));
            }
        }), handledEventsToo: true);
        mic.PointerExited += (_, _) => { if (holding) movedOff = true; };
        mic.PointerEntered += (_, _) => { if (holding) movedOff = false; };
        // The keyboard (Space, Enter on the button): a click starts or stops a clip.
        mic.Click += (_, _) =>
        {
            if (swallowClick)
            {
                swallowClick = false;
                return;
            }
            ToggleClip();
        };
        recordingLevel.Height = 4;
        recordingLevel.MinHeight = 4;
        var dot = new FontIcon { Glyph = "", FontSize = 10, Foreground = Ui.Red, VerticalAlignment = VerticalAlignment.Center };
        var row = Ui.Horizontal(dot, recordingText, recordingLevel);
        row.Spacing = 8;
        recordingBar.Child = row;
        recordingBar.Padding = new Thickness(4, 2, 4, 2);
        AddKey(Windows.System.VirtualKey.R, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, () =>
        {
            if (mic.Visibility == Visibility.Visible || recorder is not null) ToggleClip();
        });
    }

    bool Primary(PointerRoutedEventArgs e) => e.GetCurrentPoint(mic).Properties.IsLeftButtonPressed;

    bool Inside(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(mic).Position;
        return p.X >= 0 && p.Y >= 0 && p.X <= mic.ActualWidth && p.Y <= mic.ActualHeight;
    }

    /// <summary>The model the message goes to hears: the running server's word for it, else its projector's header.</summary>
    bool ModelHears()
    {
        var app = App.Current;
        var picked = (model.SelectedItem as ComboBoxItem)?.Tag as string;
        var entry = picked is null ? app.Host.State.Model : app.Library.Current.Resolve(picked);
        if (entry is null) return false;
        if (app.Host.State.Model?.FullPath == entry.FullPath && app.Host.Hears is bool hears) return hears;
        return ProjectorAbilities.Of(entry).Hears;
    }

    /// <summary>The microphone shows for a model that hears (and while it records, whatever was picked).</summary>
    void RenderMic()
    {
        mic.Visibility = ModelHears() || recorder is not null ? Visibility.Visible : Visibility.Collapsed;
        mic.Content = new FontIcon { Glyph = recorder is null ? "" : "", FontSize = 14 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mic, recorder is null ? L.T("Record a spoken message") : L.T("Stop recording"));
    }

    void MicPressed()
    {
        swallowClick = true;
        pressedAt = DateTime.UtcNow;
        movedOff = false;
        pressStarted = recorder is null;
        holding = pressStarted && StartRecording();
    }

    void MicReleased(bool over)
    {
        var held = DateTime.UtcNow - pressedAt >= HoldTime;
        var wasHolding = holding;
        holding = false;
        // A release away from the button: no Click follows to swallow.
        if (!over) swallowClick = false;
        if (wasHolding && held)
        {
            // The walkie-talkie: released over it, the message goes; moved off first, the recording is dropped.
            if (over && !movedOff)
            {
                FinishRecording();
                SendOrStop();
            }
            else
            {
                DropRecording();
            }
            return;
        }
        // A click: the press started a clip, or this one stops it.
        if (!pressStarted) FinishRecording();
    }

    void ToggleClip()
    {
        if (recorder is null) StartRecording();
        else FinishRecording();
    }

    bool StartRecording()
    {
        if (recorder is not null) return true;
        if (pendingAudio.Count >= Wav.MaxClips)
        {
            ShowAttachProblem(L.F("At most {0} recordings in one message.", Wav.MaxClips));
            return false;
        }
        // Clips left from another chat (its redraw not yet run) don't go with this one.
        if (audioEpoch != chat.Epoch) pendingAudio.Clear();
        try
        {
            recorder = Microphone.Start();
            audioEpoch = chat.Epoch;
        }
        catch (MicrophoneException e)
        {
            ShowAttachProblem(e.Message);
            return false;
        }
        var started = recorder;
        // At 30 seconds it stops itself; the clip waits by the field (or goes, when the button is still held and then released).
        started.Full += () => DispatcherQueue.TryEnqueue(() => { if (recorder == started) FinishRecording(); });
        recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        recordingTimer.Tick += (_, _) => RenderRecording();
        recordingTimer.Start();
        RenderRecording();
        RenderMic();
        return true;
    }

    void RenderRecording()
    {
        if (recorder is not { } r)
        {
            recordingBar.Visibility = Visibility.Collapsed;
            return;
        }
        var hint = holding && DateTime.UtcNow - pressedAt >= HoldTime
            ? (movedOff ? L.T("Release to drop it") : L.T("Release to send, move away to drop"))
            : L.T("Click the microphone to stop, Esc to drop");
        recordingText.Text = L.F("Recording {0} of {1}", Wav.Clock(r.Seconds), Wav.Clock(Wav.MaxSeconds)) + " · " + hint;
        recordingLevel.Value = Math.Sqrt(r.Level);
        recordingBar.Visibility = Visibility.Visible;
    }

    /// <summary>Stops the recording; the clip waits by the field.</summary>
    void FinishRecording()
    {
        if (recorder is not { } r) return;
        recorder = null;
        StopTimer();
        var wav = r.Stop();
        var seconds = Wav.Seconds(wav) ?? 0;
        // A click with nothing said (under a tenth of a second) leaves nothing.
        if (seconds >= 0.1) pendingAudio.Add((wav, seconds));
        RenderRecording();
        RenderMic();
        RenderAttachments();
    }

    /// <summary>Drops the recording under way (Esc, moved off, another chat).</summary>
    bool DropRecording()
    {
        if (recorder is not { } r) return false;
        recorder = null;
        StopTimer();
        r.Dispose();
        RenderRecording();
        RenderMic();
        return true;
    }

    void StopTimer()
    {
        recordingTimer?.Stop();
        recordingTimer = null;
    }

    /// <summary>The clips waiting by the field: their length and a remove button.</summary>
    void RenderAudioChips()
    {
        foreach (var clip in pendingAudio.ToList())
        {
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 10 }, Padding = new Thickness(4) };
            ToolTipService.SetToolTip(remove, L.T("Remove"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, L.F("Remove the recording of {0}", Wav.Clock(clip.Seconds)));
            remove.Click += (_, _) =>
            {
                pendingAudio.Remove(clip);
                RenderAttachments();
            };
            var play = PlayButton(clip.Wav, "pending-" + clip.GetHashCode());
            var label = new TextBlock { Text = Wav.Clock(clip.Seconds), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            attachments.Children.Add(new Border
            {
                Child = Ui.Horizontal(play, label, remove),
                BorderBrush = Ui.Stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    /// <summary>Takes the clips for sending: a recording still going stops and goes too.</summary>
    List<byte[]> TakeAudio()
    {
        // Another chat came on screen since they were made (Ctrl+N, then Enter before the redraw): they stay out of it.
        if (audioEpoch != chat.Epoch)
        {
            DropRecording();
            pendingAudio.Clear();
            RenderAttachments();
            return [];
        }
        FinishRecording();
        var clips = pendingAudio.Select(c => c.Wav).ToList();
        pendingAudio.Clear();
        return clips;
    }

    /// <summary>A message's recordings (and the Mac's music): a play button and the length each.</summary>
    UIElement? AudioView(ChatMessage message, HorizontalAlignment alignment)
    {
        if (message.Audios.Count == 0) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = alignment };
        for (var i = 0; i < message.Audios.Count; i++)
        {
            var data = message.Audios[i];
            var seconds = Wav.Seconds(data);
            var caption = message.AudioPrompts.ElementAtOrDefault(i) is { Length: > 0 } prompt && prompt != "Recording" ? prompt : L.T("Recording");
            var label = new TextBlock
            {
                Text = seconds is double s ? caption + " · " + Wav.Clock(s) : caption,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260,
            };
            row.Children.Add(new Border
            {
                Child = Ui.Horizontal(PlayButton(data, $"{message.GetHashCode()}-{i}"), label),
                BorderBrush = Ui.Stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2, 2, 10, 2),
            });
        }
        return row;
    }

    /// <summary>Plays a clip, or stops it when it's the one playing; one at a time.</summary>
    Button PlayButton(byte[] data, string id)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = playing == id ? "" : "", FontSize = 12 },
            Padding = new Thickness(6),
            Style = Ui.Resource<Style>("SubtleButtonStyle", null!),
        };
        ToolTipService.SetToolTip(button, L.T("Play"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L.T("Play the recording"));
        button.Click += async (_, _) =>
        {
            if (playing == id)
            {
                StopPlaying();
                button.Content = new FontIcon { Glyph = "", FontSize = 12 };
                return;
            }
            StopPlaying();
            try
            {
                var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(data.AsBuffer());
                stream.Seek(0);
                var mime = Wav.IsWav(data) ? "audio/wav" : "audio/mp4";
                player = new MediaPlayer { Source = MediaSource.CreateFromStream(stream, mime) };
                var mine = player;
                playing = id;
                button.Content = new FontIcon { Glyph = "", FontSize = 12 };
                mine.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
                {
                    if (player != mine) return;
                    StopPlaying();
                    button.Content = new FontIcon { Glyph = "", FontSize = 12 };
                });
                mine.Play();
            }
            catch (Exception)
            {
                // No audio output, or a file Windows can't play: nothing plays.
                StopPlaying();
            }
        };
        return button;
    }

    void StopPlaying()
    {
        player?.Dispose();
        player = null;
        playing = null;
    }

    /// <summary>
    /// For <c>--screenshots</c> only: whether the runner's microphone opens (written beside the
    /// pictures), then the composer with the microphone, a clip waiting and the recording bar.
    /// </summary>
    internal async Task VoiceScreenshotsAsync(string folder, Func<UIElement, string, Task> save)
    {
        string opened;
        try
        {
            using var probe = Microphone.Start();
            await Task.Delay(500);
            opened = $"opened; {probe.Seconds:0.0} s recorded";
        }
        catch (MicrophoneException e)
        {
            opened = "refused: " + e.Message;
        }
        File.WriteAllText(Path.Combine(folder, "voice.txt"), opened + Environment.NewLine);
        var wav = Wav.FromPcm16(new byte[Wav.BytesPerSecond * 7]);
        pendingAudio.Add((wav, Wav.Seconds(wav) ?? 0));
        RenderAttachments();
        mic.Visibility = Visibility.Visible;
        recordingText.Text = L.F("Recording {0} of {1}", Wav.Clock(4), Wav.Clock(Wav.MaxSeconds)) + " · " + L.T("Click the microphone to stop, Esc to drop");
        recordingLevel.Value = 0.6;
        recordingBar.Visibility = Visibility.Visible;
        await Task.Delay(800);
        if (Content is UIElement content) await save(content, Path.Combine(folder, "chat-voice.png"));
        pendingAudio.Clear();
        recordingBar.Visibility = Visibility.Collapsed;
        RenderAttachments();
        RenderMic();
    }
}
