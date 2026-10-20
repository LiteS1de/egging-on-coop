# Stage 2 check: the guest drops onto the host's ghost from 1.5 m, then rams it at 22 m/s, under each host setting.
#   .\tools\stand_test.ps1            -> prints the "Lab stand" lines per case
# Expect: collide=1 -> pushed aside (horiz > 0.5 m); a ram cracks only with crack=1 ("Lab crack ... (friend)");
#         collide=0 -> falls through (dy about 0 or below).
$root = Split-Path $PSScriptRoot
$cases = @(
    @{ name = "collide, no crack"; args = "-coop-collide 1 -coop-crack 0" },
    @{ name = "collide, crack";    args = "-coop-collide 1 -coop-crack 1" },
    @{ name = "ghost mode";        args = "-coop-collide 0 -coop-crack 0" }
)
foreach ($c in $cases) {
    "=== $($c.name)"
    & "$root\tools\lab.ps1" up -hostArgs "$($c.args) -coop-noshove" -guestArgs "-coop-standtest" | Select-String "error|stopping"
    $log = "$root\lab\p2\BepInEx\LogOutput.log"
    $t = 0
    while ($t -lt 300 -and @(Select-String $log -Pattern "Lab stand" -ErrorAction SilentlyContinue).Count -lt 4) { Start-Sleep 3; $t += 3 }
    Select-String $log -Pattern "Lab stand|Lab crack|Lab hit|Ghost 0 body" | ForEach-Object { $_.Line -replace '^.*\] ', '' }
    & "$root\tools\lab.ps1" down
    Start-Sleep 3
}
