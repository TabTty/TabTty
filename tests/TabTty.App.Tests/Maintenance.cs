using System.IO;
using System.IO.Compression;
using System.Text;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static void CheckMaintenancePersistence(App app, string directory)
    {
        string archive = Path.Combine(directory, "maintenance.zip");
        var settings = app.SettingsRecoveryStore().Defaults();
        var profile = Encoding.UTF8.GetBytes(ConnectionProfile.CreateTelnet("Fixture", "localhost").ToEditorJson());
        SettingsArchive.Save(archive, new([
            new(SettingsArchive.Item.Settings, null, settings),
            new(SettingsArchive.Item.Profiles, "fixture.ttty", profile),
        ], 0));
        var image = SettingsArchive.Read(archive);
        if (!image.Store(SettingsArchive.Item.Settings)!.Bytes.AsSpan().SequenceEqual(settings) ||
            image.Contents.Length != 2 || image.Profiles.Single().Name != "fixture.ttty" ||
            !image.Profiles.Single().Bytes.AsSpan().SequenceEqual(profile))
            throw new InvalidOperationException("The maintenance archive did not preserve its saved data.");
        using (var tampered = ZipFile.Open(archive, ZipArchiveMode.Update)) tampered.CreateEntry("state.json");
        try
        {
            _ = SettingsArchive.Read(archive);
            throw new InvalidOperationException("The maintenance archive accepted an unexpected file.");
        }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS MaintenanceArchiveRejectsUnexpectedFiles");

        string path = Path.Combine(directory, "maintenance-protection.dat");
        var trust = new ProtectedTrustFile(path, "test-profile-trust");
        byte[] payload = "{}"u8.ToArray();
        trust.Save(trust.Encode(payload), null, stage => _ = trust.Read(out _, stage));
        if (!trust.Read(out var original)!.AsSpan().SequenceEqual(payload) || original!.AsSpan().SequenceEqual(payload))
            throw new InvalidOperationException("The protected trust file did not round-trip as protected data.");
        try
        {
            _ = new ProtectedTrustFile(path, "test-macro-trust").Read(out _);
            throw new InvalidOperationException("A protected trust file could be substituted for another category.");
        }
        catch (IOException) { }
        trust.Save(trust.Encode("{\"changed\":true}"u8.ToArray()), original, stage => _ = trust.Read(out _, stage));
        if (Directory.EnumerateFiles(directory, "maintenance-protection.dat.previous-*").Any())
            throw new InvalidOperationException("Trust retained a previous generation.");
        Console.WriteLine("PASS MaintenanceTrustProtectionKeepsCategoriesSeparate");
    }
}
