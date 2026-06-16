# Third-Party Notices

Ryn redistributes the components listed below **in compiled form** inside the
native binaries shipped by the `Ryn.Interop` package (the `saucer-bindings`
shared library and everything statically linked into it). Those binaries are
produced by building the `saucer-bindings` C++ project (a Git submodule under
`vendor/saucer-bindings`), which in turn fetches and statically links the
third-party C++ libraries enumerated here.

The native library is a single linked artifact: the source code of these
components is **not** shipped, but their object code is. The licenses below are
MIT, BSD, the Boost Software License, and Creative Commons Attribution 4.0 — all
of which require that their copyright and permission notices be preserved when
the software is redistributed, including in binary form. This file exists to
satisfy that requirement.

The license texts reproduced below were copied verbatim from the `LICENSE`
files present in each component's source tree as fetched by the build (under
`build/native/_deps/<component>-src/`). Where a copyright line is reproduced it
is quoted exactly as it appears upstream.

Ryn's own source code is licensed separately under the MIT License (see the
`LICENSE` file at the repository root); this file covers only the bundled
third-party components.

---

## saucer

- Project: https://github.com/saucer/saucer
- Component: the saucer cross-platform webview library and its `saucer-bindings`
  C ABI wrapper, plus the saucer support libraries `saucer/fill` and
  `saucer/embed`.
- License: MIT

```
MIT License

Copyright (c) 2023 Saucer

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The saucer support libraries carry their own copyright years under the same
MIT terms:

- `saucer/fill` — Copyright (c) 2025 Saucer
- `saucer/embed` — Copyright (c) 2025 Saucer
- `saucer/desktop` — Copyright (c) 2024 Saucer
- `saucer/loop` — Copyright (c) 2025 Saucer

---

## glaze

- Project: https://github.com/stephenberry/glaze
- Component: JSON serialization library (saucer's default serializer; linked
  into the shipped binary).
- License: MIT

```
MIT License

Copyright (c) 2019 - present, Stephen Berry

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## lockpp

- Project: https://github.com/Curve/lockpp
- Component: C++ wrapper for synchronized access to shared state (linked into
  saucer).
- License: MIT

```
MIT License

Copyright (c) 2024 Curve

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## flagpp

- Project: https://github.com/Curve/flagpp
- Component: C++ bit-flag enum helpers (linked into saucer).
- License: MIT

```
MIT License

Copyright (c) 2023 Curve

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## rebind

- Project: https://github.com/Curve/rebind
- Component: compile-time reflection / type-rebinding utilities (linked into
  saucer).
- License: MIT

```
MIT License

Copyright (c) 2024 Curve

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## ereignis

- Project: https://github.com/Curve/ereignis
- Component: C++ event/signal-slot library (linked into saucer).
- License: MIT

```
MIT License

Copyright (c) 2023 Curve

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## coco

- Project: https://github.com/Curve/coco
- Component: C++20 coroutine helper library (linked into saucer).
- License: MIT

```
MIT License

Copyright (c) 2025 Curve

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## polo

- Project: https://github.com/Curve/polo
- Component: small C++23 value-semantic container for polymorphic types (linked
  into saucer).
- License: MIT

```
MIT License

Copyright (c) 2024 Curve

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## nontype_functional

- Project: https://github.com/zhihaoy/nontype_functional
- Component: reference implementation of `std::function_ref` / `std::move_only_function`
  (fetched as `functional`; used by `saucer/fill` to provide the `std` namespace
  fill on platforms lacking these types).
- License: BSD 2-Clause

```
BSD 2-Clause License

Copyright (c) 2022, Zhihao Yuan
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

---

## jthread

- Project: https://github.com/saucer/jthread (a vendored copy of the
  `std::jthread` / `stop_token` reference implementation by Nicolai Josuttis and
  Lewis Baker, used as a polyfill on macOS until libc++ ships an official
  implementation). Fetched by `saucer/fill`.
- License: Creative Commons Attribution 4.0 International (CC-BY-4.0)

The component's `LICENSE` file contains the full Creative Commons Attribution
4.0 International legalcode, which opens verbatim as follows (the complete text
is preserved in the component source tree at
`build/native/_deps/jthread-src/LICENSE`, and is also available canonically at
https://creativecommons.org/licenses/by/4.0/legalcode):

```
Attribution 4.0 International

=======================================================================

Creative Commons Corporation ("Creative Commons") is not a law firm and
does not provide legal services or legal advice. ...

