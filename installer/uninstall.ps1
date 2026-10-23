# Egging On Co-op: удаление (запускается из «Удалить.bat»). Убирает мод; BepInEx — только если других модов нет
# и вы согласны. Сохранения игры не трогаются (они не в папке игры).
param([string]$GameDir, [switch]$NoPause, [switch]$Yes)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "find-game.ps1")

function Done($code) { if (-not $NoPause) { Write-Host ""; Read-Host "Нажмите Enter, чтобы закрыть" | Out-Null }; exit $code }

Write-Host "=== Egging On Co-op — удаление ===" -ForegroundColor Yellow
if (-not $GameDir) { $GameDir = Find-Game }
if (-not (Test-Game $GameDir)) { Write-Host "Папка игры не выбрана — ничего не удалено." -ForegroundColor Red; Done 1 }
Write-Host "Игра: $GameDir"
if (-not (Test-Writable $GameDir)) { Restart-Elevated $PSCommandPath $GameDir; exit 0 }
Wait-GameClosed $GameDir

$plugin = Join-Path $GameDir "BepInEx\plugins\EggCoop"
if (Test-Path -LiteralPath $plugin) { Remove-Item -LiteralPath $plugin -Recurse -Force; Write-Host "Мод удалён." -ForegroundColor Green }
else { Write-Host "Мода в этой папке нет." }

$plugins = Join-Path $GameDir "BepInEx\plugins"
$others = @(Get-ChildItem -LiteralPath $plugins -Recurse -File -Filter *.dll -ErrorAction SilentlyContinue)
if (-not (Test-Path -LiteralPath (Join-Path $GameDir "BepInEx\core\BepInEx.dll"))) { Done 0 }
if ($others.Count -gt 0) { Write-Host "BepInEx оставлен: в BepInEx\plugins есть другие моды ($($others.Count) dll)."; Done 0 }
$answer = if ($Yes) { "y" } else { Read-Host "Удалить и BepInEx (загрузчик модов)? Вместе с ним удалятся настройки мода и доска позора. [y/N]" }
if ($answer -match "^(y|д)") {
    foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt") { Remove-Item -LiteralPath (Join-Path $GameDir $f) -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath (Join-Path $GameDir "BepInEx") -Recurse -Force
    Write-Host "BepInEx удалён. Игра снова как была." -ForegroundColor Green
}
Done 0
