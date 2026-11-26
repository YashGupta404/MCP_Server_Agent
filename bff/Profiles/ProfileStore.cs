using System.Text.Json;

// File-backed integration-profile store (data/profiles.json under the BFF content root).
sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();

    public ProfileStore(string contentRoot)
    {
        _path = Path.Combine(contentRoot, "data", "profiles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public List<IntegrationProfile> All()
    {
        lock (_lock) return Load();
    }

    public IntegrationProfile? Get(string id)
    {
        lock (_lock) return Load().FirstOrDefault(p => p.Id == id);
    }

    public void Upsert(IntegrationProfile profile)
    {
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(p => p.Id == profile.Id);
            list.Add(profile);
            Save(list);
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var list = Load();
            var removed = list.RemoveAll(p => p.Id == id) > 0;
            if (removed) Save(list);
            return removed;
        }
    }

    private List<IntegrationProfile> Load()
    {
        if (!File.Exists(_path)) return new();
        var text = File.ReadAllText(_path);
        return string.IsNullOrWhiteSpace(text) ? new() : JsonSerializer.Deserialize<List<IntegrationProfile>>(text, Json) ?? new();
    }

    private void Save(List<IntegrationProfile> list)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
