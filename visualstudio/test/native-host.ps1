# The native Visual Studio check of Runic DevKit (VS-8; README.md in this folder). It runs in the signed-in desktop
# session, started by run-native-checks.ps1 (which installs the VSIX first) or by runic-sdk's
# run-windows-ui-automation.ps1. It starts its own Visual Studio experimental instance on a temporary copy of
# fixtures/ and quits it at the end, so no instance with the same root suffix may run. It writes progress to
# <ReceiptPath>.log, the failure to <ReceiptPath>.error and the result to <ReceiptPath>.
param(
    [string]$Executable,
    [string]$ReceiptPath,
    [string]$RootSuffix = 'RunicRmf2',
    # The dotnet-runic release that the temporary workspace's tool manifest pins for Run Doctor.
    [string]$RunicToolVersion = '0.21.0',
    # The VSIX version that must be installed (run-native-checks.ps1 passes the one it installed).
    [string]$ExpectedVersion = ''
)
$ErrorActionPreference = 'Stop'
trap { ($_.Exception.ToString() + "`n" + $_.ScriptStackTrace + "`n" + $_.InvocationInfo.PositionMessage) | Set-Content "$ReceiptPath.error"; exit 1 }
Add-Type -Path "$PSScriptRoot\VisualStudioAutomation.cs"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$checks = [Collections.Generic.List[string]]::new()
function Note([string]$text) { "$([DateTime]::UtcNow.ToString('HH:mm:ss')) $text" | Add-Content "$ReceiptPath.log" }
function Passed([string]$check) { $checks.Add($check); Note "PASS $check" }

# Visual Studio rejects automation calls while it is busy (RPC_E_CALL_REJECTED, RPC_E_SERVERCALL_RETRYLATER); a
# reading block that fails that way, or returns nothing, is tried again until the deadline.
function Busy($error) { $error.Exception -is [Runtime.InteropServices.COMException] -or $error.Exception.InnerException -is [Runtime.InteropServices.COMException] }
function Wait-For([scriptblock]$read, [string]$failure, [int]$seconds = 45) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        try { $value = & $read; if ($value) { return $value } }
        catch { if (!(Busy $_)) { throw } }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $failure
}
# A command that Visual Studio has not enabled yet (the package still scans the solution) fails with a COMException too.
function Run-Command([string]$name, [int]$seconds = 60) {
    Wait-For { $dte.ExecuteCommand($name, ''); $true } "Command $name did not run." $seconds | Out-Null
}
function Pane-Text([string]$name) { [VisualStudioAutomation]::PaneText($dte, $name) }
# Waits for a line in an Output pane; the failure shows the panes and the pane's text.
function Wait-Pane([string]$name, [scriptblock]$match, [string]$failure, [int]$seconds = 60) {
    try { return Wait-For { $text = Pane-Text $name; & $match $text } $failure $seconds }
    catch { if ($_.Exception.Message -ne $failure) { throw }; throw "$failure`nPanes: $([VisualStudioAutomation]::PaneNames($dte))`n$name pane:`n$(Pane-Text $name)" }
}
function Preview([string]$key) {
    $handle = Wait-For { $h = [VisualStudioAutomation]::FindTitle("Runic $([char]0xb7) $key"); if ($h -ne [IntPtr]::Zero) { $h } } "Preview $key did not open."
    return [Windows.Automation.AutomationElement]::FromHandle($handle)
}
function Named($window, [string]$name, $type) {
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, $type))
    return $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Like($window, [string]$pattern, $type) {
    return @($window.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, $type)) | Where-Object { $_.Current.Name -like $pattern })[0]
}
function Texts($window) {
    return (@($window.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)) | ForEach-Object {$_.Current.Name}) -join '|')
}
function Render($window) { (Named $window 'Render preview' ([Windows.Automation.ControlType]::Button)).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Close-Preview($window) { $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
function Write-Session([string]$path, $session) {
    # dotnet runic dev replaces the file by rename; do the same so the extension never reads half a file.
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $session | ConvertTo-Json -Depth 6 | Set-Content "$path.tmp" -Encoding UTF8
    Move-Item "$path.tmp" $path -Force
}

$running = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Executable -and $_.CommandLine -match "/RootSuffix\s+$RootSuffix(?:\s|$)" })
if ($running.Count) { throw "Close the /RootSuffix $RootSuffix instance first (process $($running.ProcessId -join ', ')): this check starts its own." }

