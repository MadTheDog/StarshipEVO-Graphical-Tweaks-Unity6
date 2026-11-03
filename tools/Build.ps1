param([string]$OutputDirectory='dist/GraphicalTweaks',[switch]$NoRestore,[string]$ShaderDirectory='dist/GlassRendering',[switch]$SkipTests,[Parameter(Mandatory=$true)][string]$GameDirectory,[string]$LoaderDirectory='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'BuildPaths.ps1')
$env:DOTNET_CLI_HOME=Join-Path $root '.tools/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:NUGET_PACKAGES=Join-Path $root '.tools/nuget'
$dotnetPath=Join-Path $root '.tools/dotnet/dotnet.exe'
if(!(Test-Path -LiteralPath $dotnetPath)){$dotnetPath=(Get-Command dotnet -ErrorAction Stop).Source}
Push-Location $root
try {
 $restoreOptions=@()
 if($NoRestore){$restoreOptions=@('--no-restore')}
 if(!$SkipTests){
 foreach($suite in @('GraphicalTweaks','GlassFaceCoverage','NearShadowDetail','GlassSceneSupport','GlassRayClassifier','DecalVisibilityWork','GlassNominalCaps','GlassNominalProfiles','GlassShapePublication','GlassPartialGeometry','GlassComparisonOutcome')) {
  & $dotnetPath run --project ('tests/'+$suite+'.Tests') -c Release @restoreOptions
  if($LASTEXITCODE){throw ($suite+' behavior checks failed')}
 }
 foreach($project in @('tests/GlassPartialCoverage.Tests','tests/GlassPartialCache.Tests','tests/GlassPartialTail.Native/PolicyTests','tests/GlassPartialCoverage.Native/PolicyTests','tests/GlassPartialReferenceChecks.Tests','tests/Unity6.Runtime.Tests')) {
  & $dotnetPath run --project $project -c Release @restoreOptions
  if($LASTEXITCODE){throw ($project+' behavior checks failed')}
 }
 } else {Write-Output 'Behavior suites skipped for this wording/version build.'}
 $buildProperties=@()
 if($GameDirectory){$buildProperties+=('-p:GameDir='+$GameDirectory)}
 if($LoaderDirectory){$buildProperties+=('-p:LoaderDir='+$LoaderDirectory)}
 & $dotnetPath build src/GraphicalTweaks.Unity6 -c Release @restoreOptions @buildProperties
 if($LASTEXITCODE){throw 'Plugin build failed'}
 $shaderRoot=Get-BuildPath $root $ShaderDirectory
 $bundle=Get-BuildPath $root (Join-Path $shaderRoot 'graphicaltweaks-glass.bundle')
 $buildInfo=Get-BuildPath $root (Join-Path $shaderRoot 'build-info.txt')
 if(!(Test-Path -LiteralPath $bundle) -or !(Test-Path -LiteralPath $buildInfo)) {throw 'Build the original glass shader bundle with the matching Unity Editor first; see README.md'}
 if((Get-Content -LiteralPath $buildInfo -TotalCount 1) -ne '6000.6.6f1') {throw 'Glass bundle must be built with Unity 6000.6.6f1'}
 $output=Get-BuildPath $root $OutputDirectory
 New-Item -ItemType Directory -Force -Path $output | Out-Null
 $plugin = Get-BuildPath $root (Join-Path $output 'BepInEx/plugins/GraphicalTweaks')
 New-Item -ItemType Directory -Force -Path $plugin | Out-Null
 Copy-Item -LiteralPath 'src/GraphicalTweaks.Unity6/bin/Release/netstandard2.1/GraphicalTweaks.dll' -Destination (Get-BuildPath $root (Join-Path $plugin 'GraphicalTweaks.dll'))
 Copy-Item -LiteralPath $bundle -Destination (Get-BuildPath $root (Join-Path $plugin 'graphicaltweaks-glass.bundle'))
 Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Get-BuildPath $root (Join-Path $output 'README.md'))
 Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Get-BuildPath $root (Join-Path $output 'LICENSE'))
 [string[]]$hashLines=foreach($name in @('BepInEx/plugins/GraphicalTweaks/GraphicalTweaks.dll','BepInEx/plugins/GraphicalTweaks/graphicaltweaks-glass.bundle','README.md','LICENSE')) {
  (Get-FileHash -LiteralPath (Join-Path $output $name)).Hash.ToLowerInvariant()+'  '+$name
 }
 [IO.File]::WriteAllLines((Get-BuildPath $root (Join-Path $output 'SHA256SUMS.txt')),$hashLines,[Text.UTF8Encoding]::new($false))
 Write-Output ('Local package: '+$output)
} finally { Pop-Location }
