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
        function Find-Named($root, [string]$name) {
            $root.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($NameProp, $name)))
        }
        $window = $A::RootElement.FindFirst($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($NameProp, 'LLMTray')))
        (Find-Named $window 'Get models…').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 10
        (Find-Named $window 'Kaggle').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Seconds 2
        # The list item holding the text "Gemma 4 E2B …".
        $all = $window.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $text = $all | Where-Object { $_.Current.Name -like 'Gemma 4 E2B*' } | Select-Object -First 1
        if (-not $text) { throw 'Gemma 4 E2B is not in the download window' }
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $item = $text
        while ($item -and $item.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { $item = $walker.GetParent($item) }
        if (-not $item) { throw 'no list item around Gemma 4 E2B' }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Seconds 1
        Save-Screenshot '5-kaggle-chosen'
        (Find-Named $window 'Download').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 15
        Save-Screenshot '6-downloading'
        $wanted = 'gemma-4-E2B-it-qat-phone-Q4_0'
        $deadline = (Get-Date).AddMinutes(15)
        do {
            Start-Sleep -Seconds 10
            $listed = (Invoke-RestMethod "$api/v1/models").data.id
        } until (($listed -contains $wanted) -or (Get-Date) -gt $deadline)
        Save-Screenshot '7-downloaded'
        if ($listed -notcontains $wanted) { throw "the Kaggle download did not finish: models are $($listed -join ', ')" }
        Write-Host "downloaded from Kaggle through the window: $wanted"
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
