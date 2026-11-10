# End-to-end check of an installed LLMTray package on a Windows runner:
# starts the app, asks the API for a model in the models folder, streams an
# answer, takes a screenshot, and checks llama-server dies with the app.
#   pwsh scripts/smoke.ps1 -Model <name> -Out <folder>
param(
    [Parameter(Mandatory)][string]$Model,
    [Parameter(Mandatory)][string]$Out,
    # Also download a model from Kaggle through the window, as a user would.
    [switch]$UiDownload
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$api = 'http://127.0.0.1:8765'

function Save-Screenshot([string]$name) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}

function Copy-Logs {
    $package = Get-AppxPackage IPSupportLLC.LLMTray
    $logs = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\logs"
    if (Test-Path $logs) { Copy-Item "$logs\*" $Out -ErrorAction SilentlyContinue }
}

try {
    $package = Get-AppxPackage IPSupportLLC.LLMTray
    if (-not $package) { throw 'LLMTray is not installed' }
    Start-Process "shell:AppsFolder\$($package.PackageFamilyName)!App"

    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Seconds 2
        try { $models = Invoke-RestMethod "$api/v1/models" } catch { $models = $null }
    } until ($models -or (Get-Date) -gt $deadline)
    if (-not $models) { throw 'the API did not come up on 8765' }
    Write-Host "models: $($models.data.id -join ', ')"
    if ($models.data.id -notcontains $Model) { throw "$Model is not listed" }
    Save-Screenshot '1-started'

    # The first-run wizard (adr/0010) opens on a fresh install: each step
    # photographed, then Finish, which opens the main window.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $wauto = [System.Windows.Automation.AutomationElement]
        $wname = [System.Windows.Automation.AutomationElement]::NameProperty
        $deadline = (Get-Date).AddSeconds(30)
        do {
            Start-Sleep -Seconds 1
            $setup = $wauto::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition($wname, 'Set Up LLMTray')))
        } until ($setup -or (Get-Date) -gt $deadline)
        if (-not $setup) { throw 'the setup wizard did not open on a fresh install' }
        function Press([string]$label) {
            $until = (Get-Date).AddSeconds(15)
            do {
                $b = $setup.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition($wname, $label)))
                if (-not $b) { Start-Sleep -Milliseconds 500 }
            } until ($b -or (Get-Date) -gt $until)
            if (-not $b) { throw "no '$label' button in the wizard" }
            $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Seconds 2
        }
        Save-Screenshot 'setup-0'
        Press 'Get Started'
        foreach ($i in 1..6) {
            Save-Screenshot "setup-$i"
            Press 'Next'
        }
        Save-Screenshot 'setup-7'
        Press 'Finish'
        $settings = Get-Content (Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\settings.json") -Raw | ConvertFrom-Json
        if ($settings.setupCompleted -ne 1) { throw "setup didn't record that it finished: $($settings.setupCompleted)" }
        $until = (Get-Date).AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 500
            $main = $wauto::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition($wname, 'LLMTray')))
        } until ($main -or (Get-Date) -gt $until)
        if (-not $main) { throw 'the main window did not open after Finish' }
        Write-Host 'setup wizard: 8 steps walked through, finished'
    }
    catch {
        Save-Screenshot 'setup-failure'
        throw
    }

    $body = @{ model = $Model; max_tokens = 48; messages = @(@{ role = 'user'; content = 'Say hello in five words.' }) } | ConvertTo-Json -Depth 5
    $started = Get-Date
    $answer = Invoke-RestMethod "$api/v1/chat/completions" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 900
    $text = $answer.choices[0].message.content
    Write-Host "answer in $([int]((Get-Date) - $started).TotalSeconds)s: $text"
    if ([string]::IsNullOrWhiteSpace($text)) { throw 'empty answer' }
    Save-Screenshot '2-loaded'

    # The download window, through UI Automation: opened, given time for
    # its reachability check, photographed, closed.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $auto = [System.Windows.Automation.AutomationElement]
        $name = [System.Windows.Automation.AutomationElement]::NameProperty
        $window = $auto::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition($name, 'LLMTray')))
        $button = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($name, 'Get models…')))
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 10
        Save-Screenshot '3-get-models'
        $cancel = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($name, 'Cancel')))
        $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    catch {
        Write-Host "download window not checked: $_"
    }

    # By now (15 s after start) the update check has run: this dev-versioned
    # package finds the published betas, so the window shows the update bar.
    Start-Sleep -Seconds 5
    Save-Screenshot '4-update'

    if ($UiDownload) {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $A = [System.Windows.Automation.AutomationElement]
        $Scope = [System.Windows.Automation.TreeScope]
        $NameProp = [System.Windows.Automation.AutomationElement]::NameProperty
        # Waits up to 30 s for an element: the UI builds itself asynchronously.
        function Wait-Element($root, [scriptblock]$match, [string]$what) {
            $deadline = (Get-Date).AddSeconds(30)
            do {
                $found = $root.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                    Where-Object { & $match $_ } | Select-Object -First 1
                if ($found) { return $found }
                Start-Sleep -Milliseconds 500
            } until ((Get-Date) -gt $deadline)
            throw "not found in the window: $what"
        }
        $wanted = 'gemma-4-E2B-it-qat-phone-Q4_0'
        # From nothing: no copy from an earlier run (or the CI cache) counts.
        $gemma = Join-Path $HOME '.llmtray/models/roman220220/gemma-4-E2B-it-qat-phone-GGUF'
        if (Test-Path $gemma) { Remove-Item $gemma -Recurse -Force }
        Invoke-RestMethod "$api/v1/models" | Out-Null
        if ((Invoke-RestMethod "$api/v1/models").data.id -contains $wanted) { throw "$wanted is listed before it was downloaded" }

        $window = $A::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($NameProp, 'LLMTray')))
        (Wait-Element $window { $args[0].Current.Name -eq 'Get models…' } 'Get models…').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        # The reachability check must be over, or it could switch the source back.
        Wait-Element $window { $args[0].Current.Name -like 'Hugging Face: *Kaggle: reachable*' } 'Kaggle reachable' | Out-Null
        (Wait-Element $window { $args[0].Current.Name -eq 'Kaggle' } 'Kaggle').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $text = Wait-Element $window { $args[0].Current.Name -like 'Gemma 4 E2B*' } 'Gemma 4 E2B'
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $item = $text
        while ($item -and $item.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { $item = $walker.GetParent($item) }
        if (-not $item) { throw 'no list item around Gemma 4 E2B' }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Seconds 1
        Save-Screenshot '5-kaggle-chosen'
        (Wait-Element $window { $args[0].Current.Name -eq 'Download' } 'Download').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 15
        Save-Screenshot '6-downloading'

        $deadline = (Get-Date).AddMinutes(15)
        $listed = @()
        do {
            Start-Sleep -Seconds 10
            try { $listed = (Invoke-RestMethod "$api/v1/models").data.id } catch { $listed = @() }
            $failed = $window.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.Name -like 'Gemma 4 E2B*:*' -and $_.Current.Name -notlike '*Downloading*' -and $_.Current.Name -notlike '*is downloaded*' } |
                Select-Object -First 1
            if ($failed) { throw "the download failed: $($failed.Current.Name)" }
        } until (($listed -contains $wanted) -or (Get-Date) -gt $deadline)
        Save-Screenshot '7-downloaded'
        if ($listed -notcontains $wanted) { throw "the Kaggle download did not finish: models are $($listed -join ', ')" }

        $package = Get-AppxPackage IPSupportLLC.LLMTray
        $settings = Get-Content (Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\settings.json") -Raw | ConvertFrom-Json
        if ($settings.downloadSource -ne 1) { throw "the download source is $($settings.downloadSource), not Kaggle (1)" }
        Write-Host "downloaded from Kaggle through the window: $wanted"
        # Not into the CI cache: the next run downloads it again.
        Remove-Item $gemma -Recurse -Force -ErrorAction SilentlyContinue
    }

    # The chat: opened from the window, a question typed and sent, the
    # answer streamed in and the chat saved with its title.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $np = [System.Windows.Automation.AutomationElement]::NameProperty
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $main = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($np, 'LLMTray')))
        $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($np, 'Chat'))).
            GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 3
        $chatWindow = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($np, 'Chat — LLMTray')))
        if (-not $chatWindow) { throw 'the chat window did not open' }
        $box = $chatWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($np, 'Message')))
        $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Write a haiku about a lighthouse, then list three facts about lighthouses as a markdown list.')
        $chatWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($np, 'Send'))).
            GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $package = Get-AppxPackage IPSupportLLC.LLMTray
        $sessions = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\sessions"
        $deadline = (Get-Date).AddMinutes(5)
        $saved = $null
        do {
            Start-Sleep -Seconds 3
            $file = Get-ChildItem $sessions -Filter *.json -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($file) {
                $json = Get-Content $file.FullName -Raw | ConvertFrom-Json
                if ($json.messages.Count -ge 2 -and $json.messages[1].content -and $json.title) { $saved = $json }
            }
        } until ($saved -or (Get-Date) -gt $deadline)
        Save-Screenshot '8-chat'
        if (-not $saved) { throw 'the chat was not answered and saved' }
        Write-Host "chat '$($saved.title)': $($saved.messages[1].content.Substring(0, [Math]::Min(80, $saved.messages[1].content.Length)))"
        Copy-Item $file.FullName (Join-Path $Out 'chat.json')
    }
    catch {
        Save-Screenshot 'chat-failure'
        throw
    }

    # Each tab of the window, for a look at the layout.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $nameProp = [System.Windows.Automation.AutomationElement]::NameProperty
        $win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition($nameProp, 'LLMTray')))
        foreach ($tab in 'Profiles', 'Server', 'General', 'Updates', 'About', 'Models') {
            $item = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($nameProp, $tab)))
            $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Seconds 2
            Save-Screenshot "tab-$($tab.ToLower())"
        }
    }
    catch {
        Write-Host "tabs not photographed: $_"
    }

    $stream = @{ model = $Model; max_tokens = 24; stream = $true; messages = @(@{ role = 'user'; content = 'Count to five.' }) } | ConvertTo-Json -Depth 5
    $streamFile = Join-Path $Out 'stream.txt'
    $stream | Set-Content -NoNewline (Join-Path $Out 'stream-request.json')
    curl.exe -sS -N -H 'Content-Type: application/json' --data-binary "@$(Join-Path $Out 'stream-request.json')" "$api/v1/chat/completions" -o $streamFile
    $events = (Select-String -Path $streamFile -Pattern '^data: ' -AllMatches).Count
    Write-Host "stream events: $events"
    if (-not (Select-String -Path $streamFile -Pattern 'data: \[DONE\]' -Quiet)) { throw 'the stream did not finish with [DONE]' }

    $unknown = curl.exe -s -o NUL -w '%{http_code}' -H 'Content-Type: application/json' --data '{"model":"gpt-4o","messages":[]}' "$api/v1/chat/completions"
    if ($unknown -ne '404') { throw "an unknown model answered $unknown, not 404" }

    # The Job Object: llama-server goes when the app goes, however it goes.
    $server = Get-Process llama-server -ErrorAction SilentlyContinue
    if (-not $server) { throw 'no llama-server process' }
    Copy-Logs
    Stop-Process -Name LLMTray -Force
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process llama-server -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if (Get-Process llama-server -ErrorAction SilentlyContinue) { throw 'llama-server outlived the app' }
    Write-Host 'smoke test passed'
}
catch {
    Save-Screenshot 'failure'
    Copy-Logs
    throw
}
