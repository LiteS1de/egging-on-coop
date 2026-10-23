# Two-copy co-op test on one PC.
#   .\tools\lab.ps1 up      - build+deploy plugin, start p1 as host and p2 joining 127.0.0.1
#   .\tools\lab.ps1 down    - stop both copies, then check the owner's real saves
#   .\tools\lab.ps1 log     - tail both BepInEx logs
#   .\tools\lab.ps1 pack    - build dist/EggCoop-<ver>.zip for friends
#
# Safety (learned the hard way, 2026-10-21): a copy that starts WITHOUT the plugin is the plain game and writes
# straight into the owner's LocalLow saves. So: never start next to a running copy, start only from a fresh log,
# kill any copy that does not report its isolated data dir, check RAM first, and snapshot + verify the real save
# folder around every run (stop and report if it changed; never restore automatically: on 2026-10-23 a stale
# guard from an earlier run "restored" over the owner's real playthrough).
param([ValidateSet("up", "down", "log", "pack")] [string]$cmd = "up", [string]$extra = "dev", [string]$join = "127.0.0.1:7788", [string]$hostArgs = "", [string]$guestArgs = "")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot
$real = "$env:USERPROFILE\AppData\LocalLow\Egobounds\Egging On"
$guard = "$root\lab\real-guard"
$minFreeGB = 6

function Get-LabProcs { Get-Process "Egging On" -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\lab\*" } }

function Stop-Lab {
    $procs = @(Get-LabProcs)
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    # A dying copy still holds its log files: wait until it is really gone.
    for ($i = 0; $i -lt 60 -and @(Get-LabProcs).Count; $i++) { Start-Sleep 1 }
    if (@(Get-LabProcs).Count) { throw "lab copies did not exit" }
}

function Get-RealState { Get-ChildItem $real -Recurse -File | Where-Object Extension -ne ".log" | ForEach-Object { "$($_.FullName.Substring($real.Length)) $((Get-FileHash $_.FullName).Hash)" } }

function Save-Guard {
    New-Item -ItemType Directory -Force $guard | Out-Null
    Get-RealState | Set-Content "$guard\hashes.txt"
    Compress-Archive "$real\*" "$guard\real.zip" -Force
}

function Test-Guard {
    if (-not (Test-Path "$guard\hashes.txt")) { return }
    $diff = Compare-Object @(Get-Content "$guard\hashes.txt") @(Get-RealState)
    if (-not $diff) { return }
    Stop-Lab
    $keep = "$root\backups\guard-$(Get-Date -Format yyyyMMdd-HHmmss)"
    New-Item -ItemType Directory -Force $keep | Out-Null
    Copy-Item "$real\*" $keep -Recurse
    Move-Item "$guard\real.zip" $keep
    Clear-Guard
    # No automatic restore: the owner may have played the real game meanwhile, and a restore would erase that.
    Write-Warning "The REAL save folder changed during a lab run. Nothing was restored. Current files: $keep; pre-run snapshot: $keep\real.zip. Check before restoring by hand."
    throw "real saves changed during the lab run"
}

# A guard lives for one lab run only: the owner plays the real game between runs.
function Clear-Guard { if (Test-Path "$guard\hashes.txt") { Remove-Item "$guard\hashes.txt" }; if (Test-Path "$guard\real.zip") { Remove-Item "$guard\real.zip" } }

function Start-Copy($n, $coopArgs) {
    $dir = "$root\lab\p$n"
    # A copy commits ~4.5 GB (low preset) and its working set grows after the check, so look at commit headroom,
    # which is what ran out in the 2026-10-21 crash, not at "free" RAM (that excludes the reusable standby cache).
    $m = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
    $headroom = ($m.CommitLimit - $m.CommittedBytes) / 1GB
    if ($headroom -lt $minFreeGB -or $m.AvailableMBytes -lt 3000) { throw ("only {0:N1} GB commit headroom / {1:N1} GB available, need {2} GB per copy - not starting p$n" -f $headroom, ($m.AvailableMBytes / 1KB), $minFreeGB) }
    $log = "$dir\BepInEx\LogOutput.log"
    Remove-Item "$dir\BepInEx\coop-shots", $log, "$dir\BepInEx\coop-probe.txt", "$dir\BepInEx\coop-delay.txt" -Recurse -ErrorAction SilentlyContinue
    if (Test-Path $log) { throw "p$n log is still locked - an old copy is alive" }
    $a = "-screen-fullscreen 0 -screen-width 960 -screen-height 540 $extra $coopArgs -coop-shot 10 -coop-lab -coop-data `"$dir\userdata`""
    $p = Start-Process "$dir\Egging On.exe" -ArgumentList $a -WorkingDirectory $dir -PassThru
    # The plugin isolates the data dir in its Awake, before the game reads any save. No report = no isolation.
    for ($i = 0; $i -lt 90; $i++) {
        Start-Sleep 1
        if ((Test-Path $log) -and (Select-String $log -Pattern "Lab data dir" -Quiet)) { break }
    }
    if (-not ((Test-Path $log) -and (Select-String $log -Pattern "Lab data dir" -Quiet))) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Stop-Lab
        Test-Guard
        throw "p$n started without the plugin - killed it"
    }
    Test-Guard
    "p$n pid $($p.Id)"
}

switch ($cmd) {
    "up" {
        if (Get-Process "Egging On" -ErrorAction SilentlyContinue | Where-Object { $_.Path -notlike "$root\lab\*" }) { throw "the real game is running - close it first" }
        Stop-Lab
        Clear-Guard   # never compare against an old run: that would flag the owner's own play as a lab accident
        Save-Guard
        dotnet build "$root\src\EggCoop" -c Lab -p:Deploy=true | Select-String "error|Build succeeded|успешно"
        Start-Copy 1 "-coop-nick Host -coop-host $hostArgs"
        Start-Copy 2 "-coop-nick Guest -coop-join $join $guestArgs"
    }
    "down" {
        Stop-Lab
        Test-Guard
        Clear-Guard
        "lab stopped, real saves untouched"
    }
    "pack" {
        # dist/EggCoop-<ver>.zip: our plugin + a BepInEx.cfg with HideManagerGameObject=true (the game kills the
        # visible BepInEx manager object). BepInEx itself is downloaded by each player from GitHub.
        dotnet build "$root\src\EggCoop" -c Release | Select-String "error|успешно"
        $stage = "$root\dist\stage"
        Remove-Item $stage -Recurse -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force "$stage\BepInEx\plugins\EggCoop", "$stage\BepInEx\config" | Out-Null
        $out = "$root\src\EggCoop\bin\Release\net472"
        Copy-Item "$out\EggCoop.dll", "$out\LiteNetLib.dll", "$out\Mono.Nat.dll" "$stage\BepInEx\plugins\EggCoop"
        Copy-Item "$root\lab\p1\BepInEx\config\BepInEx.cfg" "$stage\BepInEx\config"
        Copy-Item "$root\dist-files\*" $stage
        $zip = "$root\dist\EggCoop-0.4.4.zip"
        Compress-Archive "$stage\*" $zip -Force
        Get-Item $zip | Select-Object FullName, Length
    }
    "log" {
        Test-Guard
        foreach ($n in 1, 2) { "=== p$n"; Get-Content "$root\lab\p$n\BepInEx\LogOutput.log" -Tail 25 }
    }
}
