#Requires -Version 7.4
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$SigningKeyPath,
    [Parameter(Mandatory)][string]$PublicKeyPath,
    [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
$repository = 'solve-kz/Scale-monitoring'
if (Test-Path -LiteralPath $WorkDirectory) { throw 'Use a new working directory.' }
New-Item -ItemType Directory -Path $WorkDirectory | Out-Null
$release = gh release view "v$Version" --repo $repository --json isPrerelease,isDraft | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $release.isDraft -or -not $release.isPrerelease) { throw 'Publish and test the prerelease before promotion.' }
gh release download "v$Version" --repo $repository --dir $WorkDirectory --pattern 'release-manifest.*' --pattern "Scalemon-$Version-win-x64.zip"
if ($LASTEXITCODE -ne 0) { throw 'Cannot download tested artifacts.' }
$manifestPath = Join-Path $WorkDirectory 'release-manifest.json'
$signaturePath = Join-Path $WorkDirectory 'release-manifest.sig'
$bytes = [IO.File]::ReadAllBytes($manifestPath)
$signature = [IO.File]::ReadAllBytes($signaturePath)
$public = [Security.Cryptography.ECDsa]::Create()
$key = [Security.Cryptography.ECDsa]::Create()
try {
    $public.ImportFromPem([IO.File]::ReadAllText($PublicKeyPath))
    if (-not $public.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Invalid Test signature' }
    $manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    if ($manifest.version -ne $Version -or $manifest.channel -ne 'Test' -or -not $manifest.backwardCompatible) { throw 'Release cannot be promoted.' }
    $archive = Join-Path $WorkDirectory "Scalemon-$Version-win-x64.zip"
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $manifest.sha256 -or (Get-Item -LiteralPath $archive).Length -ne $manifest.size) { throw 'Tested archive changed.' }
    $manifest.channel = 'Stable'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $key.ImportFromPem([IO.File]::ReadAllText($SigningKeyPath))
    $bytes = [IO.File]::ReadAllBytes($manifestPath)
    $signature = $key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    if (-not $public.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Signing key mismatch' }
    [IO.File]::WriteAllBytes($signaturePath, $signature)
    # Архив и Setup не пересобираются и не загружаются заново. Только метаданные канала.
    # Временное несовпадение пары манифест/подпись даёт отказ проверки, а не запуск пакета.
    gh release upload "v$Version" $manifestPath $signaturePath --repo $repository --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Manifest promotion failed; keep release as prerelease.' }
    gh release edit "v$Version" --repo $repository --prerelease=false --draft=false --latest
    if ($LASTEXITCODE -ne 0) { throw 'Cannot publish Stable release.' }
} finally { $public.Dispose(); $key.Dispose() }
