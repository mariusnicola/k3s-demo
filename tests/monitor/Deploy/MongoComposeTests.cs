using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>What docs/architecture/store.md holds for the Mongo container, read from the files that start it.</summary>
public class MongoComposeTests
{
    private static string Root([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    private static string File(params string[] parts) => Path.Combine([Root(), "deploy", .. parts]);

    private static string[] Compose => System.IO.File.ReadAllLines(File("mongo", "docker-compose.yaml"));

    private static string[] Ports() => Compose
        .SkipWhile(line => line.Trim() != "ports:").Skip(1)
        .TakeWhile(line => line.TrimStart().StartsWith("- "))
        .Select(line => line.Trim()[2..])
        .ToArray();

    [Fact]
    public void The_cap_entry_holds_the_decided_limits()
    {
        using var caps = JsonDocument.Parse(System.IO.File.ReadAllText(File("caps.json")));

        var entry = caps.RootElement.GetProperty("containers").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "batchtracker-mongo");

        Assert.Equal("mongo", entry.GetProperty("compose").GetString());
        Assert.Equal("CAP_MONGO", entry.GetProperty("env").GetString());
        Assert.Equal(1000, entry.GetProperty("cpuMilli").GetInt32());
        Assert.Equal(1024, entry.GetProperty("memoryMi").GetInt32());
    }

    [Fact]
    public void The_container_takes_its_limits_from_the_cap_and_its_cache_is_set()
    {
        var text = string.Join("\n", Compose);

        Assert.Contains("container_name: batchtracker-mongo", text);
        Assert.Contains("mem_limit: ${CAP_MONGO_MEMORY:?", text);
        Assert.Contains("cpus: ${CAP_MONGO_CPU:?", text);
        Assert.Contains("""command: ["--wiredTigerCacheSizeGB", "0.25"]""", text);
        Assert.Contains("mongo-data:/data/db", text);
    }

    [Fact]
    public void The_image_is_one_exact_8_0_patch()
    {
        Assert.Single(Compose, line => Regex.IsMatch(line, @"^\s+image: mongo:8\.0\.\d+$"));
    }

    [Fact]
    public void Port_27018_binds_the_two_addresses_and_nothing_else()
    {
        Assert.Equal(["127.0.0.1:27018:27017", "<HOST_IP>:27018:27017"], Ports());
        Assert.DoesNotContain(Compose, line => line.Contains("0.0.0.0"));
    }

    [Fact]
    public void The_password_comes_from_the_environment_and_the_example_holds_names_only()
    {
        Assert.Contains(Compose, line => line.Contains("MONGO_INITDB_ROOT_PASSWORD: ${MONGO_PASSWORD:?"));

        var example = System.IO.File.ReadAllLines(File("mongo", ".env.example")).Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();

        Assert.Contains("MONGO_PASSWORD=", example);
        Assert.All(example, line => Assert.Matches("^[A-Z_]+=$", line));
    }
}
