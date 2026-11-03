using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using ProstoKVN;

public static class GitHubUpdateTests
{
    private static void Assert(bool value,string label)
    {
        if(!value) throw new Exception("GITHUB_UPDATE_TEST_FAILED: "+label);
        Console.WriteLine("PASS "+label);
    }

    public static async Task RunAsync()
    {
        string work=Path.Combine(Path.GetTempPath(),"ProstoKVN-updater-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            const string tag="v0.30.0";
            const string zipName="ProstoKVN-v0.30.0-win-x64.zip";
            const string url="https://github.com/GRESHNIK2019/ProstoKVN/releases/download/v0.30.0/"+zipName;
            string testZip=Path.Combine(work,"good.zip");
            using(var zip=ZipFile.Open(testZip,ZipArchiveMode.Create))
            {
                foreach(string name in new []{"ProstoKVN.exe","ProstoKVN.dll",
                    "ProstoKVN.runtimeconfig.json","updater/ProstoKVN.Updater.exe"})
                {
                    var e=zip.CreateEntry(name);
                    using var writer=new StreamWriter(e.Open());
                    writer.Write("dummy test payload "+name);
                }
            }
            string sha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(testZip)));
            string release=JsonSerializer.Serialize(new {
                tag_name=tag,draft=false,prerelease=false,
                html_url="https://github.com/GRESHNIK2019/ProstoKVN/releases/tag/"+tag,
                assets=new [] {new {name=zipName, size=new FileInfo(testZip).Length,
                    browser_download_url=url,digest="sha256:"+sha.ToLowerInvariant()}}
            });
            GitHubUpdateService.TestGetString=(source,_) =>
                Task.FromResult(release);
            GitHubUpdateService.TestDownload=(_,dest,progress,_)=>{
                File.Copy(testZip,dest);
                progress?.Report(100);
                return Task.CompletedTask;
            };
            var found=await GitHubUpdateService.CheckAsync(new Version(0,29,0));
            Assert(found?.Tag==tag && found.Version==new Version(0,30,0)
                && found.Sha256==sha,"Latest stable release, version and SHA-256 parsed");
            Assert(await GitHubUpdateService.CheckAsync(new Version(0,30,0))==null,
                "Already installed release not offered again");
            string stage=await GitHubUpdateService.StageAsync(found!);
            Assert(File.Exists(Path.Combine(stage,"payload","ProstoKVN.exe"))
                && File.Exists(Path.Combine(stage,"payload","updater","ProstoKVN.Updater.exe"))
                && File.Exists(Path.Combine(stage,"hashes.json")),
                "Downloaded package validated and staged outside installation directory");
            var entries=JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string,string>>(
                File.ReadAllText(Path.Combine(stage,"hashes.json")))!;
            Assert(entries.Count==4 && entries.Keys.Contains("ProstoKVN.exe"),
                "Stage per-file hashes recorded");

            var bad=new GitHubUpdate(found!.Version,found.Tag,found.ZipUrl,
                new string('0',64),found.DownloadSize,found.ReleaseUrl);
            bool rejected=false;
            try { await GitHubUpdateService.StageAsync(bad); }
            catch(InvalidOperationException){rejected=true;}
            Assert(rejected,"Checksum mismatch rejects installation");

            string traversal=Path.Combine(work,"traversal.zip");
            using(var zip=ZipFile.Open(traversal,ZipArchiveMode.Create))
            {
                var entry=zip.CreateEntry("../outside.txt");
                using var writer=new StreamWriter(entry.Open());
                writer.Write("malicious archive");
            }
            rejected=false;
            try { GitHubUpdateService.ExtractValidated(traversal,
                Path.Combine(work,"extract")); }
            catch(InvalidOperationException){rejected=true;}
            Assert(rejected && !File.Exists(Path.Combine(work,"outside.txt")),
                "ZIP path traversal rejected");
            GitHubUpdateService.TestGetString=(source,_) =>
                Task.FromResult(release.Replace("https://github.com/GRESHNIK2019/ProstoKVN/",
                    "https://evil.example/GRESHNIK2019/ProstoKVN/"));
            rejected=false;
            try { await GitHubUpdateService.CheckAsync(new Version(0,29,0)); }
            catch(InvalidOperationException){rejected=true;}
            Assert(rejected,"Non-GitHub release asset rejected");
            GitHubUpdateService.TestGetString=(source,_) =>
                Task.FromResult(release.Replace("\"prerelease\":false","\"prerelease\":true"));
            Assert(await GitHubUpdateService.CheckAsync(new Version(0,29,0))==null,
                "Prereleases ignored");
            string? real = Environment.GetEnvironmentVariable("PROSTOKVN_RELEASE_VALIDATION_ZIP");
            if (!string.IsNullOrWhiteSpace(real) && File.Exists(real))
            {
                string checksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(real)));
                var prod = new GitHubUpdate(new Version(0,29,2), "v0.29.2",
                    "https://github.com/GRESHNIK2019/ProstoKVN/releases/download/v0.29.2/ProstoKVN-v0.29.2-win-x64.zip",
                    checksum, new FileInfo(real).Length, "");
                GitHubUpdateService.TestDownload = (_, dest, _, _) =>
                {
                    File.Copy(real, dest);
                    return Task.CompletedTask;
                };
                string prodStage = await GitHubUpdateService.StageAsync(prod);
                Assert(File.Exists(Path.Combine(prodStage, "payload", "ProstoKVN.exe"))
                    && File.Exists(Path.Combine(prodStage, "payload", "updater", "ProstoKVN.Updater.exe"))
                    && File.Exists(Path.Combine(prodStage, "payload", "updater", "ProstoKVN.Updater.runtimeconfig.json")),
                    "Real release archive passes secure staging and includes updater runtime");
                var manifest = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(
                    File.ReadAllText(Path.Combine(prodStage, "hashes.json")))!;
                Assert(manifest.Count > 8, "Real release has per-file SHA-256 manifest");
            }
            Console.WriteLine("ALL_GITHUB_UPDATE_TESTS_PASS");
        }
        finally
        {
            GitHubUpdateService.TestGetString=null;
            GitHubUpdateService.TestDownload=null;
            Directory.Delete(work,recursive:true);
        }
    }
}