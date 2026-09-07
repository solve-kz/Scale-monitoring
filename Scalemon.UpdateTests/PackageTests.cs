using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Scalemon.Common.Updates;
using Scalemon.Updater;

namespace Scalemon.UpdateTests;

public sealed class PackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Scalemon.PackageTests-" + Guid.NewGuid().ToString("N"));
    public PackageTests() => Directory.CreateDirectory(_root);
    private static ReleaseManifest Manifest => new() { Version = "1.0.1", GitSha = new string('a', 40),
        Archive = "Scalemon-1.0.1-win-x64.zip", Size = 100, ExpandedSize = 10000, Sha256 = new string('b', 64), BackwardCompatible = true };
    [Fact]
    public void SignatureRejectsChangedManifestAndDifferentKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Manifest, AtomicJson.Options);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.Equal("1.0.1", SignedPackages.VerifyManifest(bytes, signature, key.ExportSubjectPublicKeyInfoPem()).Version);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => SignedPackages.VerifyManifest(bytes, signature, other.ExportSubjectPublicKeyInfoPem()));
        bytes[^2] ^= 1;
        Assert.Throws<InvalidDataException>(() => SignedPackages.VerifyManifest(bytes, signature, key.ExportSubjectPublicKeyInfoPem()));
    }
    [Theory]
    [InlineData("../escape.dll")] [InlineData("C:/escape.dll")] [InlineData("/escape.dll")]
    [InlineData("dir/../../escape.dll")] [InlineData("safe.dll:stream")]
    public void ArchiveCannotEscapeCandidateDirectory(string entry)
    {
        var archive = Path.Combine(_root, "bad.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry(entry).Open()); writer.Write("payload"); }
        Assert.Throws<InvalidDataException>(() => SignedPackages.Extract(archive, Path.Combine(_root, "candidate"), Manifest));
        Assert.False(File.Exists(Path.Combine(_root, "escape.dll")));
    }
    [Fact]
    public void RollbackChecksDllsAndNotOnlyExecutable()
    {
        File.WriteAllText(Path.Combine(_root, "Scalemon.ServiceHost.exe"), "exe");
        File.WriteAllText(Path.Combine(_root, "dependency.dll"), "dll");
        VersionInventory.Create(_root); VersionInventory.Verify(_root);
        File.Delete(Path.Combine(_root, "dependency.dll"));
        Assert.Throws<InvalidDataException>(() => VersionInventory.Verify(_root));
    }
    [Fact]
    public void AtomicJournalRoundTripPreservesRecoveryTarget()
    {
        var path = Path.Combine(_root, "operation.json");
        var operation = new UpdateOperation { FromVersion = "1.0.0", ToVersion = "1.0.1", Stage = UpdateStage.Switching, OriginalImagePath = "old.exe" };
        AtomicJson.Write(path, operation);
        Assert.Equal(operation, AtomicJson.Read<UpdateOperation>(path));
    }
    public void Dispose()
    {
        var path = Path.GetFullPath(_root);
        if (path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).StartsWith("Scalemon.PackageTests-")) Directory.Delete(path, true);
    }
}
