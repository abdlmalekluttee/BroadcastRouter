# P2-40: historical FFmpeg source and build recipe

This reconstructs the **source** used by the existing Windows DeckLink build.
It is not a new production release, a driver fix, or a hardware qualification.
The historical native crash explanation in patch comments is a hypothesis to
reproduce with symbols on the approved bench, not a newly established root cause.

## Inputs and limits

- FFmpeg 8.1.2 release archive: URL and SHA-256 in `inputs.lock.json`.
- `win-safe-terminate.patch`: the exact four-file local change, including the
  retained final device reference, callback ordering, and opt-in exit workaround.
- Desktop Video SDK **16.0 Windows include directory**, obtained independently
  from Blackmagic. Every supplied IDL/header digest is in `sdk-inputs.lock.json`.
  The SDK itself and generated proprietary headers are **not** included.
- Microsoft Windows SDK MIDL x64 file version
  `10.0.19041.685 (WinBuild.160101.0800)`, plus a configured x64 Visual Studio
  developer shell (the Windows SDK's imported system IDLs must be available).
- MSYS2 **MINGW64**, GCC 16.1.0 Rev5 and the installed package versions in
  `toolchain.lock.json`. Do not substitute the UCRT64 toolchain.
- Python 3.11+ and Git for the offline source verifier.

The package-version lock was captured from the available build workstation. The
historical manifest did **not** retain every package archive hash, compiler/SDK
input, or normalized path/timestamp. Consequently, matching the historical EXE
SHA-256 is **not yet proven**. The source can be recreated exactly; the binary
reproducibility claim remains open until a clean rebuild comparison is recorded.
Do not describe version checks alone as cryptographic toolchain reproducibility.

This full recipe intentionally preserves the original broad feature set,
including GPU libraries. P2-41 creates a separate lean profile; nothing here
enables GPU use in the router.

## License and redistribution

FFmpeg's DeckLink configuration uses `--enable-nonfree` together with GPL code.
The resulting binary is **not redistributable** under FFmpeg's configuration
rules. Build it locally; do not attach it, SDK files, or dependency DLLs to a
public release. The FFmpeg source and this patch retain the upstream source's
license notices. The small MIDL shim is project-authored glue, not an SDK copy.

## 1. Verify inputs and prepare a new source directory

Use a new, empty working location. The preparation command refuses an existing
destination, modified archive, unsafe tar member, or patch that cannot apply.
It never installs packages, downloads files, or accesses DeckLink hardware.

```powershell
python tools/ffmpeg-build/recipe.py `
  --archive 'C:\build-inputs\ffmpeg-8.1.2.tar.xz' `
  --destination 'C:\ffmpeg-build\prepared'
```

The result is `C:\ffmpeg-build\prepared\ffmpeg-8.1.2` plus a source-provenance
record. Keep the original archive/signature, source record, and patch digest.
Verify the release signature using FFmpeg's independently trusted release key
when acquiring a new archive; do not trust a key merely because it accompanies
the download. The pinned SHA-256 still has to match.

Compare `pacman -Q` with `toolchain.lock.json` before compiling. The helper
`recipe.verify_toolchain(expected, actual)` rejects missing or different versions;
unrelated additional packages are permitted. This recipe deliberately does not
run `pacman -Syu` or install/replace anything automatically. Preserve package
archives and their signatures privately for a truly repeatable toolchain.

Check each SDK include against `sdk-inputs.lock.json` using `Get-FileHash`.
Reject any mismatch. Confirm MIDL's file version using
`(Get-Item (Get-Command midl.exe).Source).VersionInfo.FileVersion`.

## 2. Generate Windows COM headers

In the configured **x64 developer PowerShell**, set the independently acquired
SDK Windows include path. The output directory must be a fresh local build
directory, never a production installation.

```powershell
$SdkInclude = 'C:\build-inputs\DeckLink-SDK-16.0\Win\include'
$GeneratedInclude = 'C:\ffmpeg-build\decklink-include'
New-Item -ItemType Directory -Path $GeneratedInclude -ErrorAction Stop | Out-Null
Push-Location -LiteralPath $GeneratedInclude
try {
  & midl.exe /env win64 /h DeckLinkAPI.h /W1 /char signed /D NDEBUG /robust /nologo `
    /I $SdkInclude (Join-Path $SdkInclude 'DeckLinkAPI.idl')
  if ($LASTEXITCODE -ne 0) { throw 'MIDL failed' }
  Copy-Item -LiteralPath (Join-Path $SdkInclude 'DeckLinkAPIVersion.h') -Destination .
} finally { Pop-Location }
Copy-Item -LiteralPath 'tools\ffmpeg-build\DeckLinkAPI_v14_2_1.shim.h' `
  -Destination (Join-Path $GeneratedInclude 'DeckLinkAPI_v14_2_1.h')
```

The top-level SDK16 IDL expands the v14.2.1 compatibility declarations; the shim
only includes that generated header. Do not substitute a downloaded SDK header
from an unrelated revision.

