param([string]$OutputName = '内容迁移.exe')
$ErrorActionPreference = 'Stop'
$rootPath = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /reference:System.Drawing.dll ('/out:' + (Join-Path $rootPath 'assets\GenerateIcon.exe')) (Join-Path $rootPath 'assets\GenerateIcon.cs')
if ($LASTEXITCODE -ne 0) { throw '图标生成器编译失败' }
& (Join-Path $rootPath 'assets\GenerateIcon.exe') (Join-Path $rootPath 'assets')
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll', (Join-Path $frameworkPath 'WPF\UIAutomationClient.dll'), (Join-Path $frameworkPath 'WPF\UIAutomationTypes.dll'), (Join-Path $frameworkPath 'WPF\WindowsBase.dll'))
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+', '/utf8output', ('/out:' + (Join-Path $rootPath '内容迁移.exe')))
foreach ($referencePath in $references) { $compilerArgs += '/reference:' + $referencePath }
$compilerArgs += Join-Path $rootPath 'ContentMover.cs'
$compilerArgs += '/win32icon:' + (Join-Path $rootPath 'assets\content-mover.ico')
$compilerArgs = $compilerArgs | ForEach-Object { if ($_ -like '/out:*') { '/out:' + (Join-Path $rootPath $OutputName) } else { $_ } }
& (Join-Path $frameworkPath 'csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
Write-Output (Join-Path $rootPath $OutputName)
