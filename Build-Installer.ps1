param(
    [string]$Compiler,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.2.0'
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
if (-not $Compiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $Compiler = $command.Source }
    else {
        $roots = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, "$env:LOCALAPPDATA\Programs")
        $Compiler = $roots | Where-Object { $_ } | ForEach-Object {
            Join-Path $_ 'Inno Setup 6\ISCC.exe'
            Join-Path $_ 'Inno Setup 7\ISCC.exe'
        } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if (-not $Compiler -or -not (Test-Path -LiteralPath $Compiler -PathType Leaf)) {
    throw 'Instale o Inno Setup 6.3 ou posterior, ou informe o caminho do ISCC.exe com -Compiler.'
}

$project = Join-Path $projectDir 'Janela.csproj'
$published = Join-Path $projectDir 'publish'
& dotnet publish $project -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $published
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação do Janela.' }

$definition = Join-Path $projectDir 'installer\Janela.iss'
& $Compiler "/DAppVersion=$Version" $definition
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o instalador.' }
Write-Output (Join-Path $projectDir 'dist\Janela-Setup-x64.exe')
