#Requires -Version 7.4
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [ValidateSet('Stable','Test')][string]$Channel = 'Test',
    [Parameter(Mandatory)][string]$SigningKeyPath,
    [Parameter(Mandatory)][string]$PublicKeyPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Makensis = 'makensis.exe',
    [string]$Notes = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$releaseRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $releaseRoot) { throw 'Use a new empty output directory for each immutable release.' }
New-Item -ItemType Directory -Path $releaseRoot | Out-Null
$payload = Join-Path $releaseRoot 'payload'
New-Item -ItemType Directory -Path $payload | Out-Null
$publicDestination = Join-Path $repoRoot 'Scalemon.Updater/release-public-key.pem'
Copy-Item -LiteralPath $PublicKeyPath -Destination $publicDestination
try {
    $projects = @(
        @('Scalemon.ServiceHost','Application'),
        @('Scalemon.Updater','Infrastructure/Updater'),
        @('Scalemon.Maintenance','Infrastructure/Maintenance')
    )
    foreach ($project in $projects) {
        & dotnet publish (Join-Path $repoRoot "$($project[0])/$($project[0]).csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:Version=$Version" -o (Join-Path $payload $project[1])
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($project[0])" }
    }
    # Публикация содержит только программные ресурсы; комплектные настройки не берём с рабочего ПК.
    $application = Join-Path $payload 'Application'
    $forbidden = Get-ChildItem -LiteralPath $application -Recurse -File | Where-Object {
        $_.Name -like 'appsettings*.json' -or $_.Extension -in '.db','.sqlite','.sqlite3','.csv','.log','.pdb' -or $_.Name -like '*.db-*'
    }
    foreach ($file in $forbidden) {
        if (-not $file.FullName.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe output path' }
        Remove-Item -LiteralPath $file.FullName
    }
    [IO.File]::WriteAllText((Join-Path $application 'appsettings.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $payload 'version.txt'), $Version)
    $archiveName = "Scalemon-$Version-win-x64.zip"
    $archive = Join-Path $releaseRoot $archiveName
    [IO.Compression.ZipFile]::CreateFromDirectory($application, $archive)
    $expanded = (Get-ChildItem -LiteralPath $application -Recurse -File | Measure-Object Length -Sum).Sum
    $gitSha = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve Git SHA' }
    $manifest = [ordered]@{
        version=$Version; gitSha=$gitSha; channel=$Channel; architecture='win-x64'; archive=$archiveName
        size=(Get-Item -LiteralPath $archive).Length; expandedSize=[long]$expanded
        sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        minimumUpdaterVersion='1.0.0'; backwardCompatible=$true; notes=$Notes
    }
    $manifestPath = Join-Path $releaseRoot 'release-manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $key.ImportFromPem([IO.File]::ReadAllText($SigningKeyPath))
        if ($key.KeySize -ne 256) { throw 'Expected ECDSA P-256' }
        $bytes = [IO.File]::ReadAllBytes($manifestPath)
        $signature = $key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
        $public = [Security.Cryptography.ECDsa]::Create()
        try {
            $public.ImportFromPem([IO.File]::ReadAllText($PublicKeyPath))
            if (-not $public.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Signing key does not match trusted public key' }
        } finally { $public.Dispose() }
        [IO.File]::WriteAllBytes((Join-Path $releaseRoot 'release-manifest.sig'), $signature)
    } finally { $key.Dispose() }
    Push-Location $releaseRoot
    try {
        & $Makensis "/DVERSION=$Version" "/DPAYLOAD=$payload" (Join-Path $PSScriptRoot 'Scalemon.nsi')
        if ($LASTEXITCODE -ne 0) { throw 'NSIS compilation failed' }
    } finally { Pop-Location }
} finally {
    # В репозиторий попадает только открытый ключ, задаваемый владельцем выпуска.
    # Закрытый ключ не копируется и не выводится.
}
