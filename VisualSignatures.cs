using System.Text;
using System.Text.Json;
using System.Globalization;

internal sealed record LearnedVisualSignature(
    string Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    int SampleIntervalMilliseconds,
    string FingerprintFormat,
    int FingerprintVersion,
    string[] FrameFingerprints);

internal sealed record VisualSignatureFile(
    int Version,
    DateTimeOffset SavedAtUtc,
    LearnedVisualSignature[] Signatures);

internal sealed record VisualSignatureLoadResult(
    IReadOnlyList<LearnedVisualSignature> Signatures,
    IReadOnlyList<string> Warnings);

internal sealed record VisualSignatureSaveResult(bool Success, int SavedCount, string? Error);

internal static class VisualFingerprint
{
    internal const int NormalizedWidth = 9;
    internal const int NormalizedHeight = 8;

    internal static string CreateDHash64(IReadOnlyList<int> normalizedLuminance)
    {
        if (normalizedLuminance.Count != NormalizedWidth * NormalizedHeight)
            throw new ArgumentException("A dHash frame must contain exactly 72 normalized luminance samples.", nameof(normalizedLuminance));

        ulong fingerprint = 0;
        for (var y = 0; y < NormalizedHeight; y++)
        {
            for (var x = 0; x < NormalizedWidth - 1; x++)
            {
                fingerprint <<= 1;
                if (normalizedLuminance[(y * NormalizedWidth) + x] >
                    normalizedLuminance[(y * NormalizedWidth) + x + 1])
                {
                    fingerprint |= 1;
                }
            }
        }

        return fingerprint.ToString("X16", CultureInfo.InvariantCulture);
    }
}

internal sealed class VisualSignatureStore
{
    internal const string DefaultFileName = "visual-signatures.json";
    internal const int CurrentFileVersion = 1;
    internal const string CurrentFingerprintFormat = "dhash-64";
    internal const int CurrentFingerprintVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    internal VisualSignatureStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    internal VisualSignatureLoadResult Load()
    {
        if (!File.Exists(_path))
            return new([], []);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var versionElement) ||
                !versionElement.TryGetInt32(out var version))
            {
                return EmptyWithWarning("visual signature file has no valid version");
            }

            if (version != CurrentFileVersion)
            {
                return EmptyWithWarning(
                    $"unsupported visual signature file version {version}; expected {CurrentFileVersion}");
            }

            if (!root.TryGetProperty("signatures", out var signaturesElement) ||
                signaturesElement.ValueKind != JsonValueKind.Array)
            {
                return EmptyWithWarning("visual signature file has no signatures array");
            }

            var signatures = new List<LearnedVisualSignature>();
            var warnings = new List<string>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var element in signaturesElement.EnumerateArray())
            {
                try
                {
                    var signature = element.Deserialize<LearnedVisualSignature>(ReadOptions);
                    if (!TryValidate(signature, out var reason))
                    {
                        warnings.Add($"visual signature {index} skipped: {reason}");
                    }
                    else if (!seenIds.Add(signature!.Id))
                    {
                        warnings.Add($"visual signature {index} skipped: duplicate ID {signature.Id}");
                    }
                    else
                    {
                        signatures.Add(Normalize(signature));
                    }
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException)
                {
                    warnings.Add($"visual signature {index} skipped: {exception.Message}");
                }

                index++;
            }

            return new(signatures, warnings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return EmptyWithWarning($"could not load visual signatures from {_path}: {exception.Message}");
        }
    }

    internal VisualSignatureSaveResult Save(IReadOnlyCollection<LearnedVisualSignature> signatures)
    {
        var normalized = new List<LearnedVisualSignature>(signatures.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var signature in signatures)
        {
            if (!TryValidate(signature, out var reason))
                return new(false, 0, $"visual signature {index} is invalid: {reason}");
            if (!seenIds.Add(signature.Id))
                return new(false, 0, $"visual signature {index} duplicates ID {signature.Id}");
            normalized.Add(Normalize(signature));
            index++;
        }

        var directory = Path.GetDirectoryName(_path)!;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            var file = new VisualSignatureFile(CurrentFileVersion, DateTimeOffset.UtcNow, [.. normalized]);
            var json = JsonSerializer.Serialize(file, WriteOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(_path))
                File.Replace(temporaryPath, _path, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, _path);

            return new(true, normalized.Count, null);
        }
        catch (Exception exception)
        {
            return new(false, 0, exception.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Temporary-file cleanup is best-effort and must not mask the save result.
            }
        }
    }

    private static VisualSignatureLoadResult EmptyWithWarning(string warning) => new([], [warning]);

    private static LearnedVisualSignature Normalize(LearnedVisualSignature signature) => signature with
    {
        Id = Guid.Parse(signature.Id).ToString("D"),
        Name = signature.Name.Trim(),
        FrameFingerprints = signature.FrameFingerprints
            .Select(fingerprint => fingerprint.ToUpperInvariant())
            .ToArray()
    };

    private static bool TryValidate(LearnedVisualSignature? signature, out string reason)
    {
        if (signature is null)
            return Invalid("entry is null", out reason);
        if (!Guid.TryParse(signature.Id, out var id) || id == Guid.Empty)
            return Invalid("ID is missing or is not a non-empty GUID", out reason);
        if (string.IsNullOrWhiteSpace(signature.Name))
            return Invalid("name is missing", out reason);
        if (signature.CreatedAtUtc == default)
            return Invalid("creation time is missing", out reason);
        if (signature.SampleIntervalMilliseconds <= 0)
            return Invalid("sample interval must be positive", out reason);
        if (!string.Equals(signature.FingerprintFormat, CurrentFingerprintFormat, StringComparison.Ordinal))
            return Invalid($"unsupported fingerprint format '{signature.FingerprintFormat}'", out reason);
        if (signature.FingerprintVersion != CurrentFingerprintVersion)
            return Invalid($"unsupported fingerprint version {signature.FingerprintVersion}", out reason);
        if (signature.FrameFingerprints is null || signature.FrameFingerprints.Length < 2)
            return Invalid("at least two ordered frame fingerprints are required", out reason);
        if (signature.FrameFingerprints.Any(fingerprint => !IsFingerprint(fingerprint)))
            return Invalid("frame fingerprints must be 16 hexadecimal characters", out reason);

        reason = string.Empty;
        return true;
    }

    private static bool IsFingerprint(string? fingerprint) =>
        fingerprint is { Length: 16 } && fingerprint.All(Uri.IsHexDigit);

    private static bool Invalid(string message, out string reason)
    {
        reason = message;
        return false;
    }
}
