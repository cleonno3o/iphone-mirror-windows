# Third-party components

The original C# / XAML launcher sources in `src/` were created for this prototype. Runtime components are separate processes and retain their original notices and licenses. No third-party GUI source was copied into the launcher.

## UxPlay

- Upstream: https://github.com/FDH2/UxPlay
- License: GNU GPL version 3 or later, as stated by the upstream source headers.
- License text: https://github.com/FDH2/UxPlay/blob/v1.72/LICENSE
- Windows package used: https://github.com/leapbtw/uxplay-windows/releases/tag/1.72.1-3
- Windows packaging/build sources: https://github.com/leapbtw/uxplay-windows/tree/1.72.1-3
- Runtime reports UxPlay 1.72. The downloaded package is pinned and SHA-256 verified.

## GStreamer and bundled libraries

The Windows UxPlay package contains GStreamer, FFmpeg, OpenSSL, libplist, MinGW runtime libraries and other native dependencies. Their respective licenses continue to apply.

- GStreamer: https://gstreamer.freedesktop.org/documentation/frequently-asked-questions/licensing.html
- FFmpeg: https://ffmpeg.org/legal.html
- Windows package dependency/build list: https://github.com/leapbtw/uxplay-windows/blob/1.72.1-3/dll-libs-list.txt
- Windows package notices: https://github.com/leapbtw/uxplay-windows/blob/1.72.1-3/LICENSE.md

This is a local development setup. A distributable product requires collecting the exact corresponding dependency sources and notices before packaging; this document alone is not that redistribution bundle.

## Extraction tool (development only)

- https://github.com/UserUnknownFactor/innoextract_win/releases/tag/643
- Original project: https://constexpr.org/innoextract/
- Original license: zlib/libpng. Additional binary dependencies retain their own licenses.
- Zip SHA-256: a61219f066cd240f7dba9043e894087397dfbda0539fb1ec2e59c9e66938d019

## Bonjour

An existing Apple Bonjour installation is used for service discovery. This project does not install or redistribute Bonjour.
