using LLMTray.Core.Mcp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>
/// The Tools tab (adr/0012, Mac Settings › Tools): the user's MCP servers.
/// None until one is added; adding one starts it, lists its tools and
/// switches it on (Mac: one click); every call is still asked unless a
/// tool is allowed to run without asking.
/// </summary>
public sealed partial class MainWindow
{
    string? mcpError;
    readonly HashSet<string> mcpBusy = [];
    readonly HashSet<string> mcpLogOpen = [];

    IEnumerable<UIElement> ToolsPage()
    {
        var app = App.Current;
        var servers = app.Mcp.Store.Load();
        foreach (var part in FoldersSection()) yield return part;
        foreach (var part in ProjectFilesSection()) yield return part;
        yield return Ui.Section(L.T("MCP servers"));
        var intro = Ui.Card(
            Ui.Row(L.T("Add a server"),
                L.T("A server gives the chat its tools: mail, files, a company's systems. Nothing runs until you add one, and every call is asked unless you let a tool run without asking. Temporary chats never use them."),
                null),
            Ui.ButtonRow(
                [Ui.Button(L.T("Add a Command…"), () => _ = AddMcpAsync(command: true)), Ui.Button(L.T("Add a URL…"), () => _ = AddMcpAsync(command: false))],
                [Ui.Button(L.T("Paste a Config…"), () => _ = PasteMcpAsync())]));
        yield return intro;
        if (mcpError is { } error) yield return Ui.Caption(error, Ui.Red);
        if (servers.Count == 0) yield break;

        foreach (var server in servers)
        {
            yield return Ui.Section(server.Name);
            var state = app.Mcp.State(server);
            var key = server.Key;
            var toggle = Ui.Toggle(server.Enabled, on => _ = SetMcpOnAsync(server, on), L.F("{0} on", server.Name));
            toggle.IsEnabled = !mcpBusy.Contains(key);
            var signingIn = app.McpSignIn.IsSigningIn(key);
            var status = signingIn ? L.T("Finish signing in in your browser…")
                : mcpBusy.Contains(key) ? L.T("Starting…")
                : state.NeedsSignIn ? L.T("Needs sign-in")
                : McpStatus(state);
            var rows = new List<UIElement?>
            {
                Ui.Row(server.IsCommand ? L.T("Command") : L.T("URL"), status, Ui.Horizontal(Ui.Value(server.Summary, mono: true), toggle)),
            };
            if (!server.IsCommand)
            {
                // Signing in (adr/0015): the browser shows who and what; Settings shows where.
                if (signingIn)
                {
                    rows.Add(Ui.Row(L.T("Sign-in"), L.T("A page opened in your browser. Sign in there; LLMTray waits up to 5 minutes."),
                        Ui.Button(L.T("Cancel"), () => { if (mcpSignIns.TryGetValue(key, out var cancel)) cancel.Cancel(); })));
                }
                else if (state.NeedsSignIn)
                {
                    rows.Add(Ui.Row(L.T("Sign-in"),
                        server.OAuth is { } was ? L.F("Signed out of {0}. Sign in again to use its tools.", was.IssuerHost) : L.T("This server asks you to sign in. Your browser opens to do it."),
                        Ui.Button(L.T("Sign In"), () => _ = SignInMcpAsync(server, justAdded: false), accent: true)));
                }
                else if (server.OAuth is { } oauth)
                {
                    var where = oauth.Scope is { Length: > 0 } scope ? L.F("At {0} · {1}", oauth.IssuerHost, scope) : L.F("At {0}", oauth.IssuerHost);
                    var buttons = new List<UIElement>();
                    if (oauth.StepUpScope is { Length: > 0 })
                    {
                        where += "\n" + L.T("A call needed more access than was granted. Sign in again to allow it.");
                        buttons.Add(Ui.Button(L.T("Sign In Again"), () => _ = SignInMcpAsync(server, justAdded: false), accent: true));
                    }
                    buttons.Add(Ui.Button(L.T("Sign Out"), () => _ = SignOutMcpAsync(server)));
                    rows.Add(Ui.Row(L.T("Signed in"), where, Ui.Horizontal(buttons.ToArray())));
                }
            }
            if (state.Tools is { } tools)
            {
                foreach (var tool in tools)
                {
                    var reviewed = server.Reviewed.TryGetValue(tool.Name, out var hash) && hash == tool.DefinitionHash;
                    var help = tool.Description.Length > 240 ? tool.Description[..240] + "…" : tool.Description;
                    if (tool.ReadOnlyHint == true) help += " " + L.T("(the server says it only reads)");
                    if (!reviewed)
                    {
                        rows.Add(Ui.Row(tool.Title ?? tool.Name, L.T("New or changed since you reviewed it: hidden from the chat until you accept it."),
                            Ui.Button(L.T("Accept"), () => _ = AcceptMcpToolAsync(server, tool.Name, tool.DefinitionHash))));
                        continue;
                    }
                    var free = server.RunWithoutAsking.Contains(tool.DefinitionHash);
                    var ask = Ui.Toggle(free, on => app.Mcp.Store.Update(key, c =>
                    {
                        var set = new HashSet<string>(c.RunWithoutAsking);
                        if (on) set.Add(tool.DefinitionHash);
                        else set.Remove(tool.DefinitionHash);
                        return c with { RunWithoutAsking = set };
                    }), L.F("Run {0} without asking", tool.Name));
                    ToolTipService.SetToolTip(ask, L.T("Run without asking"));
                    rows.Add(Ui.Row(tool.Title ?? tool.Name, help, Ui.Horizontal(Ui.Caption(L.T("Run without asking")), ask)));
                }
            }
            else if (server.Enabled || state.Running)
            {
                rows.Add(Ui.Row(L.T("Tools"), null, Ui.Button(L.T("Show Tools"), () => _ = ShowMcpToolsAsync(server))));
            }
            rows.Add(Ui.ButtonRow(
                [
                    Ui.Button(mcpLogOpen.Contains(key) ? L.T("Hide Log") : L.T("Show Log"), () =>
                    {
                        if (!mcpLogOpen.Remove(key)) mcpLogOpen.Add(key);
                        RenderPage();
                    }),
                    Ui.Button(L.T("Restart"), () => _ = RestartMcpAsync(server)),
                ],
                [Ui.Button(L.T("Remove"), () => _ = RemoveMcpAsync(server))]));
            if (mcpLogOpen.Contains(key))
            {
                rows.Add(new TextBox
                {
                    Text = state.Log.Count == 0 ? L.T("Nothing logged yet.") : string.Join("\n", state.Log),
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = Ui.Mono,
                    MaxHeight = 260,
                    Margin = new Thickness(16, 0, 16, 12),
                });
            }
            yield return Ui.Card(rows.ToArray());
        }
    }

