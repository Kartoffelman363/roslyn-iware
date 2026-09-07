<#
.SYNOPSIS
    Compiles Roslyn SDK

.EXAMPLE
    .\compiler_debug.ps1                                       # Compiles and saves to ./artifacts/CompilerDebug
    .\compiler_debug.ps1 -Live .dotnet\sdk\10.0.301-sql-live   # Compiles, saves to ./artifacts/CompilerDebug and attempts to copy to live
#>
[CmdletBinding(DefaultParameterSetName = 'Patch')]
param(
    [System.IO.DirectoryInfo]$live,
    [switch]$nocompile
)

$debug = ".\artifacts\CompilerDebug"
$bincore = Join-Path $debug "bincore"
$ok = $true

if (-Not ($nocompile)){
    dotnet publish .\src\Compilers\Core\MSBuildTask\MSBuild\Microsoft.Build.Tasks.CodeAnalysis.csproj -c Debug -o $debug /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
    if ($LASTEXITCODE -ne 0) {
        $ok = $false
    }
    dotnet publish .\src\Compilers\Server\VBCSCompiler\AnyCpu\VBCSCompiler.csproj -c Debug -o $bincore /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
    if ($LASTEXITCODE -ne 0) {
        $ok = $false
    }
    dotnet publish .\src\Compilers\CSharp\csc\AnyCpu\csc.csproj -c Debug -o $bincore /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
    if ($LASTEXITCODE -ne 0) {
        $ok = $false
    }
    dotnet publish .\src\Compilers\VisualBasic\vbc\AnyCpu\vbc.csproj -c Debug -o $bincore /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
    if ($LASTEXITCODE -ne 0) {
        $ok = $false
    }
}

if ($ok) {
    if($live) {
        $live = Join-Path $live "Roslyn"
        Copy-Item -Path "$debug\*" -Destination $live -Recurse -Force
    }
}