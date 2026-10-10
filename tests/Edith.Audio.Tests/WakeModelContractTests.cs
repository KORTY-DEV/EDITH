using Edith.Audio.Tests.Support;
using Edith.Audio.Wake;

namespace Edith.Audio.Tests;

public class WakeModelContractTests
{
    private static WakeTensorInfo T(
        string name,
        params int[] dims) =>
        new(name, "float32", dims);


    // ---- Чистая проверка metadata (без ORT) ----------------------

    [Fact]
    public void RealTrainerMelContract_IsAccepted()
    {
        // melspectrogram.onnx: [batch, samples] -> [time, 1, frames, 32]
        Assert.Null(
            WakeModelContractValidator.ValidateMel(
                [T("input", -1, -1)],
                [T("output", -1, 1, -1, 32)]));
    }


    [Fact]
    public void RealTrainerEmbeddingContract_IsAccepted()
    {
        // embedding_model.onnx: [n, 76, 32, 1] -> [n, 1, 1, 96]
        Assert.Null(
            WakeModelContractValidator.ValidateEmbedding(
                [T("input_1", -1, 76, 32, 1)],
                [T("conv2d_19", -1, 1, 1, 96)]));
    }


    [Fact]
    public void RealTrainerWakeModelContract_IsAccepted()
    {
        // edith.onnx (train.py): [1, 16, 96] -> [1, 1] probability
        Assert.Null(
            WakeModelContractValidator.ValidateClassifier(
                [T("onnx::Flatten_0", 1, 16, 96)],
                [T("probability", 1, 1)],
                out int frames));

        Assert.Equal(16, frames);
    }


    [Theory]
    [InlineData(1, 16, 64)]   // не 96-мерные embeddings
    [InlineData(1, -1, 96)]   // динамическое число кадров
    [InlineData(2, 16, 96)]   // batch != 1
    [InlineData(16, 96, 0)]   // rank 2
    public void WrongClassifierInput_IsRejected(
        int a, int b, int c)
    {
        WakeTensorInfo input =
            c == 0
                ? T("x", a, b)
                : T("x", a, b, c);

        Assert.NotNull(
            WakeModelContractValidator.ValidateClassifier(
                [input],
                [T("probability", 1, 1)],
                out _));
    }


    [Fact]
    public void MultiClassOutput_IsRejected()
    {
        Assert.NotNull(
            WakeModelContractValidator.ValidateClassifier(
                [T("x", 1, 16, 96)],
                [T("probability", 1, 3)],
                out _));
    }


    [Fact]
    public void ClassifierWithTwoInputs_IsRejected()
    {
        Assert.NotNull(
            WakeModelContractValidator.ValidateClassifier(
                [T("x", 1, 16, 96), T("y", 1)],
                [T("probability", 1, 1)],
                out _));
    }


    [Fact]
    public void NonFloatTensor_IsRejected()
    {
        Assert.NotNull(
            WakeModelContractValidator.ValidateClassifier(
                [new WakeTensorInfo("x", "int64", [1, 16, 96])],
                [T("probability", 1, 1)],
                out _));
    }


    [Fact]
    public void WrongEmbeddingWindow_IsRejected()
    {
        Assert.NotNull(
            WakeModelContractValidator.ValidateEmbedding(
                [T("input_1", -1, 64, 32, 1)],
                [T("o", -1, 1, 1, 96)]));
    }


    [Fact]
    public void WrongMelBins_IsRejected()
    {
        Assert.NotNull(
            WakeModelContractValidator.ValidateMel(
                [T("input", -1, -1)],
                [T("output", -1, 1, -1, 40)]));
    }


    // ---- Через настоящий ORT на настоящих ONNX-файлах ------------

    [Fact]
    public void ValidSyntheticSet_LoadsAndReportsMetadata()
    {
        using var dir = new TempDir();

        OnnxTestModels.WriteValidSet(dir.Path);

        var report =
            WakeModelLoader.TryLoad(
                Paths(dir),
                out var models);

        using (models)
        {
            Assert.NotNull(models);
            Assert.True(report.AllValid, report.Describe());
            Assert.Equal(16, models!.ClassifierFrames);

            Assert.Equal(
                "onnx::Flatten_0",
                report.Classifier.Inputs.Single().Name);

            Assert.Equal(
                [1, 16, 96],
                report.Classifier.Inputs.Single().Dims);

            Assert.Contains(
                "input=",
                report.Describe());
        }
    }


    [Fact]
    public void ClassifierFrameCount_IsTakenFromMetadata_NotGuessed()
    {
        using var dir = new TempDir();

        OnnxTestModels.WriteValidSet(
            dir.Path,
            classifierFrames: 8);

        WakeModelLoader.TryLoad(
            Paths(dir),
            out var models);

        using (models)
        {
            Assert.NotNull(models);
            Assert.Equal(8, models!.ClassifierFrames);
        }
    }