[Full Creative Commons Attribution 4.0 International Public License text
 follows in the component's LICENSE file.]
```

The component's own README states the applicable terms and authorship:

```
The code is licensed under a Creative Commons Attribution 4.0 International
License (http://creativecommons.org/licenses/by/4.0/).

Reference implementation of std::jthread / stop_token.
Main authors: Nicolai Josuttis and Lewis Baker.
```

> Note: CC-BY-4.0 requires attribution to the original authors when the work is
> redistributed, including in binary form. The attribution above is provided to
> satisfy that condition; the verbatim full license text is preserved in the
> component source tree.

---

## range-v3

- Project: https://github.com/ericniebler/range-v3 (version 0.12.0)
- Component: Eric Niebler's Range library (a transitive dependency pulled into
  the native build alongside glaze). The `range-v3` `LICENSE.txt` is the Boost
  Software License 1.0, and additionally reproduces the bundled-portion notices
  for libc++, the SGI STL, and Stepanov & McJones' "Elements of Programming"
  (range-v3 incorporates small pieces of each).
- License: Boost Software License 1.0 (with the additional bundled notices noted
  above)

```
Boost Software License - Version 1.0 - August 17th, 2003

Permission is hereby granted, free of charge, to any person or organization
obtaining a copy of the software and accompanying documentation covered by
this license (the "Software") to use, reproduce, display, distribute,
execute, and transmit the Software, and to prepare derivative works of the
Software, and to permit third-parties to whom the Software is furnished to
do so, all subject to the following:

The copyright notices in the Software and this entire statement, including
the above license grant, this restriction and the following disclaimer,
must be included in all copies of the Software, in whole or in part, and
all derivative works of the Software, unless such copies or derivative
works are solely in the form of machine-executable object code generated by
a source language processor.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE, TITLE AND NON-INFRINGEMENT. IN NO EVENT
SHALL THE COPYRIGHT HOLDERS OR ANYONE DISTRIBUTING THE SOFTWARE BE LIABLE
FOR ANY DAMAGES OR OTHER LIABILITY, WHETHER IN CONTRACT, TORT OR OTHERWISE,
ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

The range-v3 `LICENSE.txt` additionally bundles the following notices (libc++
dual NCSA/MIT, the "Elements of Programming" license, and the SGI STL license).
The complete text is preserved in the component source tree at
`build/native/_deps/range-v3-src/LICENSE.txt`:

```
==============================================================================
libc++ License
==============================================================================

The libc++ library is dual licensed under both the University of Illinois
"BSD-Like" license and the MIT license. As a user of this code you may choose
to use it under either license.

Copyright (c) 2009-2014 by the contributors listed in CREDITS.TXT
  http://llvm.org/svn/llvm-project/libcxx/trunk/CREDITS.TXT

==============================================================================
Stepanov and McJones, "Elements of Programming" license
==============================================================================

// Copyright (c) 2009 Alexander Stepanov and Paul McJones
//
// Permission to use, copy, modify, distribute and sell this software
// and its documentation for any purpose is hereby granted without
// fee, provided that the above copyright notice appear in all copies
// and that both that copyright notice and this permission notice
// appear in supporting documentation.

==============================================================================
SGI C++ Standard Template Library license
==============================================================================

// Copyright (c) 1994 Hewlett-Packard Company
// Copyright (c) 1996 Silicon Graphics Computer Systems, Inc.
//
// Permission to use, copy, modify, distribute and sell this software
// and its documentation for any purpose is hereby granted without fee,
// provided that the above copyright notice appear in all copies and
// that both that copyright notice and this permission notice appear
// in supporting documentation.
```

---

## Platform webview runtimes (not redistributed by Ryn)

The native binary links against the operating system's webview at runtime; Ryn
does **not** redistribute these — they are provided by the host OS — but they are
listed here for completeness:

- **macOS:** Apple `WebKit`, `Cocoa`, and `CoreImage` system frameworks.
- **Linux:** WebKitGTK 6.0, GTK 4, libadwaita, and json-glib system libraries
  (resolved via `pkg-config`; LGPL/system-provided, dynamically linked).
- **Windows:** Microsoft Edge WebView2. On Windows the build statically links
  `WebView2LoaderStatic.lib` from the `Microsoft.Web.WebView2` NuGet package; the
  WebView2 SDK is distributed by Microsoft under its own license terms. Ryn does
  not currently ship a Windows native binary — see the verification note below.

---

## Components requiring verification

These items are present in the native dependency graph but were not confirmed to
be statically linked into every shipped binary, or are pulled in only for a
platform Ryn does not yet ship. They are recorded here so a maintainer can
confirm or prune them before the package is finalized:

- **PackageProject.cmake** (https://github.com/TheLartians/PackageProject.cmake,
  MIT, Copyright (c) 2020 Lars Melchior). This is a CMake build-time packaging
  helper only; it contributes no object code to the shipped binary and so likely
  does **not** require a binary-redistribution notice. Listed for completeness;
  remove if confirmed build-only.
- **range-v3 — exact bundling.** range-v3 0.12.0 appears in the fetched
  dependency tree (its license is reproduced above). VERIFY whether any range-v3
  object code actually survives into the linked `saucer-bindings` binary, since
  it is a header-only template library and may be fully inlined/elided. The Boost
  license requires the notice regardless of whether machine code remains, so it
  has been kept.
- **WebView2 SDK (Microsoft).** Only relevant once a Windows native binary is
  shipped. If/when Windows packaging lands, confirm the exact license text that
  ships with the `Microsoft.Web.WebView2` NuGet version in use and add a section
  for it (the WebView2 SDK has its own redistribution terms).
- **reflect-cpp** (https://github.com/getml/reflect-cpp). Present in the
  saucer dependency graph as an *alternative* serializer. It is **not** linked in
  the default Glaze configuration Ryn builds, so it is intentionally omitted. If
  the serializer backend ever changes to `Rflpp`, add reflect-cpp (MIT, plus its
  own bundled third-party notices) here.

---

*This notice file covers the third-party components bundled in the `Ryn.Interop`
native binaries as of saucer-bindings v8.0.4 / saucer 8.0.4. Re-verify the list
against `vendor/saucer-bindings` and the build's `_deps` tree whenever the native
submodule or the saucer version is bumped.*
