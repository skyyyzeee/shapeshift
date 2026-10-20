# Shapeshift: установка программ при первом входе. Сценарий создан движком Shapeshift.
# Журнал — programs.log рядом. Сбой одной программы не останавливает остальные.
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$log = Join-Path $root 'programs.log'
$timeoutMinutes = 30

$programs = @(
    @{ Name = 'Telegram.TelegramDesktop'; Winget = 'Telegram.TelegramDesktop' }
    @{ Name = 'Marker ''test'''; Folder = '01'; Run = 'marker.cmd'; Args = 'C:\ProgramData\marker.txt' }
    @{ Name = 'Office'; Folder = '02'; Run = 'setup.exe'; Args = '/configure configuration.xml' }
)

function Write-Log([string] $Text) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $Text
    Add-Content -Path $log -Value $line -Encoding UTF8
    Write-Host $line
}

# Ждёт процесс не дольше таймаута; код выхода или $null, если пришлось прервать.
function Wait-Installer($Process) {
    $null = $Process.Handle  # без этого Start-Process -PassThru не отдаёт код выхода
    if ($Process.WaitForExit($timeoutMinutes * 60 * 1000)) { return $Process.ExitCode }
    try { $Process.Kill() } catch { }
    return $null
}

function Install-Local($Program) {
    $folder = Join-Path $root "Programs\$($Program.Folder)"
    $path = Join-Path $folder $Program.Run
    $extra = $Program.Args
    switch ([IO.Path]::GetExtension($path).ToLowerInvariant()) {
        '.msi' { $file = 'msiexec.exe'; $arguments = "/i `"$path`" /qn /norestart $extra" }
        { $_ -in '.cmd', '.bat' } { $file = 'cmd.exe'; $arguments = "/c `"`"$path`" $extra`"" }
        '.ps1' { $file = 'powershell.exe'; $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$path`" $extra" }
        default { $file = $path; $arguments = $extra }
    }
    $start = @{ FilePath = $file; WorkingDirectory = (Split-Path $path -Parent); PassThru = $true }
    if ($arguments.Trim()) { $start.ArgumentList = $arguments.Trim() }
    $code = Wait-Installer (Start-Process @start)
    if ($null -eq $code) { return "stopped after $timeoutMinutes min" }
    # 3010 и 1641 — установлено, нужна перезагрузка.
    if ($code -in 0, 3010, 1641) { return $null }
    return "exit code $code"
}

$script:winget = $null
$script:online = $null

# winget регистрируется у нового пользователя не сразу после входа — ждём и подталкиваем.
function Find-Winget {
    if ($script:winget) { return $script:winget }
    $candidate = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
    for ($i = 0; $i -lt 24; $i++) {
        if (Test-Path $candidate) { $script:winget = $candidate; return $candidate }
        if ($i -eq 0) {
            try { Add-AppxPackage -RegisterByFamilyName -MainPackage 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' -ErrorAction Stop } catch { }
        }
        Start-Sleep -Seconds 5
    }
    return $null
}

# Сеть могла ещё не подняться — ждём до минуты.
function Test-Online {
    if ($null -ne $script:online) { return $script:online }
    for ($i = 0; $i -lt 12; $i++) {
        try { $null = [Net.Dns]::GetHostAddresses('cdn.winget.microsoft.com'); $script:online = $true; return $true } catch { }
        Start-Sleep -Seconds 5
    }
    $script:online = $false
    return $false
}

function Install-Winget($Program) {
    if (-not (Test-Online)) { return 'skipped: no internet connection' }
    $winget = Find-Winget
    if (-not $winget) { return 'skipped: winget (App Installer) is not available' }
    & $winget install --id $Program.Winget --exact --source winget --silent --disable-interactivity `
        --accept-package-agreements --accept-source-agreements | Out-Null
    # 0x8A15002B: уже установлено и обновлять нечего.
    if ($LASTEXITCODE -in 0, -1978335189) { return $null }
    return ('exit code 0x{0:X8}' -f $LASTEXITCODE)
}

Write-Log "Installing $($programs.Count) program(s)"
foreach ($program in $programs) {
    Write-Log "Installing $($program.Name)"
    try {
        $problem = if ($program.Winget) { Install-Winget $program } else { Install-Local $program }
    }
    catch {
        $problem = "$_"
    }
    if (-not $problem) { Write-Log "OK $($program.Name)" }
    elseif ($problem -like 'skipped: *') { Write-Log "SKIPPED $($program.Name): $($problem.Substring(9))" }
    else { Write-Log "FAILED $($program.Name): $problem" }
}

# Установщики больше не нужны, а места занимают много.
Remove-Item (Join-Path $root 'Programs') -Recurse -Force -ErrorAction SilentlyContinue
Write-Log 'Done'
