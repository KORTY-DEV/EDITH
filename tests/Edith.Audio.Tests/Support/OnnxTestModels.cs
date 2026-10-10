using System.Text;

namespace Edith.Audio.Tests.Support;

// Минимальный генератор ONNX (protobuf вручную) для тестов.
// Даёт настоящие ONNX-файлы, которые грузит ORT, с нужными
// input/output shape — без Python и без внешних моделей.
//
// Синтетический pipeline "громкий звук = wake":
//   mel        : output = zeros[1,1,8,32] + ReduceMax(input)
//   embedding  : output = zeros[1,1,1,96] + ReduceMean(input_1)
//   classifier : sigmoid((ReduceMean(x) - 100) * 0.1)   (тишина ~ 0.0002)
public static class OnnxTestModels
{
    public static void WriteValidSet(
        string directory,
        int classifierFrames = 16)
    {
        Directory.CreateDirectory(directory);

        File.WriteAllBytes(
            Path.Combine(directory, "melspectrogram.onnx"),
            Mel());

        File.WriteAllBytes(
            Path.Combine(directory, "embedding_model.onnx"),
            Embedding());

        File.WriteAllBytes(
            Path.Combine(directory, "edith.onnx"),
            Classifier(classifierFrames));
    }


    public static byte[] Mel(
        string inputName = "input",
        long outputLastDim = 32)
    {
        var g = new Graph("mel");

        g.Input(inputName, [-1, -1]);

        g.Initializer(
            "zeros",
            [1, 1, 8, outputLastDim]);

        g.Node("ReduceMax", [inputName], ["m"],
            Int("keepdims", 0));

        g.Node("Add", ["zeros", "m"], ["output"]);

        g.Output("output", [-1, 1, -1, outputLastDim]);

        return g.Build();
    }


    public static byte[] Embedding(
        long windowFrames = 76)
    {
        var g = new Graph("embedding");

        g.Input("input_1", [-1, windowFrames, 32, 1]);

        g.Initializer("zeros", [1, 1, 1, 96]);

        g.Node("ReduceMean", ["input_1"], ["m"],
            Int("keepdims", 0));

        g.Node("Add", ["zeros", "m"], ["conv2d_19"]);

        g.Output("conv2d_19", [-1, 1, 1, 96]);

        return g.Build();
    }


    // Классификатор: score растёт вместе с громкостью.
    public static byte[] Classifier(
        int frames = 16,
        long embeddingDim = 96,
        bool unbounded = false,
        bool multiClass = false,
        float pivot = 100.0f)
    {
        var g = new Graph("classifier");

        g.Input("onnx::Flatten_0", [1, frames, embeddingDim]);

        g.Node("ReduceMean", ["onnx::Flatten_0"], ["s"],
            Int("keepdims", 0));

        if (unbounded)
        {
            // Может дать значения > 1: имитация сбойной модели.
            g.ScalarInitializer("k", 0.01f);
            g.Node("Mul", ["s", "k"], ["p"]);
        }
        else
        {
            g.ScalarInitializer("t", pivot);
            g.ScalarInitializer("k", 0.1f);
            g.Node("Sub", ["s", "t"], ["d"]);
            g.Node("Mul", ["d", "k"], ["z"]);
            g.Node("Sigmoid", ["z"], ["p"]);
        }

        long width =
            multiClass
                ? 3
                : 1;

        g.Initializer("zc", [1, width]);

        g.Node("Add", ["p", "zc"], ["probability"]);

        g.Output("probability", [1, width]);

        return g.Build();
    }


    // ---------------------------------------------------------
    // Protobuf
    // ---------------------------------------------------------

    private sealed record Attr(
        string Name,
        long Value);

    private static Attr Int(
        string name,
        long value) =>
        new(name, value);


    private sealed class Graph
    {
        private readonly string _name;
        private readonly List<byte[]> _nodes = [];
        private readonly List<byte[]> _initializers = [];
        private readonly List<byte[]> _inputs = [];
        private readonly List<byte[]> _outputs = [];

