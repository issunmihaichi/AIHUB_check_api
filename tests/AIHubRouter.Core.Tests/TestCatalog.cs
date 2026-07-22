namespace AIHubRouter.Core.Tests;

internal readonly record struct TestCase(string Name, Action Body);

internal static class TestCatalog
{
    public static IReadOnlyList<TestCase> Create(params TestCase[] tests)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var test in tests)
        {
            if (!names.Add(test.Name))
            {
                throw new InvalidOperationException($"Duplicate test name: {test.Name}.");
            }
        }

        return Array.AsReadOnly(tests);
    }
}
