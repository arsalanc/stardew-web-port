# Third-party notices

This repository contains no part of Stardew Valley. Setup decompiles the player's own installed copy
into `local/` (git-ignored); that code and the game's assets remain copyright ConcernedApe LLC.

## KNI (MS-PL)

The browser platform is [KNI](https://github.com/kniEngine/kni) by Kastellanos Nikos and the
MonoGame Team, licensed under the Microsoft Public License (Ms-PL). KNI is used as NuGet packages;
the following files in `web/StardewWeb.Client` started from the KNI web project template
(`dotnet new kni-blazor-gl`) and remain under Ms-PL:

- `App.razor`, `MainLayout.razor`, `MainLayout.razor.css`, `_Imports.razor`, `Program.cs`
- `Pages/Index.razor`, `Pages/Index.razor.cs` (modified)
- `wwwroot/index.html` (modified), `wwwroot/css/app.css`, `wwwroot/favicon.ico`
- `wwwroot/js/micProcessor.js`, `wwwroot/js/streamProcessor2.js`

The complete license text follows.

```
Microsoft Public License (Ms-PL)

This license governs use of the accompanying software. If you use the software, you accept this license. If you do not accept the license, do not use the software.

1.  Definitions
The terms "reproduce," "reproduction," "derivative works," and "distribution" have the same meaning here as under U.S. copyright law. A "contribution" is the original software, or any additions or changes to the software. A "contributor" is any person that distributes its contribution under this license. "Licensed patents" are a contributor's patent claims that read directly on its contribution.

2.  Grant of Rights
     (A) Copyright Grant- Subject to the terms of this license, including the license conditions and limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free copyright license to reproduce its contribution, prepare derivative works of its contribution, and distribute its contribution or any derivative works that you create.

     (B) Patent Grant- Subject to the terms of this license, including the license conditions and limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free license under its licensed patents to make, have made, use, sell, offer for sale, import, and/or otherwise dispose of its contribution in the software or derivative works of the contribution in the software.

3.  Conditions and Limitations
     (A) No Trademark License- This license does not grant you rights to use any contributors' name, logo, or trademarks.

     (B) If you bring a patent claim against any contributor over patents that you claim are infringed by the software, your patent license from such contributor to the software ends automatically.

     (C) If you distribute any portion of the software, you must retain all copyright, patent, trademark, and attribution notices that are present in the software.

     (D) If you distribute any portion of the software in source code form, you may do so only under this license by including a complete copy of this license with your distribution. If you distribute any portion of the software in compiled or object code form, you may only do so under a license that complies with this license.

     (E) The software is licensed "as-is." You bear the risk of using it. The contributors give no express warranties, guarantees, or conditions. You may have additional consumer rights under your local laws which this license cannot change. To the extent permitted under your local laws, the contributors exclude the implied warranties of merchantability, fitness for a particular purpose and non-infringement.
```

## Build and setup tools (not redistributed)

- [ILSpy / ilspycmd](https://github.com/icsharpcode/ILSpy) (MIT) decompiles your game copy during setup.
- [Roslyn](https://github.com/dotnet/roslyn) (MIT) powers `tools/PortPatcher`.
- [.NET and ASP.NET Core](https://github.com/dotnet) (MIT).

## Runtime content

- The Wiki and Ask panels fetch short summaries from the
  [Stardew Valley Wiki](https://stardewvalleywiki.com) at runtime, licensed
  [CC BY-NC-SA 3.0](https://creativecommons.org/licenses/by-nc-sa/3.0/). They are shown with credit
  and a link, and are never stored in this repository.
- The optional assistant runs models through [Ollama](https://ollama.com); models are downloaded by
  the user under their own licenses.
