$ErrorActionPreference='Stop'
$rootPath=$PSScriptRoot
$frameworkPath=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$outputPath=Join-Path $rootPath 'dist\内容迁移安装.exe'
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:Microsoft.CSharp.dll ('/win32icon:'+(Join-Path $rootPath 'assets\content-mover.ico')) ('/resource:'+(Join-Path $rootPath 'dist\内容迁移-Windows.zip')+',payload.zip') ('/out:'+$outputPath) (Join-Path $rootPath 'installer\Setup.cs')
if($LASTEXITCODE -ne 0){throw '安装程序编译失败'}
Write-Output $outputPath
