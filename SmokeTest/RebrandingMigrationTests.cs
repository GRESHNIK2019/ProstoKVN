using System;
using System.IO;
using ProstoKVN;

public static class RebrandingMigrationTests
{
    public static void Run()
    {
        string test = Path.Combine(Path.GetTempPath(), "ProstoKVN-migrate-" + Guid.NewGuid().ToString("N"));
        string legacy=Path.Combine(test,"old");
        string modern=Path.Combine(test,"modern");
        try
        {
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(modern);
            Directory.CreateDirectory(Path.Combine(legacy, "hot-rules"));
            File.WriteAllText(Path.Combine(legacy,"settings.json"),"""{"Theme":"dark","SelectedNode":"Germany"}""");
            File.WriteAllText(Path.Combine(legacy,"routes.json"),"""[{"Domain":"discord.com"}]""");
            File.WriteAllText(Path.Combine(legacy,"subscription-cache.json"),"protected-cache");
            File.WriteAllText(Path.Combine(legacy,"hot-rules","manual.yaml"),"payload: []");
            UserStore.ImportLegacyInto(legacy,modern);
            Assert(File.Exists(Path.Combine(modern,"settings.json")),"Saved settings copied to new name");
            Assert(File.Exists(Path.Combine(modern,"routes.json")),"Routing rules preserved");
            Assert(File.Exists(Path.Combine(modern,"subscription-cache.json")),"DPAPI cache bytes preserved");
            Assert(File.Exists(Path.Combine(modern,"hot-rules","manual.yaml")),"Hot rule provider preserved");
            Assert(File.Exists(Path.Combine(legacy,"settings.json")),"Legacy folder was NOT deleted");
            File.WriteAllText(Path.Combine(modern,"settings.json"),"modified-newer");
            UserStore.ImportLegacyInto(legacy,modern);
            Assert(File.ReadAllText(Path.Combine(modern,"settings.json"))=="modified-newer",
                "Re-run never overwrites newer saved preferences");
            var assembly=typeof(UserStore).Assembly.GetName().Name;
            Assert(assembly=="ProstoKVN", "Renamed assembly has no legacy project name");
            Console.WriteLine("ALL_REBRAND_MIGRATION_TESTS_PASS");
        }
        finally { Directory.Delete(test,true); }
    }
    static void Assert(bool value,string label)
    {
        if(!value) throw new Exception("REBRAND_MIGRATION_TEST_FAILED: "+label);
        Console.WriteLine("PASS "+label);
    }
}
