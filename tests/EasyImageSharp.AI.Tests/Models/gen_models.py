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


def document_grid(name: str, flip: bool = False) -> None:
    inp = helper.make_tensor_value_info("image", TensorProto.FLOAT, [1, 3, "height", "width"])
    out = helper.make_tensor_value_info("grid", TensorProto.FLOAT, [1, 2, 2, 2])
    x = [[1, -1], [1, -1]] if flip else [[-1, 1], [-1, 1]]
    grid = [[x, [[-1, -1], [1, 1]]]]
    _save(name, helper.make_graph([_const("grid", grid)], name, [inp], [out]))


def first_channel_image() -> None:
    inp = helper.make_tensor_value_info("image", TensorProto.FLOAT, [1, 3, "height", "width"])
    out = helper.make_tensor_value_info("output", TensorProto.FLOAT, [1, 3, "height", "width"])
    nodes = [
        helper.make_node("Split", ["image"], ["first", "second", "third"], axis=1),
        _const("zero", [0.0]),
        helper.make_node("Mul", ["first", "zero"], ["zeros"]),
        helper.make_node("Concat", ["first", "zeros", "zeros"], ["output"], axis=1),
    ]
    _save("first_channel_image.onnx", helper.make_graph(nodes, "first_channel_image", [inp], [out]))



# ----- Batched-inference stand-ins (dynamic batch axis "batch") -----

def identity_batch(name: str, channels: int) -> None:
    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, ["batch", channels, "height", "width"])
    out = helper.make_tensor_value_info("output", TensorProto.FLOAT, ["batch", channels, "height", "width"])
    node = helper.make_node("Identity", ["input"], ["output"])
    _save(name, helper.make_graph([node], name.replace(".onnx", ""), [inp], [out]))


def upscale2x_nearest_batch() -> None:
    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, ["batch", 3, "height", "width"])
    out = helper.make_tensor_value_info("output", TensorProto.FLOAT, ["batch", 3, "height2", "width2"])
    nodes = [
        _const("roi", np.zeros(0, dtype=np.float32)),
        _const("scales", [1.0, 1.0, 2.0, 2.0]),
        helper.make_node(
            "Resize", ["input", "roi", "scales"], ["output"],
            mode="nearest", coordinate_transformation_mode="asymmetric", nearest_mode="floor"),
    ]
    _save("upscale2x_nearest_batch.onnx", helper.make_graph(nodes, "upscale2x_nearest_batch", [inp], [out]))


def classifier_quadrant_batch() -> None:
    """Like classifier_quadrant but [N,3,224,224] -> [N,4], so N pages can be classified in one pass."""
    inp = helper.make_tensor_value_info("x", TensorProto.FLOAT, ["batch", 3, 224, 224])
    out = helper.make_tensor_value_info("logits", TensorProto.FLOAT, ["batch", 4])
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
        nodes.append(helper.make_node("ReduceMean", [f"q{i}"], [f"m{i}"], axes=[1, 2, 3], keepdims=0))  # [N]
        nodes.append(_const(f"shape{i}", [-1, 1], np.int64))
        nodes.append(helper.make_node("Reshape", [f"m{i}", f"shape{i}"], [f"c{i}"]))  # [N,1]
        means.append(f"c{i}")
    nodes.append(helper.make_node("Concat", means, ["logits"], axis=1))  # [N,4]
    _save("classifier_quadrant_batch.onnx", helper.make_graph(nodes, "classifier_quadrant_batch", [inp], [out]))


# ----- Face-detector stand-in with the YuNet output contract -----

