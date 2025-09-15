<p align="center">
<img width="450" src="https://user-images.githubusercontent.com/46729679/109719841-17b7dd00-7b5e-11eb-8f5e-87eb2d4d1be9.png" alt="Roslyn logo">
</p>

<h1 align="center">The .NET Compiler Platform -- iWare edition</h1>

This repository originates from the [NET-SDK-9.0.301 tag](https://github.com/dotnet/roslyn/tree/NET-SDK-9.0.301) of roslyn. The only thing kept in this repository is the work done after cloning the specified tag.

The project entrypoint is `src/Compilers/CSharp/sql-csc.sln`. Although you need to first follow the instructions at `docs/contributing/Building, Debugging, and Testing on Windows.md` before you can start work. The results of building `sql-csc.sln` should show up in `/.dotnet/sdk/9.0.304-sql/Roslyn/bincore`. Also if you built the project from the console using the `global.json` provided you should just be able to copy `/.dotnet/sdk/9.0.304-sql` to `C:/Program Files/dotnet/sdk` and use the compiler in any project where it's in global.json such as it is in the [example project](http://iwareis01:7990/projects/IWCS/repos/example/browse), taking into account that you must also have the [iWare.Database](http://iwareis01:7990/projects/IWCS/repos/iware.database/browse) library included in your project and you're builing from your console, not Visual Studio as it won't recognize this compiler version.

### Contributing

All work on the C# and Visual Basic compiler happens directly on [GitHub](https://github.com/dotnet/roslyn). Both core team members and external contributors send pull requests which go through the same review process.

If you are interested in fixing issues and contributing directly to the code base, a great way to get started is to ask some questions on [GitHub Discussions](https://github.com/dotnet/roslyn/discussions)! Then check out our [contributing guide](https://github.com/dotnet/roslyn/blob/main/CONTRIBUTING.md) which covers the following:

- [Coding guidelines](https://github.com/dotnet/roslyn/blob/main/docs/wiki/Contributing-Code.md)
- [The development workflow, including debugging and running tests](https://github.com/dotnet/roslyn/blob/main/docs/contributing/Building%2C%20Debugging%2C%20and%20Testing%20on%20Windows.md)
- [Submitting pull requests](<https://github.com/dotnet/roslyn/blob/main/CONTRIBUTING.md#How-to-submit-a-PR>)
- Finding a bug to fix in the [IDE](https://aka.ms/roslyn-ide-bugs-help-wanted) or [Compiler](https://aka.ms/roslyn-compiler-bugs-help-wanted)
- Finding a feature to implement in the [IDE](https://aka.ms/roslyn-ide-feature-help-wanted) or [Compiler](https://aka.ms/roslyn-compiler-feature-help-wanted)
- Roslyn API suggestions should go through the [API review process](<docs/contributing/API Review Process.md>)

### Community

The Roslyn community can be found on [GitHub Discussions](https://github.com/dotnet/roslyn/discussions), where you can ask questions, voice ideas, and share your projects.

To chat with other community members, you can join the Roslyn channel on the [CSharp Community Discord](https://discord.com/invite/tGJvv88).

Our [Code of Conduct](CODE-OF-CONDUCT.md) applies to all Roslyn community channels and has adopted the [.NET Foundation Code of Conduct](https://dotnetfoundation.org/code-of-conduct).

### Documentation

Visit [Roslyn Architecture Overview](https://docs.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model) to get started with Roslyn’s API’s.

### NuGet Feeds

**The latest pre-release builds** are available from the following public NuGet feeds: 
- [Compiler](https://dev.azure.com/dnceng/public/_packaging?_a=feed&feed=dotnet-tools): `https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json`
- [IDE Services](https://dev.azure.com/azure-public/vside/_packaging?_a=feed&feed=vssdk): `https://pkgs.dev.azure.com/azure-public/vside/_packaging/vssdk/nuget/v3/index.json`
- [.NET SDK](https://dev.azure.com/dnceng/public/_packaging?_a=feed&feed=dotnet5): `https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet5/nuget/v3/index.json`

[//]: # (Begin current test results)

This project is part of the [.NET Foundation](http://www.dotnetfoundation.org/projects) along with other projects like [the .NET Runtime](https://github.com/dotnet/runtime/).
