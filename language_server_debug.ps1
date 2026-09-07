<#
.SYNOPSIS
    Compiles Roslyn SDK

.EXAMPLE
    .\langauge_server_debug.ps1                                       # Compiles and saves to ./artifacts/LanguageServerDebug
    .\langauge_server_debug.ps1 -Live C:\Users\aljaz\.vscode\extensions\ms-dotnettools.csharp-2.140.9-win32-x64   # Compiles, saves to ./artifacts/LanguageServerDebug and attempts to copy to live
#>
[CmdletBinding(DefaultParameterSetName = 'Patch')]
param(
    [System.IO.DirectoryInfo]$live,
    [switch]$nocompile
)

$debug = ".\artifacts\LanguageServerDebug"
$ok = $true

if (-Not ($nocompile)){
    dotnet publish .\src\LanguageServer\Microsoft.CodeAnalysis.LanguageServer\ -c Debug -o .\artifacts\LanguageServerDebug /p:DebugType=portable /p:IncludeSymbols=true
    if ($LASTEXITCODE -ne 0) {
        $ok = $false
    }
}

if ($ok) {
    if($live) {
        $live = Join-Path $live ".roslyn"
        Copy-Item -Path "$debug\*" -Destination $live -Recurse -Force
    }
}