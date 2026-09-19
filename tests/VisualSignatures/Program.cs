using System.Text.Json;

var testDirectory = Path.Combine(Path.GetTempPath(), $"pluto-visual-signatures-{Guid.NewGuid():N}");
Directory.CreateDirectory(testDirectory);
try
{
    var path = Path.Combine(testDirectory, VisualSignatureStore.DefaultFileName);
    var store = new VisualSignatureStore(path);
    var missing = store.Load();
    Equal(0, missing.Signatures.Count);
    Equal(0, missing.Warnings.Count);

    var createdAt = new DateTimeOffset(2026, 9, 19, 12, 34, 56, TimeSpan.Zero);
    var first = Signature("11111111-1111-1111-1111-111111111111", " Test signature ", createdAt,
        "0123456789abcdef", "fedcba9876543210");
    var second = Signature("22222222-2222-2222-2222-222222222222", "Second", createdAt.AddMinutes(1),
        "0000000000000000", "ffffffffffffffff");

    var firstSave = store.Save([first, second]);
    Equal(true, firstSave.Success);
    Equal(2, firstSave.SavedCount);
    using (var document = JsonDocument.Parse(File.ReadAllText(path)))
    {
        var root = document.RootElement;
        Equal(1, root.GetProperty("version").GetInt32());
        Equal(2, root.GetProperty("signatures").GetArrayLength());
        Equal("dhash-64", root.GetProperty("signatures")[0].GetProperty("fingerprintFormat").GetString());
        Equal(1, root.GetProperty("signatures")[0].GetProperty("fingerprintVersion").GetInt32());
        Equal(2, root.GetProperty("signatures")[0].GetProperty("frameFingerprints").GetArrayLength());
    }

    var loaded = store.Load();
    Equal(2, loaded.Signatures.Count);
    Equal(0, loaded.Warnings.Count);
    Equal("Test signature", loaded.Signatures[0].Name);
    Equal("0123456789ABCDEF", loaded.Signatures[0].FrameFingerprints[0]);
    Equal(500, loaded.Signatures[0].SampleIntervalMilliseconds);

    var validJson = JsonSerializer.Serialize(first, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
    File.WriteAllText(path, $$"""
        {
          "version": 1,
          "savedAtUtc": "{{createdAt:O}}",
          "signatures": [
            {{validJson}},
            { "id": "not-a-guid", "name": "Bad", "createdAtUtc": "{{createdAt:O}}",
              "sampleIntervalMilliseconds": 500, "fingerprintFormat": "dhash-64",
              "fingerprintVersion": 1, "frameFingerprints": ["bad", "0000000000000000"] },
            null
          ]
        }
        """);
    var mixed = store.Load();
    Equal(1, mixed.Signatures.Count);
    Equal(2, mixed.Warnings.Count);

    File.WriteAllText(path, """{ "version": 99, "signatures": [] }""");
    var unsupported = store.Load();
    Equal(0, unsupported.Signatures.Count);
    Equal(true, unsupported.Warnings.Single().Contains("unsupported", StringComparison.Ordinal));

    var restoreSave = store.Save([second]);
    Equal(true, restoreSave.Success);
    var previousContents = File.ReadAllText(path);
    var invalid = second with { FrameFingerprints = ["too-short", "0000000000000000"] };
    var failedSave = store.Save([invalid]);
    Equal(false, failedSave.Success);
    Equal(previousContents, File.ReadAllText(path));
    Equal(0, Directory.GetFiles(testDirectory, "*.tmp").Length);
}
finally
{
    Directory.Delete(testDirectory, recursive: true);
}

Console.WriteLine("PASS: versioned visual signatures, per-entry recovery, compact fingerprints, and atomic save behavior");

static LearnedVisualSignature Signature(
    string id,
    string name,
    DateTimeOffset createdAt,
    params string[] fingerprints) =>
    new(
        id,
        name,
        createdAt,
        500,
        VisualSignatureStore.CurrentFingerprintFormat,
        VisualSignatureStore.CurrentFingerprintVersion,
        fingerprints);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}