    static string McpStatus(McpServerState state) => state.Status switch
    {
        "running" => L.T("Running"),
        "not started" => state.Config.Enabled ? L.T("Starts when a chat needs it") : L.T("Off"),
        "starting" => L.T("Starting…"),
        "stopped" => L.T("Stopped"),
        var other => other,
    };

    /// <summary>Sign-ins open in the browser, to cancel from the row.</summary>
    readonly Dictionary<string, CancellationTokenSource> mcpSignIns = [];

    /// <summary>
    /// Signs in (the browser), then starts the server with its token and
    /// switches it on. A server just added that can't be signed in to goes again, with why.
    /// </summary>
    async Task SignInMcpAsync(McpServerConfig server, bool justAdded)
    {
        var app = App.Current;
        var key = server.Key;
        if (mcpSignIns.ContainsKey(key)) return;
        mcpError = null;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        mcpSignIns[key] = cancel;
        RenderPage();
        try
        {
            await app.McpSignIn.SignInAsync(key, app.Mcp.Challenge(key), cancel.Token);
            await app.Mcp.SignedInAsync(key);
            mcpBusy.Add(key);
            RenderPage();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (app.Mcp.Current(key) is { } now) await app.Mcp.EnableAsync(now, timeout.Token);
        }
        catch (Exception e)
        {
            var why = e is OperationCanceledException ? L.T("the sign-in was cancelled") : e.Message;
            if (justAdded)
            {
                await app.Mcp.StopAsync(key);
                app.McpSignIn.Forget(key);
                app.Mcp.Store.Remove(key);
                mcpError = L.F("{0} wasn't added: {1}", server.Name, why);
            }
            else
            {
                mcpError = L.F("{0}: {1}", server.Name, why);
            }
        }
        finally
        {
            mcpSignIns.Remove(key);
            mcpBusy.Remove(key);
        }
        RenderPage();
    }

