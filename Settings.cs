using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace GameVaultRemote
{
    public class Settings : ObservableObject
    {
        public string ServerUrl { get; set; } = "";
        public string Username { get; set; } = "";
        public string InstallRoot { get; set; } = "";

        // ServerUrl with whitespace/trailing slash removed and https:// added if the scheme was forgotten.
        [DontSerialize]
        public string BaseUrl
        {
            get
            {
                var u = (ServerUrl ?? "").Trim().TrimEnd('/');
                return u.Length == 0 || u.Contains("://") ? u : "https://" + u;
            }
        }

        [DontSerialize]
        public string Password { get; set; } = "";

        [DontSerialize]
        public string RefreshToken { get; set; } = "";

        public string RefreshTokenEncrypted
        {
            get => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(RefreshToken ?? ""), null, DataProtectionScope.CurrentUser));
            set => RefreshToken = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }

        // Stored DPAPI-encrypted (current Windows user only), never as plain text.
        public string PasswordEncrypted
        {
            get => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Password ?? ""), null, DataProtectionScope.CurrentUser));
            set => Password = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
    }

    public class SettingsViewModel : ObservableObject, ISettings
    {
        private readonly Plugin plugin;
        private Settings backup;
        public Settings Settings { get; private set; }

        public SettingsViewModel(Plugin plugin)
        {
            this.plugin = plugin;
            Settings = plugin.LoadPluginSettings<Settings>() ?? new Settings();
        }

        public void BeginEdit() => backup = Serialization.GetClone(Settings);
        public void CancelEdit() { Settings = backup; OnPropertyChanged(nameof(Settings)); }
        public void EndEdit() => plugin.SavePluginSettings(Settings);

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (!Uri.TryCreate(Settings.BaseUrl, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
                errors.Add("Server URL is not a valid http(s) address.");
            if (string.IsNullOrWhiteSpace(Settings.InstallRoot)) errors.Add("Install folder is required.");
            return errors.Count == 0;
        }
    }
}
