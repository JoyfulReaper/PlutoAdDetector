var defaults = DetectorOptions.Parse([]);
Equal<string?>(null, defaults.CaptureDirectory);
Equal(TimeSpan.FromSeconds(30), defaults.CaptureInterval);

var enabled = DetectorOptions.Parse(["--captures", "foo"]);
Equal<string?>(Path.GetFullPath("foo"), enabled.CaptureDirectory);
Equal(TimeSpan.FromSeconds(30), enabled.CaptureInterval);

var intervalOnly = DetectorOptions.Parse(["--capture-seconds", "7"]);
Equal<string?>(null, intervalOnly.CaptureDirectory);
Equal(TimeSpan.FromSeconds(7), intervalOnly.CaptureInterval);

foreach (var arguments in new[]
{
    new[] { "--captures", "foo", "--capture-seconds", "7" },
    new[] { "--capture-seconds", "7", "--captures", "foo" }
})
{
    var options = DetectorOptions.Parse(arguments);
    Equal<string?>(Path.GetFullPath("foo"), options.CaptureDirectory);
    Equal(TimeSpan.FromSeconds(7), options.CaptureInterval);
}

foreach (var option in new[] { "--capture-seconds", "--poll-ms", "--confirm", "--min-duration-seconds" })
{
    DetectorOptions.Parse([option, "1"]);
    foreach (var value in new[] { "0", "-1", "1.5", "abc", "2147483648" })
    {
        try
        {
            DetectorOptions.Parse([option, value]);
            throw new Exception($"Expected {option} to reject {value}.");
        }
        catch (ArgumentException exception)
        {
            Equal($"{option} must be a positive integer.", exception.Message);
        }
    }
}

Console.WriteLine("PASS: diagnostic captures default off, explicit resolved directory, interval independence, option order, and positive-integer validation");

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}
