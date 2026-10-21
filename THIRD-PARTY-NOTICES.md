# Third-party components

Shapeshift itself is licensed under the MIT License (see `LICENSE`). It ships with these components:

| Component | License | Source |
|---|---|---|
| wimlib 1.14.4 (`libwim-15.dll`) by Eric Biggers | LGPL-3.0-or-later | https://wimlib.net |
| ManagedWimLib 2.6.0 | LGPL-3.0-or-later | https://github.com/ied206/ManagedWimLib |
| Joveler.DynLoader | MIT | https://github.com/ied206/Joveler.DynLoader |
| Microsoft.Dism (ManagedDism) 6.1.0 | MIT | https://github.com/jeffkl/ManagedDism |
| LTRData.DiscUtils 1.0.90 | MIT | https://github.com/LTRData/DiscUtils |
| System.CommandLine 2.0.13 | MIT | https://github.com/dotnet/command-line-api |
| CommunityToolkit.Mvvm 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| .NET runtime | MIT | https://github.com/dotnet/runtime |
| Windows App SDK 2.5.1 | Microsoft Software License Terms | https://github.com/microsoft/WindowsAppSDK |

## wimlib and ManagedWimLib (LGPL-3.0-or-later)

`libwim-15.dll` and `ManagedWimLib.dll` are separate files loaded at run time; you may replace them with
your own builds of the same interface. Their complete source code is available at the addresses above.
The LGPL-3.0 is a set of additional permissions on top of the GPL-3.0: see `licenses\GPL-3.0.txt` and
https://www.gnu.org/licenses/lgpl-3.0.txt.