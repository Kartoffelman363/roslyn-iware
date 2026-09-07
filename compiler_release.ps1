<#
.SYNOPSIS
    Compiles Roslyn SDK

.EXAMPLE
    .\compiler_release.ps1                                       # Compiles and saves to ./artifacts/CompilerRelease
    .\compiler_release.ps1 -Live .dotnet\sdk\10.0.301-sql-live   # Compiles, saves to ./artifacts/CompilerRelease and attempts to copy to live
#>
[CmdletBinding(DefaultParameterSetName = 'Patch')]
param(
    [System.IO.DirectoryInfo]$live,
    [switch]$nocompile
)

$release = ".\artifacts\CompilerRelease"
$bincore = Join-Path $release "bincore"
$ok = $true

if (-Not ($nocompile)){
    dotnet publish .\src\Compilers\Core\MSBuildTask\MSBuild\Microsoft.Build.Tasks.CodeAnalysis.csproj -c Release -o $release /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
    if ($LASTEXITCODE -ne 0) {
        $ok = $false
    }
    dotnet publish .\src\Compilers\Server\VBCSCompiler\AnyCpu\VBCSCompiler.csproj -c Release -o $bincore /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
    if ($LASTEXITCODE -ne 0) {
        $ok = $false
    }
    dotnet publish .\src\Compilers\CSharp\csc\AnyCpu\csc.csproj -c Release -o $bincore /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
    if ($LASTEXITCODE -ne 0) {
        $ok = $false
    }
    dotnet publish .\src\Compilers\VisualBasic\vbc\AnyCpu\vbc.csproj -c Release -o $bincore /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
    if ($LASTEXITCODE -ne 0) {
        $ok = $false
    }
}

if ($ok) {
    if($live) {
        $live = Join-Path $live "Roslyn"
        Copy-Item -Path "$release\*" -Destination $live -Recurse -Force
    }
}