    async Task SignOutMcpAsync(McpServerConfig server)
    {
        mcpBusy.Add(server.Key);
        RenderPage();
        await App.Current.McpSignIn.SignOutAsync(server.Key);
        await App.Current.Mcp.StopAsync(server.Key);
        mcpBusy.Remove(server.Key);
        RenderPage();
    }

    async Task SetMcpOnAsync(McpServerConfig server, bool on, bool justAdded = false)
    {
        var app = App.Current;
        mcpError = null;
        if (!on)
        {
            app.Mcp.Store.Update(server.Key, c => c with { Enabled = false });
            await app.Mcp.StopAsync(server.Key);
            RenderPage();
            return;
        }
        mcpBusy.Add(server.Key);
        RenderPage();
        try
        {
            // Started and switched on. A new server's tools are taken as reviewed; one
            // switched on again keeps its reviews, and what changed waits for its own Accept.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (app.Mcp.Current(server.Key) is { } now) await app.Mcp.EnableAsync(now, timeout.Token);
        }
        catch (McpAuthException)
        {
            // It asks to sign in: a server just added signs in now (it stays only if that works).
            mcpBusy.Remove(server.Key);
            if (justAdded)
            {
                await SignInMcpAsync(server, justAdded: true);
                return;
            }
            mcpError = L.F("{0} asks you to sign in.", server.Name);
        }
        catch (Exception e)
        {
            mcpError = L.F("{0} didn't start: {1}", server.Name, e.Message);
            mcpLogOpen.Add(server.Key);
        }
        mcpBusy.Remove(server.Key);
        RenderPage();
    }