        public Graph(string name) =>
            _name = name;

        public void Input(string name, long[] dims) =>
            _inputs.Add(ValueInfo(name, dims));

        public void Output(string name, long[] dims) =>
            _outputs.Add(ValueInfo(name, dims));

        public void Node(
            string op,
            string[] inputs,
            string[] outputs,
            params Attr[] attributes)
        {
            var w = new Proto();

            foreach (string i in inputs)
                w.String(1, i);

            foreach (string o in outputs)
                w.String(2, o);

            w.String(3, op + "_" + _nodes.Count);
            w.String(4, op);

            foreach (Attr a in attributes)
            {
                var attr = new Proto();
                attr.String(1, a.Name);
                attr.Varint(3, a.Value);
                attr.Varint(20, 2); // INT
                w.Message(5, attr.ToArray());
            }

            _nodes.Add(w.ToArray());
        }

        public void Initializer(
            string name,
            long[] dims)
        {
            long count = 1;

            foreach (long d in dims)
                count *= d;

            AddTensor(
                name,
                dims,
                new float[count]);
        }

        public void ScalarInitializer(
            string name,
            float value) =>
            AddTensor(name, [], [value]);

        private void AddTensor(
            string name,
            long[] dims,
            float[] data)
        {
            var t = new Proto();

            foreach (long d in dims)
                t.Varint(1, d);

            t.Varint(2, 1); // FLOAT
            t.String(8, name);

            var raw = new byte[data.Length * 4];

            Buffer.BlockCopy(data, 0, raw, 0, raw.Length);

            t.Bytes(9, raw);

            _initializers.Add(t.ToArray());
        }

        public byte[] Build()
        {
            var g = new Proto();

            foreach (var n in _nodes)
                g.Message(1, n);

            g.String(2, _name);

            foreach (var i in _initializers)
                g.Message(5, i);

            foreach (var i in _inputs)
                g.Message(11, i);

            foreach (var o in _outputs)
                g.Message(12, o);

            var opset = new Proto();
            opset.Varint(2, 13);

            var m = new Proto();
            m.Varint(1, 8); // ir_version
            m.String(2, "edith-tests");
            m.Message(7, g.ToArray());
            m.Message(8, opset.ToArray());

            return m.ToArray();
        }

        private static byte[] ValueInfo(
            string name,
            long[] dims)
        {
            var shape = new Proto();

            for (int index = 0; index < dims.Length; index++)
            {
                long d = dims[index];

                var dim = new Proto();

                if (d >= 0)
                    dim.Varint(1, d);
                else
                    dim.String(2, name + "_dim" + index);

                shape.Message(1, dim.ToArray());
            }

            var tensor = new Proto();
            tensor.Varint(1, 1); // FLOAT
            tensor.Message(2, shape.ToArray());

            var type = new Proto();
            type.Message(1, tensor.ToArray());

            var info = new Proto();
            info.String(1, name);
            info.Message(2, type.ToArray());

            return info.ToArray();
        }
    }


    private sealed class Proto
    {
        private readonly MemoryStream _stream = new();

        public void Varint(int field, long value)
        {
            Tag(field, 0);
            WriteVarint((ulong)value);
        }

        public void String(int field, string value) =>
            Bytes(field, Encoding.UTF8.GetBytes(value));

        public void Message(int field, byte[] value) =>
            Bytes(field, value);

        public void Bytes(int field, byte[] value)
        {
            Tag(field, 2);
            WriteVarint((ulong)value.Length);
            _stream.Write(value);
        }

        public byte[] ToArray() =>
            _stream.ToArray();

        private void Tag(int field, int wire) =>
            WriteVarint((ulong)((field << 3) | wire));

        private void WriteVarint(ulong value)
        {
            while (value >= 0x80)
            {
                _stream.WriteByte(
                    (byte)(value | 0x80));

                value >>= 7;
            }

            _stream.WriteByte((byte)value);
        }
    }
}
