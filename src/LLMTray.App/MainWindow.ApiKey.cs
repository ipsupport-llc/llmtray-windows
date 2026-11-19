using System.Net.Sockets;
using LLMTray.Core;
using LLMTray.Core.Proxy;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>Settings › Server: the API key and the network (adr/0027).</summary>
public sealed partial class MainWindow
{
    bool showsKey;
    string? keyError;

    IEnumerable<UIElement> ApiKeyPart()
    {
        var app = App.Current;
        var settings = app.Settings;
        if (settings.RequireApiKey) app.ReadApiKey();
        var unreadable = settings.RequireApiKey && app.ApiKey is null;

        yield return Ui.Section(L.T("API key"));
        var rows = new List<UIElement?>
        {
            Ui.Row(L.T("Require an API key"),
                L.T("Clients must send this key (Authorization: Bearer <key>, or x-api-key) or get 401. LLMTray's own chat never needs it."),
                Ui.Toggle(settings.RequireApiKey, on => _ = SetRequireKeyAsync(on), L.T("Require an API key"))),
        };
        if (settings.RequireApiKey)
        {
            var key = app.ApiKey;
            var copy = Ui.Button(L.T("Copy"), () => Copy(key!));
            copy.IsEnabled = key is not null;
            rows.Add(Ui.Row(L.T("Key"),
                L.T("Paste it into the client as its API key (OPENAI_API_KEY, ANTHROPIC_API_KEY…). Kept encrypted for your Windows account."),
                Ui.Horizontal(
                    Ui.Value(key is null ? L.T("Can't be read") : showsKey ? key : new string('•', 16), mono: true),
                    Ui.Button(showsKey ? L.T("Hide") : L.T("Show"), () =>
                    {
                        showsKey = !showsKey;
                        RenderPage();
                    }),
                    copy,
                    Ui.Button(L.T("New Key…"), () => _ = NewKeyAsync()))));
            rows.Add(Ui.Row(L.T("Only for connections from other devices"),
                L.T("Apps on this PC go without the key; devices on your network need it. Off: every client of LLMTray's port needs it, this PC's too."),
                Ui.Toggle(settings.ApiKeyOnlyFromOtherDevices, on =>
                {
                    app.UpdateSettings(app.Settings with { ApiKeyOnlyFromOtherDevices = on });
                    RenderPage();
                }, L.T("Only for connections from other devices"))));
        }

        var network = Ui.Toggle(settings.ListensOnNetwork, on => _ = SetNetworkAsync(on), L.T("Allow devices on this network"));
        network.IsEnabled = settings.RequireApiKey;
        rows.Add(Ui.Row(L.T("Allow devices on this network"),
            settings.RequireApiKey
                ? L.T("Other devices on your network can use the API with the key. Windows Firewall asks once: allow private networks only.")
                : L.T("Needs the API key: turn it on first. A network isn't always one you trust (a café, a hotel)."),
            network));
        if (app.ProxyOnNetwork)
        {
            foreach (var address in ThisPc.NetworkAddresses().Take(4))
            {
                var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
                var url = $"http://{host}:{AppInfo.ProxyPort}/v1";
                rows.Add(Ui.Row(L.T("On your network"), null, Ui.Horizontal(Ui.Value(url, mono: true), Ui.Button(L.T("Copy"), () => Copy(url)))));
            }
        }
        yield return Ui.Card(rows.ToArray());
        if (settings.RequireApiKey && settings.ApiKeyOnlyFromOtherDevices && !settings.ListensOnNetwork)
        {
            yield return Ui.Caption(L.T("Only this PC can connect now (network access is off), so no client is asked for the key."));
        }
        if (keyError is not null) yield return Ui.Caption(keyError, Ui.Red);
        else if (unreadable) yield return Ui.Caption(L.T("The saved key can't be read now, so every client asked for it is refused. Make a new key."), Ui.Red);
    }

    async Task SetRequireKeyAsync(bool on)
    {
        var app = App.Current;
        keyError = null;
        if (on && app.EnsureApiKey() is null)
        {
            keyError = app.ApiKeyUnreadable
                ? L.T("The saved key can't be read now. Try again, or make a new key.")
                : L.T("The key couldn't be saved.");
            RenderPage();
            return;
        }
        // Without the key the network goes too (adr/0027).
        app.UpdateSettings(app.Settings with { RequireApiKey = on, AllowNetwork = on && app.Settings.AllowNetwork });
        await app.ApplyNetworkAsync();
        RenderPage();
    }

    async Task SetNetworkAsync(bool on)
    {
        var app = App.Current;
        app.UpdateSettings(app.Settings with { AllowNetwork = on });
        await app.ApplyNetworkAsync();
        RenderPage();
    }

    async Task NewKeyAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = L.T("Make a new key?"),
            Content = new TextBlock { Text = L.T("Clients using the current key get 401 until they're given the new one."), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = L.T("New Key"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        keyError = App.Current.NewApiKey() is null ? L.T("The key couldn't be saved.") : null;
        RenderPage();
    }
}
