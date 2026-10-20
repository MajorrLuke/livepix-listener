using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LivePixListener.Setup
{
    internal static class Program
    {
        internal const string Version = "1.1.0";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (Has(args, "--silent"))
            {
                try
                {
                    Installer.Install(true, false);
                    Installer.Log("ok");
                }
                catch (Exception ex)
                {
                    Installer.Log(ex.ToString());
                    Environment.ExitCode = 1;
                }
                return;
            }
            Application.Run(new SetupForm());
        }

        private static bool Has(string[] args, string flag)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    internal static class Installer
    {
        public static string InstallDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LivePixListener");
        }

        public static string InstalledExe()
        {
            return Path.Combine(InstallDirectory(), "LivePixListener.exe");
        }

        public static bool IsInstalled()
        {
            return File.Exists(InstalledExe());
        }

        public static void Install(bool startWithWindows, bool launch)
        {
            Log("instalar startup=" + startWithWindows + " launch=" + launch);
            StopRunning();
            var dir = InstallDirectory();
            Log("pasta " + dir);
            Directory.CreateDirectory(dir);
            var target = InstalledExe();
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.exe");
            if (stream == null) throw new InvalidOperationException("O instalador está sem o programa.");
            using (stream)
            using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    file.Write(buffer, 0, read);
            }
            Log("gravado " + new FileInfo(target).Length);

            WriteConfig(startWithWindows);
            CreateShortcut(Environment.SpecialFolder.Programs, target);
            if (startWithWindows)
                CreateShortcut(Environment.SpecialFolder.Startup, target);
            else
                DeleteShortcut(Environment.SpecialFolder.Startup);
            RegisterUninstall(target);
            if (launch)
                Process.Start(target);
        }

        public static void Uninstall()
        {
            var exe = InstalledExe();
            if (!File.Exists(exe))
            {
                DeleteShortcut(Environment.SpecialFolder.Startup);
                DeleteShortcut(Environment.SpecialFolder.Programs);
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LivePixListener", false);
                }
                catch { }
                return;
            }

            var info = new ProcessStartInfo();
            info.FileName = exe;
            info.Arguments = "--uninstall";
            info.UseShellExecute = false;
            Process.Start(info);
        }

        private static void StopRunning()
        {
            var processes = Process.GetProcessesByName("LivePixListener");
            Log("processos " + processes.Length);
            for (int i = 0; i < processes.Length; i++)
            {
                try
                {
                    Log("encerrar " + processes[i].Id);
                    processes[i].Kill();
                    processes[i].WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Log("encerrar falhou " + ex.Message);
                }
            }
        }

        private static void WriteConfig(bool startWithWindows)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LivePixListener");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "config.json");
            var serializer = new JavaScriptSerializer();
            Dictionary<string, object> map = null;
            if (File.Exists(path))
            {
                try
                {
                    map = serializer.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                }
                catch
                {
                    map = null;
                }
            }
            if (map == null)
            {
                map = new Dictionary<string, object>();
                map["WidgetUrl"] = "";
                map["Volume"] = 100;
            }
            if (!map.ContainsKey("WidgetUrl") || map["WidgetUrl"] == null) map["WidgetUrl"] = "";
            if (!map.ContainsKey("Volume")) map["Volume"] = 100;
            map["StartWithWindows"] = startWithWindows;
            File.WriteAllText(path, serializer.Serialize(map));
        }

        private static void CreateShortcut(Environment.SpecialFolder folder, string target)
        {
            var link = Path.Combine(Environment.GetFolderPath(folder), "LivePix Listener.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("Não consegui criar o atalho.");
            var shell = Activator.CreateInstance(shellType);
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlagsInvoke(), null, shell, new object[] { link });
            var type = shortcut.GetType();
            type.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { target });
            type.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(target) });
            type.InvokeMember("WindowStyle", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { 7 });
            type.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "LivePix Listener" });
            type.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }

        private static System.Reflection.BindingFlags BindingFlagsInvoke()
        {
            return System.Reflection.BindingFlags.InvokeMethod;
        }

        private static void DeleteShortcut(Environment.SpecialFolder folder)
        {
            var path = Path.Combine(Environment.GetFolderPath(folder), "LivePix Listener.lnk");
            if (File.Exists(path)) File.Delete(path);
        }

        public static void Log(string line)
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "livepix-setup.log");
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine);
            }
            catch { }
        }

        private static void RegisterUninstall(string target)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LivePixListener"))
            {
                key.SetValue("DisplayName", "LivePix Listener");
                key.SetValue("DisplayVersion", Program.Version);
                key.SetValue("Publisher", "LivePix Listener");
                key.SetValue("InstallLocation", Path.GetDirectoryName(target));
                key.SetValue("DisplayIcon", target + ",0");
                key.SetValue("UninstallString", "\"" + target + "\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                var bytes = new FileInfo(target).Length;
                key.SetValue("EstimatedSize", (int)Math.Max(1, bytes / 1024), RegistryValueKind.DWord);
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly CheckBox _startup;
        private readonly CheckBox _launch;
        private readonly Button _install;
        private readonly Button _remove;
        private readonly Label _status;

        public SetupForm()
        {
            Text = "Instalar LivePix Listener";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 300);
            Font = new Font("Segoe UI", 10f);
            BackColor = Color.FromArgb(246, 248, 246);

            var title = new Label();
            title.Text = "LivePix Listener";
            title.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(22, 32, 28);
            title.BackColor = BackColor;
            title.SetBounds(20, 16, 440, 32);

            var hint = new Label();
            hint.Text = "Instala só para o seu usuário, sem pedir senha de administrador. O programa fica na bandeja e toca os alertas do LivePix no PC do fone.";
            hint.ForeColor = Color.FromArgb(90, 110, 100);
            hint.BackColor = BackColor;
            hint.SetBounds(20, 56, 440, 52);

            _startup = new CheckBox();
            _startup.Text = "Iniciar com o Windows";
            _startup.Checked = true;
            _startup.ForeColor = Color.FromArgb(22, 32, 28);
            _startup.BackColor = BackColor;
            _startup.SetBounds(20, 120, 440, 28);

            _launch = new CheckBox();
            _launch.Text = "Abrir depois de instalar";
            _launch.Checked = true;
            _launch.ForeColor = Color.FromArgb(22, 32, 28);
            _launch.BackColor = BackColor;
            _launch.SetBounds(20, 152, 440, 28);

            _install = new Button();
            _install.Text = Installer.IsInstalled() ? "Atualizar" : "Instalar";
            _install.SetBounds(20, 200, 140, 36);
            _install.FlatStyle = FlatStyle.Flat;
            _install.FlatAppearance.BorderSize = 0;
            _install.BackColor = Color.FromArgb(0, 158, 112);
            _install.ForeColor = Color.White;
            _install.Cursor = Cursors.Hand;
            _install.Click += delegate { DoInstall(); };

            _remove = new Button();
            _remove.Text = "Remover";
            _remove.SetBounds(172, 200, 140, 36);
            _remove.FlatStyle = FlatStyle.Flat;
            _remove.Enabled = Installer.IsInstalled();
            _remove.Click += delegate { DoRemove(); };

            _status = new Label();
            _status.Text = Installer.IsInstalled()
                ? "Já existe uma instalação. Atualizar troca o programa e mantém o link do widget."
                : " ";
            _status.ForeColor = Color.FromArgb(90, 110, 100);
            _status.BackColor = BackColor;
            _status.SetBounds(20, 248, 440, 40);

            Controls.Add(title);
            Controls.Add(hint);
            Controls.Add(_startup);
            Controls.Add(_launch);
            Controls.Add(_install);
            Controls.Add(_remove);
            Controls.Add(_status);
            AcceptButton = _install;
        }

        private void DoInstall()
        {
            _install.Enabled = false;
            _remove.Enabled = false;
            try
            {
                Installer.Install(_startup.Checked, false);
                _status.ForeColor = Color.FromArgb(0, 158, 112);
                _status.Text = _startup.Checked
                    ? "Instalado. Ele vai abrir junto com o Windows."
                    : "Instalado.";
                if (_launch.Checked)
                {
                    Process.Start(Installer.InstalledExe());
                    Close();
                    return;
                }
                _install.Enabled = true;
                _install.Text = "Atualizar";
                _remove.Enabled = true;
            }
            catch (Exception ex)
            {
                _status.ForeColor = Color.FromArgb(176, 64, 48);
                _status.Text = ex.Message;
                _install.Enabled = true;
                _remove.Enabled = Installer.IsInstalled();
            }
        }

        private void DoRemove()
        {
            Installer.Uninstall();
            Close();
        }
    }
}