## 3. Configure and compile the historical full profile

Run these commands in an MSYS2 MINGW64 shell. Paths below are deliberately
generic. Keep them constant across comparison builds: paths are embedded in the
binary's build configuration. Configuration/compilation must never use the
production application or production FFmpeg directory as an output prefix.

```bash
set -Eeuo pipefail
export PATH=/mingw64/bin:/usr/bin
export PKG_CONFIG_PATH=/mingw64/lib/pkgconfig:/mingw64/share/pkgconfig
SOURCE=/c/ffmpeg-build/prepared/ffmpeg-8.1.2
BUILD=/c/ffmpeg-build/objects
INCLUDE=/c/ffmpeg-build/decklink-include
PREFIX=/c/ffmpeg-build/output
test ! -e "$BUILD" && test ! -e "$PREFIX"
mkdir "$BUILD" "$PREFIX"
cd "$BUILD"
"$SOURCE/configure" \
  --prefix="$PREFIX" --arch=x86_64 --target-os=mingw32 --pkg-config=pkg-config \
  --extra-cflags="-O3 -I$INCLUDE" --extra-cxxflags="-O3 -I$INCLUDE" \
  --extra-libs="-lpthread -lm" \
  --enable-gpl --enable-version3 --enable-static --disable-shared \
  --enable-ffmpeg --enable-ffprobe --enable-ffplay \
  --enable-amf --enable-ffnvcodec --enable-nvenc --enable-nvdec --enable-libvpl \
  --enable-opencl --enable-vulkan --enable-openssl --enable-libsrt --enable-librist \
  --enable-libaom --enable-libdav1d --enable-libsvtav1 --enable-libx264 \
  --enable-libx265 --enable-libvpx --enable-libopus --enable-libvorbis \
  --enable-libmp3lame --enable-libtheora --enable-libwebp --enable-libopenjpeg \
  --enable-libjxl --enable-libass --enable-libfreetype --enable-libharfbuzz \
  --enable-libfontconfig --enable-libzimg --enable-libsoxr --enable-libspeex \
  --enable-libtwolame --enable-libssh --enable-libzmq --enable-libplacebo \
  --enable-libvmaf --enable-libcodec2 --enable-nonfree --enable-decklink
make -j2
make install
```

Two build jobs are a conservative local default, not a runtime tuning change.
No command above runs DeckLink discovery or opens an input/output connector.

## 4. Collect dependent DLLs and a private build record

The FFmpeg libraries are static, but external libraries can require runtime
DLLs. Starting with each of `ffmpeg.exe`, `ffprobe.exe`, and `ffplay.exe`, inspect
`objdump -p <binary>` and its `DLL Name` entries. Copy each **matching MINGW64**
DLL from `/mingw64/bin` beside the executable, inspect that DLL in turn, and repeat
until no new dependency remains. Do not copy Windows system DLLs or obtain DLLs
from an unrelated machine/download. A missing non-system dependency fails the
build qualification; do not silently omit it.

Privately record hashes of every packaged EXE/DLL, source/patch/SDK input hashes,
the exact configure argv, `gcc --version`, `midl` file version, `pacman -Q`, and:

```powershell
& 'C:\ffmpeg-build\output\bin\ffmpeg.exe' -version
& 'C:\ffmpeg-build\output\bin\ffmpeg.exe' -buildconf
& 'C:\ffmpeg-build\output\bin\ffprobe.exe' -version
& 'C:\ffmpeg-build\output\bin\ffmpeg.exe' -hide_banner -h muxer=decklink
```

Check that the muxer help exposes `win_safe_terminate`, default false. These
commands inspect registered capabilities only; **do not** add `-sources`,
`-sinks`, a DeckLink capture, or a DeckLink output outside an approved session.

Do not publish `config.log`, environment dumps, paths from the build workstation,
private diagnostics, or media captures. FFmpeg configure logs may contain the
complete environment. A source-recipe PR is not authorization to distribute the
nonfree build.

## Workaround semantics and remaining proof

With the option off, ordinary teardown remains enabled. With it on, the patched
dedicated output process attempts scheduled-playback/audio/video cleanup and
callback removal, deliberately retains its final device reference, then calls
Windows `TerminateProcess` on **itself** with exit code0, avoiding DLL detach.
That exit code is **not** proof that SDI audio is silent or hardware was released.
It can also hide a later teardown failure; retain independent physical evidence.

Open P2-40/P2-41 gates: symbolized native crash reproduction, clean binary/hash
comparison, patched/unpatched matrix, 500-cycle physical tests, Session0,
reboot and approved long soak. This source/recipe work closes none of those rows.

## Offline recipe regression tests

```powershell
python -m unittest discover -s tools/ffmpeg-build -p test_recipe.py -v
```

Tests exercise real tar extraction and `git apply`, altered inputs, traversal,
links, existing-output preservation, patch failure and version mismatch. No
hardware or proprietary SDK is needed for them.
