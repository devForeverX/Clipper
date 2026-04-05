$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "Clipper.csproj"
dotnet publish $project -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
