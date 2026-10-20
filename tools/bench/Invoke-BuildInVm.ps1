<#
.SYNOPSIS
    Сборка движком внутри ВМ стенда — для шагов с DISM на хостах, где DISM не открывает образы.

.DESCRIPTION
    Берёт ВМ, оставленную стендом (Invoke-Bench.ps1 -KeepVm; к ней подключается диск для данных, как в
    Invoke-AdminTestsInVm.ps1), публикует движок самодостаточно, копирует в ВМ движок, профиль и кэш UUP
    рабочей папки (cache\uup), запускает build с правами администратора и возвращает ISO на хост.
    Пути source.iso / output.iso профиля подменяются на пути в ВМ.

.EXAMPLE
    .\tools\bench\Invoke-BuildInVm.ps1 -VmName shapeshift-bench-… -Profile .\uup.json -Workspace .\.bench\uup-ws -Output .\.bench\uup-updated.iso
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $VmName,
    [Parameter(Mandatory)] [string] $Profile,
    # Рабочая папка хоста: её cache\uup копируется в ВМ (скачанное там не качается заново).
    [string] $Workspace,
    [Parameter(Mandatory)] [string] $Output,
    [string] $Scenario = 'full-unattended',
    [int] $DataDiskGB = 120,
    # Только забрать уже собранный в ВМ ISO (например, если сеанс оборвался посреди долгой сборки).
    [switch] $FetchOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$vm = Get-VM -Name $VmName
$work = $vm.Path
$log = Join-Path $work 'build-in-vm.log'
function Write-Step([string] $Message) {
    $line = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $Message
    Write-Host $line
    Add-Content -Path $log -Value $line -Encoding UTF8
}

$profileText = (Get-Content (Join-Path $PSScriptRoot "scenarios\$Scenario\profile.json") -Raw -Encoding UTF8) -replace '(?m)^\s*//.*$', ''
$user = ($profileText | ConvertFrom-Json).setup.user
$credential = New-Object System.Management.Automation.PSCredential (
    $user.name, (ConvertTo-SecureString $user.password.value -AsPlainText -Force))

