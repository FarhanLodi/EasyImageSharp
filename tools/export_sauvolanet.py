#!/usr/bin/env python3
"""Exports SauvolaNet to ONNX as the per-pixel threshold map that BinarizeAI expects.

SauvolaNet is a Keras project, so this needs a TensorFlow toolchain the other exports do not. onnx 1.21
and TensorFlow 2.15 disagree over ml_dtypes, so run it in an environment of its own:

    python -m venv sauvola-env
    sauvola-env/Scripts/pip install "tensorflow-cpu==2.15.1" "tf2onnx==1.16.1" "onnx==1.16.2" \
        "protobuf<5" numpy==1.26.4

Then put these next to the script, in a working directory of your choice:

    SauvolaDocBin/    the package from github.com/Leedeng/SauvolaNet (MIT)
    weights.h5        pretrained_models/Sauvola_v3_att_w7.15.23.31.39.47.55.63_k1_R1_a1_inorm_*.h5

Two things make this awkward, and both are handled below.

The checkpoint cannot be opened with ``load_model``: it embeds marshalled Python bytecode for its Lambda
layers, which will not unmarshal on a newer interpreter. The architecture is therefore rebuilt from the
configuration recorded inside the checkpoint itself, and only the weight arrays are loaded — those are
interpreter independent.

The published network also ends in ``DifferenceThresh``, which emits a signed hinge score rather than a
threshold. The library wants the threshold map, so the graph is cut at the attention layer that produces
it, then wrapped in permutes because Keras is NHWC while the library feeds NCHW.
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "SauvolaDocBin"))
os.environ["TF_CPP_MIN_LOG_LEVEL"] = "3"

import numpy as np
import tensorflow as tf
from tensorflow.keras.layers import Input, Permute
from tensorflow.keras.models import Model
from modelUtils import create_multiscale_sauvola

# Configuration read out of the checkpoint's own embedded model_config, not guessed:
#   conv0..conv5 with 8,16,24,32,40,48 filters  ->  base_filters = 8
#   windows [7,15,23,31,39,47,55,63], k and R trainable, instance normalisation.
core = create_multiscale_sauvola(
    window_size_list=[7, 15, 23, 31, 39, 47, 55, 63],
    train_k=True, train_R=True, train_alpha=True,
    norm_type="inorm", base_filters=8)
core.load_weights("weights.h5")
print("weights loaded into the rebuilt graph")

core_threshold = Model(core.input, core.get_layer("attention").output, name="sauvolanet_threshold")

nchw_in = Input(shape=(1, None, None), name="input")
y = Permute((2, 3, 1))(nchw_in)
y = core_threshold(y)
y = Permute((3, 1, 2))(y)
wrapped = Model(nchw_in, y, name="sauvolanet")
print("input :", wrapped.input.shape)
print("output:", wrapped.output.shape)

# Behavioural check, expressed the way the library applies the map: a pixel is white where
# luminance >= threshold and black otherwise. The network raises the threshold over ink and lowers it
# over paper, so the absolute values matter less than the classification they produce.
page = np.full((1, 1, 64, 96), 0.85, dtype="float32")
page[:, :, 20:40, 30:60] = 0.15
th = wrapped.predict(page, verbose=0)
binary = page >= th
ink_black = not binary[0, 0, 30, 45]
paper_white = binary[0, 0, 5, 5]
inside = binary[0, 0, 20:40, 30:60]
outside = np.concatenate([binary[0, 0, :20, :].ravel(), binary[0, 0, 40:, :].ravel()])
print(f"probe: threshold over ink {float(th[0, 0, 30, 45]):.3f}, over paper {float(th[0, 0, 5, 5]):.3f}")
print(f"       ink region {100 * (1 - inside.mean()):.1f}% black, "
      f"paper {100 * outside.mean():.1f}% white")
if not (ink_black and paper_white):
    raise SystemExit("FAIL: applying the threshold map does not separate ink from paper")

import tf2onnx

spec = (tf.TensorSpec((1, 1, None, None), tf.float32, name="input"),)
tf2onnx.convert.from_keras(wrapped, input_signature=spec, opset=17, output_path="sauvolanet.onnx")
print("wrote sauvolanet.onnx", os.path.getsize("sauvolanet.onnx"), "bytes")
