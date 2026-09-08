# Third-Party Notices

This product is an unofficial community desktop wrapper. It is not affiliated with or endorsed by DeepSeek AI.

## DeepSeek Harness and whale icon

DeepSeek Harness, including the source whale favicon used to derive this application's icon, is obtained from:

https://github.com/deepseek-ai/deepseek-harness

MIT License

Copyright (c) 2026 DeepSeek

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of
the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

The application icon adds a neutral rounded-square background and a blue status dot to the upstream black whale
favicon so it remains legible on both light and dark Windows surfaces.

## Microsoft WebView2 SDK

Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
following conditions are met:

- Redistributions of source code must retain the above copyright notice, this list of conditions and the following
  disclaimer.
- Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the
  following disclaimer in the documentation and/or other materials provided with the distribution.
- The name of Microsoft Corporation, or the names of its contributors may not be used to endorse or promote
  products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The release build copies the complete WebView2 `NOTICE.txt` into the `licenses` directory.

## Node.js and package dependencies

The full portable package bundles an unmodified official Node.js Windows binary distribution. Its `LICENSE` file
and dependency notices remain in `runtime/node`. The online package uses Node.js Corepack to obtain the pinned pnpm
version. DeepSeek Harness npm packages retain their package metadata and license files under
`runtime/dsh/node_modules`.

The fs-ext 2.1.1 package is redistributed under its MIT license with an unmodified
Windows x64 binding compiled for the bundled Node.js 24.14.1 (ABI 137).
Its install scripts are disabled to avoid requiring a compiler on user machines.
The redistributed package retains the upstream JavaScript, C++ source, license,
and a PREBUILD.json record containing the binary SHA-256 and build target.
