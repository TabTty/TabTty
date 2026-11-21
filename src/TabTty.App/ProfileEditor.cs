using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class ProfileEditor
{
    private ConnectionProfile original;
    private readonly JsonObject draft;
    private List<Action> apply = [];
    private readonly FormView form;
    private readonly bool fromExplorer;
    private System.Windows.FrameworkElement names;
    private readonly Dictionary<string, TransportFields> transports = new(StringComparer.Ordinal);
    private readonly JsonObject commonConnection;
    private string selectedProtocol;

    public ProfileEditor(FormView form, ConnectionProfile profile, App app, bool creating = false, bool fromExplorer = false)
    {
        this.form = form;
        this.fromExplorer = fromExplorer;
        names = form;
        System.Windows.NameScope.SetNameScope(form, new System.Windows.NameScope());
        original = profile;
        draft = JsonNode.Parse(profile.ToEditorJson())!.AsObject();
        commonConnection = draft["connection"]!.AsObject();
        var terminal = draft["terminal"]!.AsObject();
        var logging = draft["logging"]!.AsObject();
        selectedProtocol = profile.Type;
        var navigation = form.AddNavigation(("ProfileConnectionGroup", BuildConnection),
            ("ProfileAuthenticationGroup", BuildAuthentication), ("ProfileTerminalGroup", BuildTerminal),
            ("ProfileLoggingGroup", BuildLogging));
        var sshOption = (RadioButton)form.FindName("ProtocolSSH");
        var authenticationMenu = navigation.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "ProfileAuthenticationGroup"));
        authenticationMenu.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(ToggleButton.IsChecked))
        {
            Source = sshOption, Converter = new BooleanToVisibilityConverter(),
        });
        sshOption.Unchecked += (_, _) =>
        {
            if (authenticationMenu.IsSelected) navigation.SelectedIndex = 0;
        };

        void BuildConnection()
        {
            if (creating) draft["displayName"] = "";
            String(draft, "displayName", "Name");
            var groupName = new TextBox { Text = profile.GroupName ?? "", MaxLength = ConnectionProfile.MaximumGroupNameLength * 2 };
            Add("ProfileGroupName", groupName);
            apply.Add(() =>
            {
                string? group = ConnectionProfile.NormalizeGroupName(groupName.Text);
                if (group is null) draft.Remove("groupName");
                else draft["groupName"] = group;
            });
            var choices = new WrapPanel();
            var telnetDescription = form.AddChoicePanel("ConnectionType", choices, "TelnetPrivacyNotice");
            Number(commonConnection, "waitSeconds", "WaitSeconds");
            string[] protocols = ["telnet", "ssh", "serial"];
            string[] protocolLabels = ["Telnet", "SSH", "COM"];
            for (int index = 0; index < protocols.Length; index++)
            {
                string type = protocols[index];
                ConnectionProfile defaults = type == profile.Type ? profile : NewProfile(type);
                JsonObject settings = JsonNode.Parse(defaults.ToEditorJson())!.AsObject();
                var transportConnection = settings["connection"]!.AsObject();
                var transportTerminal = settings["terminal"]!.AsObject();
                var radio = new RadioButton { Content = protocolLabels[index], Margin = new System.Windows.Thickness(0, 0, 18, 6) };
                form.RegisterName("Protocol" + protocolLabels[index], radio);
                choices.Children.Add(radio);
                var transportActions = new List<Action>();
                StackPanel fields = AddTransportSection(type, type + "Fields", transportActions,
                    () => BuildTransport(defaults, transportConnection, transportTerminal, creating || type != profile.Type));
                transports.Add(type, new TransportFields(transportConnection, transportTerminal, transportActions, fields));
                fields.Visibility = type == selectedProtocol ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                radio.IsChecked = type == selectedProtocol;
                form.TrackValue(() => radio.IsChecked, value => radio.IsChecked = value);
                radio.Checked += (_, _) =>
                {
                    transports[selectedProtocol].Panel.Visibility = System.Windows.Visibility.Collapsed;
                    selectedProtocol = type;
                    fields.Visibility = System.Windows.Visibility.Visible;
                };
            }
            var telnetOption = (RadioButton)form.FindName("ProtocolTelnet");
            telnetDescription.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(ToggleButton.IsChecked))
            {
                Source = telnetOption, Converter = new BooleanToVisibilityConverter(),
            });
            telnetOption.SetBinding(AutomationProperties.HelpTextProperty,
                new Binding(nameof(TextBlock.Text)) { Source = telnetDescription });
        }

        void BuildAuthentication()
        {
            TransportFields ssh = transports["ssh"];
            AddTransportSection("ssh", "sshAuthenticationFields", ssh.Apply, () =>
            {
                if (creating || profile.Type != "ssh") ssh.Connection["username"] = "";
                String(ssh.Connection, "username", "SshUsername");
                ConfigureSshAuthentication(ssh.Connection["authentication"]!.AsObject(), app);
            });
        }

        void BuildTerminal()
        {
            var receive = Choice(terminal, "receiveEncoding", "ReceiveEncoding", TerminalEncodings.Identifiers.ToArray());
            var same = Boolean(terminal, "sameEncoding", "SameAsReceive");
            var transmit = Choice(terminal, "transmitEncoding", "TransmitEncoding", TerminalEncodings.Identifiers.ToArray());
            Number(terminal, "ambiguousWidth", "AmbiguousWidth");
            var jis = terminal["jis"]!.AsObject();
            var receiveKana = Choice(jis, "receiveKana", "ReceiveKana", ["eightBit", "soSi"], ["8-bit", "SO/SI"]);
            var transmitKana = Choice(jis, "transmitKana", "TransmitKana", ["eightBit", "soSi"], ["8-bit", "SO/SI"]);
            receive.SelectionChanged += (_, _) => UpdateEncoding();
            transmit.SelectionChanged += (_, _) => UpdateEncoding();
            same.Checked += (_, _) => UpdateEncoding();
            same.Unchecked += (_, _) => UpdateEncoding();
            UpdateEncoding();
            void UpdateEncoding()
            {
                bool separateTransmit = same.IsChecked != true;
                transmit.IsEnabled = separateTransmit;
                transmit.Visibility = separateTransmit ? Visibility.Visible : Visibility.Collapsed;
                string rx = (string)receive.SelectedValue;
                string tx = separateTransmit ? (string)transmit.SelectedValue : rx;
                bool showReceiveKana = rx is "jis" or "cp932";
                receiveKana.IsEnabled = showReceiveKana;
                transmitKana.IsEnabled = tx == "jis";
                receiveKana.Visibility = showReceiveKana ? Visibility.Visible : Visibility.Collapsed;
                transmitKana.Visibility = tx == "jis" ? Visibility.Visible : Visibility.Collapsed;
            }
            InheritedFont(terminal["font"]!.AsObject(), app);
            InheritedRetention(terminal["retainedRows"]!.AsObject(), app);
            var getHighlight = app.Highlights.AddPicker(form, profile.HighlightSetId);
            apply.Add(() => terminal["highlightSetId"] = getHighlight());
        }

        void BuildLogging()
        {
            form.AddCheckBoxGroup("RecordedLogs", () =>
            {
                Boolean(logging, "raw", "RecordRaw");
                Boolean(logging, "text", "RecordText");
                Boolean(logging, "terminal", "RecordTerminal");
            });
            String(logging, "directory", "LogDirectory", "NetworkLogDestinationNotice", optional: true);
            String(logging, "filenameTemplate", "LogFilenameTemplate", "LogFilenameTemplateDescription", optional: true)
                .MaxLength = RecordingNameTemplate.MaximumLength;
            var collision = new ComboBox { SelectedValuePath = nameof(ComboBoxItem.Tag) };
            foreach (var policy in LogCollisionPolicies.All)
            {
                var item = new ComboBoxItem { Tag = LogCollisionPolicies.Format(policy) };
                form.AddLocalizedUpdate(() => item.Content = Text("LogCollision" + policy));
                collision.Items.Add(item);
            }
            collision.SelectedValue = LogCollisionPolicies.Format(profile.LogCollision);
            Add("LogCollision", collision);
            bool hasCollision = logging.ContainsKey("collision");
            apply.Add(() =>
            {
                string selected = (string)collision.SelectedValue;
                if (hasCollision || selected != "error") logging["collision"] = selected;
                else logging.Remove("collision");
            });
        }
        var validation = new TextBox { IsReadOnly = true, TextWrapping = System.Windows.TextWrapping.Wrap,
            Visibility = System.Windows.Visibility.Collapsed, MaxHeight = 120,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        form.AddDisplay("ProfileValidation", validation);
        var timer = new DispatcherTimer(DispatcherPriority.Background, form.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(150) };
        bool updating = false;
        timer.Tick += (_, _) => Validate();
        form.Loaded += (_, _) => Validate();
        form.Unloaded += (_, _) => timer.Stop();
        form.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Schedule()), true);
        form.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => Schedule()), true);
        form.AddHandler(ToggleButton.CheckedEvent, new System.Windows.RoutedEventHandler((_, _) => Schedule()), true);
        form.AddHandler(ToggleButton.UncheckedEvent, new System.Windows.RoutedEventHandler((_, _) => Schedule()), true);

        void Schedule()
        {
            if (updating || !form.IsLoaded) return;
            timer.Stop();
            timer.Start();
        }

        void Validate()
        {
            timer.Stop();
            updating = true;
            try { _ = GetDraft(); validation.Clear(); }
            catch (Exception exception) when (ProfileCatalog.IsFileProblem(exception))
            { validation.Text = Text("InvalidProfileSettings") + "\n" + exception.Message; }
            finally
            {
                validation.Visibility = validation.Text.Length == 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
                updating = false;
            }
        }
    }

    public ConnectionProfile GetDraft()
    {
        foreach (Action action in apply) action();
        TransportFields selected = transports[selectedProtocol];
        foreach (Action action in selected.Apply) action();
        selected.Connection["waitSeconds"] = commonConnection["waitSeconds"]!.DeepClone();
        draft["connection"] = selected.Connection.DeepClone();
        var terminal = draft["terminal"]!.AsObject();
        foreach (string key in new[] { "advertisedType", "enter", "backspace", "localEcho" })
            terminal[key] = selected.Terminal[key]!.DeepClone();
        ConnectionProfile profile = original.Edit(draft.ToJsonString());
        profile.ValidateEditorSettings();
        return profile;
    }

    public void UseSavedRevision(ConnectionProfile profile) => original = profile;

    internal static ConnectionProfile NewProfile(string type) => type switch
    {
        "telnet" => ConnectionProfile.CreateTelnet("New profile", "localhost"),
        "ssh" => ConnectionProfile.CreateSsh("New profile", "localhost", "user"),
        "serial" => ConnectionProfile.CreateSerial("New profile", new SerialSettings("COM1")),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private StackPanel AddTransportSection(string type, string scope, List<Action> actions, Action build)
    {
        List<Action> previousActions = apply;
        FrameworkElement previousNames = names;
        apply = actions;
        try
        {
            return form.AddSection(panel =>
            {
                names = panel;
                NameScope.SetNameScope(panel, new NameScope());
                form.RegisterName(scope, panel);
                build();
            }, () => type == selectedProtocol);
        }
        finally
        {
            names = previousNames;
            apply = previousActions;
        }
    }

    private void BuildTransport(ConnectionProfile profile, JsonObject connection, JsonObject terminal, bool blank)
    {
        if (profile.Type == "serial")
        {
            var port = SerialPorts.Picker(blank ? "" : profile.Serial.PortName);
            Add("ComPort", port, trackEdit: false);
            form.TrackValue(() => SerialPorts.SelectedName(port), name =>
            {
                port.SelectedIndex = -1;
                port.Text = name;
            });
            form.AddAction("RefreshPorts", () => SerialPorts.Refresh(port));
            apply.Add(() => connection["portName"] = SerialPorts.SelectedName(port));
            Number(connection, "baudRate", "BaudRate");
            Number(connection, "dataBits", "DataBits");
            Choice(connection, "parity", "Parity", ["none", "odd", "even", "mark", "space"],
                [Text("None"), Text("Odd"), Text("Even"), Text("Mark"), Text("Space")]);
            Number(connection, "stopBits", "StopBits");
            var flow = Choice(connection, "flowControl", "FlowControl", ["none", "xonXoff", "rtsCts", "dsrDtr"],
                [Text("None"), "XON/XOFF", "RTS/CTS", "DSR/DTR"]);
            var dtr = Boolean(connection, "dtr", "Dtr");
            var rts = Boolean(connection, "rts", "Rts");
            flow.SelectionChanged += (_, _) => UpdateLines();
            UpdateLines();
            void UpdateLines()
            {
                dtr.IsEnabled = (string)flow.SelectedValue != "dsrDtr";
                rts.IsEnabled = (string)flow.SelectedValue != "rtsCts";
            }
        }
        else
        {
            if (blank) connection["host"] = "";
            String(connection, "host", "Host");
            Number(connection, "port", "Port");
            if (profile.Type == "telnet")
            {
                Boolean(connection, "initiateBinary", "InitiateBinary");
            }
            else
            {
                var live = connection["liveness"]!.AsObject();
                var enabled = Boolean(live, "enabled", "SshLiveness");
                var interval = Number(live, "intervalSeconds", "SshLivenessInterval");
                interval.IsEnabled = enabled.IsChecked == true;
                enabled.Checked += (_, _) => interval.IsEnabled = true;
                enabled.Unchecked += (_, _) => interval.IsEnabled = false;
            }
        }
        String(terminal, "advertisedType", "AdvertisedType");
        Choice(terminal, "enter", "EnterSequence", ["cr", "lf", "crlf"]);
        Choice(terminal, "backspace", "BackspaceSequence", ["bs", "del"]);
        string[] echo = profile.Type switch { "serial" => ["off", "on"], "telnet" => ["off", "followRemote"], _ => ["off"] };
        Choice(terminal, "localEcho", "LocalEcho", echo, echo.Select(value => Text(value switch
            { "off" => "Off", "on" => "On", _ => "FollowRemote" })).ToArray());
    }

    private void ConfigureSshAuthentication(JsonObject auth, App app)
    {
        string[] methods = ["password", "publicKey"];
        string[] labels = [Text("VaultPassword"), Text("PublicKey")];
        string[] savedMethods = auth["methods"]!.AsArray().Select(method => method!.GetValue<string>()).ToArray();
        var primary = Selection("PrimarySshMethod", methods, labels, savedMethods[0]);
        ComboBox secondary = null!;
        var secondaryRow = form.AddSection(_ => secondary = Selection("SecondarySshMethod", [.. methods, ""],
            [.. labels, Text("None")], savedMethods.Length == 2 ? savedMethods[1] : ""));
        var addMethod = form.AddAction("AddSshMethod", () => secondary.SelectedValue = OtherMethod((string)primary.SelectedValue));
        primary.SelectionChanged += (_, _) =>
        {
            if (secondary.SelectedValue is string { Length: > 0 } second && Equals(primary.SelectedValue, second))
                secondary.SelectedValue = OtherMethod(second);
            Update();
        };
        secondary.SelectionChanged += (_, _) =>
        {
            if (secondary.SelectedValue is string { Length: > 0 } second && Equals(primary.SelectedValue, second))
                primary.SelectedValue = OtherMethod(second);
            Update();
        };
        form.AddGroup("SshAdvancedAuthentication", () => Boolean(auth, "allowKeyboardInteractive", "AllowKeyboardInteractive"));
        apply.Add(() =>
        {
            auth["methods"] = secondary.SelectedValue is string { Length: > 0 } second
                ? new JsonArray((string)primary.SelectedValue, second) : new JsonArray((string)primary.SelectedValue);
        });
        Credential(auth, "password", "SelectSavedPassword", app);
        Credential(auth, "privateKey", "SelectPrivateKey", app);
        Update();

        static string OtherMethod(string value) => value == "password" ? "publicKey" : "password";

        void Update()
        {
            bool hasSecondary = secondary.SelectedValue is string { Length: > 0 };
            secondaryRow.Visibility = hasSecondary ? Visibility.Visible : Visibility.Collapsed;
            addMethod.Visibility = hasSecondary ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void InheritedFont(JsonObject font, App app)
    {
        var mode = Selection("FontMode", ["inherit", "override"], [Text("Inherit"), Text("Override")],
            font["mode"]!.GetValue<string>());
        string previous = font["family"]?.GetValue<string>() ?? app.TerminalFontFamily;
        var family = FontCatalog.Picker(previous);
        var size = new TextBox { Text = (font["sizePoints"]?.GetValue<double>() ?? app.TerminalFontSize)
            .ToString(CultureInfo.InvariantCulture) };
        Add("DefaultTerminalFont", family);
        Add("FontSizePoints", size);
        mode.SelectionChanged += (_, _) => Update();
        Update();
        void Update()
        {
            bool useOverride = (string)mode.SelectedValue == "override";
            family.IsEnabled = size.IsEnabled = useOverride;
            family.Visibility = size.Visibility = useOverride ? Visibility.Visible : Visibility.Collapsed;
        }
        apply.Add(() =>
        {
            bool inherit = (string)mode.SelectedValue == "inherit";
            if (!inherit) FontCatalog.Validate(family.Text, previous);
            font["mode"] = (string)mode.SelectedValue;
            font["family"] = inherit ? null : family.Text;
            font["sizePoints"] = inherit ? null : double.Parse(size.Text, CultureInfo.InvariantCulture);
        });
    }

    private void InheritedRetention(JsonObject retention, App app)
    {
        var mode = Selection("RetentionMode", ["inherit", "override"], [Text("Inherit"), Text("Override")],
            retention["mode"]!.GetValue<string>());
        var value = new TextBox { Text = (retention["value"]?.GetValue<int>() ?? app.RetainedRows)
            .ToString(CultureInfo.InvariantCulture) };
        Add("DefaultRetainedRows", value);
        mode.SelectionChanged += (_, _) => Update();
        Update();
        void Update()
        {
            bool useOverride = (string)mode.SelectedValue == "override";
            value.IsEnabled = useOverride;
            value.Visibility = useOverride ? Visibility.Visible : Visibility.Collapsed;
        }
        apply.Add(() =>
        {
            retention["mode"] = (string)mode.SelectedValue;
            retention["value"] = (string)mode.SelectedValue == "inherit" ? null :
                int.Parse(value.Text, NumberStyles.None, CultureInfo.InvariantCulture);
        });
    }

    private void Credential(JsonObject auth, string kind, string key, App app)
    {
        var value = new TextBox { IsReadOnly = true, Text = auth[kind]?.GetValue<string>() is { } savedName
            ? PermissionDisplay.Literal(savedName) : Text("AskAtConnect") };
        Add(kind == "password" ? "SshPasswordSource" : "PublicKey", value,
            descriptionKey: kind == "password" ? "SshSavedPasswordDestinationNotice" : null);
        form.TrackValue(() => (Reference: auth[kind], Label: value.Text), saved =>
        {
            auth[kind] = saved.Reference?.DeepClone();
            value.Text = saved.Label;
        }, (saved, current) => JsonNode.DeepEquals(saved.Reference, current.Reference));
        form.AddAsyncAction(key, async () =>
        {
            void Chosen(string name)
            {
                auth[kind] = name;
                value.Text = PermissionDisplay.Literal(name);
            }
            if (kind == "password") await app.Vault.ChoosePasswordAsync(form.OwnerWindow, Chosen);
            else await app.Vault.ChoosePrivateKeyAsync(form.OwnerWindow, Chosen);
        });
        form.AddAction(kind == "password" ? "AskAtConnect" : "ClearPrivateKey", () =>
        {
            auth[kind] = null;
            value.Text = Text("AskAtConnect");
        });
    }

    private TextBox String(JsonObject node, string property, string key, string? descriptionKey = null, bool optional = false)
    {
        bool present = node.ContainsKey(property);
        var box = new TextBox { Text = optional ? node[property]?.GetValue<string>() ?? "" : node[property]!.GetValue<string>(), MaxLength = 32760 };
        Add(key, box, descriptionKey: descriptionKey);
        apply.Add(() =>
        {
            if (!optional || present || box.Text.Length != 0) node[property] = box.Text;
            else node.Remove(property);
        });
        return box;
    }

    private TextBox Number(JsonObject node, string property, string key)
    {
        var box = new TextBox { Text = node[property]!.ToJsonString(), MaxLength = 12 };
        Add(key, box);
        apply.Add(() => node[property] = long.Parse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture));
        return box;
    }

    private CheckBox Boolean(JsonObject node, string property, string key)
    {
        var box = new CheckBox { IsChecked = node[property]!.GetValue<bool>() };
        Add(key, box);
        apply.Add(() => node[property] = box.IsChecked == true);
        return box;
    }

    private ComboBox Choice(JsonObject node, string property, string key, string[] values, string[]? labels = null)
    {
        var box = Selection(key, values, labels ?? values.Select(value => value.ToUpperInvariant()).ToArray(),
            node[property]!.GetValue<string>());
        apply.Add(() => node[property] = (string)box.SelectedValue);
        return box;
    }

    private ComboBox Selection(string key, string[] values, string[] labels, string selected)
    {
        var box = new ComboBox { ItemsSource = values.Select((value, index) => new Option(value, labels[index])).ToArray(),
            DisplayMemberPath = nameof(Option.Label), SelectedValuePath = nameof(Option.Value), SelectedValue = selected };
        Add(key, box);
        return box;
    }

    private void Add(string key, Control control, bool trackEdit = true, string? descriptionKey = null)
    {
        names.RegisterName(key, control);
        form.AddField(key, control, trackEdit, descriptionKey: descriptionKey);
    }

    private sealed record Option(string Value, string Label);
    private sealed record TransportFields(JsonObject Connection, JsonObject Terminal, List<Action> Apply, StackPanel Panel);
}
