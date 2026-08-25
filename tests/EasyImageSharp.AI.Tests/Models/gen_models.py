"""Generates the tiny, deterministic ONNX graphs used by EasyImageSharp.AI.Tests.

Run from anywhere:  python tests/EasyImageSharp.AI.Tests/Models/gen_models.py
Requires the `onnx` package (no runtime needed). Every file is a few hundred bytes and
byte-identical on rerun (opset 13, IR version 8, fixed producer name, no weights beyond scalars).

Models
------
identity_rgb.onnx        [1,3,H,W]  -> [1,3,H,W]   Identity (dynamic H/W)
identity_gray.onnx       [1,1,H,W]  -> [1,1,H,W]   Identity (dynamic H/W)
classifier_fixed.onnx    x [1,3,224,224] -> logits [1,4] = [0.1, 2.0, 0.5, -1.0] (input-independent)
classifier_quadrant.onnx x [1,3,224,224] -> logits [1,4] = mean of the TL, TR, BR, BL quadrants
                         (a page whose bright corner sits top-left when upright is classified 0/1/2/3
                          after 0/90/180/270 degrees clockwise rotation, like the real doc-ori model)
upscale2x_nearest.onnx   [1,3,H,W]  -> [1,3,2H,2W] Resize nearest/asymmetric/floor = exact pixel replication
residual_zero_gray.onnx  [1,1,H,W]  -> [1,1,H,W]   all zeros (a residual denoiser that predicts no noise)
constant_half_gray.onnx  [1,1,H,W]  -> [1,1,H,W]   all 0.5 (a threshold map at mid gray)
saliency_brightness.onnx [1,3,320,320] -> [1,1,320,320] mean over channels (brightness as saliency)
"""
from __future__ import annotations

import os

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

OUT_DIR = os.path.dirname(os.path.abspath(__file__))
OPSET = 13
IR_VERSION = 8


def _save(name: str, graph: onnx.GraphProto) -> None:
    model = helper.make_model(graph, producer_name="EasyImageSharp.AI.Tests", opset_imports=[helper.make_opsetid("", OPSET)])
    model.ir_version = IR_VERSION
    onnx.checker.check_model(model)
    path = os.path.join(OUT_DIR, name)
    onnx.save(model, path)
    print(f"{name}: {os.path.getsize(path)} bytes")


def _const(name: str, values, dtype=np.float32) -> onnx.NodeProto:
    arr = np.asarray(values, dtype=dtype)
    return helper.make_node("Constant", [], [name], value=numpy_helper.from_array(arr, name + "_value"))


def identity(name: str, channels: int) -> None:
    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, channels, "height", "width"])
    out = helper.make_tensor_value_info("output", TensorProto.FLOAT, [1, channels, "height", "width"])
    node = helper.make_node("Identity", ["input"], ["output"])
    _save(name, helper.make_graph([node], name.replace(".onnx", ""), [inp], [out]))


def classifier_fixed() -> None:
    inp = helper.make_tensor_value_info("x", TensorProto.FLOAT, [1, 3, 224, 224])
    out = helper.make_tensor_value_info("logits", TensorProto.FLOAT, [1, 4])
    nodes = [
        helper.make_node("ReduceMean", ["x"], ["mean4d"], axes=[1, 2, 3], keepdims=1),  # [1,1,1,1]
        _const("shape11", [1, 1], np.int64),
        helper.make_node("Reshape", ["mean4d", "shape11"], ["mean2d"]),  # [1,1]
        _const("zero", [0.0]),
        helper.make_node("Mul", ["mean2d", "zero"], ["zeros"]),  # [1,1]
        _const("fixed", [[0.1, 2.0, 0.5, -1.0]]),
        helper.make_node("Add", ["zeros", "fixed"], ["logits"]),  # broadcast -> [1,4]
    ]
    _save("classifier_fixed.onnx", helper.make_graph(nodes, "classifier_fixed", [inp], [out]))


def classifier_quadrant() -> None:
    inp = helper.make_tensor_value_info("x", TensorProto.FLOAT, [1, 3, 224, 224])
    out = helper.make_tensor_value_info("logits", TensorProto.FLOAT, [1, 4])
    nodes = [
        _const("axes_hw", [2, 3], np.int64),
        _const("s0", [0, 0], np.int64),
        _const("s1", [0, 112], np.int64),
        _const("s2", [112, 112], np.int64),
        _const("s3", [112, 0], np.int64),
        _const("e0", [112, 112], np.int64),
        _const("e1", [112, 224], np.int64),
        _const("e2", [224, 224], np.int64),
        _const("e3", [224, 112], np.int64),
    ]
    means = []
    for i in range(4):
        nodes.append(helper.make_node("Slice", ["x", f"s{i}", f"e{i}", "axes_hw"], [f"q{i}"]))
        nodes.append(helper.make_node("ReduceMean", [f"q{i}"], [f"m{i}"], axes=[1, 2, 3], keepdims=0))  # [1]
        means.append(f"m{i}")
    nodes.append(helper.make_node("Concat", means, ["cat"], axis=0))  # [4]
    nodes.append(_const("shape14", [1, 4], np.int64))
    nodes.append(helper.make_node("Reshape", ["cat", "shape14"], ["logits"]))
    _save("classifier_quadrant.onnx", helper.make_graph(nodes, "classifier_quadrant", [inp], [out]))


def upscale2x_nearest() -> None:
    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, 3, "height", "width"])
    out = helper.make_tensor_value_info("output", TensorProto.FLOAT, [1, 3, "height2", "width2"])
    nodes = [
        _const("roi", np.zeros(0, dtype=np.float32)),
        _const("scales", [1.0, 1.0, 2.0, 2.0]),
        helper.make_node(
            "Resize", ["input", "roi", "scales"], ["output"],
            mode="nearest", coordinate_transformation_mode="asymmetric", nearest_mode="floor"),
    ]
    _save("upscale2x_nearest.onnx", helper.make_graph(nodes, "upscale2x_nearest", [inp], [out]))


def gray_affine(name: str, scale: float, offset: float) -> None:
    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, 1, "height", "width"])
    out = helper.make_tensor_value_info("output", TensorProto.FLOAT, [1, 1, "height", "width"])
    nodes = [
        _const("scale", [scale]),
        helper.make_node("Mul", ["input", "scale"], ["scaled"]),
        _const("offset", [offset]),
        helper.make_node("Add", ["scaled", "offset"], ["output"]),
    ]
    _save(name, helper.make_graph(nodes, name.replace(".onnx", ""), [inp], [out]))


def saliency_brightness() -> None:
    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, 3, 320, 320])
    out = helper.make_tensor_value_info("output", TensorProto.FLOAT, [1, 1, 320, 320])
    node = helper.make_node("ReduceMean", ["input"], ["output"], axes=[1], keepdims=1)
    _save("saliency_brightness.onnx", helper.make_graph([node], "saliency_brightness", [inp], [out]))


if __name__ == "__main__":
    identity("identity_rgb.onnx", 3)
    identity("identity_gray.onnx", 1)
    classifier_fixed()
    classifier_quadrant()
    upscale2x_nearest()
    gray_affine("residual_zero_gray.onnx", 0.0, 0.0)
    gray_affine("constant_half_gray.onnx", 0.0, 0.5)
    saliency_brightness()
