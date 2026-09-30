param([switch]$Install, [switch]$InstallOnly)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:PUPPETEER_CACHE_DIR = Join-Path (Split-Path -Parent $PSScriptRoot) '.local\whatsapp\browser'

$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
if (!$nodeCommand) {
    $candidates = @(
        "$env:USERPROFILE\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe",
        "$env:LOCALAPPDATA\OpenAI\Codex\runtimes\cua_node\4004642ff3fabdc7\bin\node.exe",
        "C:\Program Files\nodejs\node.exe",
        "C:\Program Files (x86)\nodejs\node.exe"
    )
    foreach ($cand in $candidates) {
        if (Test-Path -LiteralPath $cand) {
            $nodeCommand = [PSCustomObject]@{ Source = $cand }
            break
        }
    }
}
if (!$nodeCommand) {
    throw 'Node.js was not found in PATH or standard installation directories. Please install Node.js 22 or later.'
}
$nodeMajor = [int]((& $nodeCommand.Source -v).TrimStart('v').Split('.')[0])
if ($nodeMajor -lt 22) { throw 'Node.js 22 or later is required. Please update Node.js.' }
if ($Install -or $InstallOnly -or !(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'node_modules\whatsapp-web.js'))) {
    $npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($npmCommand) {
        & $npmCommand.Source install
    } else {
        # Codex's bundled Node runtime includes pnpm alongside its bin directory.
        $pnpmCli = Join-Path (Split-Path -Parent (Split-Path -Parent $nodeCommand.Source)) 'node_modules\pnpm\bin\pnpm.cjs'
        if (!(Test-Path -LiteralPath $pnpmCli)) { throw 'Install Node.js 22 or later with npm, then run this script again.' }
        & $nodeCommand.Source $pnpmCli install --store-dir '..\.local\pnpm-store' --config.cache-dir='..\.local\pnpm-cache'
    }
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
}
if ($InstallOnly) {
    Write-Host 'Setup complete. Start SaleTracking; it will run WhatsApp in the background.'
    exit 0
}
Write-Host 'Manual bridge mode. Connect the sender from the admin account in SaleTrack Settings.'
& $nodeCommand.Source --env-file-if-exists=.env server.js
exit $LASTEXITCODE
