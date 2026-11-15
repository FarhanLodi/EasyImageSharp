"""Exports the idealo NIMA MobileNet quality models (technical + aesthetic, Apache-2.0) to one ONNX graph.

Usage (Python 3.11, tensorflow 2.15, tf2onnx 1.17, onnx, onnxruntime, h5py, numpy < 2):

    python -I export_nima.py <dir with weights_mobilenet_technical_0.11.hdf5 + weights_mobilenet_aesthetic_0.07.hdf5>
                             <output.onnx> [--reference out.json]

Both weight files were trained with Keras 2.1.6 (their HDF5 `keras_version` attribute says so), whose MobileNet
pads every 3x3 convolution with ZeroPadding2D((1, 1)) followed by a 'valid' convolution. Current Keras uses
asymmetric 'same'-style padding for the strided layers instead, which shifts the sampling grid by one pixel, so
the 2.1.6 layer stack is rebuilt here verbatim (same layer names and order) before the weights are loaded.

The two networks share one input and run side by side; the graph is converted with tf2onnx (opset 17) with the
input transposed to NCHW. Verification compares ONNX Runtime with Keras, and both with an independent NumPy
(float64) implementation that reads the HDF5 weights directly, on seeded random tensors and on the deterministic
scene from reference_images.py; it fails when any probability differs by more than 1e-4.

Contract of the exported file:
    input  pixel_values float32 [N,3,224,224] RGB, image stretched to 224x224 with nearest-neighbour sampling
           (the evaluation pipeline of the upstream predict.py: keras load_img(target_size=(224, 224))), then
           x / 127.5 - 1  (== (x/255 - 0.5) / 0.5)
    output technical    float32 [N,10]  softmax over quality ratings 1..10 (TID2013-trained)
    output aesthetic    float32 [N,10]  softmax over aesthetic ratings 1..10 (AVA-trained)
    The NIMA score is the mean of the distribution: sum_i i * p_i for i = 1..10.
"""
from __future__ import annotations

import hashlib
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import h5py  # noqa: E402
import numpy as np  # noqa: E402
import onnx  # noqa: E402
import onnxruntime as ort  # noqa: E402
import tensorflow as tf  # noqa: E402
import tf2onnx  # noqa: E402
from PIL import Image  # noqa: E402

from reference_images import scene  # noqa: E402

SIZE = 224
TECHNICAL = "weights_mobilenet_technical_0.11.hdf5"
AESTHETIC = "weights_mobilenet_aesthetic_0.07.hdf5"
# (pointwise filters, stride) of the 13 depthwise-separable blocks of MobileNet v1, alpha 1.0.
BLOCKS = [(64, 1), (128, 2), (128, 1), (256, 2), (256, 1), (512, 2), (512, 1), (512, 1), (512, 1), (512, 1),
          (512, 1), (1024, 2), (1024, 1)]


def keras216_nima(name: str) -> tf.keras.Model:
    """MobileNet v1 (alpha 1, Keras 2.1.6 layout) + GlobalAveragePooling + Dropout + Dense(10, softmax)."""
    layers = tf.keras.layers
    inputs = layers.Input((SIZE, SIZE, 3), name="input_1")
    x = layers.ZeroPadding2D((1, 1), name="conv1_pad")(inputs)
    x = layers.Conv2D(32, 3, strides=2, padding="valid", use_bias=False, name="conv1")(x)
    x = layers.BatchNormalization(axis=-1, epsilon=1e-3, name="conv1_bn")(x)
    x = layers.ReLU(6.0, name="conv1_relu")(x)
    for block, (filters, stride) in enumerate(BLOCKS, start=1):
        x = layers.ZeroPadding2D((1, 1), name=f"conv_pad_{block}")(x)
        x = layers.DepthwiseConv2D(3, strides=stride, padding="valid", use_bias=False, name=f"conv_dw_{block}")(x)
        x = layers.BatchNormalization(axis=-1, epsilon=1e-3, name=f"conv_dw_{block}_bn")(x)
        x = layers.ReLU(6.0, name=f"conv_dw_{block}_relu")(x)
        x = layers.Conv2D(filters, 1, padding="same", use_bias=False, name=f"conv_pw_{block}")(x)
        x = layers.BatchNormalization(axis=-1, epsilon=1e-3, name=f"conv_pw_{block}_bn")(x)
        x = layers.ReLU(6.0, name=f"conv_pw_{block}_relu")(x)
    x = layers.GlobalAveragePooling2D(name="global_average_pooling2d_1")(x)
    x = layers.Dropout(0.75, name="dropout_1")(x)
    x = layers.Dense(10, activation="softmax", name="dense_1")(x)
    return tf.keras.Model(inputs, x, name=name)


