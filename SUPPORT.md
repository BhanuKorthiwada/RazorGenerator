# Support Policy

This fork focuses on classic ASP.NET MVC Razor generation for modern developer
machines.

## Supported

- .NET Framework 4.7.2 and later
- ASP.NET MVC 5 / Razor 3
- Visual Studio 2022 and Visual Studio 2026
- Windows builds and classic ASP.NET/IIS applications
- Visual Studio `Custom Tool = RazorGenerator` workflows
- MSBuild-based Razor generation for repeatable CI builds
- Repositories that commit generated Razor C# files

## Primary Validation Targets

- .NET Framework 4.8
- Visual Studio 2026
- ASP.NET MVC 5.2.x / Razor 3

## Out Of Scope

- ASP.NET Core, Razor SDK, Razor Class Libraries, and Blazor
- Visual Studio 2019 and older
- ASP.NET MVC 3 / Razor 1
- ASP.NET MVC 4 / Razor 2
- Cross-platform runtime support

For ASP.NET Core applications, use the built-in Razor SDK and Razor Class
Libraries instead of this project.

## Generated Files

Generated `.cs` files are expected to be committed next to their `.cshtml`
inputs. The default build validates that RazorGenerator inputs still have
matching generated outputs.
