# vp8enc fixtures

Source images for the VP8 lossy encoder tests. These are inputs, not encoded fixtures: the
tests encode them with the library's VP8 encoder and decode the result with the library's
bit-exact VP8 decoder, which is itself verified against libwebp elsewhere in the suite.

| file | size | content |
| --- | --- | --- |
| photo.png | 224x160 | photographic stand-in: five octaves of smooth value noise per
channel plus a soft disc, a dark band and a vignette |
| sharp.png | 112x80 | hard-edged synthetic geometry: checkerboard, thin diagonals and a
solid rectangle, which stresses the intra mode decision |

Regenerate with `python tests/EasyImageSharp.Tests/Fixtures/generate.py`.
