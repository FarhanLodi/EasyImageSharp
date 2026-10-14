"""Convex quadrilateral IoU and one-to-one detection matching (no ignored regions)."""
import math


def polygon(value):
    if not isinstance(value, list) or len(value) != 4:
        raise ValueError("each detection box must contain four perimeter-ordered [x,y] points")
    points = []
    for point in value:
        if (not isinstance(point, list) or len(point) != 2 or
                any(isinstance(x, bool) or not isinstance(x, (float, int)) or not math.isfinite(x) for x in point)):
            raise ValueError("box coordinates must be finite numbers")
        points.append(tuple(point))
    turns = [cross(points[i], points[(i + 1) % 4], points[(i + 2) % 4]) for i in range(4)]
    if not all(math.isfinite(t) for t in turns) or not (all(t > 0 for t in turns) or all(t < 0 for t in turns)):
        raise ValueError("boxes must be non-degenerate convex quadrilaterals in perimeter order")
    return points if turns[0] > 0 else list(reversed(points))


def cross(a, b, c):
    return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])


def area(points):
    return abs(sum(p[0] * q[1] - q[0] * p[1] for p, q in zip(points, points[1:] + points[:1]))) / 2


def iou(left, right):
    clipped = left[:]
    for a, b in zip(right, right[1:] + right[:1]):
        source, clipped = clipped, []
        if not source:
            return 0.0
        previous = source[-1]
        before = cross(a, b, previous)
        for current in source:
            after = cross(a, b, current)
            if (after >= 0) != (before >= 0):
                t = before / (before - after)
                clipped.append((previous[0] + t * (current[0] - previous[0]),
                                previous[1] + t * (current[1] - previous[1])))
            if after >= 0:
                clipped.append(current)
            previous, before = current, after
    intersection = area(clipped)
    union = area(left) + area(right) - intersection
    return min(1.0, max(0.0, intersection / union)) if union > 0 else 0.0


def score_boxes(reference, predicted, threshold=0.5):
    if not isinstance(reference, list) or not isinstance(predicted, list):
        raise ValueError("boxes must be arrays, including empty arrays for blank pages")
    if isinstance(threshold, bool) or not isinstance(threshold, (float, int)) or not 0 < threshold <= 1:
        raise ValueError("detection_iou_threshold must be in (0,1]")
    expected = [polygon(value) for value in reference]
    actual = [polygon(value) for value in predicted]
    edges = [[j for j, target in enumerate(expected) if iou(box, target) >= threshold] for box in actual]
    # Augmenting paths give maximum-cardinality matching, avoiding greedy order-dependent scores.
    matched = {}
    for start in range(len(actual)):
        queue = [start]
        previous = {start: None}
        target_from = {}
        found = None
        for source in queue:
            for target in edges[source]:
                if target in target_from:
                    continue
                target_from[target] = source
                if target not in matched:
                    found = target
                    break
                next_source = matched[target]
                if next_source not in previous:
                    previous[next_source] = target
                    queue.append(next_source)
            if found is not None:
                break
        while found is not None:
            source = target_from[found]
            matched[found] = source
            found = previous[source]
    true_positives = len(matched)
    return {"true_positives": true_positives, "false_positives": len(actual) - true_positives,
            "false_negatives": len(expected) - true_positives}


def aggregate_detection(rows):
    counts = {key: sum(row[key] for row in rows) for key in ("true_positives", "false_positives", "false_negatives")}
    tp, fp, fn = (counts[key] for key in counts)
    return dict(counts, precision=tp / (tp + fp) if tp + fp else None,
                recall=tp / (tp + fn) if tp + fn else None,
                f1=2 * tp / (2 * tp + fp + fn) if 2 * tp + fp + fn else None)
