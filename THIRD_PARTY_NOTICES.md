# Dependencies and native runtimes

This workspace references the sibling `dmr` and `iq-channelizer` source trees. Their licenses and notices remain in those repositories. Review the DMR `THIRD_PARTY_NOTICES.md` and IqChannelizer `LICENSE` plus `docs/runtime-packages.md` for the libraries' dependencies and native-runtime distribution terms.

- [Avalonia](https://github.com/AvaloniaUI/Avalonia): MIT. Packages include their own dependency notices.
- [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore): MIT; SQLite is supplied through SQLitePCLRaw's native bundle.
- [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw): Apache-2.0. [SQLite](https://sqlite.org/copyright.html) is public domain.
- [UHD](https://github.com/EttusResearch/uhd): installed separately; the app does not bundle UHD or device images.
- [libusb](https://github.com/libusb/libusb): LGPL-2.1-or-later. An optional externally supplied `libusb-1.0.dll` is copied beside the executable when `BadSdrLibUsbPath` is configured, or when the existing sibling Windows SDK location is found. Preserve its license and source availability obligations if distributing that DLL.
- [FFTW](https://www.fftw.org/): the sibling channelizer checkout supplies its FFTW runtime. FFTW's GPL/commercial licensing applies to redistribution; alternative oneMKL deployment is described by the channelizer repository.

The repository contains application source and package references. Native binaries copied into build output remain governed by their upstream licenses; this file does not relicense them.
