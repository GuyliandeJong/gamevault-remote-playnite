using System.Windows;
using System.Windows.Controls;

namespace GameVaultRemote
{
    public partial class SettingsView : UserControl
    {
        private readonly SettingsViewModel vm;
        private readonly Playnite.SDK.IPlayniteAPI playnite;

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dir = playnite.Dialogs.SelectFolder();
            if (!string.IsNullOrEmpty(dir)) Root.Text = dir; // typing in the box still works too
        }

        private void Sso_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ok = sso();
                playnite.Dialogs.ShowMessage(ok ? "Signed in. Click OK/Save to keep it." : "Sign-in did not complete.", "GameVault");
            }
            catch (System.Exception ex) { playnite.Dialogs.ShowErrorMessage(ex.Message, "GameVault sign-in failed"); }
        }

        private readonly System.Func<bool> sso;

        public SettingsView(SettingsViewModel vm, Playnite.SDK.IPlayniteAPI playnite, System.Func<bool> sso)
        {
            this.sso = sso;
            this.vm = vm;
            this.playnite = playnite;
            InitializeComponent();
            DataContext = vm;
            Pw.Password = vm.Settings.Password; // PasswordBox can't be data-bound
        }

        private void Pw_Changed(object sender, RoutedEventArgs e) => vm.Settings.Password = Pw.Password;
    }
}
