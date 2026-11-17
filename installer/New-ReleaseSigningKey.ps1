#Requires -Version 7.4
param([Parameter(Mandatory)][string]$PrivateKeyPath, [Parameter(Mandatory)][string]$PublicKeyPath)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)) + [IO.Path]::DirectorySeparatorChar
$privatePath = [IO.Path]::GetFullPath($PrivateKeyPath)
if ($privatePath.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep private signing keys outside the repository.' }
if ((Test-Path -LiteralPath $PrivateKeyPath) -or (Test-Path -LiteralPath $PublicKeyPath)) { throw 'Refusing to replace an existing key.' }
$privateDirectory = Split-Path $privatePath -Parent
$publicDirectory = Split-Path ([IO.Path]::GetFullPath($PublicKeyPath)) -Parent
if (-not (Test-Path -LiteralPath $privateDirectory)) { New-Item -ItemType Directory -Path $privateDirectory | Out-Null }
if (-not (Test-Path -LiteralPath $publicDirectory)) { New-Item -ItemType Directory -Path $publicDirectory | Out-Null }
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow'))
    }
    # Сначала ограничить ACL пустого файла, только затем записать закрытый ключ.
    [IO.File]::WriteAllText($privatePath, '')
    Set-Acl -LiteralPath $privatePath -AclObject $acl
    [IO.File]::WriteAllText($privatePath, $key.ExportPkcs8PrivateKeyPem(), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($PublicKeyPath), $key.ExportSubjectPublicKeyInfoPem(), [Text.UTF8Encoding]::new($false))
    Write-Output 'Signing key created. Store the private file securely; never commit it.'
} finally { $key.Dispose() }