# ----- Independent NumPy reference (reads the HDF5 weights directly) -----

def read_weights(path: str) -> dict[str, list[np.ndarray]]:
    weights = {}
    with h5py.File(path, "r") as fh:
        for layer in fh.attrs["layer_names"]:
            layer = layer.decode() if isinstance(layer, bytes) else layer
            group = fh[layer]
            names = [n.decode() if isinstance(n, bytes) else n for n in group.attrs["weight_names"]]
            if names:
                weights[layer] = [np.asarray(group[n], dtype=np.float64) for n in names]
    return weights


def numpy_nima(weights: dict[str, list[np.ndarray]], x: np.ndarray) -> np.ndarray:
    """x: [N,H,W,3] float64 already normalised; returns [N,10] probabilities."""
    def bn(t, name):
        gamma, beta, mean, var = weights[name]
        return (t - mean) / np.sqrt(var + 1e-3) * gamma + beta

    def relu6(t):
        return np.clip(t, 0.0, 6.0)

    def pad(t):
        return np.pad(t, ((0, 0), (1, 1), (1, 1), (0, 0)))

    def windows(t, stride):
        # The nine 3x3 taps of a 'valid' convolution over an already padded tensor.
        n, h, w, c = t.shape
        oh, ow = (h - 3) // stride + 1, (w - 3) // stride + 1
        return [(ky, kx, t[:, ky:ky + stride * (oh - 1) + 1:stride, kx:kx + stride * (ow - 1) + 1:stride, :])
                for ky in range(3) for kx in range(3)]

    kernel = weights["conv1"][0]  # [3,3,3,32]
    x = sum(win @ kernel[ky, kx] for ky, kx, win in windows(pad(x), 2))
    x = relu6(bn(x, "conv1_bn"))
    for block, (_, stride) in enumerate(BLOCKS, start=1):
        depthwise = weights[f"conv_dw_{block}"][0]  # [3,3,C,1]
        x = sum(win * depthwise[ky, kx, :, 0] for ky, kx, win in windows(pad(x), stride))
        x = relu6(bn(x, f"conv_dw_{block}_bn"))
        x = x @ weights[f"conv_pw_{block}"][0][0, 0]  # [C,F]
        x = relu6(bn(x, f"conv_pw_{block}_bn"))
    x = x.mean(axis=(1, 2))
    kernel, bias = weights["dense_1"]
    logits = x @ kernel + bias
    e = np.exp(logits - logits.max(-1, keepdims=True))
    return e / e.sum(-1, keepdims=True)


def preprocess(image: Image.Image) -> np.ndarray:
    """The upstream evaluation transform: stretch to 224x224 with PIL nearest, then x / 127.5 - 1 (NHWC)."""
    image = image.convert("RGB").resize((SIZE, SIZE), Image.NEAREST)
    return (np.asarray(image, dtype=np.float32) / 127.5 - 1.0)[None]


def mean_score(probabilities: np.ndarray) -> float:
    return float((probabilities * np.arange(1, 11)).sum() / probabilities.sum())


