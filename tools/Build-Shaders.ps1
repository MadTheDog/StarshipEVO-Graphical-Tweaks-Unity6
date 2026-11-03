param([switch]$ViaUnityCli,[string]$OutputDirectory='dist/GlassRendering',[string]$EditorPath='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'BuildPaths.ps1')
$editor=if($EditorPath){[IO.Path]::GetFullPath($EditorPath)}else{Join-Path $root '.tools/Unity-6000.6.6f1/Editor/Unity.exe'}
if(!(Test-Path -LiteralPath $editor)){throw 'Unity Editor 6000.6.6f1 is required'}
if((Get-Item -LiteralPath $editor).VersionInfo.FileVersion -ne '6000.6.6.13765786'){throw 'Expected Unity Editor 6000.6.6f1'}
$output=Get-BuildPath $root $OutputDirectory
$assets=Get-BuildPath $root 'shader-project/Assets/GraphicalTweaks'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
foreach($source in Get-ChildItem -LiteralPath (Join-Path $root 'shaders') -File){
 if($source.Extension -notin @('.shader','.hlsl','.cginc','.compute')){continue}
 $target=Get-BuildPath $root (Join-Path $assets $source.Name)
 Get-BuildPath $root ($target+'.meta') | Out-Null
 Copy-Item -LiteralPath $source.FullName -Destination $target
 if(!(Test-Path -LiteralPath ($target+'.meta'))){
  $md5=[Security.Cryptography.MD5]::Create()
  try{$guid=([BitConverter]::ToString($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes('GraphicalTweaks/'+$source.Name)))).Replace('-','').ToLowerInvariant()}finally{$md5.Dispose()}
  [IO.File]::WriteAllText($target+'.meta',"fileFormatVersion: 2`nguid: $guid`n")
 }
}
$project=Get-BuildPath $root 'shader-project'
foreach($leaf in @('Packages','ProjectSettings','build.request')){Get-BuildPath $root (Join-Path $project $leaf) | Out-Null}
foreach($leaf in @('graphicaltweaks-glass.bundle','build-info.txt',(Split-Path -Leaf $output))){Get-BuildPath $root (Join-Path $output $leaf) | Out-Null}
$outputRequest=Get-BuildPath $root (Join-Path $project 'build.output')
[IO.File]::WriteAllText($outputRequest,$output,[Text.UTF8Encoding]::new($false))
$started=[DateTime]::UtcNow
try {
if($ViaUnityCli){
 $cli=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Unity/bin/unity.exe'
 if(!(Test-Path -LiteralPath $cli)){throw 'The already-installed Unity CLI was not found; no tool will be installed automatically'}
 $started=[DateTime]::UtcNow
 [IO.File]::WriteAllText((Join-Path $project 'build.request'),'build')
 & $cli open $project --editor-path $editor --non-interactive --no-banner --format json
 if($LASTEXITCODE){throw 'Unity CLI did not launch the approved shader project'}
 $bundle=Join-Path $output 'graphicaltweaks-glass.bundle'
 $info=Join-Path $output 'build-info.txt'
 $until=[DateTime]::UtcNow.AddSeconds(120)
 while([DateTime]::UtcNow -lt $until){
  if((Test-Path -LiteralPath $bundle) -and (Test-Path -LiteralPath $info) -and
     (Get-Item -LiteralPath $bundle).LastWriteTimeUtc -ge $started -and
     (Get-Item -LiteralPath $info).LastWriteTimeUtc -ge $started){
   if((Get-Content -LiteralPath $info -TotalCount 1) -ne '6000.6.6f1'){throw 'Expected Unity 6000.6.6f1'}
   Write-Output ('Matching Unity shader build completed: '+$bundle)
   return
  }
  Start-Sleep -Milliseconds 500
 }
 throw 'Unity CLI launched but no fresh completed shader bundle appeared within120 seconds; inspect the Editor log'
}
$log=Get-BuildPath $root ('local-evidence/shader-build-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.log')
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null
$args='-batchmode -nographics -quit -projectPath "'+$project+'" -executeMethod BuildGraphicalTweaks.Build -logFile "'+$log+'"'
$process=Start-Process -FilePath $editor -ArgumentList $args -WindowStyle Hidden -PassThru
$process.WaitForExit()
$buildLog=[IO.File]::ReadAllText($log).Replace([string][char]0,'')
Write-Output ('Unity build log: '+$log)
Write-Output ($buildLog.Substring([Math]::Max(0,$buildLog.Length-7000)))
if($process.ExitCode -ne 0){throw ('Unity shader build exited '+$process.ExitCode)}
$bundle=Join-Path $output 'graphicaltweaks-glass.bundle'
$info=Join-Path $output 'build-info.txt'
if(!(Test-Path -LiteralPath $bundle) -or !(Test-Path -LiteralPath $info) -or
   (Get-Item -LiteralPath $bundle).LastWriteTimeUtc -lt $started -or
   (Get-Item -LiteralPath $info).LastWriteTimeUtc -lt $started){throw 'Fresh shader bundle/build info missing; inspect the Editor log'}
if((Get-Content -LiteralPath $info -TotalCount 1) -ne '6000.6.6f1'){throw 'Expected Unity 6000.6.6f1'}
} finally {
 if($ViaUnityCli -and (Test-Path -LiteralPath (Join-Path $project 'build.request'))){Remove-Item -LiteralPath (Join-Path $project 'build.request') -Force}
 if(Test-Path -LiteralPath $outputRequest){Remove-Item -LiteralPath $outputRequest -Force}
}