    [Fact]
    public void MissingFiles_AreReported_NotThrown()
    {
        using var dir = new TempDir();

        var report =
            WakeModelLoader.TryLoad(
                Paths(dir),
                out var models);

        Assert.Null(models);
        Assert.False(report.AllValid);
        Assert.False(report.AllPresent);
        Assert.Equal("file not found", report.Classifier.Error);
    }


    [Fact]
    public void GarbageFile_IsNotAValidOnnxModel()
    {
        using var dir = new TempDir();

        OnnxTestModels.WriteValidSet(dir.Path);

        File.WriteAllText(
            dir.File("edith.onnx"),
            "this is not an onnx model");

        var report =
            WakeModelLoader.TryLoad(
                Paths(dir),
                out var models);

        Assert.Null(models);
        Assert.False(report.AllValid);
        Assert.True(report.Mel.Valid);
        Assert.False(report.Classifier.Valid);
        Assert.Contains(
            "not a loadable ONNX",
            report.Classifier.Error);
    }


    [Fact]
    public void EmptyFile_IsRejected()
    {
        using var dir = new TempDir();

        OnnxTestModels.WriteValidSet(dir.Path);

        File.WriteAllBytes(dir.File("edith.onnx"), []);

        var report =
            WakeModelLoader.TryLoad(
                Paths(dir),
                out var models);

        Assert.Null(models);
        Assert.Equal("file is empty", report.Classifier.Error);
    }


    [Fact]
    public void ValidOnnx_WithWrongContract_IsRejected()
    {
        using var dir = new TempDir();

        OnnxTestModels.WriteValidSet(dir.Path);

        // Настоящий ONNX, но 64-мерные embeddings вместо 96.
        File.WriteAllBytes(
            dir.File("edith.onnx"),
            OnnxTestModels.Classifier(embeddingDim: 64));

        var report =
            WakeModelLoader.TryLoad(
                Paths(dir),
                out var models);

        Assert.Null(models);
        Assert.False(report.Classifier.Valid);
        Assert.Contains("[1, frames, 96]", report.Classifier.Error);
    }


    [Fact]
    public void MultiOutputWakeModel_IsRejected()
    {
        using var dir = new TempDir();

        OnnxTestModels.WriteValidSet(dir.Path);

        File.WriteAllBytes(
            dir.File("edith.onnx"),
            OnnxTestModels.Classifier(multiClass: true));

        var report =
            WakeModelLoader.TryLoad(
                Paths(dir),
                out var models);

        Assert.Null(models);
        Assert.False(report.Classifier.Valid);
    }


    [Fact]
    public void SwappedModels_AreRejected()
    {
        using var dir = new TempDir();

        OnnxTestModels.WriteValidSet(dir.Path);

        // melspectrogram.onnx подсунули как embedding_model.onnx.
        File.Copy(
            dir.File("melspectrogram.onnx"),
            dir.File("embedding_model.onnx"),
            overwrite: true);

        var report =
            WakeModelLoader.TryLoad(
                Paths(dir),
                out var models);

        Assert.Null(models);
        Assert.False(report.Embedding.Valid);
    }


    [Fact]
    public void ModelFiles_AreNotLockedAfterLoad()
    {
        using var dir = new TempDir();

        OnnxTestModels.WriteValidSet(dir.Path);

        WakeModelLoader.TryLoad(
            Paths(dir),
            out var models);

        using (models)
        {
            // Установщик должен уметь атомарно заменить edith.onnx,
            // пока EDITH работает.
            string replacement = dir.File("edith.new");

            File.WriteAllBytes(
                replacement,
                OnnxTestModels.Classifier());

            File.Replace(
                replacement,
                dir.File("edith.onnx"),
                null);
        }
    }


    // ---- Реальные модели trainer-а (если есть на этой машине) ----

    private const string GenericModels =
        @"C:\EDITH\data\wake-training\models";

    [Fact]
    public void RealGenericModels_MatchTrainerContract()
    {
        string mel =
            Path.Combine(GenericModels, "melspectrogram.onnx");

        string embedding =
            Path.Combine(GenericModels, "embedding_model.onnx");

        if (!File.Exists(mel) ||
            !File.Exists(embedding))
        {
            return;
        }

        using var dir = new TempDir();

        // Классификатор — синтетический; здесь проверяются
        // именно настоящие mel/embedding.
        File.WriteAllBytes(
            dir.File("edith.onnx"),
            OnnxTestModels.Classifier());

        var report =
            WakeModelLoader.TryLoad(
                new WakeModelPaths(
                    mel,
                    embedding,
                    dir.File("edith.onnx")),
                out var models);

        using (models)
        {
            Assert.True(report.Mel.Valid, report.Describe());
            Assert.True(report.Embedding.Valid, report.Describe());

            Assert.Equal("input", report.Mel.Inputs.Single().Name);
            Assert.Equal("input_1", report.Embedding.Inputs.Single().Name);

            Assert.Equal(
                [-1, 76, 32, 1],
                report.Embedding.Inputs.Single().Dims);
        }
    }


    private static WakeModelPaths Paths(TempDir dir) =>
        new(
            dir.File("melspectrogram.onnx"),
            dir.File("embedding_model.onnx"),
            dir.File("edith.onnx"));
}
