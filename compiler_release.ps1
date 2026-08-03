dotnet publish .\src\Compilers\Server\VBCSCompiler\AnyCpu\VBCSCompiler.csproj -c Release -o .\artifacts\CompilerRelease /p:DebugType=portable /p:IncludeSymbols=true -f net9.0
dotnet publish .\src\Compilers\CSharp\csc\AnyCpu\csc.csproj -c Release -o .\artifacts\CompilerRelease /p:DebugType=portable /p:IncludeSymbols=true -f net9.0
dotnet publish .\src\Compilers\VisualBasic\vbc\AnyCpu\vbc.csproj -c Release -o .\artifacts\CompilerRelease /p:DebugType=portable /p:IncludeSymbols=true -f net9.0
