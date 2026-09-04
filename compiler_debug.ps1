$debug = ".\artifacts\CompilerDebug"
$bincore = Join-Path $debug "bincore"

dotnet publish .\src\Compilers\Core\MSBuildTask\MSBuild\Microsoft.Build.Tasks.CodeAnalysis.csproj -c Debug -o $debug /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
dotnet publish .\src\Compilers\Server\VBCSCompiler\AnyCpu\VBCSCompiler.csproj -c Debug -o $bincore /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
dotnet publish .\src\Compilers\CSharp\csc\AnyCpu\csc.csproj -c Debug -o $bincore /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
dotnet publish .\src\Compilers\VisualBasic\vbc\AnyCpu\vbc.csproj -c Debug -o $bincore /p:DebugType=portable /p:IncludeSymbols=true -f net10.0