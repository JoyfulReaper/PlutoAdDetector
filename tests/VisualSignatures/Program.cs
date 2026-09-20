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

    var descending = Enumerable.Range(0, VisualFingerprint.NormalizedHeight)
        .SelectMany(_ => Enumerable.Range(0, VisualFingerprint.NormalizedWidth).Select(x => 255 - x))
        .ToArray();
    var ascending = Enumerable.Range(0, VisualFingerprint.NormalizedHeight)
        .SelectMany(_ => Enumerable.Range(0, VisualFingerprint.NormalizedWidth))
        .ToArray();
    Equal("FFFFFFFFFFFFFFFF", VisualFingerprint.CreateDHash64(descending));
    Equal("0000000000000000", VisualFingerprint.CreateDHash64(ascending));
    Equal(64, VisualFingerprint.HammingDistance(
        VisualFingerprint.ParseDHash64("0000000000000000"),
        VisualFingerprint.ParseDHash64("FFFFFFFFFFFFFFFF")));

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
        Equal(VisualSignatureStore.CurrentFingerprintVersion,
            root.GetProperty("signatures")[0].GetProperty("fingerprintVersion").GetInt32());
        Equal(2, root.GetProperty("signatures")[0].GetProperty("frameFingerprints").GetArrayLength());
    }

    var loaded = store.Load();
    Equal(2, loaded.Signatures.Count);
    Equal(0, loaded.Warnings.Count);
    Equal("Test signature", loaded.Signatures[0].Name);
    Equal("0123456789ABCDEF", loaded.Signatures[0].FrameFingerprints[0]);
    Equal(500, loaded.Signatures[0].SampleIntervalMilliseconds);

    var legacyJson = File.ReadAllText(path).Replace(
        $"\"fingerprintVersion\": {VisualSignatureStore.CurrentFingerprintVersion}",
        "\"fingerprintVersion\": 1",
        StringComparison.Ordinal);
    File.WriteAllText(path, legacyJson);
    var legacy = store.Load();
    Equal(0, legacy.Signatures.Count);
    Equal(true, legacy.Warnings.All(warning => warning.Contains("fingerprint version 1", StringComparison.Ordinal)));
    Equal(true, store.Save([first, second]).Success);

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

    var referenceFrames = new[]
    {
        "0000000000000000",
        "FFFFFFFF00000000",
        "AAAAAAAAAAAAAAAA",
        "5555555555555555",
        "F0F0F0F0F0F0F0F0",
        "0F0F0F0F0F0F0F0F",
        "CCCCCCCC33333333",
        "33333333CCCCCCCC"
    };
    var matchSignature = Signature(
        "33333333-3333-3333-3333-333333333333",
        "Ordered promo",
        createdAt,
        referenceFrames);
    var diagnostics = new List<string>();
    var matcher = new VisualSequenceMatcher([matchSignature], debugEnabled: true, diagnostics.Add);
    var matchStart = createdAt.AddHours(1);
    Equal(0, matcher.AddSample(Noisy(referenceFrames[0]), matchStart).Count);
    Equal(0, matcher.AddSample(Noisy(referenceFrames[2]), matchStart.AddMilliseconds(500)).Count);
    Equal(0, matcher.AddSample(Noisy(referenceFrames[4]), matchStart.AddMilliseconds(1_000)).Count);
    var firstMatch = matcher.AddSample(Noisy(referenceFrames[7]), matchStart.AddMilliseconds(1_500));
    Equal(1, firstMatch.Count);
    Equal("Ordered promo", firstMatch[0].SignatureName);
    Equal(true, diagnostics.Any(message =>
        message.Contains("bestRef=0", StringComparison.Ordinal) &&
        message.Contains("distance=5", StringComparison.Ordinal) &&
        message.Contains("threshold=10", StringComparison.Ordinal)));

    // A single similar frame repeated cannot advance through ordered references.
    var repeatedFrameMatcher = new VisualSequenceMatcher([matchSignature]);
    for (var index = 0; index < 10; index++)
        Equal(0, repeatedFrameMatcher.AddSample(referenceFrames[0], matchStart.AddMilliseconds(index * 500)).Count);

    // Reversed anchors must not be mistaken for forward progression.
    var reverseMatcher = new VisualSequenceMatcher([matchSignature]);
    foreach (var referenceIndex in new[] { 7, 4, 2, 0 })
        Equal(0, reverseMatcher.AddSample(referenceFrames[referenceIndex], matchStart).Count);

    // The same continuing occurrence is suppressed. After cooldown plus six
    // dissimilar frames, the signature is armed for a future occurrence.
    foreach (var referenceIndex in new[] { 0, 2, 4, 7 })
        Equal(0, matcher.AddSample(referenceFrames[referenceIndex], matchStart.AddSeconds(2)).Count);
    const string unrelated = "0123456789ABCDEF";
    Equal(true, referenceFrames.All(frame => VisualFingerprint.HammingDistance(
        VisualFingerprint.ParseDHash64(frame), VisualFingerprint.ParseDHash64(unrelated)) >
        VisualSequenceMatcher.RearmHammingDistance));
    for (var index = 0; index < VisualSequenceMatcher.RearmDissimilarSamples; index++)
        Equal(0, matcher.AddSample(unrelated, matchStart.AddSeconds(20).AddMilliseconds(index * 500)).Count);
    Equal(0, matcher.AddSample(referenceFrames[0], matchStart.AddSeconds(24)).Count);
    Equal(0, matcher.AddSample(referenceFrames[2], matchStart.AddSeconds(24.5)).Count);
    Equal(0, matcher.AddSample(referenceFrames[4], matchStart.AddSeconds(25)).Count);
    Equal(1, matcher.AddSample(referenceFrames[7], matchStart.AddSeconds(25.5)).Count);
}
finally
{
    Directory.Delete(testDirectory, recursive: true);
}

Console.WriteLine("PASS: visual persistence, dHash distance, ordered matching, single-frame rejection, and re-arming");

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

static string Noisy(string fingerprint) =>
    (VisualFingerprint.ParseDHash64(fingerprint) ^ 0x1FUL).ToString("X16");
