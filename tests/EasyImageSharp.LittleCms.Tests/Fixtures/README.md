# ICC reference fixtures

Unmodified synthetic profiles from Little CMS 2.19.1, upstream commit `21c582a594fe5279f90c0b93437c398f93bf62b0`, `testbed/`. Accompanying upstream MIT license is in `LICENSE.txt`.

These profiles label themselves **not suitable for real use**. They are interoperability fixtures, not profiles for production printing or displays.

| File | Coverage | SHA-256 |
| --- | --- | --- |
| test1.icc | CMYK output profile, LUT pipeline | 1a996ebe6b5d1e7a21620993ce1903df16e4e8fa6cca312bedfb5ae133e62b4d |
| test3.icc | RGB color-space/Lab LUT profile | cfc64b172c5f4f054d39e4eceeda4b9ed8e598ecb022e51936d501d84c34c381 |
| test5.icc | RGB display matrix/TRC profile | 33d771add8e26ad129a5c448ed41da3f8cd08d7e527a3f637ee13220af0afdc7 |

Golden measurements are generated with upstream `transicc` compiled separately by `scripts/build-littlecms.ps1 -BuildReferenceTool`. RGB command-line values are 0-255; adapter RGB floats divide those values by 255. CMYK values are percentages in both interfaces. Tests allow small float/interpolation differences rather than requiring double-precision CLI text to match float processing bit-for-bit.

Source: https://github.com/mm2/Little-CMS/tree/lcms2.19.1/testbed
