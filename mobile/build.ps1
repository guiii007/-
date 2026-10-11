param([switch]$SkipAndroid,[switch]$SkipDesktop,[string]$ReceiverExecutable="手机接收.exe")
$ErrorActionPreference='Stop'
$rootPath=Split-Path -Parent $PSScriptRoot
if(-not $SkipDesktop){
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$qrAssembly=Join-Path $PSScriptRoot 'toolchain\zxing-net\lib\net40\zxing.dll'
$references=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll',(Join-Path $framework 'WPF\UIAutomationClient.dll'),(Join-Path $framework 'WPF\UIAutomationTypes.dll'),(Join-Path $framework 'WPF\WindowsBase.dll'),$qrAssembly)
$argsList=@('/nologo','/target:winexe','/platform:x64','/optimize+','/main:ContentMover.MobileProgram',('/out:'+(Join-Path $rootPath $ReceiverExecutable)),('/win32icon:'+(Join-Path $rootPath 'assets\content-mover.ico')),('/resource:'+$qrAssembly+',zxing.dll'))
foreach($reference in $references){$argsList+='/reference:'+$reference}
$argsList+=@((Join-Path $rootPath 'ContentMover.cs'),(Join-Path $PSScriptRoot 'desktop\PairingForm.cs'),(Join-Path $PSScriptRoot 'desktop\MobileReceiver.cs'),(Join-Path $PSScriptRoot 'desktop\MobileTests.cs'))
& (Join-Path $framework 'csc.exe') @argsList
if($LASTEXITCODE -ne 0){throw '手机接收编译失败'}
}
if($SkipAndroid){exit 0}
$jdkPath=(Get-ChildItem (Join-Path $PSScriptRoot 'toolchain\jdk') -Directory | Select-Object -First 1).FullName
$buildTools=(Get-ChildItem (Join-Path $PSScriptRoot 'toolchain\build-tools') -Directory | Select-Object -First 1).FullName
$platform=(Get-ChildItem (Join-Path $PSScriptRoot 'toolchain\platform') -Directory | Select-Object -First 1).FullName
$stagePath=Join-Path $env:TEMP 'ContentMoverAndroidBuild'
$androidJar=Join-Path $stagePath 'android.jar'
New-Item -ItemType Directory -Path $stagePath,(Join-Path $stagePath 'classes'),(Join-Path $stagePath 'dex') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $platform 'android.jar') -Destination $androidJar -Force
Copy-Item -LiteralPath (Join-Path $rootPath 'assets\content-mover.png') -Destination (Join-Path $PSScriptRoot 'android\res\drawable\icon.png') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'android\res') -Destination $stagePath -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'android\AndroidManifest.xml') -Destination $stagePath -Force
& (Join-Path $buildTools 'aapt2.exe') compile --dir (Join-Path $stagePath 'res') -o (Join-Path $stagePath 'resources.zip')
if($LASTEXITCODE -ne 0){throw '资源编译失败'}
& (Join-Path $buildTools 'aapt2.exe') link -o (Join-Path $stagePath 'unsigned.apk') -I $androidJar --manifest (Join-Path $stagePath 'AndroidManifest.xml') (Join-Path $stagePath 'resources.zip')
if($LASTEXITCODE -ne 0){throw '资源链接失败'}
$javaFiles=@(Get-ChildItem (Join-Path $PSScriptRoot 'android\src') -Filter '*.java' -Recurse | ForEach-Object {$_.FullName})
& (Join-Path $jdkPath 'bin\javac.exe') -encoding UTF-8 -source 8 -target 8 -classpath ($androidJar+';'+(Join-Path $PSScriptRoot 'toolchain\zxing-core.jar')) -d (Join-Path $stagePath 'classes') @javaFiles
if($LASTEXITCODE -ne 0){throw 'Android Java 编译失败'}
$classFiles=@(Get-ChildItem (Join-Path $stagePath 'classes') -Filter '*.class' -Recurse | ForEach-Object {$_.FullName})
& (Join-Path $jdkPath 'bin\java.exe') -cp (Join-Path $buildTools 'lib\d8.jar') com.android.tools.r8.D8 --lib $androidJar --min-api 26 --output (Join-Path $stagePath 'dex') @classFiles (Join-Path $PSScriptRoot 'toolchain\zxing-core.jar')
if($LASTEXITCODE -ne 0){throw 'Dex 编译失败'}
& (Join-Path $jdkPath 'bin\jar.exe') uf (Join-Path $stagePath 'unsigned.apk') -C (Join-Path $stagePath 'dex') classes.dex
& (Join-Path $buildTools 'zipalign.exe') -f -p 4 (Join-Path $stagePath 'unsigned.apk') (Join-Path $stagePath 'aligned.apk')
if($LASTEXITCODE -ne 0){throw 'APK 对齐失败'}
$privatePath=Join-Path $PSScriptRoot '.signing'
New-Item -ItemType Directory -Path $privatePath -Force | Out-Null
$secretFile=Join-Path $privatePath 'password.txt'
if(-not (Test-Path $secretFile)){$secretBytes=New-Object byte[] 32;$rng=[Security.Cryptography.RandomNumberGenerator]::Create();$rng.GetBytes($secretBytes);$rng.Dispose();[IO.File]::WriteAllText($secretFile,[Convert]::ToBase64String($secretBytes))}
$env:CONTENT_MOVER_SIGNING_PASSWORD=[IO.File]::ReadAllText($secretFile)
try{
  $keyStore=Join-Path $privatePath 'release.p12'
  if(-not (Test-Path $keyStore)){& (Join-Path $jdkPath 'bin\keytool.exe') -genkeypair -keystore $keyStore -storetype PKCS12 -alias contentmover -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=Content Mover' -storepass:env CONTENT_MOVER_SIGNING_PASSWORD -keypass:env CONTENT_MOVER_SIGNING_PASSWORD;if($LASTEXITCODE -ne 0){throw '签名密钥生成失败'}}
  $apkPath=Join-Path $rootPath 'dist\ContentMover-Android.apk'
  & (Join-Path $jdkPath 'bin\java.exe') -jar (Join-Path $buildTools 'lib\apksigner.jar') sign --ks $keyStore --ks-key-alias contentmover --ks-pass env:CONTENT_MOVER_SIGNING_PASSWORD --out $apkPath (Join-Path $stagePath 'aligned.apk')
  if($LASTEXITCODE -ne 0){throw 'APK 签名失败'}
  & (Join-Path $jdkPath 'bin\java.exe') -jar (Join-Path $buildTools 'lib\apksigner.jar') verify $apkPath
  if($LASTEXITCODE -ne 0){throw 'APK 签名校验失败'}
  Write-Output $apkPath
}finally{Remove-Item Env:\CONTENT_MOVER_SIGNING_PASSWORD}
