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
Add-Type -AssemblyName System.Windows.Forms

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
# The pre-F5 guard's question: a Visual Studio message box ("Microsoft Visual Studio") whose text starts with "Runic DevKit".
function Guard-Question {
    $main = (Get-Process -Id $ide.Id).MainWindowHandle
    $windows = @([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $ide.Id)))
    if ($main -ne [IntPtr]::Zero) { $windows += @([Windows.Automation.AutomationElement]::FromHandle($main).FindAll([Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Window))) }
    foreach ($window in $windows) {
        if ($window.Current.NativeWindowHandle -eq [int]$main) { continue }
        # Visual Studio shows the title and the message as one text.
        if (@($window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)) |
            Where-Object { $_.Current.Name -like '*Runic DevKit*' }).Count) { return $window }
    }
}
function Render($window) { (Named $window 'Render preview' ([Windows.Automation.ControlType]::Button)).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Close-Preview($window) { $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
# The text of an open document (DTE TextDocument).
function Text($document) { $text = $document.Object('TextDocument'); $text.StartPoint.CreateEditPoint().GetText($text.EndPoint) }
function Find-Document([string]$path) { @($dte.Documents | Where-Object { $_.FullName -eq $path })[0] }
function Foreground { [VisualStudioAutomation]::SetForegroundWindow((Get-Process -Id $ide.Id).MainWindowHandle) | Out-Null }
# An invokable element of Visual Studio whose name matches: the light bulb's menu is a popup window of the process or
# part of the main window.
function Find-Invokable([string]$pattern) {
    $windows = @([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $ide.Id)))
    foreach ($window in $windows) {
        foreach ($element in $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
            if ($element.Current.Name -like $pattern -and $element.GetSupportedPatterns() -contains [Windows.Automation.InvokePattern]::Pattern) { return $element }
        }
    }
}
# Opens the light bulb (View.QuickActionsForPosition, Ctrl+.) at the caret and runs the action whose title matches.
function Invoke-QuickAction($document, [string]$pattern) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $document.Activate(); Foreground
        Run-Command 'View.QuickActionsForPosition'
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do { $action = Find-Invokable $pattern; if ($action) { break }; Start-Sleep -Milliseconds 300 } while ([DateTime]::UtcNow -lt $deadline)
        if ($action) {
            $title = $action.Current.Name
            $action.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            return $title.TrimEnd(' ', '.')
        }
        [Windows.Forms.SendKeys]::SendWait('{ESC}')
    }
    throw "The light bulb offered no action like '$pattern'."
}
# F2 (Refactor.Rename) at the first match of a pattern in a document, plus an offset; the server's prepareRename puts
# the current name in the rename box, which takes the new name and Enter.
function Rename-At($document, [string]$pattern, [int]$offset, [string]$current, [string]$name) {
    $document.Activate()
    $lines = (Text $document) -split "`n"
    $line = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -match $pattern })[0]
    if ($null -eq $line) { throw "No '$pattern' in $($document.FullName)." }
    $document.Selection.MoveToLineAndOffset($line + 1, [regex]::Match($lines[$line], $pattern).Index + $offset + 1)
    Foreground
    Run-Command 'Refactor.Rename'
    $main = [Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $ide.Id).MainWindowHandle)
    $box = Wait-For { @($main.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)) |
        Where-Object { $_.Current.ClassName -eq 'TextBox' -and !$_.Current.AutomationId -and $_.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $current })[0] } "F2 opened no rename box with '$current'." 20
    $box.SetFocus(); $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($name)
    Start-Sleep -Milliseconds 500
    [Windows.Forms.SendKeys]::SendWait('{ENTER}')
}
# A button of the InfoBar that Visual Studio shows for the server's window/showMessageRequest.
function InfoBar-Button([string]$message, [string]$button) {
    $main = [Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $ide.Id).MainWindowHandle)
    foreach ($bar in $main.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ClassNameProperty, 'InfoBarControl'))) {
        if ($bar.Current.Name -notlike "*$message*") { continue }
        return @($bar.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)) |
            Where-Object { $_.Current.Name -eq $button })[0]
    }
}
function Write-Session([string]$path, $session) {
    # dotnet runic dev replaces the file by rename; do the same so the extension never reads half a file.
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $session | ConvertTo-Json -Depth 6 | Set-Content "$path.tmp" -Encoding UTF8
    Move-Item "$path.tmp" $path -Force
}

$running = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Executable -and $_.CommandLine -match "/RootSuffix\s+$RootSuffix(?:\s|$)" })
if ($running.Count) { throw "Close the /RootSuffix $RootSuffix instance first (process $($running.ProcessId -join ', ')): this check starts its own." }

