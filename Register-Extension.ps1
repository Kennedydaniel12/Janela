param(
    [Parameter(Mandatory)][ValidateSet('Chrome','Edge')][string]$Browser,
    [Parameter(Mandatory)][ValidatePattern('^[a-p]{32}$')][string]$ExtensionId,
    [Parameter(Mandatory)][string]$Executable
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
if ((Split-Path -Leaf $exe) -ne 'Janela.exe') { throw 'Informe o caminho de Janela.exe.' }
$family = $Browser.ToLowerInvariant()
$hostName = "com.janela.$family"
$vendor = if ($Browser -eq 'Chrome') { 'Google\Chrome' } else { 'Microsoft\Edge' }
$bridge = Join-Path (Split-Path -Parent $exe) "Janela.$family.exe"
Copy-Item -LiteralPath $exe -Destination $bridge -Force
$folder = Join-Path $env:LOCALAPPDATA 'Janela'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$manifest = Join-Path $folder "$hostName.json"
$json = @{
    name = $hostName
    description = 'Janela video bridge'
    path = $bridge
    type = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
} | ConvertTo-Json
[IO.File]::WriteAllText($manifest, $json)
$key = "HKCU:\Software\$vendor\NativeMessagingHosts\$hostName"
New-Item -Path $key -Force | Out-Null
Set-Item -LiteralPath $key -Value $manifest
Write-Output "Extensao registrada para $Browser."
