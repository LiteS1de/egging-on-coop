# Egging On Co-op: удаление (run by Uninstall.bat). Убирает мод; BepInEx — только если других модов нет
# и вы согласны. Сохранения игры не трогаются (они не в папке игры).
param([string]$GameDir, [switch]$NoPause, [switch]$Yes)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "find-game.ps1")

function Done($code) { if (-not $NoPause) { Write-Host ""; Read-Host (T "Нажмите Enter, чтобы закрыть" "Press Enter to close") | Out-Null }; exit $code }

Write-Host (T "=== Egging On Co-op — удаление ===" "=== Egging On Co-op: uninstall ===") -ForegroundColor Yellow
if (-not $GameDir) { $GameDir = Find-Game }
if (-not (Test-Game $GameDir)) { Write-Host (T "Папка игры не выбрана — ничего не удалено." "No game folder picked: nothing was removed.") -ForegroundColor Red; Done 1 }
Write-Host (T "Игра: $GameDir" "Game: $GameDir")
if (-not (Test-Writable $GameDir)) { Restart-Elevated $PSCommandPath $GameDir; exit 0 }
Wait-GameClosed $GameDir

$plugin = Join-Path $GameDir "BepInEx\plugins\EggCoop"
if (Test-Path -LiteralPath $plugin) { Remove-Item -LiteralPath $plugin -Recurse -Force; Write-Host (T "Мод удалён." "The mod is removed.") -ForegroundColor Green }
else { Write-Host (T "Мода в этой папке нет." "The mod is not in this folder.") }

$plugins = Join-Path $GameDir "BepInEx\plugins"
$others = @(Get-ChildItem -LiteralPath $plugins -Recurse -File -Filter *.dll -ErrorAction SilentlyContinue)
if (-not (Test-Path -LiteralPath (Join-Path $GameDir "BepInEx\core\BepInEx.dll"))) { Done 0 }
if ($others.Count -gt 0) { $n = $others.Count
    Write-Host (T "BepInEx оставлен: в BepInEx\plugins есть другие моды ($n dll)." "BepInEx is kept: BepInEx\plugins has other mods ($n dll)."); Done 0 }
$answer = if ($Yes) { "y" } else { Read-Host (T "Удалить и BepInEx (загрузчик модов)? Вместе с ним удалятся настройки мода и доска позора. [y/N]" "Remove BepInEx (the mod loader) too? The mod settings and the shame board go with it. [y/N]") }
if ($answer -match "^(y|д)") {
    foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt") { Remove-Item -LiteralPath (Join-Path $GameDir $f) -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath (Join-Path $GameDir "BepInEx") -Recurse -Force
    Write-Host (T "BepInEx удалён. Игра снова как была." "BepInEx removed. The game is as it was.") -ForegroundColor Green
}
Done 0
