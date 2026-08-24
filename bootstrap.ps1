$ErrorActionPreference = "Stop"

Write-Host "== StoneAge Online v0.1-01 bootstrap ==" -ForegroundColor Cyan

dotnet --version

if (-not (Test-Path "StoneAgeOnline.slnx")) {
    dotnet new sln --name StoneAgeOnline
}

$projects = @(
    "src/StoneAge.Domain/StoneAge.Domain.csproj",
    "src/StoneAge.Infrastructure/StoneAge.Infrastructure.csproj",
    "src/StoneAge.Network/StoneAge.Network.csproj",
    "src/StoneAge.Game/StoneAge.Game.csproj",
    "src/StoneAge.Shared/StoneAge.Shared.csproj",
    "src/StoneAge.Server/StoneAge.Server.csproj",
    "tools/StoneAge.TestClient/StoneAge.TestClient.csproj"
)

foreach ($project in $projects) {
    dotnet sln StoneAgeOnline.slnx add $project
}

dotnet restore StoneAgeOnline.slnx
dotnet build StoneAgeOnline.slnx

Write-Host "Bootstrap complete." -ForegroundColor Green
