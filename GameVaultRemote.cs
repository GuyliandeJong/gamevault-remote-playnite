using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace GameVaultRemote
{
    public class InstallInfo { public string Dir { get; set; } public string Exe { get; set; } }

    public class GameVaultRemote : LibraryPlugin
    {
        private readonly SettingsViewModel settings;
        private readonly GameVaultApi api;
        private readonly string installsFile;
        private Dictionary<string, InstallInfo> installs;

        public override Guid Id { get; } = Guid.Parse("5d1c7a52-3b0e-4f6e-9a47-2c8f0d6b91e3");
        public override string Name => "GameVault";
        // Shown in Playnite's "Update" menu and sidebar; icon.png sits next to the dll.
        public override string LibraryIcon => Path.Combine(Path.GetDirectoryName(typeof(GameVaultRemote).Assembly.Location), "icon.png");

        public GameVaultRemote(IPlayniteAPI playnite) : base(playnite)
        {
            Properties = new LibraryPluginProperties { HasSettings = true };
            settings = new SettingsViewModel(this);
            api = new GameVaultApi(() => settings.Settings, () => SavePluginSettings(settings.Settings));
            installsFile = Path.Combine(GetPluginUserDataPath(), "installs.json");
            installs = File.Exists(installsFile)
                ? Serialization.FromJsonFile<Dictionary<string, InstallInfo>>(installsFile)
                : new Dictionary<string, InstallInfo>();
        }

        public override ISettings GetSettings(bool firstRun) => settings;
        public override UserControl GetSettingsView(bool firstRun) => new SettingsView(settings, PlayniteApi, AuthenticateSso);

        // Opens the server's SSO login in Playnite's browser. After the IdP callback the server redirects
        // to "<origin>/?access_token=...&refresh_token=...", which we catch from the address.
        internal bool AuthenticateSso()
        {
            var cfg = settings.Settings;
            if (!Uri.TryCreate(cfg.BaseUrl, UriKind.Absolute, out var server) || (server.Scheme != "http" && server.Scheme != "https"))
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Enter a valid server URL first (e.g. https://vault.example.com).", "GameVault");
                return false;
            }
            var host = server.Host;
            GvToken tokens = null;
            using (var view = PlayniteApi.WebViews.CreateView(600, 750))
            {
                view.LoadingChanged += (s, e) =>
                {
                    // An exception escaping this handler would crash Playnite, so never throw here.
                    if (!Uri.TryCreate(view.GetCurrentAddress(), UriKind.Absolute, out var uri)) return;
                    if (uri.Host != host) return; // only trust tokens coming back on the server's own host
                    var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    if (q["access_token"] == null) return;
                    tokens = new GvToken { access_token = q["access_token"], refresh_token = q["refresh_token"] };
                    view.Close();
                };
                view.Navigate(cfg.BaseUrl + "/api/auth/oauth2/login");
                view.OpenDialog();
            }
            if (tokens == null) return false;
            api.SetTokens(tokens);
            return true;
        }

        private void SaveInstalls() => File.WriteAllText(installsFile, Serialization.ToJson(installs));

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            List<GvGame> games;
            try { games = api.GetGamesAsync(args.CancelToken).GetAwaiter().GetResult(); }
            catch (Exception e)
            {
                PlayniteApi.Notifications.Add("gamevault-import", "GameVault import failed: " + e.Message, NotificationType.Error);
                throw;
            }

            var coverDir = Directory.CreateDirectory(Path.Combine(GetPluginUserDataPath(), "covers")).FullName;

            return games.Select(g =>
            {
                // Server covers need auth, so cache them locally. No cover => left empty, and Playnite's
                // own metadata downloader (IGDB etc.) fills it in when you run it on the library.
                var coverPath = Path.Combine(coverDir, g.metadata?.cover?.id + ".img");
                if (g.metadata?.cover != null && !File.Exists(coverPath))
                {
                    try { api.DownloadMediaAsync(g.metadata.cover.id, coverPath, args.CancelToken).GetAwaiter().GetResult(); }
                    catch { coverPath = null; } // one bad cover must not fail the whole import
                }
                else if (g.metadata?.cover == null) coverPath = null;

                var id = g.id.ToString();
                // Playnite overwrites install state on each refresh, so re-assert it from our own record.
                var inst = installs.TryGetValue(id, out var i) && Directory.Exists(i.Dir) ? i : null;
                return new GameMetadata
                {
                    GameId = id,
                    // Server-matched title beats the filename-derived one (which IGDB often can't match).
                    Name = string.IsNullOrWhiteSpace(g.metadata?.title) ? g.title : g.metadata.title,
                    Version = g.version,
                    Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty(
                        (g.versions.Count > 0 ? g.versions.All(v => v.type.StartsWith("LINUX")) : g.type?.StartsWith("LINUX") == true) ? "pc_linux" : "pc_windows") },
                    CoverImage = coverPath != null ? new MetadataFile(coverPath) : null,
                    IsInstalled = inst != null,
                    InstallDirectory = inst?.Dir,
                };
            }).ToList();
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId == Id) yield return new RemoteInstall(args.Game, this);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId == Id) yield return new RemoteUninstall(args.Game, this);
        }

        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            if (args.Game.PluginId != Id || !installs.TryGetValue(args.Game.GameId, out var i) || i.Exe == null)
                yield break;
            yield return new AutomaticPlayController(args.Game)
            {
                Name = "Play",
                Type = AutomaticPlayActionType.File,
                Path = i.Exe,
                WorkingDir = Path.GetDirectoryName(i.Exe),
            };
        }

        // Used when server metadata has no launch_executable: largest non-helper exe wins.
        // shortcut: size heuristic, can pick wrong for launcher+game bundles; switch to per-game override if so.
        private static readonly string[] Junk = { "unins", "crash", "redist", "vcredist", "dxsetup", "dotnet", "setup", "uninstall", "helper", "report" };

        internal static string PickExecutable(string installDir) =>
            Directory.GetFiles(installDir, "*.exe", SearchOption.AllDirectories)
                .Where(f => !Junk.Any(j => Path.GetFileName(f).IndexOf(j, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar)) // shallowest first
                .ThenByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();

        private void Register(Game game, string dir, string launchRel, Action<GameInstalledEventArgs> done)
        {
            var exe = !string.IsNullOrEmpty(launchRel) && File.Exists(Path.Combine(dir, launchRel))
                ? Path.Combine(dir, launchRel) : PickExecutable(dir);
            installs[game.GameId] = new InstallInfo { Dir = dir, Exe = exe };
            SaveInstalls();
            done(new GameInstalledEventArgs { InstalledInfo = new GameInstallationData { InstallDirectory = dir } });
        }

        // The server sends folder-type games as an uncompressed .tar. shortcut: no 7z/rar/iso, add when a game needs it.
        private static void Unpack(string file, string dir, CancellationToken ct)
        {
            var head = new byte[512];
            int got;
            using (var f = File.OpenRead(file)) got = f.Read(head, 0, 512);
            if (got >= 2 && head[0] == 'P' && head[1] == 'K') ZipFile.ExtractToDirectory(file, dir);
            else if (got >= 2 && head[0] == 'M' && head[1] == 'Z') { Directory.CreateDirectory(dir); File.Copy(file, Path.Combine(dir, "setup.exe")); }
            else if (got == 512 && Encoding.ASCII.GetString(head, 257, 5) == "ustar") ExtractTar(file, dir);
            else SevenZip(file, dir, ct, BitConverter.ToString(head, 0, Math.Min(got, 6)));
        }

        // 7z, rar, iso etc.: hand off to an installed 7-Zip instead of bundling extractors.
        private static void SevenZip(string file, string dir, CancellationToken ct, string signature)
        {
            // Playnite is a 32-bit process, so ProgramFiles points at "Program Files (x86)"; ProgramW6432 is the real 64-bit one.
            var exe = new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                .Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.Combine(p, "7-Zip", "7z.exe")).FirstOrDefault(File.Exists);
            if (exe == null)
                throw new Exception("This download is not a zip/tar/exe (starts with " + signature + "). Install 7-Zip (https://7-zip.org) to unpack other formats.");
            using (var p = Process.Start(new ProcessStartInfo(exe, $"x \"{file}\" -o\"{dir}\" -y") { UseShellExecute = false, CreateNoWindow = true }))
            using (ct.Register(() => { try { p.Kill(); } catch { } }))
            {
                p.WaitForExit();
                ct.ThrowIfCancellationRequested();
                if (p.ExitCode != 0) throw new Exception("7-Zip failed to extract the download (exit code " + p.ExitCode + ").");
            }
        }

        private static void ExtractTar(string file, string dir)
        {
            var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            Directory.CreateDirectory(dir);
            string Str(byte[] b, int o, int n) => Encoding.UTF8.GetString(b, o, n).TrimEnd('\0', ' ');
            using (var s = File.OpenRead(file))
            {
                var h = new byte[512]; string next = null;
                while (s.Read(h, 0, 512) == 512 && h[0] != 0)
                {
                    long size = 0;
                    if ((h[124] & 0x80) != 0) for (var i = 125; i < 136; i++) size = size << 8 | h[i]; // base-256 size for files > 8 GB
                    else { var t = Str(h, 124, 12).Trim(); if (t.Length > 0) size = Convert.ToInt64(t, 8); }
                    var type = (char)h[156];
                    long used = 0;
                    if (type == 'L' || type == 'x') // GNU long name / pax header: applies to the next entry
                    {
                        var d = new byte[size]; s.Read(d, 0, d.Length); used = size;
                        var text = Str(d, 0, d.Length);
                        next = type == 'L' ? text : System.Text.RegularExpressions.Regex.Match(text, @"\d+ path=([^\n]*)\n").Groups[1].Value;
                    }
                    else
                    {
                        var name = !string.IsNullOrEmpty(next) ? next
                            : Str(h, 257, 5) == "ustar" && h[345] != 0 ? Str(h, 345, 155) + "/" + Str(h, 0, 100) : Str(h, 0, 100);
                        next = null;
                        var target = Path.GetFullPath(Path.Combine(dir, name.Replace('/', '\\')));
                        if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) // never write outside dir
                        {
                            if (type == '5') Directory.CreateDirectory(target);
                            else if (type == '0' || type == '\0')
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(target));
                                using (var o = File.Create(target))
                                {
                                    var buf = new byte[81920]; long left = size; int n;
                                    while (left > 0 && (n = s.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0) { o.Write(buf, 0, n); left -= n; }
                                }
                                used = size;
                            }
                        }
                    }
                    s.Seek((size + 511) / 512 * 512 - used, SeekOrigin.Current);
                }
            }
        }

        // Runs the UI-thread-only parts (Playnite's notification/dialog APIs) from the background install.
        private void OnUi(Action a) => PlayniteApi.MainView.UIDispatcher.BeginInvoke(a);

        // Install runs in the background: Playnite stays usable and progress shows as a notification.
        // Cancelling the install in Playnite disposes the controller, which cancels ct.
        internal void Install(Game game, Action<GameInstalledEventArgs> done, CancellationToken ct)
        {
            var nid = "gamevault-install-" + game.GameId;
            void Notify(string msg, NotificationType t = NotificationType.Info) => OnUi(() =>
            {
                PlayniteApi.Notifications.Remove(nid); // Add() with an existing id would not update the text
                PlayniteApi.Notifications.Add(nid, msg, t);
            });

            Task.Run(() =>
            {
                GvMeta meta = null; string dir = null; var setup = false;
                var tmp = Path.Combine(Path.GetTempPath(), "gv_" + game.GameId + ".tmp");
                try
                {
                    Notify("Fetching info for " + game.Name + "...");
                    var gv = api.GetGameAsync(game.GameId, ct).GetAwaiter().GetResult();
                    if (gv.versions.Count > 0 && gv.versions.All(v => v.type.StartsWith("LINUX")))
                        throw new Exception("This game is Linux-only and can't be installed on Windows.");
                    var ver = new[] { "WINDOWS_PORTABLE", "WINDOWS_SOFTWARE", "UNDETECTABLE", "WINDOWS_SETUP" }
                        .Select(t => gv.versions.FirstOrDefault(v => v.type == t)).FirstOrDefault(v => v != null)
                        ?? throw new Exception("No installable version. Server versions: "
                            + (gv.versions.Count == 0 ? "none" : string.Join(", ", gv.versions.Select(v => v.type))) + ".");
                    setup = ver.type == "WINDOWS_SETUP";
                    meta = gv.metadata;

                    Notify("Downloading " + game.Name + "...");
                    var last = DateTime.UtcNow;
                    api.DownloadAsync(game.GameId, ver.id, tmp, f =>
                    {
                        if (DateTime.UtcNow - last < TimeSpan.FromSeconds(3)) return; // don't flood the notification list
                        last = DateTime.UtcNow;
                        Notify($"Downloading {game.Name}: {f:P0}");
                    }, ct).GetAwaiter().GetResult();

                    Notify("Extracting " + game.Name + "...");
                    // Setup games unpack into a temporary "_setup" folder that is deleted after the installer finishes.
                    dir = Path.Combine(settings.Settings.InstallRoot, Paths.Safe(game.Name) + (setup ? "_setup" : ""));
                    Unpack(tmp, dir, ct);
                }
                catch (OperationCanceledException) { OnUi(() => PlayniteApi.Notifications.Remove(nid)); return; }
                catch (Exception e) { Notify("GameVault install of " + game.Name + " failed: " + e.Message, NotificationType.Error); return; }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }

                OnUi(() =>
                {
                    PlayniteApi.Notifications.Remove(nid);
                    if (setup) FinishSetup(game, dir, meta, done);
                    else
                    {
                        Register(game, dir, meta?.launch_executable, done);
                        PlayniteApi.Notifications.Add(nid, game.Name + " installed.", NotificationType.Info);
                    }
                });
            });
        }

        private void FinishSetup(Game game, string dir, GvMeta meta, Action<GameInstalledEventArgs> done)
        {
            var rel = meta?.installer_executable;
            var installer = !string.IsNullOrEmpty(rel) && File.Exists(Path.Combine(dir, rel)) ? Path.Combine(dir, rel)
                : Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories)
                    .OrderBy(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), "setup|install", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 0 : 1).FirstOrDefault();
            if (installer == null)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("No installer .exe found in the download.", "GameVault install failed");
                return;
            }

            // Pre-select <InstallRoot>\<Name> as the target (/DIR is honoured by Inno Setup; other installers ignore it).
            // Waiting happens off the UI thread so Playnite stays usable while the installer runs.
            var target = Path.Combine(settings.Settings.InstallRoot, Paths.Safe(game.Name));
            var proc = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer), Arguments = "/DIR=\"" + target + "\"" });
            Task.Run(() => proc?.WaitForExit()).ContinueWith(_ => PlayniteApi.MainView.UIDispatcher.Invoke(() =>
            {
                try { Directory.Delete(dir, true); } catch { /* leftover installer files are harmless */ }
                // Bind by searching the target folder; only ask the user if the installer ignored /DIR.
                var found = Directory.Exists(target) && PickExecutable(target) != null ? target : null;
                if (found == null)
                {
                    PlayniteApi.Dialogs.ShowMessage("Couldn't find the game in " + target + ". Choose the folder it was installed to.", "GameVault");
                    found = PlayniteApi.Dialogs.SelectFolder();
                }
                if (!string.IsNullOrEmpty(found)) Register(game, found, meta?.launch_executable, done);
            }));
        }

        internal void Uninstall(Game game, Action<GameUninstalledEventArgs> done)
        {
            if (!installs.TryGetValue(game.GameId, out var i)) { done(new GameUninstalledEventArgs()); return; }
            // Only delete inside our own install root, never an arbitrary stored path.
            var root = Path.GetFullPath(settings.Settings.InstallRoot).TrimEnd('\\') + "\\";
            var delete = Path.GetFullPath(i.Dir).StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(i.Dir) ? i.Dir : null;
            // Deleting a multi-GB folder takes a while: do it off the UI thread so Playnite doesn't freeze.
            Task.Run(() =>
            {
                string error = null;
                if (delete != null)
                    try { Directory.Delete(delete, true); } catch (Exception e) { error = e.Message; }
                OnUi(() =>
                {
                    if (error != null) PlayniteApi.Notifications.Add(Guid.NewGuid().ToString(), "Couldn't fully delete " + delete + ": " + error, NotificationType.Error);
                    installs.Remove(game.GameId);
                    SaveInstalls();
                    done(new GameUninstalledEventArgs());
                });
            });
        }
    }

    internal static class Paths
    {
        public static string Safe(string name) =>
            string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }

    internal class RemoteInstall : InstallController
    {
        private readonly GameVaultRemote plugin;
        public RemoteInstall(Game game, GameVaultRemote p) : base(game) { plugin = p; Name = "Install from GameVault"; }
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        public override void Install(InstallActionArgs args) => plugin.Install(Game, InvokeOnInstalled, cts.Token);
        public override void Dispose() { cts.Cancel(); base.Dispose(); }
    }

    internal class RemoteUninstall : UninstallController
    {
        private readonly GameVaultRemote plugin;
        public RemoteUninstall(Game game, GameVaultRemote p) : base(game) { plugin = p; Name = "Uninstall"; }
        public override void Uninstall(UninstallActionArgs args) => plugin.Uninstall(Game, InvokeOnUninstalled);
    }
}
