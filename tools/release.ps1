param(
    # the version to release; must be the <Version> in ClipNest.csproj
    [Parameter(Mandatory)][string]$Version,
    # the private release key (ECDSA P-256, PKCS#8 PEM). Keep it off the repository, and backed up.
    [string]$Key = (Join-Path $HOME '.clipnest-release\signing-key.pem'),
    # release notes (Markdown); without them GitHub's list of changes is used
    [string]$Notes,
    # build and sign only, without publishing the release
    [switch]$DryRun
)
# Builds both editions, signs each exe with the release key and publishes a GitHub release with the exe and .sig
# files. The built-in updater installs an exe only if its .sig verifies against the public key compiled into it.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$csproj = [xml](Get-Content (Join-Path $root 'ClipNest.csproj'))
$projectVersion = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if ($projectVersion -ne $Version) { throw "ClipNest.csproj has version $projectVersion, not $Version" }
if (-not (Test-Path $Key)) { throw "no release key at $Key" }

$stage = Join-Path ([IO.Path]::GetTempPath()) "clipnest-release-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null

$editions = @(
    @{ Ocr = 'false'; Out = 'publish'; Asset = 'ClipNest.exe' },
    @{ Ocr = 'true'; Out = 'publish-ocr'; Asset = 'ClipNest-OCR.exe' }
)
$ec = [System.Security.Cryptography.ECDsa]::Create()
$ec.ImportFromPem((Get-Content $Key -Raw))
foreach ($e in $editions) {
    dotnet publish (Join-Path $root 'ClipNest.csproj') -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:ClipNestOcr=$($e.Ocr) -o (Join-Path $root $e.Out) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build of $($e.Asset) failed" }
    $exe = Join-Path $stage $e.Asset
    Copy-Item (Join-Path $root "$($e.Out)\ClipNest.exe") $exe
    $hash = [System.Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($exe))
    $sig = $ec.SignHash($hash)
    # check it the way the app does: with the public key only
    $check = [System.Security.Cryptography.ECDsa]::Create()
    $check.ImportSubjectPublicKeyInfo($ec.ExportSubjectPublicKeyInfo(), [ref]0)
    if (-not $check.VerifyHash($hash, $sig)) { throw "the signature of $($e.Asset) doesn't verify" }
    Set-Content -NoNewline -Path "$exe.sig" -Value ([Convert]::ToBase64String($sig))
    "{0}: {1:N0} B, sha256 {2}" -f $e.Asset, (Get-Item $exe).Length, ([Convert]::ToHexString($hash).ToLower())
}

$assets = Get-ChildItem $stage | ForEach-Object { $_.FullName }
if ($DryRun) { "dry run: signed files are in $stage"; return }
$ghArgs = @('release', 'create', "v$Version", '--target', 'main', '--title', "ClipNest $Version") + $assets
if ($Notes) { $ghArgs += @('--notes-file', $Notes) } else { $ghArgs += '--generate-notes' }
gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
