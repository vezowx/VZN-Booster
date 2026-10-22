#requires -Version 5.1
<#
.SYNOPSIS
    Publica o VZN Booster e gera o pacote Velopack (Setup + atualizacoes) para o GitHub Releases.

.DESCRIPTION
    Execute na RAIZ do projeto (onde esta o VznBooster.sln), num Windows com:
      - .NET 8 SDK          (https://dotnet.microsoft.com/download)
      - GitHub CLI (gh)     (https://cli.github.com)  [somente para -EnviarParaGitHub]

    Antes da primeira vez:
      1. Crie o repositorio no GitHub e suba o codigo.
      2. Troque $Repositorio abaixo (e RepositoryUrl em Services/UpdateService.cs) pelo seu "usuario/repo".

    Exemplos:
      powershell -ExecutionPolicy Bypass -File .\publicar\Publicar-Velopack.ps1 -Versao 1.0.0
      powershell -ExecutionPolicy Bypass -File .\publicar\Publicar-Velopack.ps1 -Versao 1.1.0 -EnviarParaGitHub -Notas "Correcoes da barra lateral"

    Saida: publicar\releases\  ->  VZN.VznBooster-win-Setup.exe (instalador), pacotes .nupkg e releases.win.json
#>
param(
    [Parameter(Mandatory = $true)][string]$Versao,
    [string]$Repositorio = "vezowx/VZN-Booster",
    [string]$Notas = "",
    [switch]$EnviarParaGitHub
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($env:PWF_APP_SECRET)) {
    throw "Defina a variavel PWF_APP_SECRET com o App Secret do PWF Auth (setx PWF_APP_SECRET ""...""), feche e abra o terminal."
}
$raiz    = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$projeto = Join-Path $raiz "src\VznBooster.Desktop\VznBooster.Desktop.csproj"
$saida   = Join-Path $raiz "publicar\publicado"
$releases = Join-Path $raiz "publicar\releases"

# packId = pasta de instalacao em %LocalAppData%. Propositalmente DIFERENTE de "VznBooster"
# (pasta de dados do app), para o Velopack nunca tocar em settings/logs/backups.
$packId  = "VZN.VznBooster"
$vpkVersao = "1.2.161"   # mesma versao do pacote Velopack no .csproj

if (-not (Test-Path $projeto)) { throw "Estrutura do projeto nao encontrada. Confira se o script esta em <raiz>\publicar\." }
if ($Repositorio -eq "SEU-USUARIO/VznBooster") { Write-Warning "Repositorio padrao: ajuste -Repositorio e RepositoryUrl no UpdateService.cs antes de distribuir." }

# ---------------------------------------------------------------- 1. Testes
Write-Host "`n==> Rodando testes do nucleo..." -ForegroundColor Cyan
dotnet test (Join-Path $raiz "tests\VznBooster.Core.Tests\VznBooster.Core.Tests.csproj") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Testes falharam. Publicacao abortada." }

# ---------------------------------------------------------------- 2. Publish (pasta comum; o Velopack empacota, NAO usar PublishSingleFile)
Write-Host "`n==> Publicando $Versao (win-x64, self-contained)..." -ForegroundColor Cyan
if (Test-Path $saida) { Remove-Item $saida -Recurse -Force }
dotnet publish $projeto -c Release -r win-x64 --self-contained true `
    -p:Version=$Versao -p:DebugType=none -p:DebugSymbols=false `
    -p:PwfAppSecret=$env:PWF_APP_SECRET `
    -o $saida --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou." }

# ---------------------------------------------------------------- 3. Ferramenta vpk (mesma versao do pacote Velopack)
$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    Write-Host "`n==> Instalando a ferramenta vpk $vpkVersao..." -ForegroundColor Cyan
    dotnet tool install -g vpk --version $vpkVersao
    if ($LASTEXITCODE -ne 0) { throw "Nao foi possivel instalar o vpk." }
}

# ---------------------------------------------------------------- 4. Empacotar (Setup + pacote full + delta + releases.win.json)
Write-Host "`n==> Empacotando com Velopack..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force $releases | Out-Null
vpk download github --repoUrl "https://github.com/$Repositorio" --outputDir $releases
vpk pack --packId $packId --packVersion $Versao --packDir $saida --outputDir $releases `
    --runtime win-x64 `
    --mainExe VznBooster.exe --packTitle "VZN Booster" --packAuthors "VZN" `
    --icon (Join-Path $raiz "src\VznBooster.Desktop\VznBooster.ico")
if ($LASTEXITCODE -ne 0) { throw "vpk pack falhou." }

Write-Host "`n==> Pacotes gerados:" -ForegroundColor Green
Get-ChildItem $releases | Format-Table Name, @{ N = "MB"; E = { [math]::Round($_.Length / 1MB, 1) } }

# ---------------------------------------------------------------- 5. Publicar no GitHub Releases (opcional)
if ($EnviarParaGitHub) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { throw "GitHub CLI (gh) nao encontrado. Instale em https://cli.github.com e rode 'gh auth login'." }

    $tag = "v$Versao"
    $titulo = "VZN Booster $Versao"
    $arquivos = Get-ChildItem $releases -File | ForEach-Object { $_.FullName }

    Write-Host "`n==> Publicando release $tag em $Repositorio..." -ForegroundColor Cyan
        $ErrorActionPreference = "Continue"
        gh release view $tag --repo $Repositorio *> $null
        $releaseExiste = ($LASTEXITCODE -eq 0)
        $ErrorActionPreference = "Stop"

        if ($releaseExiste) {
        gh release upload $tag $arquivos --repo $Repositorio --clobber
    } else {
        gh release create $tag $arquivos --repo $Repositorio --title $titulo --notes $Notas
    }
    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar a release no GitHub." }
    Write-Host "`n==> Release publicada. Usuarios da versao anterior receberao a atualizacao na proxima abertura do app." -ForegroundColor Green
} else {
    Write-Host "`nPara publicar: rode de novo com -EnviarParaGitHub, ou suba manualmente os arquivos de" -ForegroundColor Yellow
    Write-Host "$releases para uma release 'v$Versao' em https://github.com/$Repositorio/releases" -ForegroundColor Yellow
}