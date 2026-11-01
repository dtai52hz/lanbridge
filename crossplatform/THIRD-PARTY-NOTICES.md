# Desktop preview: third-party components

LanBridge's own source is MIT. The desktop preview includes the following separate components and their notices in `licenses`:

- Avalonia 12.1.4, Avalonia.Desktop, Fluent, Skia integration and platform backends: MIT; copyright Avalonia contributors. Source revision `2d79ba55fae1b1e71c69f93a668080c9029e6a31`, <https://github.com/AvaloniaUI/Avalonia>. See `AVALONIA-LICENSE.txt`.
- SharpHook 8.0.1: MIT; copyright Anatoliy Pylypchuk. Source revision `3bb6ecb03d71af7b9d888a50a277c830a9de77c0`, <https://github.com/TolikPylypchuk/SharpHook>. See `SHARPHOOK-LICENSE.txt`.
- The separate native **libuiohook** libraries used by SharpHook: LGPL-3.0-or-later, with the GPL text also supplied. Corresponding source revision `3890543a1aa09f20966b1784650487093daad97e` is included as `libuiohook-source-3890543.tar.gz`; upstream <https://github.com/TolikPylypchuk/libuiohook>. LanBridge does not modify these native libraries. Native binaries are not embedded in a single executable and may be replaced with compatible rebuilt versions. Follow that source archive's CMake build instructions and SharpHook's build workflow for platform dependencies.
- SkiaSharp 3.119.4 and HarfBuzzSharp 8.3.1.3: MIT wrappers plus notices for their native components, supplied in `SKIASHARP-LICENSE.txt`, `HARFBUZZSHARP-LICENSE.txt` and the platform `*-NOTICES.txt` files. Source <https://github.com/mono/SkiaSharp>.
- MicroCom.Runtime 0.11.6: MIT, copyright Nikita Tsukanov; <https://github.com/kekekeks/MicroCom>, revision `76785efcafd91b5902fd19dd11145f6dd655b7b4`. See `MICROCOM-LICENSE.txt`.
- Self-contained packages include Microsoft .NET 10 runtime components under their applicable licenses and notices. See `DOTNET-LICENSE.txt` and `DOTNET-THIRD-PARTY-NOTICES.txt`, <https://github.com/dotnet/runtime>.

Linux X11 uses the host's Xlib, XInput2, XTEST and xkbcommon shared libraries; these operating-system packages are not bundled. NAudio, Inno Setup and FFmpeg are not included in this desktop preview.
