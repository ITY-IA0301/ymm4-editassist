using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace EditAssist.Versioning;

public sealed record VersionPackage(string Path, Version Version, int RuntimeMajor, string Sha256)
{
    public string Display => $"{Version.ToString(3)} / .NET {RuntimeMajor}";
}

public static class VersionPackages
{
    public const long MaximumPackageBytes = 32 * 1024 * 1024;
    public static readonly string[] Assemblies = ["YMM4.EditAssist.dll", "EditAssist.Core.dll"];

    public static VersionPackage Inspect(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaximumPackageBytes) throw new InvalidDataException("パッケージが大きすぎます。");
        var hash = Convert.ToHexString(SHA256.HashData(file)); file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        ValidateEntries(zip);
        var plugin = ReadAssembly(ReadEntry(zip, Assemblies[0]));
        var core = ReadAssembly(ReadEntry(zip, Assemblies[1]));
        if (plugin.Name != "YMM4.EditAssist" || core.Name != "EditAssist.Core" || plugin.Version != core.Version ||
            plugin.Runtime != core.Runtime || plugin.Runtime is < 8 or > 10)
            throw new InvalidDataException("EditAssistのDLL名・版・実行環境が一致しません。");
        return new(System.IO.Path.GetFullPath(path), plugin.Version, plugin.Runtime, hash);
    }

    public static void Extract(VersionPackage package, string destination)
    {
        // Validate the SAME open bytes that are extracted; a replaced package must not bypass inspection.
        using var file = File.OpenRead(package.Path);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("選択後にパッケージが変更されました。読み込み直してください。");
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read); ValidateEntries(zip);
        if (Directory.Exists(destination)) throw new IOException("切替準備フォルダが既に存在します。");
        Directory.CreateDirectory(destination);
        try
        {
            foreach (var entry in zip.Entries.Where(x => !x.FullName.EndsWith('/')))
            {
                var target = System.IO.Path.Combine(destination, entry.FullName.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                using var output = new FileStream(target, FileMode.CreateNew);
                using var input = entry.Open(); input.CopyTo(output);
            }
        }
        catch { Directory.Delete(destination, true); throw; }
    }

    public static string FindInstallation(string hostDirectory)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetFullPath(hostDirectory), "user", "plugin");
        if (!Directory.Exists(root)) return System.IO.Path.Combine(root, "EditAssist");
        var copies = Directory.GetFiles(root, Assemblies[0], SearchOption.AllDirectories);
        if (copies.Length > 1) throw new InvalidDataException("EditAssistが複数の場所にあります。重複を解消してから切り替えてください。");
        var folder = copies.Length == 0 ? System.IO.Path.Combine(root, "EditAssist") : System.IO.Path.GetDirectoryName(copies[0])!;
        if (folder.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EditAssistを専用のプラグインフォルダへ移してから切り替えてください。");
        if (Directory.Exists(folder))
        {
            if (Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Any(x => File.GetAttributes(x).HasFlag(FileAttributes.ReparsePoint)))
                throw new InvalidDataException("リンクを含む導入フォルダには切替を適用できません。");
            var other = Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories)
                .Where(x => !Assemblies.Contains(System.IO.Path.GetFileName(x), StringComparer.OrdinalIgnoreCase));
            if (other.Any()) throw new InvalidDataException("他のプラグインと同じフォルダにあります。専用フォルダへ移してください。");
        }
        return folder;
    }

    public static (string Name, Version Version, int Runtime) ReadAssembly(byte[] bytes)
    {
        using var pe = new PEReader(new MemoryStream(bytes));
        if (!pe.HasMetadata) throw new InvalidDataException(".NETのDLLではありません。");
        var metadata = pe.GetMetadataReader(); var assembly = metadata.GetAssemblyDefinition();
        var runtime = 0;
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attr = metadata.GetCustomAttribute(handle);
            if (attr.Constructor.Kind != HandleKind.MemberReference) continue;
            var parent = metadata.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
            if (metadata.GetString(type.Namespace) != "System.Runtime.Versioning" || metadata.GetString(type.Name) != "TargetFrameworkAttribute") continue;
            var blob = metadata.GetBlobReader(attr.Value);
            if (blob.ReadUInt16() != 1) throw new InvalidDataException("実行環境情報が不正です。");
            var tfm = blob.ReadSerializedString() ?? "";
            const string prefix = ".NETCoreApp,Version=v";
            if (tfm.StartsWith(prefix) && Version.TryParse(tfm[prefix.Length..], out var parsed)) runtime = parsed.Major;
        }
        return (metadata.GetString(assembly.Name), assembly.Version, runtime);
    }

    private static byte[] ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.Entries.SingleOrDefault(x => x.FullName == name) ?? throw new InvalidDataException($"{name}がありません。");
        using var output = new MemoryStream(); using var input = entry.Open(); input.CopyTo(output); return output.ToArray();
    }
    private static void ValidateEntries(ZipArchive zip)
    {
        if (zip.Entries.Count > 100) throw new InvalidDataException("ファイル数が多すぎます。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') ||
                name.TrimEnd('/').Split('/').Any(x => x is "" or "." or "..") || !names.Add(name.TrimEnd('/')))
                throw new InvalidDataException("パッケージ内のパスが不正または重複しています。");
            total = checked(total + entry.Length);
            if (total > MaximumPackageBytes) throw new InvalidDataException("展開後のファイルが大きすぎます。");
            if (name.EndsWith('/')) { if (name != "docs/") throw new InvalidDataException("許可されていないフォルダです。"); continue; }
            if (!Assemblies.Contains(name) && name != "README.md" && name != "NOTICES.md" && name != "CHANGELOG.md" &&
                !(name.StartsWith("docs/") && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"許可されていないファイルです：{name}");
        }
    }
}
