param([Parameter(Mandatory=$true)][string]$ImagePath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null=[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
$null=[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$null=[Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
$null=[Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime]
$asyncMethod=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {$_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'} | Select-Object -First 1
function Await-WinRT($operation,[Type]$resultType) {
    $task=$asyncMethod.MakeGenericMethod($resultType).Invoke($null,@($operation))
    $task.GetAwaiter().GetResult()
}
$stream=$null;$bitmap=$null
try {
    $file=Await-WinRT ([Windows.Storage.StorageFile]::GetFileFromPathAsync([IO.Path]::GetFullPath($ImagePath))) ([Windows.Storage.StorageFile])
    $stream=Await-WinRT ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder=Await-WinRT ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap=Await-WinRT ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('zh-Hans'))
    if(-not $engine){$engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()}
    if(-not $engine){throw 'Windows 没有可用的文字识别语言，请在 Windows 语言设置中安装中文或英语的 OCR 组件。'}
    $result=Await-WinRT ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    $lines=@($result.Lines | ForEach-Object {$_.Text})
    [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
    [Console]::Write((@{Text=($lines -join "`r`n");Language=$engine.RecognizerLanguage.LanguageTag} | ConvertTo-Json -Compress))
} catch {
    [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
    [Console]::Write((@{Error=$_.Exception.Message} | ConvertTo-Json -Compress));exit 1
} finally {
    if($bitmap){$bitmap.Dispose()};if($stream){$stream.Dispose()}
}
