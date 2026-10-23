# Egging On Co-op: установка в один клик (run by Install.bat).
# Находит игру (архив распакован в её папку, библиотеки Steam, иначе спрашивает), ставит BepInEx, если его нет,
# и кладёт мод. Настройки мода (ник, клавиши, статистика) и другие плагины не трогаются.
param([string]$GameDir, [switch]$NoPause)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "find-game.ps1")
$src = (Resolve-Path (Join-Path $PSScriptRoot "..\files\game")).Path

function Done($code) { if (-not $NoPause) { Write-Host ""; Read-Host (T "Нажмите Enter, чтобы закрыть" "Press Enter to close") | Out-Null }; exit $code }

Write-Host (T "=== Egging On Co-op — установка ===" "=== Egging On Co-op: install ===") -ForegroundColor Yellow
if (-not $GameDir) { $GameDir = Find-Game }
if (-not (Test-Game $GameDir)) { Write-Host (T "Папка игры не выбрана — ничего не установлено." "No game folder picked: nothing was installed.") -ForegroundColor Red; Done 1 }
Write-Host (T "Игра: $GameDir" "Game: $GameDir")
if (-not (Test-Writable $GameDir)) { Restart-Elevated $PSCommandPath $GameDir; exit 0 }
Wait-GameClosed $GameDir

# BepInEx ставим, только если его ещё нет: чужой не перезаписываем (у человека могут быть другие моды).
$bep = Join-Path $GameDir "BepInEx\core\BepInEx.dll"
if (Test-Path -LiteralPath $bep) {
    $v = (Get-Item -LiteralPath $bep).VersionInfo.FileVersion
    Write-Host (T "BepInEx уже установлен ($v) — оставляю как есть." "BepInEx is already installed ($v): leaving it as is.")
} else {
    foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt") { Copy-Item -LiteralPath (Join-Path $src $f) -Destination $GameDir -Force }
    New-Item -ItemType Directory -Force (Join-Path $GameDir "BepInEx\core") | Out-Null
    Copy-Item -Path (Join-Path $src "BepInEx\core\*") -Destination (Join-Path $GameDir "BepInEx\core") -Force
    Write-Host (T "BepInEx 5.4.23.5 установлен." "BepInEx 5.4.23.5 installed.")
}

# Игра уничтожает видимый объект-менеджер BepInEx, поэтому его надо прятать (HideManagerGameObject = true).
$cfgDir = Join-Path $GameDir "BepInEx\config"
$cfg = Join-Path $cfgDir "BepInEx.cfg"
New-Item -ItemType Directory -Force $cfgDir | Out-Null
if (Test-Path -LiteralPath $cfg) {
    $text = [IO.File]::ReadAllText($cfg)
    $fixed = $text -replace "HideManagerGameObject\s*=\s*false", "HideManagerGameObject = true"
    if ($fixed -ne $text) { [IO.File]::WriteAllText($cfg, $fixed); Write-Host (T "BepInEx.cfg: включён HideManagerGameObject." "BepInEx.cfg: HideManagerGameObject turned on.") }
} else { Copy-Item -LiteralPath (Join-Path $src "BepInEx\config\BepInEx.cfg") -Destination $cfg }

# Папка мода заменяется целиком (уходят файлы старых версий); его настройки eggcoop.cfg остаются.
$plugin = Join-Path $GameDir "BepInEx\plugins\EggCoop"
if (Test-Path -LiteralPath $plugin) { Remove-Item -LiteralPath $plugin -Recurse -Force }
New-Item -ItemType Directory -Force $plugin | Out-Null
Copy-Item -Path (Join-Path $src "BepInEx\plugins\EggCoop\*") -Destination $plugin -Force
$ver = (Get-Item -LiteralPath (Join-Path $plugin "EggCoop.dll")).VersionInfo.FileVersion
Write-Host (T "Мод Egging On Co-op $ver установлен." "Egging On Co-op $ver installed.") -ForegroundColor Green
Write-Host ""
Write-Host (T "Запустите игру и нажмите F9 — откроется окно кооператива. У друзей должна быть та же версия мода." "Start the game and press F9 to open the co-op window. Friends need the same mod version.")
Done 0
