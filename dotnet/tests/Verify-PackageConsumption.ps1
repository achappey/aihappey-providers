param(
    [string] $WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('aihappey-providers-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$siblings = Split-Path $repo
$work = [IO.Path]::GetFullPath($WorkDirectory)
$feed = Join-Path $work 'feed'
$consumer = Join-Path $work 'consumer'
$packages = Join-Path $work 'packages'
$version = '0.0.0-verification.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')
New-Item -ItemType Directory -Force $feed, $consumer, $packages | Out-Null

foreach ($project in @(
    'aihappey-responses/AIHappey.Responses.csproj',
    'aihappey-chatcompletions/AIHappey.ChatCompletions.csproj',
    'aihappey-messages/AIHappey.Messages.csproj',
    'aihappey-interactions/AIHappey.Interactions.csproj',
    'aihappey-openai/AIHappey.OpenAI.csproj',
    'aihappey-openai/Decisions/AIHappey.Decisions.csproj',
    'aihappey-vercel/AIHappey.Vercel.csproj',
    'aihappey-unified/AIHappey.Unified.csproj',
    'aihappey-providers/dotnet/AIHappey.Providers/AIHappey.Providers.csproj'
)) {
    dotnet pack (Join-Path $siblings $project) -c Release -o $feed "-p:Version=$version" -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Package build failed: $project" }
}

# Only these consumer files cross the boundary: no project/source references or gateway files.
Copy-Item (Join-Path $PSScriptRoot 'PackageConsumer/PackageConsumer.csproj') $consumer
Copy-Item (Join-Path $PSScriptRoot 'PackageConsumer/Program.cs') $consumer
$project = Join-Path $consumer 'PackageConsumer.csproj'
$config = Join-Path $consumer 'NuGet.Config'
$escapedFeed = [System.Security.SecurityElement]::Escape($feed)
@"
<configuration>
  <packageSources>
    <clear />
    <add key="verification" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content $config -Encoding utf8
dotnet restore $project --configfile $config --packages $packages "-p:ProvidersPackageVersion=$version" -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Isolated package restore failed.' }
$assets = Get-Content (Join-Path $consumer 'obj/project.assets.json') -Raw | ConvertFrom-Json
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { throw "Unexpected source/project dependency: $($library.Name)" }
    if ($library.Name -match '^AIHappey\.(Core|Common|Gateway|Telemetry)([./]|$)') {
        throw "Unexpected gateway dependency: $($library.Name)"
    }
}
dotnet run --project $project -c Release --no-restore "-p:ProvidersPackageVersion=$version" "-p:RestorePackagesPath=$packages"
if ($LASTEXITCODE -ne 0) { throw 'Isolated provider package consumer failed.' }
# A package-only intermediary proves buildTransitive behavior, not just direct
# content copying. No gateway or repository project is referenced by this bridge.
$bridge = Join-Path $work 'bridge'
New-Item -ItemType Directory -Force $bridge | Out-Null
$bridgeProject = Join-Path $bridge 'ProvidersCatalogBridge.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="AIHappey.Providers" Version="$version" /></ItemGroup>
</Project>
"@ | Set-Content $bridgeProject -Encoding utf8
dotnet pack $bridgeProject -c Release -o $feed "-p:Version=$version" "-p:RestoreConfigFile=$config" "-p:RestorePackagesPath=$packages" -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Transitive package fixture failed to pack.' }
$consumerProject = [IO.File]::ReadAllText($project).Replace('Include="AIHappey.Providers"', 'Include="ProvidersCatalogBridge"')
[IO.File]::WriteAllText($project, $consumerProject)
dotnet restore $project --configfile $config --packages $packages "-p:ProvidersPackageVersion=$version" -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Transitive package-only restore failed.' }
dotnet run --project $project -c Release --no-restore "-p:ProvidersPackageVersion=$version" "-p:RestorePackagesPath=$packages"
if ($LASTEXITCODE -ne 0) { throw 'Transitive provider package consumer failed.' }
# Publishing must carry the complete package-owned catalog as well as build output.
$published = Join-Path $work 'published'
dotnet publish $project -c Release --no-restore -o $published "-p:ProvidersPackageVersion=$version" "-p:RestorePackagesPath=$packages" -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Isolated provider package publish failed.' }
dotnet (Join-Path $published 'PackageConsumer.dll')
if ($LASTEXITCODE -ne 0) { throw 'Published package consumer failed.' }
Write-Output "Verified provider package-only consumer with private dependency cache in $work"
