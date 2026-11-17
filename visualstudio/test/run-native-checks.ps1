# Installs a Runic DevKit VSIX into a Visual Studio 2026 experimental instance and runs native-host.ps1 in the
# signed-in desktop session (README.md in this folder). Run it over SSH or from a console of the test user; it needs no
# elevation. Exit code 0 and "PASS" mean every automated check passed; the receipt, log and Visual Studio activity log
# stay next to -ReceiptPath.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$Vsix,
    [string]$Executable = "$env:ProgramFiles\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe",
    [string]$RootSuffix = 'RunicRmf2',
    [string]$ReceiptPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\native-host-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).json"),
    [string]$RunicToolVersion = '0.21.0',
    [ValidateRange(60, 3600)][int]$TimeoutSeconds = 900,
    # Check the VSIX that is already installed (native-host.ps1 still requires its version).
    [switch]$SkipInstall
)
$ErrorActionPreference = 'Stop'
$Vsix = (Resolve-Path -LiteralPath $Vsix).Path
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
if (Test-Path -LiteralPath $ReceiptPath) { throw "Receipt path already exists: $ReceiptPath" }
New-Item -ItemType Directory -Force (Split-Path $ReceiptPath) | Out-Null
$installer = Join-Path (Split-Path $Executable) 'VSIXInstaller.exe'
$running = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Executable -and $_.CommandLine -match "/RootSuffix\s+$RootSuffix(?:\s|$)" })
if ($running.Count) { throw "Close the /RootSuffix $RootSuffix instance first (process $($running.ProcessId -join ', '))." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($Vsix)
try {
    $reader = [IO.StreamReader]::new($archive.GetEntry('extension.vsixmanifest').Open())
    $identity = ([xml]$reader.ReadToEnd()).PackageManifest.Metadata.Identity
    $reader.Dispose()
}
finally { $archive.Dispose() }
if ($identity.Id -ne 'RunicArtifex.DevKit.VisualStudio') { throw "$Vsix is not Runic DevKit ($($identity.Id))." }

function Install([string[]]$arguments, [string]$what) {
    $log = "$ReceiptPath.vsixinstaller-$what.log"
    $process = Start-Process $installer -ArgumentList ($arguments + "/logFile:`"$log`"") -Wait -PassThru
    # 0: done; 1002: the extension is not installed (uninstall only).
    if ($process.ExitCode -ne 0 -and !($what -eq 'uninstall-former' -and $process.ExitCode -eq 1002)) { throw "VSIXInstaller ($what) failed with $($process.ExitCode); see $log." }
    Write-Output "VSIXInstaller $what`: exit $($process.ExitCode)"
}
# Runic MF2 Translations is the former identity (D070.5); in this test hive it would start a second language client.
if (!$SkipInstall) {
    Install @('/quiet', "/rootSuffix:$RootSuffix", '/u:Runic.Artifex.Translations.Rmf2') 'uninstall-former'
    Install @('/quiet', "/rootSuffix:$RootSuffix", "`"$Vsix`"") 'install'
    # A reinstall into an existing hive can leave a stale MEF catalog without this.
    Start-Process $Executable -ArgumentList '/RootSuffix', $RootSuffix, '/updateconfiguration' -Wait
}

# OpenSSH runs in session 0; an Interactive task of the signed-in user runs the check on the real desktop. The task
# name is unique and the task is removed in finally.
$taskName = "RunicDevKit.NativeHost.$([Guid]::NewGuid().ToString('N'))"
$script = Join-Path $PSScriptRoot 'native-host.ps1'
$arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Executable "{1}" -ReceiptPath "{2}" -RootSuffix {3} -RunicToolVersion {4} -ExpectedVersion {5}' -f `
    $script, $Executable, $ReceiptPath, $RootSuffix, $RunicToolVersion, $identity.Version
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument $arguments
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromSeconds($TimeoutSeconds + 60))
try {
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Seconds 1
        $task = Get-ScheduledTask -TaskName $taskName
        $done = $task.State -ne 'Running' -and ((Test-Path $ReceiptPath) -or (Test-Path "$ReceiptPath.error"))
    } while (!$done -and [DateTime]::UtcNow -lt $deadline)
    if (Test-Path "$ReceiptPath.log") { Get-Content "$ReceiptPath.log" }
    if (Test-Path "$ReceiptPath.error") { throw "Native check failed: $(Get-Content "$ReceiptPath.error" -Raw)" }
    if (!$done) { throw "Timed out after $TimeoutSeconds s waiting for $ReceiptPath." }
    $receipt = Get-Content $ReceiptPath -Raw | ConvertFrom-Json
    if ($receipt.sessionId -eq 0) { throw 'The receipt came from session 0, not the desktop session.' }
    Write-Output "PASS Runic DevKit $($receipt.vsixVersion) native check: $($receipt.checks.Count) checks, receipt $ReceiptPath"
}
finally {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    # The check quits its own Visual Studio; end it here if the task was stopped first.
    if (Test-Path "$ReceiptPath.running") {
        $owned = Get-Content "$ReceiptPath.running" -Raw | ConvertFrom-Json
        $process = Get-Process -Id $owned.processId -ErrorAction SilentlyContinue
        if ($process -and $process.Path -eq $owned.executable) { Stop-Process -Id $process.Id -Force }
        Remove-Item "$ReceiptPath.running"
    }
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
}
