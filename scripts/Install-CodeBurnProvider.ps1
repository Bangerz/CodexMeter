param(
    [string]$VendorDirectory = (Join-Path $PSScriptRoot 'vendor\codeburn-0.9.25'),
    [string]$NodePath = (Get-Command node.exe).Source
)
$ErrorActionPreference = 'Stop'
$expectedIntegrity = 'sha512-ozjgg463v2D3Tm2LqYpK2EySjFnpglq6cyZL2N/Bz3tC7sgif11ttf7OL6lt7+7VAY4HYj/rOZLG9u+3erecwA=='
New-Item -ItemType Directory -Force -Path $VendorDirectory | Out-Null
$archive = Join-Path $VendorDirectory 'codeburn.tgz'
Invoke-WebRequest 'https://registry.npmjs.org/codeburn/-/codeburn-0.9.25.tgz' -OutFile $archive
$sha = [Security.Cryptography.SHA512]::Create()
try { $integrity = 'sha512-' + [Convert]::ToBase64String($sha.ComputeHash([IO.File]::ReadAllBytes($archive))) }
finally { $sha.Dispose() }
if ($integrity -ne $expectedIntegrity) { throw 'CodeBurn tarball integrity does not match the pinned package.' }
& tar.exe -xf $archive -C $VendorDirectory
if ($LASTEXITCODE -ne 0) { throw 'CodeBurn extraction failed.' }
& $NodePath --no-warnings (Join-Path $PSScriptRoot 'Prepare-CodeBurn.mjs') (Join-Path $VendorDirectory 'package') (Join-Path $VendorDirectory 'passive')
if ($LASTEXITCODE -ne 0) { throw 'CodeBurn provider reconstruction failed.' }
$dependencies = @(
    @{ Name='strip-ansi'; Version='7.2.0'; Integrity='sha512-yDPMNjp4WyfYBkHnjIRLfca1i6KMyGCtsVgoKe/z1+6vukgaENdgGBZt+ZmKPc4gavvEZ5OgHfHdrazhgNyG7w==' },
    @{ Name='ansi-regex'; Version='6.2.2'; Integrity='sha512-Bq3SmSpyFHaWjPk8If9yc6svM8c56dB5BAtW4Qbw5jHTwwXXcTLoRMkpDJp6VL0XzlWaCHTXrkFURMYmD0sLqg==' }
)
foreach ($dependency in $dependencies) {
    $dependencyArchive = Join-Path $VendorDirectory ($dependency.Name + '.tgz')
    Invoke-WebRequest ('https://registry.npmjs.org/{0}/-/{0}-{1}.tgz' -f $dependency.Name,$dependency.Version) -OutFile $dependencyArchive
    $dependencySha = [Security.Cryptography.SHA512]::Create()
    try { $dependencyIntegrity = 'sha512-' + [Convert]::ToBase64String($dependencySha.ComputeHash([IO.File]::ReadAllBytes($dependencyArchive))) }
    finally { $dependencySha.Dispose() }
    if ($dependencyIntegrity -ne $dependency.Integrity) { throw ('Dependency integrity mismatch: ' + $dependency.Name) }
    $dependencyDirectory = Join-Path $VendorDirectory ('passive\node_modules\' + $dependency.Name)
    New-Item -ItemType Directory -Force -Path $dependencyDirectory | Out-Null
    & tar.exe -xf $dependencyArchive -C $dependencyDirectory --strip-components=1
    if ($LASTEXITCODE -ne 0) { throw ('Dependency extraction failed: ' + $dependency.Name) }
}
Write-Output (Join-Path $VendorDirectory 'passive')
