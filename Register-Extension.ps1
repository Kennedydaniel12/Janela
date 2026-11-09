param(
    [Parameter(Mandatory)][ValidateSet('Chrome','Edge','Brave')][string]$Browser,
    [Parameter(Mandatory)][ValidatePattern('^[a-p]{32}$')][string]$ExtensionId,
    [Parameter(Mandatory)][string]$Executable
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
if ((Split-Path -Leaf $exe) -ne 'Janela.exe') { throw 'Informe o caminho de Janela.exe.' }
$family = if ($Browser -eq 'Edge') { 'edge' } else { 'chrome' }
$hostName = "com.janela.$family"
$vendors = switch ($Browser) {
    'Edge' { 'Microsoft\Edge' }
    'Brave' { 'BraveSoftware\Brave-Browser'; 'Google\Chrome' }
    default { 'Google\Chrome' }
}
$bridge = Join-Path (Split-Path -Parent $exe) "Janela.$family.exe"
if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) {
    Copy-Item -LiteralPath $exe -Destination $bridge
}
$folder = Join-Path $env:LOCALAPPDATA 'Janela'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$manifest = Join-Path $folder "$hostName.json"
$origins = @("chrome-extension://$ExtensionId/")
if (Test-Path -LiteralPath $manifest) {
    $existing = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $origins += @($existing.allowed_origins | Where-Object { $_ -match '^chrome-extension://[a-p]{32}/$' })
}
$json = @{
    name = $hostName
    description = 'Janela video bridge'
    path = $bridge
    type = 'stdio'
    allowed_origins = @($origins | Select-Object -Unique)
} | ConvertTo-Json
[IO.File]::WriteAllText($manifest, $json)
foreach ($vendor in $vendors) {
    $key = "HKCU:\Software\$vendor\NativeMessagingHosts\$hostName"
    New-Item -Path $key -Force | Out-Null
    Set-Item -LiteralPath $key -Value $manifest
}
Write-Output "Extensao registrada para $Browser."
