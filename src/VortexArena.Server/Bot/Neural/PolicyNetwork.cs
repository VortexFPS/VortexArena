using System;
using System.IO;
using System.Numerics;
using System.Text;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// The learned locomotion policy: a small feed-forward network, its weights loaded from a file the Python
/// trainer exports.
///
/// <para><b>Why a hand-written evaluator and not ONNX Runtime.</b> The network is roughly 45,000 weights.
/// ONNX Runtime would add a ~15 MB native dependency per platform to evaluate three matrix-vector products.
/// The dedicated server has to build from a bare <c>git clone</c> (the same constraint that drove the
/// <c>Conductor.Protocol</c> vendoring decision in <c>VortexArena.Net.csproj</c>), so the shipping game
/// carries no ML runtime at all: a weight file and this class.</para>
///
/// <para><b>Shared weights, per-bot activations.</b> One instance serves every bot on the server. At
/// 2 x 128 the weights are 178 KB and stay resident in L2 while sixteen bots take turns through them; only
/// the activation buffers are per-caller, and those are supplied by the caller
/// (<see cref="Scratch"/>) so the evaluator itself is stateless and safe to share.</para>
/// </summary>
public sealed class PolicyNetwork
{
    /// <summary>File magic: "VXPW".</summary>
    private const uint Magic = 0x57505856;

    /// <summary>On-disk layout version. The Python exporter writes the same constant; a mismatch is fatal to the load, not to the server.</summary>
    public const int Version = 1;

    /// <summary>Hidden-layer activation. Only the values the trainer can emit.</summary>
    public enum Activation : byte
    {
        /// <summary>Identity: the output layer.</summary>
        None = 0,
        Tanh = 1,
        Relu = 2,
    }

    private sealed class Layer
    {
        public int InSize, OutSize;
        public float[] Weights = Array.Empty<float>();   // row-major [OutSize, InSize]
        public float[] Biases = Array.Empty<float>();
        public Activation Act;
    }

    private readonly Layer[] _layers;

    /// <summary>Per-input mean, from the trainer's running observation normaliser.</summary>
    private readonly float[] _obsMean;

    /// <summary>Per-input reciprocal standard deviation, precomputed so the hot path multiplies.</summary>
    private readonly float[] _obsInvStd;

    /// <summary>Expected observation length. A mismatch means the layout changed under the weights.</summary>
    public int InputSize { get; }

    /// <summary>Network output length; see <see cref="NeuralAction"/> for the head layout.</summary>
    public int OutputSize { get; }

    /// <summary>Free-form label the trainer stamps (run id, curriculum stage) for the console report.</summary>
    public string Label { get; }

    /// <summary>Total scalar parameters, for the load log and the perf bench.</summary>
    public long ParameterCount
    {
        get
        {
            long n = 0;
            foreach (Layer l in _layers) n += l.Weights.Length + l.Biases.Length;
            return n;
        }
    }

    /// <summary>Widest layer, so callers can size their scratch once.</summary>
    public int MaxLayerWidth
    {
        get
        {
            int m = InputSize;
            foreach (Layer l in _layers) if (l.OutSize > m) m = l.OutSize;
            return m;
        }
    }

    private PolicyNetwork(Layer[] layers, float[] obsMean, float[] obsInvStd, string label)
    {
        _layers = layers;
        _obsMean = obsMean;
        _obsInvStd = obsInvStd;
        InputSize = layers[0].InSize;
        OutputSize = layers[^1].OutSize;
        Label = label;
    }

    /// <summary>
    /// Per-caller activation buffers. One per bot (or one per thread in the trainer); never share across
    /// concurrent <see cref="Evaluate"/> calls.
    /// </summary>
    public sealed class Scratch
    {
        internal readonly float[] A;
        internal readonly float[] B;
        public Scratch(PolicyNetwork net)
        {
            int w = net.MaxLayerWidth;
            A = new float[w];
            B = new float[w];
        }
    }