def face_grid_stub() -> None:
    """A YuNet-shaped graph whose stride-32 cells are "faces" scored by their mean brightness.

    input [1,3,H,W] (BGR 0-255, H and W multiples of 32) -> cls/obj/bbox/kps for strides 8, 16 and 32, each
    [1, (H/s)*(W/s), 1|4|10] in row-major anchor order. Strides 8 and 16 score 0 everywhere. For stride 32,
    cls = obj = mean(cell)/255 (so score = brightness), bbox = (0.5, 0.5, ln 2, ln 2): a 64x64 box centred on
    the cell, so two neighbouring bright cells overlap with IoU 1/3 and NMS keeps the brighter one. Landmarks
    sit at fixed fractions of the cell: right eye (0.3,0.4), left eye (0.7,0.4), nose (0.5,0.55), mouth
    corners (0.35,0.75) and (0.65,0.75).
    """
    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, 3, "height", "width"])
    nodes = [
        helper.make_node("ReduceMean", ["input"], ["gray"], axes=[1], keepdims=1),  # [1,1,H,W]
        _const("inv255", [1.0 / 255.0]),
        _const("zero", [0.0]),
        _const("flat", [1, -1, 1], np.int64),
        _const("box_const", [[[0.5, 0.5, float(np.log(2.0)), float(np.log(2.0))]]]),
        _const("kps_const", [[[0.3, 0.4, 0.7, 0.4, 0.5, 0.55, 0.35, 0.75, 0.65, 0.75]]]),
    ]
    outputs = []
    for stride in (8, 16, 32):
        nodes += [
            helper.make_node("AveragePool", ["gray"], [f"pool{stride}"], kernel_shape=[stride, stride], strides=[stride, stride]),
            helper.make_node("Reshape", [f"pool{stride}", "flat"], [f"cells{stride}"]),  # [1,N,1]
            helper.make_node("Mul", [f"cells{stride}", "zero"], [f"zeros{stride}"]),
        ]
        if stride == 32:
            nodes.append(helper.make_node("Mul", [f"cells{stride}", "inv255"], [f"cls_{stride}"]))
        else:
            nodes.append(helper.make_node("Identity", [f"zeros{stride}"], [f"cls_{stride}"]))
        nodes += [
            helper.make_node("Identity", [f"cls_{stride}"], [f"obj_{stride}"]),
            helper.make_node("Add", [f"zeros{stride}", "box_const"], [f"bbox_{stride}"]),  # [1,N,4]
            helper.make_node("Add", [f"zeros{stride}", "kps_const"], [f"kps_{stride}"]),  # [1,N,10]
        ]
    for kind, width in (("cls", 1), ("obj", 1), ("bbox", 4), ("kps", 10)):
        for stride in (8, 16, 32):
            outputs.append(helper.make_tensor_value_info(f"{kind}_{stride}", TensorProto.FLOAT, [1, f"anchors{stride}", width]))
    _save("face_grid_stub.onnx", helper.make_graph(nodes, "face_grid_stub", [inp], outputs))


def classifier_false_batch() -> None:
    """Declares a dynamic batch axis but hard-codes batch 1 inside: x [N,3,224,224] -> logits [1,4] = [0, mean, 0, 0].

    Feeding N > 1 fails in Reshape, which is how real exports with a cosmetic dynamic batch axis behave; the batch
    runner must fall back to one sample at a time and still return the right scores.
    """
    inp = helper.make_tensor_value_info("x", TensorProto.FLOAT, ["batch", 3, 224, 224])
    out = helper.make_tensor_value_info("logits", TensorProto.FLOAT, [1, 4])
    nodes = [
        helper.make_node("ReduceMean", ["x"], ["mean"], axes=[1, 2, 3], keepdims=0),  # [N]
        _const("shape11", [1, 1], np.int64),
        helper.make_node("Reshape", ["mean", "shape11"], ["mean11"]),  # [1,1]; fails for N > 1
        _const("zero11", [[0.0]]),
        helper.make_node("Concat", ["zero11", "mean11", "zero11", "zero11"], ["logits"], axis=1),
    ]
    _save("classifier_false_batch.onnx", helper.make_graph(nodes, "classifier_false_batch", [inp], [out]))