def main() -> None:
    args = sys.argv[1:]
    reference_path = None
    if "--reference" in args:
        index = args.index("--reference")
        reference_path = args[index + 1]
        del args[index:index + 2]
    source_dir, output = args[0], args[1]

    technical = keras216_nima("technical_net")
    technical.load_weights(os.path.join(source_dir, TECHNICAL))
    aesthetic = keras216_nima("aesthetic_net")
    aesthetic.load_weights(os.path.join(source_dir, AESTHETIC))

    pixels = tf.keras.layers.Input((SIZE, SIZE, 3), name="pixel_values")
    outputs = [
        tf.keras.layers.Activation("linear", name="technical")(technical(pixels)),
        tf.keras.layers.Activation("linear", name="aesthetic")(aesthetic(pixels)),
    ]
    combined = tf.keras.Model(pixels, outputs, name="nima_mobilenet")

    spec = (tf.TensorSpec((None, SIZE, SIZE, 3), tf.float32, name="pixel_values"),)
    model_proto, _ = tf2onnx.convert.from_keras(combined, input_signature=spec, opset=17, inputs_as_nchw=["pixel_values"])
    onnx.checker.check_model(model_proto)
    output_names = [o.name for o in model_proto.graph.output]
    assert output_names == ["technical", "aesthetic"], output_names
    model_proto.producer_name = "EasyImageSharp ModelExport (tf2onnx)"
    model_proto.doc_string = ("idealo/image-quality-assessment NIMA MobileNet technical (TID2013) + aesthetic (AVA), "
                              "Apache-2.0. Outputs: softmax over ratings 1..10.")
    del model_proto.metadata_props[:]
    onnx.helper.set_model_props(model_proto, {"ratings": "1,2,3,4,5,6,7,8,9,10"})
    onnx.save(model_proto, output)

    session = ort.InferenceSession(output, providers=["CPUExecutionProvider"])
    reference = {name: read_weights(os.path.join(source_dir, file)) for name, file in (("technical", TECHNICAL), ("aesthetic", AESTHETIC))}
    rng = np.random.default_rng(1234)
    scene_nhwc = preprocess(scene())
    batches = [rng.uniform(-1, 1, (2, SIZE, SIZE, 3)).astype(np.float32), scene_nhwc]
    worst_keras = worst_numpy = 0.0
    for batch in batches:
        keras_out = combined.predict(batch, verbose=0)
        onnx_out = session.run(["technical", "aesthetic"], {"pixel_values": batch.transpose(0, 3, 1, 2).copy()})
        for i, name in enumerate(("technical", "aesthetic")):
            expected = numpy_nima(reference[name], batch.astype(np.float64))
            worst_keras = max(worst_keras, float(np.abs(keras_out[i] - onnx_out[i]).max()))
            worst_numpy = max(worst_numpy, float(np.abs(expected - onnx_out[i]).max()))

    print(f"max |keras - onnxruntime| = {worst_keras:.2e}; max |numpy reference - onnxruntime| = {worst_numpy:.2e}")
    if worst_keras > 1e-4 or worst_numpy > 1e-4:
        raise SystemExit("The ONNX export does not reproduce the reference networks.")

    scene_probabilities = {name: numpy_nima(reference[name], scene_nhwc.astype(np.float64))[0] for name in reference}
    for name, probabilities in scene_probabilities.items():
        print(f"scene_320x240 {name}: mean score {mean_score(probabilities):.4f}")
    if reference_path:
        with open(reference_path, "w", encoding="utf-8") as fh:
            json.dump({f"scene_320x240_{name}": [round(float(v), 6) for v in p] for name, p in scene_probabilities.items()}, fh)
        print("wrote", reference_path)

    with open(output, "rb") as fh:
        digest = hashlib.sha256(fh.read()).hexdigest().upper()
    print(f"{os.path.basename(output)} {os.path.getsize(output)} bytes SHA-256 {digest}")


if __name__ == "__main__":
    main()