    /// <summary>
    /// Run the forward pass. <paramref name="observation"/> is raw (un-normalised); normalisation happens
    /// here using the statistics baked into the weight file, so the caller never has to keep them in step.
    /// Writes <see cref="OutputSize"/> values to <paramref name="output"/>.
    /// </summary>
    public void Evaluate(ReadOnlySpan<float> observation, Scratch scratch, Span<float> output)
    {
        if (observation.Length != InputSize)
            throw new ArgumentException($"observation is {observation.Length} floats, weights expect {InputSize}", nameof(observation));
        if (output.Length < OutputSize)
            throw new ArgumentException($"output needs {OutputSize} floats", nameof(output));

        float[] cur = scratch.A, next = scratch.B;

        // Normalise in place into the first buffer. Clipping at +/-10 sigma matters more than it looks: a
        // bot that falls into the void produces observation values far outside anything training saw, and
        // an unclipped input can saturate every downstream tanh at once and freeze the output.
        for (int i = 0; i < InputSize; i++)
        {
            float v = (observation[i] - _obsMean[i]) * _obsInvStd[i];
            cur[i] = v < -10f ? -10f : v > 10f ? 10f : v;
        }

        for (int li = 0; li < _layers.Length; li++)
        {
            Layer l = _layers[li];
            Forward(l, cur, next);
            (cur, next) = (next, cur);
        }

        cur.AsSpan(0, OutputSize).CopyTo(output);
    }

    private static void Forward(Layer l, float[] input, float[] output)
    {
        int inSize = l.InSize, outSize = l.OutSize;
        float[] w = l.Weights;
        int lanes = Vector<float>.Count;

        for (int o = 0; o < outSize; o++)
        {
            int row = o * inSize;
            float sum = l.Biases[o];

            // Vector<float> rather than an explicit AVX intrinsic: it compiles to the widest ISA the runtime
            // has and still works on ARM, which matters because the dedicated server targets both.
            int i = 0;
            if (inSize >= lanes)
            {
                var acc = Vector<float>.Zero;
                for (; i <= inSize - lanes; i += lanes)
                {
                    var vw = new Vector<float>(w, row + i);
                    var vi = new Vector<float>(input, i);
                    acc += vw * vi;
                }
                sum += Vector.Dot(acc, Vector<float>.One);
            }
            for (; i < inSize; i++) sum += w[row + i] * input[i];

            output[o] = l.Act switch
            {
                Activation.Tanh => MathF.Tanh(sum),
                Activation.Relu => sum > 0f ? sum : 0f,
                _ => sum,
            };
        }
    }

    // =============================================================================================
    // load / save
    // =============================================================================================

