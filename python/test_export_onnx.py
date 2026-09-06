"""Stdlib(-ish) self-check for export_onnx.py's `_strip_noop_allowzero` (no pytest). Run:
python test_export_onnx.py  ->  prints "ok" or asserts. Also collectable by
`pytest test_export_onnx.py`.

The one dependency this can't avoid is `onnx` itself, since the function under test operates on
an ONNX graph proto; skip cleanly with a printed reason if it (or export_onnx.py's own import-time
deps) isn't importable, the same way test_server.py's tests guard around missing pyiqa rather than
failing outright.

Every Reshape in the real aesthetic-v2.5 export graph is provably strippable, so this file is the
only place `_strip_noop_allowzero`'s three "leave it alone" branches get exercised at all:
  * shape input is a constant that DOES contain a 0 -- allowzero=1 means something different there
    (literal 0 vs. "copy the input's size in this dim"), so stripping would change what the graph
    computes. This is the one that matters most.
  * shape input is not a constant (fed at run time) -- nothing here proves it's safe, so leave it.
  * no allowzero attribute at all -- nothing to strip.
It also checks the ordinary case (constant, no zero -> stripped) and that the strip only ever
touches Reshape nodes, never anything else carrying an attribute of the same name.
"""
import os
import tempfile

try:
    import numpy as np
    import onnx
    from onnx import helper, numpy_helper, TensorProto
except ImportError:
    onnx = None

try:
    import onnxruntime as ort
except ImportError:
    ort = None

try:
    import export_onnx
except ImportError:
    export_onnx = None


def _have_onnx():
    if onnx is None or export_onnx is None:
        print("skipping: onnx (or export_onnx.py's own import-time deps) not importable here")
        return False
    return True


def _tmp_onnx_path():
    fd, path = tempfile.mkstemp(suffix=".onnx")
    os.close(fd)
    return path


def _make_model(node, inputs, outputs, initializers=()):
    graph = helper.make_graph([node], "g", inputs, outputs, initializer=list(initializers))
    return helper.make_model(graph, opset_imports=[helper.make_opsetid("", 18)])


def test_strips_allowzero_when_shape_is_a_constant_with_no_zero():
    """The ordinary case: every Reshape in the real export graph looks like this."""
    if not _have_onnx():
        return
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [2, 3])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [6])
    shape_init = numpy_helper.from_array(np.array([6], dtype=np.int64), name="shape")
    node = helper.make_node("Reshape", ["x", "shape"], ["y"], name="r", allowzero=1)
    path = _tmp_onnx_path()
    try:
        onnx.save_model(_make_model(node, [x], [y], [shape_init]), path)
        stripped = export_onnx._strip_noop_allowzero(path)
        assert stripped == 1, stripped
        reloaded = onnx.load(path)
        r = reloaded.graph.node[0]
        assert not any(a.name == "allowzero" for a in r.attribute), r.attribute
    finally:
        os.remove(path)


def test_successful_strip_leaves_no_temp_file_behind():
    """_strip_noop_allowzero writes to a same-directory temp file and os.replace()s it onto the
    real path so an interrupted write can never corrupt the file the app loads at startup. This
    only covers the happy path -- simulating an actual interrupted write would need patching
    onnx.save_model mid-call, which is more machinery than this defect is worth."""
    if not _have_onnx():
        return
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [2, 3])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [6])
    shape_init = numpy_helper.from_array(np.array([6], dtype=np.int64), name="shape")
    node = helper.make_node("Reshape", ["x", "shape"], ["y"], name="r", allowzero=1)
    path = _tmp_onnx_path()
    try:
        onnx.save_model(_make_model(node, [x], [y], [shape_init]), path)
        stripped = export_onnx._strip_noop_allowzero(path)
        assert stripped == 1, stripped
        assert os.path.exists(path)
        assert not os.path.exists(path + ".tmp")
    finally:
        os.remove(path)


def test_keeps_allowzero_when_shape_contains_zero_and_the_graph_still_runs_correctly():
    """The case that matters most. A 0 in the shape means something different under allowzero=1
    (take it literally) than under the default (copy the input's size in that dim) -- so build a
    case where the two interpretations disagree enough that getting this wrong isn't a subtly
    different answer, it's an invalid reshape that raises.

    x is genuinely empty ([1, 0, 4], zero elements). Target shape [0, 8, 0] taken literally
    (allowzero=1, the correct/kept behaviour) is valid -- also zero elements (0*8*0) -- and the
    model runs, producing a (0, 8, 0) output both before and after the (no-op, since kept) strip.
    Taken as "copy from input" (what happens if the attribute were wrongly stripped), positions 0
    and 2 would copy input's sizes (1 and 4): target becomes [1, 8, 4] = 32 elements, which
    ONNX Runtime refuses to reshape a 0-element input into. A regression here doesn't quietly pass
    with a different shape -- it raises, which is exactly what makes this test worth having.
    """
    if not _have_onnx():
        return
    if ort is None:
        print("skipping runnable half of this test: onnxruntime not importable")
        return
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [1, 0, 4])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [0, 8, 0])
    shape_init = numpy_helper.from_array(np.array([0, 8, 0], dtype=np.int64), name="shape")
    node = helper.make_node("Reshape", ["x", "shape"], ["y"], name="r", allowzero=1)
    path = _tmp_onnx_path()
    try:
        onnx.save_model(_make_model(node, [x], [y], [shape_init]), path)

        def run():
            sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
            x_val = np.zeros((1, 0, 4), dtype=np.float32)
            return sess.run(None, {"x": x_val})[0]

        before = run()
        stripped = export_onnx._strip_noop_allowzero(path)
        assert stripped == 0, stripped
        reloaded = onnx.load(path)
        r = reloaded.graph.node[0]
        assert any(a.name == "allowzero" and a.i == 1 for a in r.attribute), r.attribute
        after = run()
        assert before.shape == (0, 8, 0), before.shape
        assert after.shape == before.shape, (before.shape, after.shape)
    finally:
        os.remove(path)


