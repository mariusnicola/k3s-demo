public static class Settings
{
    public static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name} not set") : value;
    }
}
