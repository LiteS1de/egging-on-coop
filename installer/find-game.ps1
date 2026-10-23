# Shared by install.ps1 / uninstall.ps1: find the game, get write access, wait for it to close.

# Messages follow the Windows display language, like the mod itself.
# Russian anywhere counts: the display language, the regional format or the first preferred language.
$langs = @((Get-UICulture).Name, (Get-Culture).Name)
try { $langs += @(Get-WinUserLanguageList)[0].LanguageTag } catch {}
$script:Ru = [bool]($langs -match "^(ru|uk|be)")
function T($ru, $en) { if ($script:Ru) { $ru } else { $en } }

function Test-Game($d) { $d -and (Test-Path -LiteralPath (Join-Path $d "Egging On.exe")) }

# Steam lists its libraries in libraryfolders.vdf as  "path"  "D:\\SteamLibrary"
function Get-SteamLibraries {
    $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { $steam = (Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam" -ErrorAction SilentlyContinue).InstallPath }
    if (-not $steam) { return @() }
    return @($steam -replace "/", "\") + (Get-VdfPaths (Join-Path $steam "steamapps\libraryfolders.vdf")) | Sort-Object -Unique   # case-insensitive: the registry and the vdf spell C:\ differently
}

function Get-VdfPaths($vdf) {
    if (-not (Test-Path -LiteralPath $vdf)) { return @() }
    return @(Select-String -LiteralPath $vdf -Pattern '"path"\s+"([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value -replace "\\\\", "\" })
}

function Find-Game {
    # The archive unpacked right inside the game folder.
    $root = Split-Path $PSScriptRoot
    foreach ($d in @($root, (Split-Path $root))) { if (Test-Game $d) { return $d } }
    foreach ($lib in Get-SteamLibraries) {
        $common = Join-Path $lib "steamapps\common"
        if (-not (Test-Path -LiteralPath $common)) { continue }
        foreach ($g in Get-ChildItem -LiteralPath $common -Directory -ErrorAction SilentlyContinue) { if (Test-Game $g.FullName) { return $g.FullName } }
    }
    Write-Host (T "Игра в Steam не найдена — выберите папку игры в окне (где лежит «Egging On.exe»)." "The game was not found in Steam: pick the game folder (the one with Egging On.exe) in the window.")
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = (T "Папка игры Egging On (где лежит «Egging On.exe»)" "Egging On game folder (the one with Egging On.exe)")
    while ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        if (Test-Game $dlg.SelectedPath) { return $dlg.SelectedPath }
        [System.Windows.Forms.MessageBox]::Show((T "В этой папке нет «Egging On.exe». Выберите папку игры." "There is no Egging On.exe in this folder. Pick the game folder."), "Egging On Co-op") | Out-Null
    }
    return $null
}

function Test-Writable($dir) {
    try { $p = Join-Path $dir ".eggcoop-write-test"; [IO.File]::WriteAllText($p, ""); Remove-Item -LiteralPath $p; return $true } catch { return $false }
}

# Steam under Program Files needs admin rights to write: run the same script again elevated.
function Restart-Elevated($script, $gameDir) {
    Write-Host (T "Нужны права администратора для записи в папку игры — сейчас Windows спросит разрешение." "Writing to the game folder needs admin rights: Windows will ask now.")
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -GameDir `"$gameDir`""
}

function Wait-GameClosed($gameDir) {
    while (Get-Process "Egging On" -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($gameDir, [StringComparison]::OrdinalIgnoreCase) }) {
        Read-Host (T "Игра запущена — закройте её и нажмите Enter" "The game is running: close it and press Enter") | Out-Null
    }
}
