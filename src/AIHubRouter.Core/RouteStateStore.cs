using System.Text.Json;

namespace AIHubRouter.Core;

public interface IRouteStateStore
{
    RouteState Load();
    void Save(RouteState state);
}

public sealed class JsonRouteStateStore(string storageDirectory) : IRouteStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path = Path.Combine(storageDirectory, "route-state.json");

    public RouteState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new RouteState();
            }

            return JsonSerializer.Deserialize<RouteState>(File.ReadAllText(_path), JsonOptions) ?? new RouteState();
        }
        catch (IOException)
        {
            return new RouteState();
        }
        catch (UnauthorizedAccessException)
        {
            return new RouteState();
        }
        catch (JsonException)
        {
            return new RouteState();
        }
    }

    public void Save(RouteState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, _path, overwrite: true);
    }
}