# The temporary workspace: fixtures/rmf2 (Translations only) and fixtures/sdk-app (a Runic SDK Window project) in one
# solution, with a tool manifest for the language server (this repository's pin) and dotnet-runic (doctor).
$work = Join-Path $env:TEMP ('runic-vs-host-' + [Guid]::NewGuid().ToString('N'))
Copy-Item "$root\fixtures\rmf2" "$work\rmf2" -Recurse
Copy-Item "$root\fixtures\sdk-app" "$work\sdk-app" -Recurse
$manifest = Get-Content "$root\.config\dotnet-tools.json" -Raw | ConvertFrom-Json
$manifest.tools | Add-Member 'dotnet-runic' ([ordered]@{ version = $RunicToolVersion; commands = @('dotnet-runic') })
New-Item -ItemType Directory "$work\.config" | Out-Null
$manifest | ConvertTo-Json -Depth 5 | Set-Content "$work\.config\dotnet-tools.json" -Encoding UTF8
$project = "$work\sdk-app\App.csproj"
$sessionFile = "$work\sdk-app\obj\runic\dev-session.json"
$ide = $null; $standIn = $null; $preview = $null
try {
    Push-Location $work
    try {
        & dotnet tool restore 2>&1 | ForEach-Object { Note "tool restore: $_" }
        if ($LASTEXITCODE) { throw "dotnet tool restore failed ($LASTEXITCODE)." }
        # Doctor evaluates the project with the Runic Views targets, which come from the restored packages.
        & dotnet restore $project 2>&1 | ForEach-Object { Note "restore: $_" }
        if ($LASTEXITCODE) { throw "dotnet restore of the SDK fixture failed ($LASTEXITCODE)." }
        & dotnet new sln -n RunicHost 2>&1 | ForEach-Object { Note "new sln: $_" }
        $solution = @(Get-ChildItem $work -File | Where-Object { $_.Extension -in '.sln', '.slnx' })[0].FullName
        & dotnet sln $solution add $project 2>&1 | ForEach-Object { Note "sln add: $_" }
        if ($LASTEXITCODE) { throw "dotnet sln add failed ($LASTEXITCODE)." }
    }
    finally { Pop-Location }

    # 1. The VSIX is installed in this root suffix's hive, and the former extension is not (it would start a second
    #    language client for the same documents).
    $hives = @(Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory | Where-Object { $_.Name -like "18.0_*$RootSuffix" })
    $installed = @{}
    foreach ($file in $hives | ForEach-Object { Get-ChildItem "$($_.FullName)\Extensions" -Recurse -Filter extension.vsixmanifest -ErrorAction SilentlyContinue }) {
        $identity = ([xml](Get-Content $file.FullName -Raw)).PackageManifest.Metadata.Identity
        if ($identity) { $installed[$identity.Id] = $identity.Version }
    }
    $version = $installed['RunicArtifex.DevKit.VisualStudio']
    if (!$version) { throw "Runic DevKit is not installed in the $RootSuffix hive; install it with VSIXInstaller /rootSuffix:$RootSuffix." }
    if ($ExpectedVersion -and $version -ne $ExpectedVersion) { throw "Runic DevKit $version is installed, not $ExpectedVersion." }
    if ($installed['Runic.Artifex.Translations.Rmf2']) { throw "Uninstall Runic MF2 Translations (Runic.Artifex.Translations.Rmf2) from the $RootSuffix hive first." }
    Passed "VSIX $version installed in /RootSuffix $RootSuffix"

    # 2. Visual Studio opens the solution; the package autoloads on the Runic SDK project (UIContext rule).
    $activity = "$ReceiptPath.activity.xml"
    $ide = Start-Process $Executable -ArgumentList @("`"$solution`"", '/RootSuffix', $RootSuffix, '/Log', "`"$activity`"") -PassThru
    # runic-sdk's runner ends this process if the task does not finish.
    @{ processId = $ide.Id; executable = $Executable } | ConvertTo-Json | Set-Content "$ReceiptPath.running"
    Note "devenv $($ide.Id) started on $solution"
    $dte = Wait-For { [VisualStudioAutomation]::FindDte($ide.Id) } 'Visual Studio automation did not register (a first-run or sign-in dialog may be open).' 180
    Wait-For { $dte.Solution.IsOpen -and $dte.Solution.Projects.Count -ge 1 } 'The fixture solution did not open.' 180 | Out-Null
    Passed 'solution with a Runic SDK project opened'

    # 3. Every button of Commands.vsct is registered (by command set and ID). Visual Studio 18.12 gives the commands
    #    placed only on the tool window's toolbar no DTE name; the receipt lists the commands without their canonical name.
    $vsct = [xml](Get-Content "$root\visualstudio\Commands.vsct" -Raw)
    $symbols = $vsct.CommandTable.Symbols.GuidSymbol | Where-Object name -eq 'guidRunicCommands'
    $set = "{$($symbols.value.Trim('{}'))}"
    $buttons = @($vsct.CommandTable.Commands.Buttons.Button)
    $missing = @(); $unnamed = @()
    foreach ($button in $buttons) {
        $id = [Convert]::ToInt32(($symbols.IDSymbol | Where-Object name -eq $button.id).value, 16)
        $canonical = $button.Strings.CanonicalName.TrimStart('.')
        try { $name = Wait-For { $command = $dte.Commands.Item($set, $id); if ($command) { "$($command.Name)|" } } "missing" 10 }
        catch { $missing += $button.id; continue }
        if ($name.TrimEnd('|') -ne $canonical) { $unnamed += "$canonical ($($name.TrimEnd('|')))" }
    }
    if ($missing.Count -or !$buttons.Count) { throw "Commands not registered: $($missing -join ', ')" }
    if ($unnamed.Count) { Note "Registered without their canonical name: $($unnamed -join ', ')" }
    Passed "$($buttons.Count) commands of Commands.vsct registered"

    # 4. The package loads: Runic.ShowToolWindow opens the Runic tool window.
    Run-Command 'Runic.ShowToolWindow'
    $main = [Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $ide.Id).MainWindowHandle)
    foreach ($section in 'Dev Session', 'ViewModels', 'Doctor', 'Translations') {
        Wait-For { Named $main $section ([Windows.Automation.ControlType]::Group) } "The Runic tool window has no $section section." | Out-Null
    }
    Passed 'package loaded, Runic tool window open'

    # 5. The dev session follower: a session file of a running stand-in for dotnet runic dev reads as ready, in the
    #    Runic SDK Output pane and the tool window's Dev Session section; once the process ends, as stopped.
    $standIn = Start-Process powershell.exe -ArgumentList '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 900' -WindowStyle Hidden -PassThru
    $session = [ordered]@{
        schema = 'runic.application.tool.dev-session/1'; state = 'ready'; processId = $standIn.Id
        startedAt = [DateTime]::UtcNow.ToString('o'); processStartedAt = $standIn.StartTime.ToUniversalTime().ToString('o')
        project = $project; configuration = 'Debug'
        frontend = [ordered]@{ developmentServer = 'vite'; url = 'http://127.0.0.1:5173/'; port = 5173; stateUrl = $null; directory = "$work\sdk-app\Frontend"; outputDirectory = "$work\sdk-app\Frontend\dist"; generatedDirectory = $null }
        webRoot = "$work\sdk-app\bin\Debug\net10.0\www"; pages = @()
        window = [ordered]@{ owner = 'ide'; program = 'App'; processId = $null; starts = 0; environment = @{ DOTNET_ENVIRONMENT = 'Development' } }
        debugEndpoints = @()
    }
    Write-Session $sessionFile $session
    $ready = "App.csproj: dotnet runic dev ready (Window in the IDE) (process $($standIn.Id))"
    Wait-Pane 'Runic SDK' { param($text) $text.Contains($ready) } "The Runic SDK pane did not report: $ready" | Out-Null
    Wait-For { Like $main 'dotnet runic dev: ready (Window in the IDE)*' ([Windows.Automation.ControlType]::TreeItem) } 'The Dev Session section does not show the ready state.' 30 | Out-Null
    Passed 'dev session file followed: ready (Output pane and Dev Session section)'
    Run-Command 'Runic.OpenSessionFile'
    Wait-For { $dte.ActiveDocument -and $dte.ActiveDocument.FullName -eq $sessionFile } 'Open the dev Session File did not open it.' | Out-Null
    $dte.ActiveDocument.Close(2) # vsSaveChangesNo
    Passed 'Open the dev Session File'
    Stop-Process -Id $standIn.Id; $standIn = $null
    $stopped = "App.csproj: dotnet runic dev stopped (process $($session.processId) exited)"
    Wait-Pane 'Runic SDK' { param($text) $text.Contains($stopped) } "The Runic SDK pane did not report: $stopped" 30 | Out-Null
    Passed 'dev session liveness: an ended dev reads as stopped'

    # 6. Run dotnet runic doctor (the pinned tool) on the project; its summary reaches the Runic SDK pane.
    Run-Command 'Runic.RunDoctor'
    $summary = Wait-Pane 'Runic SDK' {
        param($text)
        if ($text -match 'doctor failed: [^\r\n]*') { throw "Run Doctor failed: $($Matches[0])" }
        if ($text -match 'doctor: \d+ failed, \d+ warnings?, \d+ passed') { $Matches[0] }
    } 'dotnet runic doctor did not report.' 240
    Passed "Run Doctor ($summary)"

    # 7. The language server and the Translations commands (fixtures/rmf2): preview, validation, locale, restart with
    #    an unsaved buffer.
    $fixture = "$work\rmf2"
    $opened = $dte.ItemOperations.OpenFile("$fixture\en.rmf2")
    $opened.Activate()
    $document = Wait-For {
        $candidate = $dte.ActiveDocument
        if ($candidate -and $candidate.FullName -eq "$fixture\en.rmf2" -and $candidate.Selection) { $candidate }
    } 'The temporary RMF2 editor did not activate.'
    Wait-Pane 'Runic Translations' { param($text) $text -match 'Starting [^\r\n]*lsp' } 'The language server did not start (Runic Translations pane).' | Out-Null
    Passed 'language server started'
    $document.Selection.GotoLine(5)
    [VisualStudioAutomation]::SetForegroundWindow((Get-Process -Id $ide.Id).MainWindowHandle) | Out-Null
    Run-Command 'Tools.RunicPreviewMessage'
    $preview = Preview 'payment'
    Wait-For { if ((Texts $preview) -like '*[[]shop:badge[]] Ready*') { $true } } 'Payment content did not render.' | Out-Null
    $retry = Named $preview 'Retry' ([Windows.Automation.ControlType]::Button)
    if (!$retry -or $retry.Current.IsEnabled) { throw 'Preview action must be present and inert.' }
    $count = Named $preview 'count' ([Windows.Automation.ControlType]::Edit)
    $count.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('invalid')
    Render $preview
    Wait-For { if ((Texts $preview) -match 'integer|number|numeric|input string.*invalid') { $true } } 'Invalid numeric sample did not report an error.' | Out-Null
    $count.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('1')
    Render $preview
    Wait-For { if ((Texts $preview) -like '*[[]shop:badge[]] Ready*') { $true } } 'Preview did not recover.' | Out-Null
    $locale = Named $preview 'Preview locale' ([Windows.Automation.ControlType]::ComboBox)
    $locale.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $german = Wait-For { Named $locale 'de' ([Windows.Automation.ControlType]::ListItem) } 'German locale option is missing.'
    $german.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $locale.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    # Selecting a locale renders without the button.
    Wait-For { if ((Texts $preview) -like '*Bereit*') { $true } } 'German preview did not render.' | Out-Null
    if (!(Named $preview 'Preview samples' ([Windows.Automation.ControlType]::ComboBox))) { throw 'Preview sample selector is missing.' }
    Close-Preview $preview; $preview = $null
    Passed 'Preview Message: inert rich content, invalid-number recovery, locale, samples'
    $original = [IO.File]::ReadAllText("$fixture\en.rmf2")
    $document.Activate(); $document.Selection.SelectAll(); $document.Selection.Insert($original.Replace('plain = Payment details', 'plain = UNSAVED preview marker'))
    $document.Selection.GotoLine(11)
    $before = @(Get-CimInstance Win32_Process | Where-Object {$_.ParentProcessId -eq $ide.Id -and $_.Name -eq 'dotnet.exe'} | ForEach-Object {$_.ProcessId})
    Run-Command 'Tools.RunicRestartLanguageServer'
    $after = Wait-For { Get-CimInstance Win32_Process | Where-Object {$_.ParentProcessId -eq $ide.Id -and $_.Name -eq 'dotnet.exe' -and $_.ProcessId -notin $before} } 'Server did not restart.'
    Start-Sleep -Seconds 2
    Run-Command 'Tools.RunicPreviewMessage'
    $preview = Preview 'plain'
    Wait-For { if ((Texts $preview) -like '*UNSAVED preview marker*') { $true } } 'Restart lost the unsaved buffer.' | Out-Null
    Passed 'Restart Language Server keeps the unsaved buffer'

    $receipt = [ordered]@{ sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId; windowName = 'Visual Studio Runic DevKit'
        snapshotName = 'install, package, commands, dev session, doctor, language server, preview'; vsixVersion = $version
        ideProcessId = $ide.Id; serverBefore = $before; serverAfter = @($after.ProcessId); commandsWithoutCanonicalName = $unnamed; checks = $checks }
}
finally {
    if ($preview) { try { Close-Preview $preview } catch { } }
    if ($standIn) { Stop-Process -Id $standIn.Id -ErrorAction SilentlyContinue }
    if ($ide) {
        # The instance belongs to this check: discard the edits and quit it, by force if it does not end.
        try { $dte.Documents.CloseAll(2); $dte.Quit() } catch { Note "quit: $($_.Exception.Message)" }
        if (!$ide.WaitForExit(60000)) { Stop-Process -Id $ide.Id -Force -ErrorAction SilentlyContinue }
        Remove-Item "$ReceiptPath.running" -ErrorAction SilentlyContinue
    }
    # The language server can hold the fixture as its current directory for a moment after Visual Studio quits.
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ((Test-Path $work) -and [DateTime]::UtcNow -lt $deadline) {
        try { [IO.Directory]::Delete($work, $true) } catch [IO.IOException] { Start-Sleep -Seconds 1 } catch [UnauthorizedAccessException] { Start-Sleep -Seconds 1 }
    }
    if (Test-Path $work) { Note "Could not remove $work" }
}

$receipt | ConvertTo-Json -Depth 4 | Set-Content $ReceiptPath
