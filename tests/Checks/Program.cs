using Hostbay.Core;

int failed = 0, passed = 0;
void Check(string what, string actual, string expected)
{
    if (actual == expected) { passed++; Console.WriteLine($"  ok   {what}: {actual}"); }
    else { failed++; Console.WriteLine($"  FAIL {what}: got \"{actual}\", expected \"{expected}\""); }
}

Console.WriteLine("Names (new servers):");
foreach (var (raw, want) in new[]
{
    ("Palworld Dedicated Server", "Palworld"),
    ("Abiotic Factor - Dedicated Server", "Abiotic Factor"),
    ("ARK: Survival Evolved Dedicated Server", "ARK: Survival Evolved"),
    ("Soulmask Dedicated Server For Windows", "Soulmask"),
    ("Killing Floor 2 - Dedicated Server", "Killing Floor 2"),
    ("Insurgency: Sandstorm Dedicated Server", "Insurgency: Sandstorm"),
    ("Don't Starve Together Dedicated Server", "Don't Starve Together"),
    ("Nightingale (Dedicated Server)", "Nightingale"),
    ("Arma 3 Server", "Arma 3"),
    ("Mordhau DS", "Mordhau"),
    ("Diamonds", "Diamonds"),
    ("Server", "Server"),
    ("Valheim", "Valheim"),
})
    Check(raw, ServerNames.Clean(raw), want);

Console.WriteLine("Names (servers already added, Steam phrasing only):");
foreach (var (raw, want) in new[]
{
    ("Palworld Dedicated Server", "Palworld"),
    ("Valheim - Server", "Valheim"),
    ("Mordhau DS", "Mordhau"),
    ("Friends Server", "Friends Server"),
    ("Bob's Palworld Server", "Bob's Palworld Server"),
    ("Enshrouded", "Enshrouded"),
})
    Check(raw, ServerNames.CleanSteamSuffix(raw), want);

Console.WriteLine("Router ports read from configs:");
var root = Path.Combine(Path.GetTempPath(), "hb-checks-" + Guid.NewGuid().ToString("N")[..6]);
string Dir(string name, params (string File, string Text)[] files)
{
    var d = Path.Combine(root, name); Directory.CreateDirectory(d);
    foreach (var (f, t) in files) File.WriteAllText(Path.Combine(d, f), t);
    return d;
}
void Ports(string what, ServerInstance s, string expected, bool showSource = true)
{
    var (entries, source) = RouterPorts.For(s);
    Check(what, RouterPorts.Format(entries), expected);
    if (showSource) Console.WriteLine($"         ({source})");
}
ServerInstance S(string game, string dir, string args = "") => new() { GameId = game, InstallDir = dir, Arguments = args };

Ports("Enshrouded, defaults in json", S("enshrouded", Dir("en1", ("enshrouded_server.json", "{\"gamePort\": 15636, \"queryPort\": 15637}"))), "UDP 15636-15637");
Ports("Enshrouded, both moved", S("enshrouded", Dir("en2", ("enshrouded_server.json", "{\"gamePort\": 16000, \"queryPort\": 16001}"))), "UDP 16000-16001");
Ports("Enshrouded, far apart", S("enshrouded", Dir("en3", ("enshrouded_server.json", "{\"gamePort\": 16000, \"queryPort\": 17000}"))), "UDP 16000-16001, UDP 17000");
Ports("Enshrouded, no config yet", S("enshrouded", Dir("en4")), "UDP 15636-15637");
Ports("Valheim -port 2457", S("valheim", Dir("vh"), "-name \"x\" -port 2457 -world w"), "UDP 2457-2459");
Ports("Minecraft port + query on", S("minecraft-java", Dir("mc1", ("server.properties", "server-port=25570\nenable-query=true\nquery.port=25580\nrcon.port=25575\nenable-rcon=true\n"))), "TCP 25570, UDP 25580");
Ports("Minecraft query off, rcon on", S("minecraft-java", Dir("mc2", ("server.properties", "server-port=25570\nenable-query=false\nquery.port=25580\nenable-rcon=true\nrcon.port=25575\n"))), "TCP 25570");
Ports("Minecraft query on, defaults", S("minecraft-java", Dir("mc3", ("server.properties", "enable-query=true\n"))), "TCP 25565, UDP 25565");
Ports("Bedrock both ports", S("minecraft-bedrock", Dir("mb", ("server.properties", "server-port=19140\nserver-portv6=19141\n"))), "UDP 19140-19141");
Ports("7 Days to Die ServerPort", S("7dtd", Dir("7d", ("serverconfig.xml", "<ServerSettings>\n<property name=\"ServerPort\" value=\"27000\"/>\n</ServerSettings>"))), "TCP 27000, UDP 27000-27003");
Ports("Satisfactory args", S("satisfactory", Dir("sf"), "-log -Port=7780 -ReliablePort=8890"), "TCP/UDP 7780, TCP 8890");
var vr = Dir("vr2"); Directory.CreateDirectory(Path.Combine(vr, "save-data", "Settings"));
File.WriteAllText(Path.Combine(vr, "save-data", "Settings", "ServerHostSettings.json"), "{\"Port\": 9900, \"QueryPort\": 9901, \"Rcon\": {\"Port\": 25575}}");
Ports("V Rising json, moved", S("vrising", vr), "UDP 9900-9901");
Ports("ARK args", S("ark-se", Dir("ark"), "TheIsland?listen?Port=7790?QueryPort=27020?RCONPort=27030"), "UDP 7790-7791, UDP 27020");
Ports("Rust args", S("rust", Dir("rust"), "-batchmode +server.port 28020 +server.queryport 28021 +rcon.port 28016 +app.port 28090"), "UDP 28020, UDP 28021, TCP 28090");
Ports("Palworld join port set in Settings", new ServerInstance { GameId = "palworld", InstallDir = Dir("pw"), JoinPort = 8300 }, "UDP 8300");
Ports("Palworld -port arg", S("palworld", Dir("pw2"), "-port=8222 -useperfthreads"), "UDP 8222");
Ports("Typed list wins", new ServerInstance { GameId = "enshrouded", InstallDir = Dir("en5", ("enshrouded_server.json", "{\"gamePort\": 16000, \"queryPort\": 16001}")), ForwardPorts = "UDP 5000" }, "UDP 5000");
try { Directory.Delete(root, true); } catch { }

if (args.Contains("--online"))
{
    Console.WriteLine("Steam art lookup by name (live store search):");
    foreach (var (name, want) in new[] { ("Abiotic Factor - Dedicated Server", 427410), ("Soulmask Dedicated Server For Windows", 2646460), ("Palworld Dedicated Server", 1623730), ("Some Server That Does Not Exist Zzqx", 0) })
        Check(name, (await ArtCache.FindGameAsync(name))?.ToString() ?? "none", want == 0 ? "none" : want.ToString());
}

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
