"""Reproduce the managed RGB-to-CMYK approximation (developer-only NumPy tool).

Run without arguments to verify the committed table, or with --emit to print
replacement Base64. This never modifies source files. The solver fits a
continuous approximation of the forward conversion, then quantizes inks.
It does not define an ICC profile or guarantee a global minimum.
"""
from pathlib import Path
import base64
import hashlib
import re
import sys
import textwrap
import numpy as np

source = (Path(__file__).resolve().parents[1] /
          "engine/KillerPdf.Engine/Rendering/PdfDeviceCmyk.cs").read_text(encoding="utf-8")

def samples(name):
    encoded = re.search(r'byte\[\] ' + name + r' = Convert.FromBase64String\("""(.*?)"""\)', source, re.S)[1]
    return base64.b64decode(encoded)

table = np.frombuffer(samples("Samples"), dtype=np.uint8).reshape(-1, 3).astype(float)
strides = np.array([729, 81, 9, 1])

def forward(x):
    nearest = np.floor((x + 16) / 32).astype(int)
    index = nearest @ strides
    first = table[index]
    result = first.copy()
    for channel in range(4):
        adjacent = np.floor(x[:, channel] / 32).astype(int)
        adjacent = np.where(adjacent == nearest[:, channel], np.where(adjacent == 8, 7, adjacent + 1), adjacent)
        delta = adjacent - nearest[:, channel]
        rate = -(x[:, channel] - nearest[:, channel] * 32) * delta / 32
        result += (first - table[index + delta * strides[channel]]) * rate[:, None]
    return np.clip(result, 0, 255)

def solve(target):
    light = np.max(target, axis=1)
    x = np.column_stack(((light[:, None] - target) * 255 / np.maximum(light[:, None], 1), 255 - light))
    best = x.copy()
    errors = np.sum((forward(x) - target) ** 2, axis=1)
    for iteration in range(80):
        actual = forward(x)
        jac = []
        for component in range(4):
            lo, hi = x.copy(), x.copy()
            lo[:, component] = np.maximum(0, lo[:, component] - 2)
            hi[:, component] = np.minimum(255, hi[:, component] + 2)
            jac.append((forward(hi) - forward(lo)) / (hi[:, component] - lo[:, component])[:, None])
        j = np.stack(jac, axis=2)
        transposed = np.transpose(j, (0, 2, 1))
        step = (transposed @ np.linalg.solve(j @ transposed + np.eye(3) * .1, (target - actual)[:, :, None]))[:, :, 0]
        proposal = np.clip(x + np.clip(step, -24, 24), 0, 255)
        new_errors = np.sum((forward(proposal) - target) ** 2, axis=1)
        better = new_errors < errors
        best[better] = proposal[better]
        errors[better] = new_errors[better]
        x = proposal
    return best, errors


values = np.linspace(0, 255, 17)
rgb = np.stack(np.meshgrid(values, values, values, indexing="ij"), axis=-1).reshape(-1, 3)
ink, errors = solve(rgb)
generated = np.rint(ink).astype(np.uint8).tobytes()
if "--emit" in sys.argv:
    print(textwrap.fill(base64.b64encode(generated).decode("ascii"), width=120))
else:
    print("NumPy:", np.__version__)
    print("SHA-256:", hashlib.sha256(generated).hexdigest().upper())
    if generated != samples("InverseSamples"):
        raise SystemExit("Generated inverse differs from the committed table.")
    print("Inverse table matches.")
