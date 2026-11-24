using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using TabTty.App;
using TabTty.Core;

internal static partial class Program
{
    private static async Task ProfileLogCollisionAsync(App app, string directory)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var profile = ConnectionProfile.CreateTelnet("Collision test", "127.0.0.1", port, recordRaw: true);
        var json = JsonNode.Parse(profile.ToEditorJson())!.AsObject();
        var logging = json["logging"]!.AsObject();
        logging.Remove("collision");
        logging["allowOverwrite"] = true;
        if (profile.Edit(json.ToJsonString()).LogCollision != LogCollisionPolicy.Error)
            throw new InvalidOperationException("The former Boolean granted an overwrite.");
        foreach (JsonNode? invalid in new JsonNode?[] { null, true, 1, "Overwrite", " append", "", "unknown" })
        {
            logging["collision"] = invalid;
            try
            {
                profile.Edit(json.ToJsonString());
                throw new InvalidOperationException("An invalid recording collision policy was accepted.");
            }
            catch (InvalidDataException) { }
        }

        string originalLanguage = app.LanguageChoice;
        try
        {
            foreach (string language in new[] { "en", "ja" })
            {
                await SetLanguage(language);
                var form = new FormView();
                var editor = new ProfileEditor(form, profile, app);
                var selector = (ComboBox)form.FindName("LogCollision");
                var container = (Panel)LogicalTreeHelper.GetParent(selector);
                var label = container.Children.OfType<Label>().Single(item => item.Target == selector);
                string heading = language == "en" ? "If a log file already exists" : "同名のログがある場合";
                string[] labels = language == "en" ? ["Error", "Overwrite", "Append"] : ["エラー", "上書き", "追記"];
                if (((TextBlock)label.Content).Text != heading ||
                    !selector.Items.Cast<ComboBoxItem>().Select(item => (string)item.Content).SequenceEqual(labels))
                    throw new InvalidOperationException("The recording policy choices are incorrectly localized.");

                foreach (string value in new[] { "error", "overwrite", "append" })
                {
                    string stem = language + "-" + value;
                    string path = Path.Combine(directory, stem + ".ttty");
                    string rawLog = Path.Combine(directory, stem + ".raw.tttylog");
                    ((TextBox)form.FindName("LogDirectory")).Text = directory;
                    ((TextBox)form.FindName("LogFilenameTemplate")).Text = stem;
                    selector.SelectedValue = value;
                    await editor.GetDraft().SaveNewAsync(path);
                    var saved = await ConnectionProfile.LoadAsync(path);
                    if (JsonNode.Parse(await File.ReadAllTextAsync(path))!["logging"]!["collision"]!.GetValue<string>() != value)
                        throw new InvalidOperationException("Unexpected saved collision policy.");
                    var reopened = new FormView();
                    _ = new ProfileEditor(reopened, saved, app);
                    if ((string)((ComboBox)reopened.FindName("LogCollision")).SelectedValue != value)
                        throw new InvalidOperationException("The reopened editor lost the recording policy.");

                    if (value != "error") await File.WriteAllBytesAsync(rawLog, [0xff]);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await using (var session = await TerminalSession.ConnectAsync(path, deadline.Token))
                    using (var peer = await listener.AcceptTcpClientAsync(deadline.Token))
                        await session.ProcessPayloadAsync(TerminalReceiveSource.Data, new byte[] { 0x41 }, deadline.Token);
                    byte[] expected = value == "append" ? [0xff, 0x41] : [0x41];
                    if (!(await File.ReadAllBytesAsync(rawLog)).SequenceEqual(expected))
                        throw new InvalidOperationException("The saved profile policy did not reach the recording.");
                }
            }
        }
        finally { await SetLanguage(originalLanguage); }
        Console.WriteLine("PASS ProfileLogCollisionSaveReopenConnectEnglishAndJapanese");

        Task SetLanguage(string language) => app.SaveSettingsAsync(app.SettingsRevision, new(Language: language));
    }
}
