# Dependencies / references

## Harmony 2.4.2

`vendor/0Harmony.dll` is the unmodified net10.0 assembly from the official Lib.Harmony 2.4.2 NuGet package.

- Package: https://www.nuget.org/packages/Lib.Harmony/2.4.2
- Project: https://github.com/pardeike/Harmony
- License: MIT, copyright (c) 2017 Andreas Pardeike. Full text: `Harmony-LICENSE.txt` in the install package, `vendor/Harmony-LICENSE.txt` in source.
- Assembly SHA-256: `FD77B88724F4104440DF0CF979A851D35EEC75EA3A7E86297D04ABE47C71AFF6`

YMM4 assemblies are referenced from the user's installation for compilation and are NOT redistributed.


## Design research

The public PreviewEnhancer project was consulted to locate preview-related host types:
https://github.com/panko200/PreviewEnhancer

PreviewLite is an independently implemented, version-guarded preview scheduler. It uses the preview-only Edit entrance for throttling and Draw for observation. It does not copy PreviewEnhancer's implementation, globally patch Task.Delay, rewrite the preview loop's error recovery, inject effects, or modify rendering buffer sizes.
