# Microsoft Visual C++ runtime

This application includes release Visual C++ runtime DLLs from the licensed
Visual Studio installation's `VC/Redist/MSVC/<version>/x64/Microsoft.VC*.CRT`
directory. These unmodified Microsoft components support native dependencies
such as wgpu-native. Copyright Microsoft Corporation. All rights reserved.

Redistribution is subject to the Microsoft Software License Terms for the
Visual Studio installation used to prepare the runtime pack. Build maintainers
must source these files from the redistributable directory, never from debug
tool directories or an end user's Windows installation.

Microsoft's redistribution documentation:
https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
