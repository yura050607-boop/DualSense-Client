# VIIPER native dependency

This public repository contains customized application source. It does not ship a prebuilt `libVIIPER.dll`, `libVIIPER.so`, or application installer. The original GPL-3.0 license and upstream attribution are retained.

## Why the binary is separate

During preparation on 2026-11-17, the previously used Windows DLL was compared with the official `DualSenseClient/VIIPER` v1.0.0 release. They differ. A shared `1.0.0` version label does not establish which source produced the local DLL. The unverified local binary is omitted from this public publication; it has not been replaced in the original installed application.

## Upstream reference

- [VIIPER upstream project](https://github.com/DualSenseClient/VIIPER)
- [Official v1.0.0 release and assets](https://github.com/DualSenseClient/VIIPER/releases/tag/v1.0.0)
- The upstream v1.0.0 tag resolved during this check to commit `5904692ec54e7c34e051eb39c3ddfe3737a8397c`.
- [Source at that commit](https://github.com/DualSenseClient/VIIPER/tree/5904692ec54e7c34e051eb39c3ddfe3737a8397c)
- [GPL-3.0 LICENSE.txt at that commit](https://github.com/DualSenseClient/VIIPER/blob/5904692ec54e7c34e051eb39c3ddfe3737a8397c/LICENSE.txt)

The checked official `libVIIPER-windows-amd64.zip` has SHA-256 `35f31f9bee198fd56f17e35fd55ca685a07f0ca4d0a6b0b8b3101392e28087de`. It includes the DLL, C header, export definition, and `licenses.txt`. This identifies the official archive checked; it does not establish that the official binary is interchangeable with the previously used local binary.

## Preparing a build

1. Obtain the native backend from the upstream project, either a verified release or a source build using its documented build recipe.
2. Preserve its GPL license, dependency-license notices, matching source references, and build instructions alongside any binary distribution. The existing fetch script extracts only the native library and discards other archive files, so retain those notices separately.
3. Place a compatible Windows library at `source/DualSenseClient.VIIPER/native/win-x64/libVIIPER.dll`, or a compatible Linux library at `source/DualSenseClient.VIIPER/native/linux-x64/libVIIPER.so`.
4. Check the exported C API against this snapshot's managed bindings before building and testing. Compatibility of an independently acquired native backend with these customized bindings has not been verified here.

For convenience, the existing `scripts/fetch_native_libraries.py` retrieves the upstream release configured in that script. Running it does not by itself prove API compatibility, reproduce the former local binary, or complete license-notice packaging.

No fresh application/native build or gameplay test was performed for this source publication. Audio, microphone quality, and intermittent per-game input issues are not claimed to be universally fixed.