def test_keeps_allowzero_when_shape_input_is_not_a_constant():
    """A Reshape fed its shape by another node (or, as here, a graph input) at run time -- there's
    nothing in the graph to prove stripping is safe, so leave it alone."""
    if not _have_onnx():
        return
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [2, 3])
    shape_in = helper.make_tensor_value_info("shape", TensorProto.INT64, [1])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [6])
    node = helper.make_node("Reshape", ["x", "shape"], ["y"], name="r", allowzero=1)
    path = _tmp_onnx_path()
    try:
        graph = helper.make_graph([node], "g", [x, shape_in], [y])  # no initializer for "shape"
        model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 18)])
        onnx.save_model(model, path)
        stripped = export_onnx._strip_noop_allowzero(path)
        assert stripped == 0, stripped
        reloaded = onnx.load(path)
        r = reloaded.graph.node[0]
        assert any(a.name == "allowzero" for a in r.attribute), r.attribute
    finally:
        os.remove(path)


def test_leaves_reshape_alone_when_it_has_no_allowzero_attribute():
    if not _have_onnx():
        return
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [2, 3])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [6])
    shape_init = numpy_helper.from_array(np.array([6], dtype=np.int64), name="shape")
    node = helper.make_node("Reshape", ["x", "shape"], ["y"], name="r")  # no allowzero at all
    path = _tmp_onnx_path()
    try:
        onnx.save_model(_make_model(node, [x], [y], [shape_init]), path)
        stripped = export_onnx._strip_noop_allowzero(path)
        assert stripped == 0, stripped
        reloaded = onnx.load(path)
        r = reloaded.graph.node[0]
        assert len(r.attribute) == 0, r.attribute
    finally:
        os.remove(path)


def test_non_reshape_node_is_never_touched_even_if_it_carries_an_allowzero_attribute():
    """Guards the op_type filter itself: the function must key off `node.op_type == "Reshape"`
    before it ever looks at attribute names, not just filter by attribute name across all nodes."""
    if not _have_onnx():
        return
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [2, 3])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [2, 3])
    node = helper.make_node("Identity", ["x"], ["y"], name="not_a_reshape", allowzero=1)
    path = _tmp_onnx_path()
    try:
        onnx.save_model(_make_model(node, [x], [y]), path)
        stripped = export_onnx._strip_noop_allowzero(path)
        assert stripped == 0, stripped
        reloaded = onnx.load(path)
        r = reloaded.graph.node[0]
        assert any(a.name == "allowzero" and a.i == 1 for a in r.attribute), r.attribute
    finally:
        os.remove(path)


def test_keeps_allowzero_when_shape_initializer_is_external_data():
    """A shape initializer pushed to external data (data_location == EXTERNAL) has its value
    loaded=False on purpose (see the docstring on the function under test) -- its actual contents
    are never read here, so it can't be proven zero-free and must be left alone."""
    if not _have_onnx():
        return
    x = helper.make_tensor_value_info("x", TensorProto.FLOAT, [2, 3])
    y = helper.make_tensor_value_info("y", TensorProto.FLOAT, [6])
    shape_init = TensorProto()
    shape_init.name = "shape"
    shape_init.data_type = TensorProto.INT64
    shape_init.dims.append(1)
    shape_init.data_location = TensorProto.EXTERNAL
    entry = shape_init.external_data.add()
    entry.key = "location"
    entry.value = "shape.bin"  # never written -- load_external_data=False never reads it
    node = helper.make_node("Reshape", ["x", "shape"], ["y"], name="r", allowzero=1)
    path = _tmp_onnx_path()
    try:
        onnx.save_model(_make_model(node, [x], [y], [shape_init]), path)
        stripped = export_onnx._strip_noop_allowzero(path)
        assert stripped == 0, stripped
        reloaded = onnx.load(path, load_external_data=False)
        r = reloaded.graph.node[0]
        assert any(a.name == "allowzero" for a in r.attribute), r.attribute
    finally:
        os.remove(path)


if __name__ == "__main__":
    test_strips_allowzero_when_shape_is_a_constant_with_no_zero()
    test_successful_strip_leaves_no_temp_file_behind()
    test_keeps_allowzero_when_shape_contains_zero_and_the_graph_still_runs_correctly()
    test_keeps_allowzero_when_shape_input_is_not_a_constant()
    test_leaves_reshape_alone_when_it_has_no_allowzero_attribute()
    test_non_reshape_node_is_never_touched_even_if_it_carries_an_allowzero_attribute()
    test_keeps_allowzero_when_shape_initializer_is_external_data()
    print("ok")
