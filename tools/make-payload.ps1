# Construit FNHybridLauncher/payload.zip : le serveur privé (LawinServer adapté
# pour X.HYBRID) et le moteur Node.js, embarqués dans l'exe.
#
# L'exe les installe tout seul au premier lancement dans %APPDATA%\FNHybrid :
# l'utilisateur n'a donc ni dossier LawinServer ni Node.js à installer.
#
# À relancer avant chaque release (le fichier n'est pas versionné dans git) :
#     powershell -ExecutionPolicy Bypass -File tools\make-payload.ps1
param(
    [string]$ServerDir = (Join-Path $PSScriptRoot "..\LawinServer"),
    [string]$NodeExe   = (Join-Path $env:ProgramFiles "nodejs\node.exe"),
    [string]$OutFile   = (Join-Path $PSScriptRoot "..\FNHybridLauncher\payload.zip"),
    # sources du serveur, à joindre aux releases : l'exe distribue du code GPL-3.0
    [string]$SourceZip = (Join-Path $PSScriptRoot "..\dist\serveur-source.zip")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$server = (Resolve-Path $ServerDir).Path
if (-not (Test-Path (Join-Path $server "index.js"))) { throw "index.js introuvable dans $server" }
if (-not (Test-Path $NodeExe)) { throw "node.exe introuvable : $NodeExe" }

$stage  = Join-Path ([System.IO.Path]::GetTempPath()) ("xh-payload-" + [Guid]::NewGuid().ToString("N"))
$serverDst = Join-Path $stage "server"
$nodeDst   = Join-Path $stage "node"
New-Item -ItemType Directory -Path $serverDst, $nodeDst -Force | Out-Null

Write-Host "1/4 copie du serveur (sans .git, *.bak, *.log)…"
robocopy $server $serverDst /E /XD ".git" /XF "*.bak" "*.log" ".gitignore" /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy a echoue (code $LASTEXITCODE)" }
$global:LASTEXITCODE = 0

Write-Host "2/4 copie du moteur Node.js…"
Copy-Item -LiteralPath $NodeExe -Destination (Join-Path $nodeDst "node.exe") -Force
$nodeVersion = (& $NodeExe --version).Trim()
$lic = Join-Path $PSScriptRoot "node-LICENSE.txt"
if (Test-Path $lic) { Copy-Item -LiteralPath $lic -Destination (Join-Path $nodeDst "LICENSE-nodejs.txt") -Force }

$info = @"
X.HYBRID - contenu embarque dans l'executable
construit le $(Get-Date -Format "yyyy-MM-dd HH:mm")

serveur : LawinServer (adapte pour X.HYBRID), licence GPL-3.0
          source amont : https://github.com/Lawin0129/LawinServer
          le fichier LICENSE d'origine est dans server\LICENSE
moteur  : Node.js $nodeVersion, licence MIT - https://nodejs.org
          texte de licence : node\LICENSE-nodejs.txt
"@
Set-Content -Path (Join-Path $stage "payload-info.txt") -Value $info -Encoding UTF8

Write-Host "3/4 compression (patience, ~100 Mo)…"
if (Test-Path $OutFile) { Remove-Item $OutFile -Force }
New-Item -ItemType Directory -Path (Split-Path $OutFile) -Force | Out-Null
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $OutFile, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "4/4 sources du serveur (GPL-3.0, à joindre a la release)…"
if (Test-Path $SourceZip) { Remove-Item $SourceZip -Force }
New-Item -ItemType Directory -Path (Split-Path $SourceZip) -Force | Out-Null
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $serverDst, $SourceZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Remove-Item $stage -Recurse -Force

$size = (Get-Item $OutFile).Length
Write-Host ("payload.zip : {0:N1} Mo -> {1}" -f ($size / 1MB), $OutFile)
Write-Host ("sources serveur : {0:N1} Mo -> {1}" -f ((Get-Item $SourceZip).Length / 1MB), $SourceZip)
Write-Host "L'exe embarque ce fichier : recompile ensuite le launcher."