    /// <summary>
    /// Read a weight file. Returns null for anything malformed. Null means "fall back to the classic steer
    /// and log once", never an exception into the server tick: a bad weight file must not stop a match.
    /// </summary>
    public static PolicyNetwork? Read(Stream stream, out string? error)
    {
        error = null;
        try
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (r.ReadUInt32() != Magic) { error = "not a policy weight file"; return null; }
            int version = r.ReadInt32();
            if (version != Version) { error = $"weight file version {version}, this build reads {Version}"; return null; }

            string label = r.ReadString();
            int inputSize = r.ReadInt32();
            int layerCount = r.ReadInt32();
            if (inputSize <= 0 || inputSize > 8192 || layerCount <= 0 || layerCount > 16)
            {
                error = $"implausible architecture: {inputSize} inputs, {layerCount} layers";
                return null;
            }

            var mean = new float[inputSize];
            var invStd = new float[inputSize];
            for (int i = 0; i < inputSize; i++) mean[i] = r.ReadSingle();
            for (int i = 0; i < inputSize; i++)
            {
                float sd = r.ReadSingle();
                // A zero-variance input (a flag that never changed during training) would divide to
                // infinity. Treat it as unit scale: the feature is constant, so the network learned to
                // ignore it anyway.
                invStd[i] = MathF.Abs(sd) < 1e-6f ? 1f : 1f / sd;
            }

            var layers = new Layer[layerCount];
            int prev = inputSize;
            for (int li = 0; li < layerCount; li++)
            {
                int outSize = r.ReadInt32();
                var act = (Activation)r.ReadByte();
                if (outSize <= 0 || outSize > 8192) { error = $"layer {li} has {outSize} outputs"; return null; }

                var layer = new Layer
                {
                    InSize = prev,
                    OutSize = outSize,
                    Act = act,
                    Weights = new float[(long)prev * outSize <= int.MaxValue ? prev * outSize : 0],
                    Biases = new float[outSize],
                };
                if (layer.Weights.Length == 0) { error = $"layer {li} is too large"; return null; }

                for (int i = 0; i < layer.Weights.Length; i++) layer.Weights[i] = r.ReadSingle();
                for (int i = 0; i < outSize; i++) layer.Biases[i] = r.ReadSingle();

                layers[li] = layer;
                prev = outSize;
            }

            return new PolicyNetwork(layers, mean, invStd, label);
        }
        catch (EndOfStreamException) { error = "weight file is truncated"; return null; }
        catch (IOException e) { error = e.Message; return null; }
    }

    /// <summary>Load from a path. Returns null (with <paramref name="error"/> set) rather than throwing.</summary>
    public static PolicyNetwork? Load(string path, out string? error)
    {
        if (!File.Exists(path)) { error = $"no weight file at {path}"; return null; }
        try
        {
            using FileStream fs = File.OpenRead(path);
            return Read(fs, out error);
        }
        catch (IOException e) { error = e.Message; return null; }
        catch (UnauthorizedAccessException e) { error = e.Message; return null; }
    }

    /// <summary>
    /// Write a network in the on-disk format. Used by the tests (round-trip) and by
    /// <see cref="CreateUntrained"/>'s callers; the real weights come from the Python exporter, which writes
    /// the identical layout.
    /// </summary>
    public void Write(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(Label);
        w.Write(InputSize);
        w.Write(_layers.Length);
        for (int i = 0; i < InputSize; i++) w.Write(_obsMean[i]);
        for (int i = 0; i < InputSize; i++) w.Write(_obsInvStd[i] == 0f ? 1f : 1f / _obsInvStd[i]);
        foreach (Layer l in _layers)
        {
            w.Write(l.OutSize);
            w.Write((byte)l.Act);
            foreach (float f in l.Weights) w.Write(f);
            foreach (float f in l.Biases) w.Write(f);
        }
    }

    /// <summary>
    /// A randomly initialised network of the shipping architecture. Not useful for play; it is what the
    /// perf bench times, what the round-trip test writes, and what the trainer starts from when no
    /// checkpoint exists.
    /// </summary>
    public static PolicyNetwork CreateUntrained(int inputSize, int outputSize, int hiddenWidth = 128,
        int hiddenLayers = 2, int seed = 1)
    {
        var rng = new Random(seed);
        var layers = new Layer[hiddenLayers + 1];
        int prev = inputSize;
        for (int i = 0; i < hiddenLayers; i++)
        {
            layers[i] = MakeLayer(rng, prev, hiddenWidth, Activation.Tanh);
            prev = hiddenWidth;
        }
        layers[hiddenLayers] = MakeLayer(rng, prev, outputSize, Activation.None);

        var mean = new float[inputSize];
        var invStd = new float[inputSize];
        for (int i = 0; i < inputSize; i++) invStd[i] = 1f;
        return new PolicyNetwork(layers, mean, invStd, "untrained");
    }

    private static Layer MakeLayer(Random rng, int inSize, int outSize, Activation act)
    {
        var l = new Layer
        {
            InSize = inSize,
            OutSize = outSize,
            Act = act,
            Weights = new float[inSize * outSize],
            Biases = new float[outSize],
        };
        // Xavier/Glorot uniform, which is what a tanh MLP wants and what the Python side initialises with.
        float limit = MathF.Sqrt(6f / (inSize + outSize));
        for (int i = 0; i < l.Weights.Length; i++)
            l.Weights[i] = (float)(rng.NextDouble() * 2.0 - 1.0) * limit;
        return l;
    }
}