if __name__ == "__main__":
    identity("identity_rgb.onnx", 3)
    identity("identity_gray.onnx", 1)
    classifier_fixed()
    classifier_quadrant()
    upscale2x_nearest()
    gray_affine("residual_zero_gray.onnx", 0.0, 0.0)
    gray_affine("constant_half_gray.onnx", 0.0, 0.5)
    saliency_brightness()
    document_grid("document_identity_grid.onnx")
    document_grid("document_flip_grid.onnx", flip=True)
    first_channel_image()
    identity_batch("identity_rgb_batch.onnx", 3)
    identity_batch("identity_gray_batch.onnx", 1)
    upscale2x_nearest_batch()
    classifier_quadrant_batch()
    face_grid_stub()
    classifier_false_batch()


# ----- Vision-model contracts: embeddings, content moderation, learned quality, inpainting -----
#
# embedding_channel_means.onnx    pixel_values [N,3,224,224] -> image_embeds [N,4] = (mean R, mean G, mean B, 1)
#                                 of the normalised input (CLIP image-tower contract)
# moderation_brightness.onnx      pixel_values [N,3,384,384] -> logits [N,2] = (m, -m), m = mean of the input;
#                                 metadata labels "NSFW,SFW" (bright images score as NSFW)
# moderation_three_labels.onnx    pixel_values [N,3,384,384] -> logits [N,3] = (0, 2, 0); labels " neutral, nsfw ,drawing"
# moderation_no_unsafe.onnx       pixel_values [N,3,384,384] -> logits [N,2]; labels "cat,dog" (no NSFW class: rejected)
# quality_fixed.onnx              pixel_values [N,3,224,224] -> technical [N,10] = 0.5 at ratings 5 and 6,
#                                 aesthetic [N,10] = 0.1 everywhere (NIMA contract)
# quality_one_head.onnx           pixel_values [N,3,224,224] -> technical [N,10] only (rejected)
# inpaint_fill_gray.onnx          image [N,3,512,512], mask [N,1,512,512] -> output [N,3,512,512] =
#                                 255 * (mask * 0.5 + (1 - mask) * image) (LaMa contract: 0-255, composited)
# inpaint_fill_gray_dynamic.onnx  the same with dynamic H/W


def _save_with_metadata(name: str, graph: onnx.GraphProto, metadata: dict[str, str]) -> None:
    model = helper.make_model(graph, producer_name="EasyImageSharp.AI.Tests", opset_imports=[helper.make_opsetid("", OPSET)])
    model.ir_version = IR_VERSION
    helper.set_model_props(model, metadata)
    onnx.checker.check_model(model)
    path = os.path.join(OUT_DIR, name)
    onnx.save(model, path)
    print(f"{name}: {os.path.getsize(path)} bytes")


def embedding_channel_means() -> None:
    inp = helper.make_tensor_value_info("pixel_values", TensorProto.FLOAT, ["batch", 3, 224, 224])
    out = helper.make_tensor_value_info("image_embeds", TensorProto.FLOAT, ["batch", 4])
    nodes = [
        helper.make_node("ReduceMean", ["pixel_values"], ["means"], axes=[2, 3], keepdims=0),  # [N,3]
        helper.make_node("ReduceMean", ["means"], ["any"], axes=[1], keepdims=1),  # [N,1]
        _const("zero", [0.0]),
        _const("one", [1.0]),
        helper.make_node("Mul", ["any", "zero"], ["zeros"]),
        helper.make_node("Add", ["zeros", "one"], ["ones"]),  # [N,1] of 1
        helper.make_node("Concat", ["means", "ones"], ["image_embeds"], axis=1),
    ]
    _save("embedding_channel_means.onnx", helper.make_graph(nodes, "embedding_channel_means", [inp], [out]))


def _moderation_graph(name: str, classes: int, nodes: list) -> onnx.GraphProto:
    inp = helper.make_tensor_value_info("pixel_values", TensorProto.FLOAT, ["batch", 3, 384, 384])
    out = helper.make_tensor_value_info("logits", TensorProto.FLOAT, ["batch", classes])
    return helper.make_graph(nodes, name, [inp], [out])


