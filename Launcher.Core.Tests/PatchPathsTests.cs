namespace Launcher.Core.Tests;

public sealed class PatchPathsTests
{
    [Theory]
    [InlineData("Zircon.exe")]
    [InlineData("Data\\System.db")]
    [InlineData("Data/System.db")]
    [InlineData("Map Data\\00\\map01.dat")]
    public void Ordinary_manifest_paths_are_safe(string fileName)
    {
        Assert.True(PatchPaths.IsSafeRelativePath(fileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("..\\escape.exe")]
    [InlineData("..")]
    [InlineData("Data\\..\\escape.exe")]
    [InlineData("Data\\..")]
    [InlineData("/etc/passwd")]
    [InlineData("\\server\\share\\file.dll")]
    [InlineData("C:\\Windows\\system32.dll")]
    [InlineData("C:file.dll")]
    [InlineData("Data\\\\double.exe")]
    [InlineData("Data//double.exe")]
    [InlineData("Data\\\\")]
    [InlineData("trailing\\")]
    [InlineData("trailing/")]
    [InlineData(".\\Zircon.exe")]
    [InlineData("wild?card.exe")]
    [InlineData("wild*card.exe")]
    [InlineData("bad|name.exe")]
    [InlineData("Data\\trailing. ")]
    [InlineData("CON")]
    [InlineData("CON.txt")]
    [InlineData("LPT1.txt")]
    [InlineData("Data\\name<bad>.dll")]
    [InlineData("bad\0name.exe")]
    public void Unsafe_manifest_paths_are_rejected(string? fileName)
    {
        Assert.False(PatchPaths.IsSafeRelativePath(fileName!));
    }

    [Fact]
    public void Web_file_name_flattens_directories_and_appends_gz()
    {
        Assert.Equal("Zircon.exe.gz", PatchPaths.ToWebFileName("Zircon.exe"));
        Assert.Equal("Data-System.db.gz", PatchPaths.ToWebFileName("Data\\System.db"));
        Assert.Equal("Data-System.db.gz", PatchPaths.ToWebFileName("Data/System.db"));
    }

    [Fact]
    public void Web_file_name_rejects_unsafe_input()
    {
        Assert.Throws<ArgumentException>(() => PatchPaths.ToWebFileName("..\\evil.exe"));
    }

    [Fact]
    public void User_owned_state_is_identified_for_install_only_updates()
    {
        Assert.True(PatchPaths.IsLocalState("Zircon.ini"));
        Assert.True(PatchPaths.IsLocalState("Data\\Users.db"));
        Assert.False(PatchPaths.IsLocalState("Data\\System.db"));
        Assert.False(PatchPaths.IsLocalState("Launcher.exe"));
    }

    [Fact]
    public void Resolve_client_path_rejects_symlinked_directory_inside_root()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mir3-patchpaths-{Guid.NewGuid():N}");
        string outside = Path.Combine(Path.GetTempPath(), $"mir3-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "Data"), outside);
            Assert.Throws<ArgumentException>(() => PatchPaths.ResolveClientPath(root, "Data\\System.db"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Resolve_client_path_stays_inside_the_root()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mir3-patchpaths-{Guid.NewGuid():N}");

        try
        {
            string resolved = PatchPaths.ResolveClientPath(root, "Data\\System.db");

            Assert.StartsWith(Path.GetFullPath(root), resolved, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