    /// <summary>This one tool as the server lists it now; the others stay as they were.</summary>
    async Task AcceptMcpToolAsync(McpServerConfig server, string tool, string seenHash)
    {
        mcpBusy.Add(server.Key);
        RenderPage();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (App.Current.Mcp.Current(server.Key) is { } now) await App.Current.Mcp.AcceptToolAsync(now, tool, seenHash, timeout.Token);
            mcpError = null;
        }
        catch (Exception e)
        {
            mcpError = L.F("{0} didn't start: {1}", server.Name, e.Message);
        }
        mcpBusy.Remove(server.Key);
        RenderPage();
    }

    /// <summary>One dialog at a time on this page (a second ShowAsync would throw).</summary>
    bool mcpDialogOpen;

    async Task<ContentDialogResult> ShowMcpDialogAsync(ContentDialog dialog)
    {
        if (mcpDialogOpen) return ContentDialogResult.None;
        mcpDialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception)
        {
            return ContentDialogResult.None;
        }
        finally
        {
            mcpDialogOpen = false;
        }
    }

    async Task ShowMcpToolsAsync(McpServerConfig server)
    {
        mcpBusy.Add(server.Key);
        RenderPage();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await App.Current.Mcp.ToolsAsync(server, timeout.Token);
        }
        catch (Exception e)
        {
            mcpError = L.F("{0} didn't start: {1}", server.Name, e.Message);
            mcpLogOpen.Add(server.Key);
        }
        mcpBusy.Remove(server.Key);
        RenderPage();
    }

    async Task RestartMcpAsync(McpServerConfig server)
    {
        mcpBusy.Add(server.Key);
        RenderPage();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await App.Current.Mcp.RestartAsync(server, timeout.Token);
            mcpError = null;
        }
        catch (Exception e)
        {
            mcpError = L.F("{0} didn't start: {1}", server.Name, e.Message);
            mcpLogOpen.Add(server.Key);
        }
        mcpBusy.Remove(server.Key);
        RenderPage();
    }

    async Task RemoveMcpAsync(McpServerConfig server)
    {
        if (Content?.XamlRoot is not { } root) return;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L.F("Remove {0}?", server.Name),
            Content = L.T("Its settings go; the chat no longer has its tools."),
            PrimaryButtonText = L.T("Remove"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowMcpDialogAsync(dialog) != ContentDialogResult.Primary) return;
        if (App.Current.McpSignIn.Tokens(server.Key) is not null) await App.Current.McpSignIn.SignOutAsync(server.Key);
        await App.Current.Mcp.StopAsync(server.Key);
        App.Current.McpSignIn.Forget(server.Key);
        App.Current.Mcp.Store.Remove(server.Key);
        RenderPage();
    }

    async Task AddMcpAsync(bool command)
    {
        if (Content?.XamlRoot is not { } root) return;
        var name = new TextBox { Header = L.T("Name"), PlaceholderText = command ? "files" : "company" };
        var main = new TextBox
        {
            Header = command ? L.T("Command") : L.T("URL"),
            PlaceholderText = command ? "npx" : "https://example.com/mcp",
            FontFamily = Ui.Mono,
        };
        var extra = new TextBox
        {
            Header = command ? L.T("Arguments, one per line") : L.T("Headers, one per line (Name: value)"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
            FontFamily = Ui.Mono,
        };
        var env = new TextBox
        {
            Header = L.T("Environment, one per line (NAME=value)"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 50,
            FontFamily = Ui.Mono,
            Visibility = command ? Visibility.Visible : Visibility.Collapsed,
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 440 };
        panel.Children.Add(name);
        panel.Children.Add(main);
        panel.Children.Add(extra);
        panel.Children.Add(env);
        panel.Children.Add(Ui.Caption(L.T("It starts right away, so its tools can be listed. Add only servers you trust: a server can do anything its command or service can.")));
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = command ? L.T("Add a Command") : L.T("Add a URL"),
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = L.T("Add"),
            CloseButtonText = L.T("Cancel"),
            // Enter in a field mustn't add (and start) a half-typed server.
            DefaultButton = ContentDialogButton.None,
        };
        if (await ShowMcpDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var lines = extra.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        McpServerConfig config;
        if (command)
        {
            if (main.Text.Trim().Length == 0) return;
            var envs = env.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && p[0].Trim().Length > 0)
                .ToDictionary(p => p[0].Trim(), p => p[1]);
            config = new McpServerConfig { Name = Named(name.Text, main.Text), Command = main.Text.Trim(), Arguments = lines, Environment = envs };
        }
        else
        {
            if (!Uri.TryCreate(main.Text.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
            {
                mcpError = L.T("That isn't a web address.");
                RenderPage();
                return;
            }
            var headers = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Length > 0)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
            if (headers.Count > 0 && url.Scheme == "http" && !url.IsLoopback)
            {
                // A token in a header would cross the network in the clear.
                mcpError = L.T("Headers go only to an https:// address (or this PC).");
                RenderPage();
                return;
            }
            config = new McpServerConfig { Name = Named(name.Text, url.Host), Url = url.AbsoluteUri, Headers = headers };
        }
        var added = App.Current.Mcp.Store.Add(config);
        await SetMcpOnAsync(added, true, justAdded: true);
    }

    static string Named(string typed, string fallback)
    {
        var name = typed.Trim();
        if (name.Length > 0) return name;
        var file = Path.GetFileNameWithoutExtension(fallback.Trim());
        return file.Length > 0 ? file : "server";
    }

    async Task PasteMcpAsync()
    {
        if (Content?.XamlRoot is not { } root) return;
        var text = new TextBox
        {
            PlaceholderText = "{ \"mcpServers\": { … } }",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 200,
            MinWidth = 460,
            FontFamily = Ui.Mono,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L.T("Paste a Config"),
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Ui.Caption(L.T("The JSON a server's README gives (\"mcpServers\"). Each server in it is added and started.")),
                    text,
                },
            },
            PrimaryButtonText = L.T("Add"),
            CloseButtonText = L.T("Cancel"),
            // Enter in a field mustn't add (and start) a half-typed server.
            DefaultButton = ContentDialogButton.None,
        };
        if (await ShowMcpDialogAsync(dialog) != ContentDialogResult.Primary) return;
        IReadOnlyList<McpServerConfig> servers;
        try
        {
            servers = McpImport.FromJson(text.Text);
        }
        catch (FormatException e)
        {
            mcpError = L.F("That config couldn't be read: {0}", e.Message);
            RenderPage();
            return;
        }
        foreach (var server in servers) await SetMcpOnAsync(App.Current.Mcp.Store.Add(server), true, justAdded: true);
    }
}