def _fixed_scores(source: str, output: str, values) -> list:
    """Nodes producing [N, len(values)] = values for every batch item of `source` ([N,C,H,W])."""
    return [
        helper.make_node("ReduceMean", [source], ["m"], axes=[1, 2, 3], keepdims=1),  # [N,1,1,1]
        _const("shape_n1", [-1, 1], np.int64),
        helper.make_node("Reshape", ["m", "shape_n1"], ["m2"]),  # [N,1]
        _const("zero", [0.0]),
        helper.make_node("Mul", ["m2", "zero"], ["zeros"]),
        _const(output + "_values", [values]),
        helper.make_node("Add", ["zeros", output + "_values"], [output]),
    ]


def moderation_models() -> None:
    brightness = [
        helper.make_node("ReduceMean", ["pixel_values"], ["m"], axes=[1, 2, 3], keepdims=1),  # [N,1,1,1]
        _const("shape_n1", [-1, 1], np.int64),
        helper.make_node("Reshape", ["m", "shape_n1"], ["m2"]),  # [N,1]
        helper.make_node("Neg", ["m2"], ["neg"]),
        helper.make_node("Concat", ["m2", "neg"], ["logits"], axis=1),
    ]
    _save_with_metadata("moderation_brightness.onnx", _moderation_graph("moderation_brightness", 2, brightness), {"labels": "NSFW,SFW"})
    _save_with_metadata(
        "moderation_three_labels.onnx",
        _moderation_graph("moderation_three_labels", 3, _fixed_scores("pixel_values", "logits", [0.0, 2.0, 0.0])),
        {"labels": " neutral, nsfw ,drawing"})
    _save_with_metadata(
        "moderation_no_unsafe.onnx",
        _moderation_graph("moderation_no_unsafe", 2, _fixed_scores("pixel_values", "logits", [1.0, 0.0])),
        {"labels": "cat,dog"})


def quality_models() -> None:
    inp = helper.make_tensor_value_info("pixel_values", TensorProto.FLOAT, ["batch", 3, 224, 224])
    technical = helper.make_tensor_value_info("technical", TensorProto.FLOAT, ["batch", 10])
    aesthetic = helper.make_tensor_value_info("aesthetic", TensorProto.FLOAT, ["batch", 10])
    head = _fixed_scores("pixel_values", "technical", [0, 0, 0, 0, 0.5, 0.5, 0, 0, 0, 0])
    both = head + [
        _const("aesthetic_values", [[0.1] * 10]),
        helper.make_node("Add", ["zeros", "aesthetic_values"], ["aesthetic"]),
    ]
    _save("quality_fixed.onnx", helper.make_graph(both, "quality_fixed", [inp], [technical, aesthetic]))
    _save("quality_one_head.onnx", helper.make_graph(head, "quality_one_head", [inp], [technical]))


def inpaint_fill_gray(name: str, size) -> None:
    height, width = (size, size) if size else ("height", "width")
    image = helper.make_tensor_value_info("image", TensorProto.FLOAT, ["batch", 3, height, width])
    mask = helper.make_tensor_value_info("mask", TensorProto.FLOAT, ["batch", 1, height, width])
    out = helper.make_tensor_value_info("output", TensorProto.FLOAT, ["batch", 3, height, width])
    nodes = [
        _const("one", [1.0]),
        _const("half", [0.5]),
        _const("scale", [255.0]),
        helper.make_node("Sub", ["one", "mask"], ["keep"]),
        helper.make_node("Mul", ["image", "keep"], ["kept"]),  # broadcast over channels
        helper.make_node("Mul", ["mask", "half"], ["fill"]),
        helper.make_node("Add", ["kept", "fill"], ["composite"]),
        helper.make_node("Mul", ["composite", "scale"], ["output"]),
    ]
    _save(name, helper.make_graph(nodes, name.replace(".onnx", ""), [image, mask], [out]))


if __name__ == "__main__":
    embedding_channel_means()
    moderation_models()
    quality_models()
    inpaint_fill_gray("inpaint_fill_gray.onnx", 512)
    inpaint_fill_gray("inpaint_fill_gray_dynamic.onnx", None)