$engine = Join-Path $work 'engine'
if (-not $FetchOnly) {
    Write-Step 'Publishing the engine (self-contained)'
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
    & $dotnet publish (Join-Path $repo 'src\Shapeshift.Engine') -c Release -r win-x64 --self-contained -o $engine -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

$dataDisk = Join-Path $work 'data.vhdx'
if (-not (Test-Path $dataDisk)) {
    Write-Step "Adding a $DataDiskGB GB data disk"
    New-VHD -Path $dataDisk -SizeBytes ($DataDiskGB * 1GB) -Dynamic | Out-Null
    Add-VMHardDiskDrive -VM $vm -Path $dataDisk
}
if ($vm.State -ne 'Running') { Start-VM -VM $vm }
$guestService = Get-VMIntegrationService -VM $vm | Where-Object Id -match '6C09BB55-D683-4DA0-8931-C9BF705F6480$'
if (-not $guestService.Enabled) { Enable-VMIntegrationService -VM $vm -Name $guestService.Name }

$session = $null
$deadline = (Get-Date).AddMinutes(5)
while (-not $session) {
    try { $session = New-PSSession -VMName $VmName -Credential $credential -ErrorAction Stop }
    catch { if ((Get-Date) -gt $deadline) { throw }; Start-Sleep -Seconds 5 }
}

# Файл с хоста в ВМ: Copy-VMFile (Copy-Item -ToSession не справляется с гигабайтами); служба в госте стартует не сразу.
function Send-File([string] $Source, [string] $Destination) {
    for ($attempt = 1; ; $attempt++) {
        try { Copy-VMFile -VM $vm -SourcePath $Source -DestinationPath $Destination -FileSource Host -CreateFullPath -Force; return }
        catch { if ($attempt -ge 12) { throw }; Start-Sleep -Seconds 10 }
    }
}

try {
    $drive = Invoke-Command -Session $session -ScriptBlock {
        $disk = Get-Disk | Where-Object PartitionStyle -eq 'RAW' | Select-Object -First 1
        if ($disk) {
            Initialize-Disk -Number $disk.Number -PartitionStyle GPT
            New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter |
                Format-Volume -FileSystem NTFS -NewFileSystemLabel 'Data' -Confirm:$false | Out-Null
        }
        (Get-Volume -FileSystemLabel 'Data').DriveLetter
    }
    $root = "${drive}:\build"
    Write-Step "VM data folder $root"

    if (-not $FetchOnly) {
    Write-Step 'Copying the engine'
    Invoke-Command -Session $session -ArgumentList $root -ScriptBlock { param($root) Remove-Item (Join-Path $root 'engine') -Recurse -Force -ErrorAction SilentlyContinue }
    foreach ($file in Get-ChildItem $engine -Recurse -File) {
        Send-File $file.FullName (Join-Path "$root\engine" $file.FullName.Substring($engine.Length + 1))
    }

    if ($Workspace) {
        $cache = Join-Path (Resolve-Path $Workspace).Path 'cache\uup'
        $present = Invoke-Command -Session $session -ArgumentList "$root\ws\cache\uup" -ScriptBlock {
            param($dir)
            $map = @{}
            if (Test-Path $dir) { Get-ChildItem $dir -Recurse -File | ForEach-Object { $map[$_.FullName.Substring($dir.Length + 1)] = $_.Length } }
            $map
        }
        $files = Get-ChildItem $cache -Recurse -File | Where-Object { $_.Extension -ne '.part' }
        Write-Step "Copying the UUP cache ($([math]::Round(($files | Measure-Object Length -Sum).Sum / 1GB, 1)) GB, files already there are skipped)"
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($cache.Length + 1)
            if ($present.ContainsKey($relative) -and $present[$relative] -eq $file.Length) { continue }
            Send-File $file.FullName "$root\ws\cache\uup\$relative"
        }
    }

    # Профиль: выход — в ВМ; свой ISO (если есть) — тоже копируется.
    $json = Get-Content $Profile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($json.source.PSObject.Properties['iso'] -and $json.source.iso) {
        Send-File (Resolve-Path $json.source.iso).Path "$root\source.iso"
        $json.source.iso = "$root\source.iso"
    }
    $json.output.iso = "$root\out.iso"
    $guestProfile = Join-Path $work 'build-profile.json'
    $json | ConvertTo-Json -Depth 10 | Set-Content $guestProfile -Encoding UTF8
    Send-File $guestProfile "$root\profile.json"

    Write-Step 'Building in the VM'
    $result = Invoke-Command -Session $session -ArgumentList $root -ScriptBlock {
        param($root)
        Remove-Item (Join-Path $root 'out.iso') -Force -ErrorAction SilentlyContinue
        $output = & (Join-Path $root 'engine\Shapeshift.Engine.exe') build --profile (Join-Path $root 'profile.json') --workspace (Join-Path $root 'ws') 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
    }
    $result.Output | Where-Object { $_ -notmatch '^\s+\d+%$' } | ForEach-Object { Write-Step "  $_" }
    if ($result.ExitCode -ne 0) { throw "Build in the VM failed with exit code $($result.ExitCode)." }
    }

    # ISO обратно — частями по 512 МБ: Copy-Item -FromSession не справляется с гигабайтами целиком.
    Write-Step 'Copying the ISO back'
    $chunk = 512MB
    $size = Invoke-Command -Session $session -ArgumentList "$root\out.iso" -ScriptBlock { param($p) (Get-Item $p).Length }
    $target = [IO.File]::Create((Join-Path (Get-Location) $Output))
    try {
        for ($offset = 0L; $offset -lt $size; $offset += $chunk) {
            $piece = Invoke-Command -Session $session -ArgumentList "$root\out.iso", $offset, $chunk, "$root\piece.bin" -ScriptBlock {
                param($path, $offset, $length, $piece)
                $in = [IO.File]::OpenRead($path)
                try {
                    $in.Position = $offset
                    $buffer = New-Object byte[] ([long][Math]::Min([long]$length, [long]($in.Length - $offset)))
                    $read = 0
                    while ($read -lt $buffer.Length) { $read += $in.Read($buffer, $read, $buffer.Length - $read) }
                    [IO.File]::WriteAllBytes($piece, $buffer)
                }
                finally { $in.Dispose() }
                $piece
            }
            $local = Join-Path $work 'piece.bin'
            Copy-Item -FromSession $session -Path $piece -Destination $local -Force
            $bytes = [IO.File]::ReadAllBytes($local)
            $target.Write($bytes, 0, $bytes.Length)
        }
    }
    finally { $target.Dispose() }
    Write-Step "Done: $Output ($([math]::Round($size / 1GB, 2)) GB)"
}
finally {
    Remove-PSSession $session
}
