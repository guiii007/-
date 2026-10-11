$ErrorActionPreference='Stop'
$rootPath=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $rootPath ('test-results\mobile-'+[Guid]::NewGuid().ToString('N'))
$test=Start-Process -FilePath (Join-Path $rootPath '手机接收.exe') -ArgumentList @('--self-test',('"'+$fixture+'"')) -Wait -WindowStyle Hidden -PassThru
if($test.ExitCode -ne 0){throw '接收端测试失败'}
Get-Content -LiteralPath (Join-Path $fixture 'mobile-test.txt')
$jdk=(Get-ChildItem (Join-Path $PSScriptRoot 'toolchain\jdk') -Directory | Select-Object -First 1).FullName
$platform=(Get-ChildItem (Join-Path $PSScriptRoot 'toolchain\platform') -Directory | Select-Object -First 1).FullName
$classes=Join-Path $fixture 'java'
New-Item -ItemType Directory -Path $classes | Out-Null
$jsonJar=Join-Path $PSScriptRoot 'toolchain\json-test.jar'
$androidJar=Join-Path $platform 'android.jar'
& (Join-Path $jdk 'bin\javac.exe') -encoding UTF-8 -source 8 -target 8 -cp ($jsonJar+';'+$androidJar) -d $classes (Join-Path $PSScriptRoot 'tests\ImagePolicyTest.java') (Join-Path $PSScriptRoot 'android\src\com\contentmover\mobile\ImageAttachments.java') (Join-Path $PSScriptRoot 'tests\Base64.java') (Join-Path $PSScriptRoot 'tests\CryptoInterop.java') (Join-Path $PSScriptRoot 'android\src\com\contentmover\mobile\Transfer.java')
if($LASTEXITCODE -ne 0){throw '互通测试编译失败'}
& (Join-Path $jdk 'bin\java.exe') -cp ($classes+';'+$jsonJar+';'+$androidJar) com.contentmover.mobile.CryptoInterop (Join-Path $fixture 'interop.json') (Join-Path $fixture 'java-envelope.json')
if($LASTEXITCODE -ne 0){throw 'Windows → Android 加密互通失败'}
$verify=Start-Process -FilePath (Join-Path $rootPath '手机接收.exe') -ArgumentList @('--verify-interop',('"'+$fixture+'"')) -Wait -WindowStyle Hidden -PassThru
if($verify.ExitCode -ne 0){throw 'Android → Windows 加密互通失败'}
Get-Content -LiteralPath (Join-Path $fixture 'interop-test.txt')

& (Join-Path $jdk 'bin\java.exe') -cp ($classes+';'+$jsonJar+';'+$androidJar) com.contentmover.mobile.ImagePolicyTest
if($LASTEXITCODE -ne 0){throw '图片策略测试失败'}
