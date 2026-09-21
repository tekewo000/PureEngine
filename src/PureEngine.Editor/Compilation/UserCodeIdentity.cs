using System.Text.Json;
using PureEngine.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PureEngine.Editor;

/// <summary>Persistent component identity, independent of its current C# name.</summary>
internal static class UserCodeIdentity
{
    private sealed record Entry(string Id, string Name, string[] Files);
    private sealed record Catalog(int Version, List<Entry> Types);

    public static void Resolve(string root, UserCodeCompileResult result)
    {
        var path = Path.Combine(root, ".pureengine", "types.json");
        var catalog = File.Exists(path)
            ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path))
            : new Catalog(1, []);
        if (catalog is null || catalog.Version != 1 || catalog.Types is null
            || catalog.Types.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id)
                || !e.Id.StartsWith("user.", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(e.Name) || e.Files is null)
            || catalog.Types.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Types.Count)
            throw new InvalidDataException("C#の型ID管理ファイルが不正です。既存のIDを保護するため反映を中止します。");

        var types = result.AttachableTypes;
        var files = types.ToDictionary(type => type, type => result.FileTypes
            .Where(pair => pair.Value.Contains(type))
            .Select(pair => Path.GetRelativePath(root, pair.Key).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray());
        var assigned = new Dictionary<Type, Entry>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Assign(Type type, Entry entry)
        {
            assigned.Add(type, entry);
            used.Add(entry.Id);
        }
        bool SharesFile(Type type, Entry entry) => files[type].Intersect(entry.Files, StringComparer.OrdinalIgnoreCase).Any();
        static string ShortName(string name) => name[(Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+')) + 1)..];

        // Seed legacy scene IDs when upgrading projects created before identity tracking.
        if (!File.Exists(path))
        {
            var scenes = Path.Combine(root, "Scenes");
            var reader = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
            if (Directory.Exists(scenes))
            foreach (var scene in Directory.EnumerateFiles(scenes, "*.pure.scene.yaml", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            {
                var document = reader.Deserialize<SceneDocument>(File.ReadAllText(scene));
                foreach (var id in (document?.Objects ?? []).SelectMany(item => item.Components ?? [])
                    .Select(component => component.TypeId).OfType<string>().Where(id => id.StartsWith("user.", StringComparison.Ordinal)))
                    if (catalog.Types.All(entry => entry.Id != id)) catalog.Types.Add(new Entry(id, id[5..], []));
            }
        }

        // Reserve exact names first so a new class cannot steal a surviving class's ID.
        foreach (var type in types)
        {
            var matches = catalog.Types.Where(entry => entry.Name == type.FullName).ToArray();
            if (matches.Length == 1) Assign(type, matches[0]);
            else if (matches.Length > 1) throw new InvalidDataException($"型IDが曖昧です: {type.FullName}");
        }
        foreach (var type in types.Where(type => !assigned.ContainsKey(type)))
        {
            var matches = catalog.Types.Where(entry => !used.Contains(entry.Id)
                && ShortName(entry.Name) == type.Name && (SharesFile(type, entry) || entry.Files.Length == 0)).ToArray();
            if (matches.Length == 1 && types.Count(other => !assigned.ContainsKey(other)
                && other.Name == type.Name && (SharesFile(other, matches[0]) || matches[0].Files.Length == 0)) == 1)
                Assign(type, matches[0]);
        }
        foreach (var type in types.Where(type => !assigned.ContainsKey(type)))
        {
            var matches = catalog.Types.Where(entry => !used.Contains(entry.Id) && SharesFile(type, entry)).ToArray();
            if (matches.Length == 1 && types.Count(other => !assigned.ContainsKey(other) && SharesFile(other, matches[0])) == 1)
                Assign(type, matches[0]);
            else if (matches.Length > 0)
                throw new InvalidDataException($"{type.FullName}: 同じファイル内の複数クラスが変わり、改名前の型を特定できません。一つずつ改名して保存してください。");
        }
        foreach (var type in types)
        {
            var entry = assigned.GetValueOrDefault(type);
            if (entry is null)
            {
                var id = UserCodeCompiler.TypeIdFor(type);
                if (catalog.Types.Any(old => old.Id == id)) id = "user." + Guid.NewGuid().ToString("N");
                entry = new Entry(id, type.FullName!, files[type]);
                catalog.Types.Add(entry);
            }
            var updated = entry with { Name = type.FullName!, Files = files[type] };
            catalog.Types[catalog.Types.IndexOf(entry)] = updated;
            result.TypeIds[type] = updated.Id;
        }
        result.IdentityPath = path;
        result.IdentityContent = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
    }

    public static void Save(UserCodeCompileResult result)
    {
        if (result.IdentityPath is not { } path || result.IdentityContent is not { } content) return;
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SceneFile.Write(path, content);
    }
}
