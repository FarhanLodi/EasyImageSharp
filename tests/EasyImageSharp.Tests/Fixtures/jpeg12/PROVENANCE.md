# 12-bit JPEG fixtures: provenance

Every file here is written by `generate.py` in this folder (Python 3.11+, `numpy>=2`, `imagecodecs>=2024.1`).
None of it comes from EasyImageSharp.

| File | Encoder | Ground truth |
| --- | --- | --- |
| `monkey12.jpg` | libjpeg-turbo's `cjpeg`, as shipped in its test suite | libjpeg-turbo 3.1.3 decode (via imagecodecs 2026.3.6), sampled rows only (`truthRows` in `manifest.json`) |
| `gray12_q90.jpg`, `ycc12_*.jpg`, `rgb12_q95.jpg` | libjpeg-turbo 3.1.3 (`imagecodecs.jpeg8_encode(bitspersample=12)`) from fixed-seed synthetic patterns | libjpeg-turbo 3.1.3 decode |
| `*_progressive.jpg`, `*_restart.jpg` | the coefficient transcoder in `generate.py`: the quantized coefficients of `gray12_q90.jpg` and `ycc12_420_q85.jpg`, re-entropy-coded as SOF2 (interleaved DC scan, AC bands 1-5 and 6-63 per component) or as SOF1 with a restart marker every 3 MCUs | libjpeg-turbo 3.1.3 decode, which the generator requires to equal its decode of the source file exactly |

`<name>.u16.gz` holds the ground truth: gzip-compressed little-endian `uint16` samples, row-major, channels
interleaved, 12-bit values. libjpeg-turbo decodes with its accurate integer IDCT and this library with a
floating-point one, so the tests compare within a stated tolerance.

## monkey12.jpg

- Source: <https://raw.githubusercontent.com/libjpeg-turbo/libjpeg-turbo/0103036f949f30c4f794e2032a03eb183b007092/testimages/monkey12.jpg>
  (libjpeg-turbo `testimages/`, commit `0103036f949f30c4f794e2032a03eb183b007092`)
- SHA-256: `a3cb218412cecfa877a4adee520aa3d58ac0d6d0f36a7a2541e598324ce481e3`
- Licence: Modified (3-clause) BSD License. libjpeg-turbo's `testimages/LICENSE.txt`
  (SHA-256 `1c4d5dfc985d5061ba11180a8ffb541cc1778bccf87fd9f0fb92cbae69d8a9cc` at that commit) states that test
  images other than `testorig*`/`testimg*` and the two Little CMS profiles "are provided under the terms of the
  Modified (3-clause) BSD License", and that the embedded `sRGB2014.icc` profile "may be copied, distributed,
  embedded, made, used, and sold without restriction" (International Color Consortium, 2015).

The licence, as given in libjpeg-turbo's `LICENSE.md`:

```
Copyright (C) 2009-2026 D. R. Commander
Copyright (C) 2018-2023 Randy <randy408@protonmail.com>

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

- Redistributions of source code must retain the above copyright notice,
  this list of conditions and the following disclaimer.
- Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.
- Neither the name of the libjpeg-turbo Project nor the names of its
  contributors may be used to endorse or promote products derived from this
  software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS",
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT HOLDERS OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

## Tools

- imagecodecs 2026.3.6 (BSD-3-Clause), bundling libjpeg-turbo 3.1.3 (IJG, BSD-3-Clause and zlib licences).
- The encoder binding cannot write 12-bit progressive files or restart intervals, hence the transcoder. It never
  changes a coefficient (libjpeg-turbo decoding the result to the same pixels as the source proves that), so these
  files test the entropy decoding paths against libjpeg-turbo's own reading of the same data. Successive
  approximation (Ah/Al refinement scans) is not among them.
