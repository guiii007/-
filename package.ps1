$ErrorActionPreference = 'Stop'
$rootPath = $PSScriptRoot
$stagePath = Join-Path $rootPath 'dist\内容迁移-Windows'
New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
foreach ($name in @('内容迁移.exe','启动内容迁移.vbs','README.md','使用说明.md')) {
    Copy-Item -LiteralPath (Join-Path $rootPath $name) -Destination $stagePath -Force
}
New-Item -ItemType Directory -Path (Join-Path $stagePath 'assets'),(Join-Path $stagePath 'browser-extension') -Force | Out-Null
foreach ($name in @('content-mover.ico','content-mover.png')) { Copy-Item -LiteralPath (Join-Path $rootPath ('assets\' + $name)) -Destination (Join-Path $stagePath 'assets') -Force }
foreach ($name in @('manifest.json','background.js','icon.png')) { Copy-Item -LiteralPath (Join-Path $rootPath ('browser-extension\' + $name)) -Destination (Join-Path $stagePath 'browser-extension') -Force }
$zipPath = Join-Path $rootPath 'dist\内容迁移-Windows.zip'
Compress-Archive -LiteralPath $stagePath -DestinationPath $zipPath -Force
Write-Output $zipPath
