using System.Security.Cryptography;
using Avion2.Engine;

namespace Avion2.Tests;

public class UpdateCheckerTests
{
    private const string Release = """
        {
          "tag_name": "v0.3.1", "html_url": "https://github.com/void-cx/avion2/releases/tag/v0.3.1",
          "draft": false, "prerelease": false, "body": "Fixes",
          "assets": [
            { "name": "AVION2-v0.3.1-win-x64.zip", "size": 70000000,
              "browser_download_url": "https://github.com/void-cx/avion2/releases/download/v0.3.1/AVION2-v0.3.1-win-x64.zip" },
            { "name": "SHA256SUMS.txt", "size": 93,
              "browser_download_url": "https://github.com/void-cx/avion2/releases/download/v0.3.1/SHA256SUMS.txt" }
          ]
        }
        """;

    [Fact]
    public void ReadsTheReleaseAndItsAssets()
    {
        var release = UpdateChecker.FromJson(Release)!;
        Assert.Equal(new Version(0, 3, 1), release.Version);
        Assert.EndsWith("AVION2-v0.3.1-win-x64.zip", release.ZipUrl);
        Assert.EndsWith("SHA256SUMS.txt", release.ChecksumsUrl);
        Assert.Equal(70_000_000, release.ZipSize);
    }

    [Fact]
    public void IgnoresPreReleasesAndReleasesWithoutTheZip()
    {
        Assert.Null(UpdateChecker.FromJson(Release.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.Null(UpdateChecker.FromJson(Release.Replace("AVION2-v0.3.1-win-x64.zip", "source.zip")));
    }

    [Theory]
    [InlineData("0.3.0", true)]
    [InlineData("0.3.1", false)]
    [InlineData("1.0.0", false)]
    [InlineData("0.0.0-dev", false)] // dev builds never update
    public void OffersOnlyNewerReleasesToReleaseBuilds(string current, bool offered) =>
        Assert.Equal(offered, UpdateChecker.IsNewer(UpdateChecker.FromJson(Release)!, current));
}

public class UpdaterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("avion2-updater").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string relative, string content)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void VerifiesTheZipAgainstSha256Sums()
    {
        string zip = Write("AVION2-v1.0.0-win-x64.zip", "zip bytes");
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();
        string sums = Write("SHA256SUMS.txt", $"{hash}  AVION2-v1.0.0-win-x64.zip");
        Avion2.Updater.Program.Verify(zip, "AVION2-v1.0.0-win-x64.zip", sums);

        File.WriteAllText(zip, "tampered");
        Assert.Throws<InvalidOperationException>(() => Avion2.Updater.Program.Verify(zip, "AVION2-v1.0.0-win-x64.zip", sums));
    }

    [Fact]
    public void InstallsNewFilesAndKeepsTheOldOnesAsBackups()
    {
        Write("install/AVION2.exe", "old exe");
        Write("new/AVION2.exe", "new exe");
        Write("new/licenses/NEW.txt", "license");

        Avion2.Updater.Program.Install(Path.Combine(_dir, "new"), Path.Combine(_dir, "install"));

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(_dir, "install/AVION2.exe")));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(_dir, "install/AVION2.exe.old")));
        Assert.True(File.Exists(Path.Combine(_dir, "install/licenses/NEW.txt")));
    }

    [Fact]
    public void RollsEverythingBackWhenAFileCannotBeReplaced()
    {
        Write("install/AVION2.exe", "old exe");
        Directory.CreateDirectory(Path.Combine(_dir, "install/Cinzel-OFL.txt")); // a folder where a file must go: copy fails
        Write("new/AVION2.exe", "new exe");
        Write("new/Cinzel-OFL.txt", "font license");

        Assert.ThrowsAny<IOException>(() => Avion2.Updater.Program.Install(Path.Combine(_dir, "new"), Path.Combine(_dir, "install")));

        Assert.Equal("old exe", File.ReadAllText(Path.Combine(_dir, "install/AVION2.exe")));
        Assert.False(File.Exists(Path.Combine(_dir, "install/AVION2.exe.old")));
    }

    [Fact]
    public void FindsAvion2InsideAWrappedZip()
    {
        Write("staged/AVION2/AVION2.exe", "exe");
        Assert.Equal(Path.Combine(_dir, "staged", "AVION2"), Avion2.Updater.Program.FindRoot(Path.Combine(_dir, "staged")));
    }
}
