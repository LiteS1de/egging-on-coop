# Two-copy co-op test on one PC.
#   .\tools\lab.ps1 up      - build+deploy plugin, start p1 as host and p2 joining 127.0.0.1
#   .\tools\lab.ps1 down    - stop both copies (by saved PID only)
#   .\tools\lab.ps1 log     - tail both BepInEx logs
#   .\tools\lab.ps1 pack    - build dist/EggCoop-<ver>.zip for friends
param([ValidateSet("up", "down", "log", "pack")] [string]$cmd = "up", [string]$extra = "dev")
$root = Split-Path $PSScriptRoot
$pids = "$root\lab\pids.txt"

function Start-Copy($n, $coopArgs) {
    $dir = "$root\lab\p$n"
    Remove-Item "$dir\BepInEx\coop-shots", "$dir\BepInEx\LogOutput.log", "$dir\BepInEx\coop-probe.txt" -Recurse -ErrorAction SilentlyContinue
    $a = "-screen-fullscreen 0 -screen-width 960 -screen-height 540 $extra $coopArgs -coop-shot 10 -coop-lab -coop-data `"$dir\userdata`""
    $p = Start-Process "$dir\Egging On.exe" -ArgumentList $a -WorkingDirectory $dir -PassThru
    Add-Content $pids $p.Id
    # BepInEx.cfg is shared-violation prone when two copies start at once: wait for this one's chainloader
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep 1
        if (Select-String "$dir\BepInEx\LogOutput.log" -Pattern "Chainloader startup complete" -Quiet -ErrorAction SilentlyContinue) { break }
    }
    if (-not (Select-String "$dir\BepInEx\LogOutput.log" -Pattern "Lab data dir" -Quiet)) { throw "p$n started without the plugin - stopping"; }
    "p$n pid $($p.Id)"
}

switch ($cmd) {
    "up" {
        dotnet build "$root\src\EggCoop" -c Release -p:Deploy=true | Select-String "error|Build succeeded|успешно"
        Remove-Item $pids -ErrorAction SilentlyContinue
        Start-Copy 1 "-coop-nick Host -coop-host"
        Start-Copy 2 "-coop-nick Guest -coop-join 127.0.0.1:7788"
    }
    "down" {
        if (Test-Path $pids) { Get-Content $pids | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }; Remove-Item $pids }
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
        $zip = "$root\dist\EggCoop-0.1.0.zip"
        Compress-Archive "$stage\*" $zip -Force
        Get-Item $zip | Select-Object FullName, Length
    }
    "log" {
        foreach ($n in 1, 2) { "=== p$n"; Get-Content "$root\lab\p$n\BepInEx\LogOutput.log" -Tail 25 }
    }
}
