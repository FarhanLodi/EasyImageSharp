# geometry fixtures

Reference outputs written by Pillow for the resize kernels and the affine/perspective warps.
`source.rgba` is the 96x64 synthetic RGB input; every other `.rgba` is Pillow's result for the
operation described in `manifest.json` (see the module docstring of `gen_geometry.py` for the
coefficient conventions). Tests compare by PSNR, except the nearest-neighbour entries which must
match exactly (affine) or on at least 99.5% of pixels (perspective, where float/double division
can flip a pixel that lands within 1e-6 of a boundary).
