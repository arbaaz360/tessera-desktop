using System.Diagnostics;
using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tessera.Desktop.Search;

namespace Tessera.Smoke;

/// <summary>
/// Raw ONNX Runtime sessions on CoreML with the <c>batch</c> dimension fixed
/// (<see cref="SessionOptions.AddFreeDimensionOverrideByName"/>), each
/// compared with a CPU session on the same input. The Core ML on macOS 26
/// refuses unbounded dimensions; this shows whether a fixed batch is enough
/// once the Conv padding is explicit. The search library does not do this
/// yet; CI runs it to test the fix before proposing it.
/// </summary>
internal static class CoreMlProbe
{
    private const string Sentence = "a solid red square";

    public static int Run(string modelDir, int batch)
    {
        var ort = OrtEnv.Instance().GetVersionString();
        var report = new Report($"CoreML with batch fixed (ONNX Runtime {ort}, model from {modelDir})");
        var manifest = ModelManifest.Embedded();

        // The sentence's token ids, from the library's own tokenizer.
        long[] ids;
        var options = new SearchOptions { ModelsDir = Path.GetDirectoryName(modelDir)!, ModelId = manifest.ModelId, PreferGpu = false };
        using (var store = new ModelStore(options))
        using (var cpu = new SiglipEmbedder(options, store, null, manifest))
        {
            cpu.LoadFromDirectory(modelDir);
            ids = cpu.Tokenize(Sentence);
        }

        var length = manifest.Tokenizer.MaxLength;
        Array.Resize(ref ids, length);
        Probe(report, "vision", Path.Combine(modelDir, "vision.onnx"), batch, batch, () => Pixels(batch, manifest.ImageSize));
        Probe(report, "text", Path.Combine(modelDir, "text.onnx"), 1, 1,
            () => OrtValue.CreateTensorValueFromMemory(ids, [1, length]));
        return report.Finish();
    }

    /// <summary>Image 0 is a red swatch as SigLIP sees it (R +1, G and B -1); the rest are noise.</summary>
    private static OrtValue Pixels(int batch, int size)
    {
        var plane = size * size;
        var data = new float[batch * 3 * plane];
        var random = new Random(7);
        for (var i = 0; i < batch; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                var span = data.AsSpan((i * 3 + c) * plane, plane);
                if (i == 0)
                {
                    span.Fill(c == 0 ? 1f : -1f);
                }
                else
                {
                    for (var k = 0; k < plane; k++)
                    {
                        span[k] = (float)(random.NextDouble() * 2 - 1);
                    }
                }
            }
        }

        return OrtValue.CreateTensorValueFromMemory(data, [batch, 3, size, size]);
    }

    private static void Probe(Report report, string name, string path, int batch, int itemsPerRun, Func<OrtValue> input)
    {
        using var cpuOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        using var cpu = new InferenceSession(path, cpuOptions);
        var inName = cpu.InputMetadata.Keys.First();
        var outName = cpu.OutputMetadata.Keys.First();
        var reference = RunOnce(cpu, inName, outName, input);
        var cpuMs = Time(cpu, inName, outName, input);

        using var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,   // prints the partition count when CoreML takes the graph in pieces
        };
        so.AddFreeDimensionOverrideByName("batch", batch);

        var sw = Stopwatch.StartNew();
        InferenceSession coreml;
        try
        {
            so.AppendExecutionProvider("CoreML", new Dictionary<string, string>
            {
                ["ModelFormat"] = "MLProgram",
                ["MLComputeUnits"] = "ALL",
            });
            coreml = new InferenceSession(path, so);
        }
        catch (Exception e) when (e is OnnxRuntimeException or NotSupportedException or EntryPointNotFoundException)
        {
            var message = e.Message.ReplaceLineEndings(" ");
            report.Check(false, $"{name}: CoreML session with batch {batch}", message.Length > 600 ? message[..600] : message);
            return;
        }

        using (coreml)
        {
            report.Check(true, $"{name}: CoreML session with batch {batch}");
            report.Number($"{name}: CoreML session load", sw.Elapsed.TotalSeconds, "s");
            float[] got;
            try
            {
                got = RunOnce(coreml, inName, outName, input);
            }
            catch (OnnxRuntimeException e)
            {
                report.Check(false, $"{name}: CoreML run", e.Message.ReplaceLineEndings(" "));
                return;
            }

            var dims = got.Length / batch;
            var lowest = Enumerable.Range(0, batch)
                .Select(i => TensorPrimitives.CosineSimilarity(got.AsSpan(i * dims, dims), reference.AsSpan(i * dims, dims))).Min();
            report.Number($"{name}: CoreML against CPU, lowest cosine", lowest, "", 5);
            report.Check(lowest > 0.98, $"{name}: CoreML output matches the CPU", $"{lowest:F5}");

            var coremlMs = Time(coreml, inName, outName, input);
            report.Number($"{name}: ms per run, CoreML / CPU (batch {batch})", $"{coremlMs:F1} / {cpuMs:F1}");
            if (itemsPerRun > 1)
            {
                report.Number($"{name}: items per second, CoreML / CPU", $"{itemsPerRun * 1000 / coremlMs:F1} / {itemsPerRun * 1000 / cpuMs:F1}");
            }
        }
    }

    private static float[] RunOnce(InferenceSession session, string inName, string outName, Func<OrtValue> input)
    {
        using var value = input();
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, [inName], [value], [outName]);
        var output = outputs[0];
        if (output.GetTensorTypeAndShape().ElementDataType == TensorElementType.Float16)
        {
            var half = output.GetTensorDataAsSpan<Half>();
            var result = new float[half.Length];
            for (var i = 0; i < half.Length; i++)
            {
                result[i] = (float)half[i];
            }

            return result;
        }

        return output.GetTensorDataAsSpan<float>().ToArray();
    }

    /// <summary>Median of five runs after one warm-up, in milliseconds.</summary>
    private static double Time(InferenceSession session, string inName, string outName, Func<OrtValue> input)
    {
        RunOnce(session, inName, outName, input);
        var times = new List<double>();
        for (var k = 0; k < 5; k++)
        {
            var sw = Stopwatch.StartNew();
            RunOnce(session, inName, outName, input);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        return times[2];
    }
}