# The temporary workspace: fixtures/rmf2 (Translations only) and fixtures/sdk-app (a Runic SDK Window project) in one
# solution, with a tool manifest for the language server (this repository's pin) and dotnet-runic (doctor). fixtures/rmf2
# gets a minimal SDK project, so its files are project items, as the refactorings' delete expects.
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
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>' | Set-Content "$work\rmf2\Translations.csproj"
        & dotnet restore "$work\rmf2\Translations.csproj" 2>&1 | ForEach-Object { Note "restore: $_" }
        if ($LASTEXITCODE) { throw "dotnet restore of the Translations project failed ($LASTEXITCODE)." }
        & dotnet sln $solution add "$work\rmf2\Translations.csproj" 2>&1 | ForEach-Object { Note "sln add: $_" }
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
    Wait-For { $dte.Solution.IsOpen -and $dte.Solution.Projects.Count -ge 2 } 'The fixture solution did not open.' 180 | Out-Null
    # The solution lists rmf2\Translations.csproj first; F5 (5b) starts the Runic SDK project.
    # (dotnet sln puts each project in a solution folder, so the startup project is named by its relative path.)
    Wait-For { $dte.Solution.SolutionBuild.StartupProjects = 'sdk-app\App.csproj'; @($dte.Solution.SolutionBuild.StartupProjects) -contains 'sdk-app\App.csproj' } 'The Runic SDK project could not be made the startup project.' 60 | Out-Null
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

    # 5b. The pre-F5 guard (D070.6): F5 with no dev --no-window running asks first; Cancel starts nothing. The key press
    #     is posted to Visual Studio's window: launched through DTE from this process, the question would open inside
    #     that automation call, and Visual Studio would not take the answer.
    $main = (Get-Process -Id $ide.Id).MainWindowHandle
    [VisualStudioAutomation]::SetForegroundWindow($main) | Out-Null
    [VisualStudioAutomation]::PostKey($main, 0x74) # VK_F5
    $question = Wait-For { Guard-Question } 'F5 did not ask whether to start dotnet runic dev --no-window.' 60
    $text = (Texts $question)
    if ($text -notlike '*dotnet runic dev --no-window does not run for App.csproj*') { throw "Unexpected F5 question: $text" }
    $cancel = @($question.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)) |
        Where-Object { ($_.Current.Name -replace '&', '') -match '^(Cancel|Abbrechen)$' })[0]
    $cancel.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { -not (Guard-Question) } 'The F5 question stayed open after Cancel.' 30 | Out-Null
    Start-Sleep -Seconds 3
    $mode = Wait-For { [VisualStudioAutomation]::Get([VisualStudioAutomation]::Get($dte, 'Debugger'), 'CurrentMode') } 'The debugger mode is unreadable.'
    if ($mode -ne 1) { throw "Cancel in the F5 question still started debugging (debugger mode $mode)." }
    Passed 'F5 asks to start dotnet runic dev --no-window; Cancel starts nothing'

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
    Close-Preview $preview; $preview = $null

    # 8. The Translations refactorings through the language server's protocol 2 (Translations 0.14). Visual Studio has
    #    no `delete` resource operation, so Inline Resource empties the child, says so in its title, and DevKit deletes
    #    the child through its project item once it and the file that received its resources are saved. The workspace is
    #    mixed (sdk-app holds C#), so renaming a resource asks first through window/showMessageRequest (an InfoBar).
    $document.Activate(); $document.Selection.SelectAll(); $document.Selection.Insert($original); $document.Save() | Out-Null
    $child = "$fixture\account\en.rmf2"
    $dte.ItemOperations.OpenFile($child).Activate() | Out-Null
    $childDocument = Wait-For { Find-Document $child } 'account/en.rmf2 did not open.'
    $childDocument.Selection.GotoLine(2)
    $title = Invoke-QuickAction $childDocument 'Inline into*'
    if ($title -ne 'Inline into ../en.rmf2 (leaves en.rmf2 empty)') { throw "Unexpected Inline Resource title: $title" }
    Wait-For { (Text $childDocument) -eq '' -and (Text $document) -match 'heading = Your account' } 'Inline Resource did not move account/en.rmf2 into en.rmf2.' | Out-Null
    if ((Get-Item $child).Length -eq 0) { throw 'Inline Resource emptied account/en.rmf2 on disk before a save.' }
    Wait-Pane 'Runic Translations' { param($text) $text.Contains('Inline Resource applied') } 'DevKit did not follow the applied Inline Resource.' | Out-Null
    $childDocument.Save() | Out-Null
    Start-Sleep -Seconds 3
    if (!(Test-Path $child)) { throw 'The emptied account/en.rmf2 was deleted while en.rmf2 still held its resources unsaved.' }
    $document.Save() | Out-Null
    Wait-For { !(Test-Path $child) } 'The emptied account/en.rmf2 was not deleted after both files were saved.' 30 | Out-Null
    Wait-For { !(Find-Document $child) -and !$dte.Solution.FindProjectItem($child) } 'The deleted account/en.rmf2 is still open or a project item.' 30 | Out-Null
    if ([IO.File]::ReadAllText("$fixture\en.rmf2") -notmatch 'heading = Your account') { throw 'en.rmf2 on disk lacks the inlined resources.' }
    Passed "Inline Resource ('$title'); the emptied child is deleted through its project item once both files are saved"

    $document.Selection.StartOfDocument()
    $lines = (Text $document) -split "`n"
    $group = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -match '^account\b' })[0]
    $document.Selection.MoveToLineAndOffset($group + 1, 3)
    $title = Invoke-QuickAction $document 'Extract group*'
    Wait-For { Find-Document $child } 'Extract Group did not create account/en.rmf2.' | Out-Null
    $dte.Documents.SaveAll()
    Wait-For { (Test-Path $child) -and [IO.File]::ReadAllText($child) -match 'heading = Your account' -and [IO.File]::ReadAllText("$fixture\en.rmf2") -notmatch '(?m)^account\b' } 'Extract Group did not move the account group back.' | Out-Null
    Passed "Extract Group ('$title')"

    # An emptied child that the user writes into before saving is kept.
    $childDocument = Find-Document $child
    $childDocument.Selection.GotoLine(2)
    Invoke-QuickAction $childDocument 'Inline into*' | Out-Null
    Wait-For { (Text $childDocument) -eq '' } 'Inline Resource did not empty account/en.rmf2.' | Out-Null
    $childDocument.Activate(); $childDocument.Selection.Insert("# Kept by the user`n")
    $dte.Documents.SaveAll()
    Wait-Pane 'Runic Translations' { param($text) $text.Contains('is not empty any more; it is kept') } 'DevKit did not keep the edited child.' | Out-Null
    Start-Sleep -Seconds 2
    if (!(Test-Path $child) -or [IO.File]::ReadAllText($child) -notmatch 'Kept by the user') { throw 'An emptied child the user wrote into was deleted.' }
    Passed 'Inline Resource: a child the user writes into before saving is kept'

    Rename-At $document '\.input \{\$count' 9 'count' 'attempts'
    Wait-For { (Text $document) -match '\{\$attempts :integer\}' } 'F2 did not rename the input.' | Out-Null
    $dte.Documents.SaveAll()
    Wait-For { [IO.File]::ReadAllText("$fixture\de.rmf2") -match '\$attempts' } 'F2 on an input did not rename it in de.rmf2.' | Out-Null
    Passed 'F2 renames an input in every locale'
    Rename-At $document 'ref=retry' 4 'retry' 'again'
    Wait-For { (Text $document) -match 'ref=again' } 'F2 did not rename the slot reference.' | Out-Null
    $dte.Documents.SaveAll()
    Wait-For { [IO.File]::ReadAllText("$fixture\runic.json") -match '"again"' } 'F2 on a slot did not rename it in runic.json.' | Out-Null
    Passed 'F2 renames a slot reference and its runic.json declaration'
    $question = 'Rename in resource sources?'
    Rename-At $document '^plain =' 2 'plain' 'summary'
    $cancel = Wait-For { InfoBar-Button $question 'Cancel' } 'Renaming a resource in a mixed workspace did not ask (window/showMessageRequest InfoBar).' 30
    $cancel.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { !(InfoBar-Button $question 'Cancel') } 'The rename question stayed open after Cancel.' | Out-Null
    Start-Sleep -Seconds 2
    if ((Text $document) -notmatch '(?m)^plain =') { throw 'Cancel in the rename question still renamed the resource.' }
    Rename-At $document '^plain =' 2 'plain' 'details'
    $confirm = Wait-For { InfoBar-Button $question 'Rename in resource sources' } 'The rename question did not come back.' 30
    $confirm.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { (Text $document) -match '(?m)^details =' } 'Confirming the question did not rename the resource.' | Out-Null
    $dte.Documents.SaveAll()
    Wait-For { [IO.File]::ReadAllText("$fixture\de.rmf2") -match '(?m)^details =' } 'The resource rename did not reach de.rmf2.' | Out-Null
    Passed 'F2 on a resource in a mixed workspace asks through window/showMessageRequest; Cancel keeps it, the confirmation renames it'

    $receipt = [ordered]@{ sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId; windowName = 'Visual Studio Runic DevKit'
        snapshotName = 'install, package, commands, dev session, doctor, language server, preview, refactorings'; vsixVersion = $version
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
