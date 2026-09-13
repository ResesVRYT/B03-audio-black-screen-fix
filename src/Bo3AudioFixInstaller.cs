// ===========================================================================
//  Black Ops III Audio Fix - Installer
//
//  Black Ops III scans every enabled audio device at startup. On systems with
//  many devices (Voicemeeter, VB-Cable, virtual headsets, streaming devices)
//  that scan never completes: the intro audio plays but the screen stays
//  black and the game hangs.
//
//  This installer deploys a proxy winmm.dll into the game folder. That DLL
//  forwards every normal winmm call to the real Windows winmm, but filters
//  the device-enumeration calls so the game only sees the devices you pick.
//  Nothing on the system is disabled; only the game's view is trimmed.
//
//  The proxy DLL is embedded in this executable as a resource, so this is a
//  single self-contained file.
//
//  Build (C# 5 compatible, targets .NET Framework 4.x):
//    csc /target:winexe /out:"BO3 Audio Fix Installer.exe"
//        /reference:System.Windows.Forms.dll /reference:System.Drawing.dll
//        /resource:winmm.dll,winmm.dll Bo3AudioFixInstaller.cs
// ===========================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bo3AudioFix
{
    // ------------------------------------------------------------- native --
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        public struct WAVEOUTCAPSW
        {
            public ushort wMid; public ushort wPid; public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats; public ushort wChannels; public ushort wReserved1; public uint dwSupport;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        public struct WAVEINCAPSW
        {
            public ushort wMid; public ushort wPid; public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats; public ushort wChannels; public ushort wReserved1;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        public struct MIXERCAPSW
        {
            public ushort wMid; public ushort wPid; public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint fdwSupport; public uint cDestinations;
        }

        // NOTE: uDeviceID is UINT_PTR. IntPtr is pointer-sized and marshals
        // identically; it also avoids the UIntPtr conversion pitfalls.
        [DllImport("winmm.dll")] public static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll")] public static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll")] public static extern uint mixerGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int waveOutGetDevCapsW(IntPtr id, ref WAVEOUTCAPSW c, uint cb);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int waveInGetDevCapsW(IntPtr id, ref WAVEINCAPSW c, uint cb);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int mixerGetDevCapsW(IntPtr id, ref MIXERCAPSW c, uint cb);

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    }

    // ------------------------------------------- default endpoint (COM) ----
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr col);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(int acc, out IntPtr props);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    internal static class AudioDefaults
    {
        // flow: 0 = render (playback), 1 = capture (recording)
        public static string GetDefaultName(int flow)
        {
            try
            {
                IMMDeviceEnumerator en = (IMMDeviceEnumerator)(new MMDeviceEnumeratorCo());
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(flow, 0, out dev) != 0 || dev == null) return null;
                string id;
                dev.GetId(out id);
                if (string.IsNullOrEmpty(id)) return null;

                // id looks like {0.0.0.00000000}.{guid}; the trailing guid is
                // the registry key under MMDevices\Audio\<Render|Capture>.
                int idx = id.LastIndexOf("}.{", StringComparison.Ordinal);
                if (idx < 0) return null;
                string guid = id.Substring(idx + 2);
                string flowKey = (flow == 0) ? "Render" : "Capture";
                string path = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\"
                            + flowKey + "\\" + guid + "\\Properties";
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k == null) return null;
                    object v = k.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2");
                    return (v == null) ? null : v.ToString();
                }
            }
            catch { return null; }
        }
    }

    // ---------------------------------------------------------- device -----
    internal class Dev
    {
        public string Name;
        public bool Playback, Recording, Mixer;
        public Dev(string n) { Name = n; }

        public string TypeText
        {
            get
            {
                List<string> parts = new List<string>();
                if (Playback) parts.Add("Playback");
                if (Recording) parts.Add("Recording");
                if (Mixer) parts.Add("Mixer");
                return string.Join(", ", parts.ToArray());
            }
        }
    }

    internal static class DeviceScan
    {
        public static List<Dev> Scan()
        {
            List<Dev> order = new List<Dev>();
            Dictionary<string, Dev> map = new Dictionary<string, Dev>(StringComparer.OrdinalIgnoreCase);

            Native.WAVEOUTCAPSW o = new Native.WAVEOUTCAPSW();
            uint so = (uint)Marshal.SizeOf(typeof(Native.WAVEOUTCAPSW));
            uint n = Native.waveOutGetNumDevs();
            for (uint i = 0; i < n; i++)
                if (Native.waveOutGetDevCapsW(new IntPtr(i), ref o, so) == 0)
                    Get(map, order, o.szPname).Playback = true;

            Native.WAVEINCAPSW c = new Native.WAVEINCAPSW();
            uint sc = (uint)Marshal.SizeOf(typeof(Native.WAVEINCAPSW));
            n = Native.waveInGetNumDevs();
            for (uint i = 0; i < n; i++)
                if (Native.waveInGetDevCapsW(new IntPtr(i), ref c, sc) == 0)
                    Get(map, order, c.szPname).Recording = true;

            Native.MIXERCAPSW m = new Native.MIXERCAPSW();
            uint sm = (uint)Marshal.SizeOf(typeof(Native.MIXERCAPSW));
            n = Native.mixerGetNumDevs();
            for (uint i = 0; i < n; i++)
                if (Native.mixerGetDevCapsW(new IntPtr(i), ref m, sm) == 0)
                    Get(map, order, m.szPname).Mixer = true;

            return order;
        }

        private static Dev Get(Dictionary<string, Dev> map, List<Dev> order, string name)
        {
            if (name == null) name = "";
            name = name.Trim();
            Dev d;
            if (!map.TryGetValue(name, out d))
            {
                d = new Dev(name);
                map[name] = d;
                order.Add(d);
            }
            return d;
        }

        // Names that are virtual / rarely needed by the game.
        private static readonly string[] VirtualPatterns = new string[] {
            "voicemeeter","vb-audio","cable","virtual","oculus","sonar","steelseries",
            "krisp","voicemod","loopback","steam streaming","splitcam","ivcam","parsec",
            "obsbot","nvidia high","meta ","vrchat","wave link","banana","potato",
            "vaio","discord","obs","streamlabs","synchronous","rust desk","rustdesk"
        };

        public static bool IsVirtual(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            for (int i = 0; i < VirtualPatterns.Length; i++)
                if (n.Contains(VirtualPatterns[i])) return true;
            return false;
        }
    }

    // ------------------------------------------------------ steam finder ---
    internal static class Bo3Finder
    {
        public static string Find()
        {
            List<string> roots = new List<string>();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam"))
                {
                    if (k != null)
                    {
                        object sp = k.GetValue("SteamPath");
                        if (sp != null)
                        {
                            string steam = sp.ToString().Replace('/', '\\');
                            roots.Add(steam);
                            string vdf = Path.Combine(steam, "steamapps\\libraryfolders.vdf");
                            if (File.Exists(vdf))
                            {
                                string text = File.ReadAllText(vdf);
                                foreach (Match mm in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
                                    roots.Add(mm.Groups[1].Value.Replace("\\\\", "\\"));
                            }
                        }
                    }
                }
            }
            catch { }

            foreach (string drive in new string[] { "C:", "D:", "E:", "F:", "G:", "H:" })
            {
                roots.Add(drive + "\\SteamLibrary");
                roots.Add(drive + "\\Steam");
                roots.Add(drive + "\\Games\\SteamLibrary");
                roots.Add(drive + "\\Program Files (x86)\\Steam");
            }

            foreach (string r in roots)
            {
                try
                {
                    string p = Path.Combine(r, "steamapps\\common\\Call of Duty Black Ops III");
                    if (File.Exists(Path.Combine(p, "BlackOps3.exe"))) return p;
                }
                catch { }
            }
            return "";
        }
    }

    // ------------------------------------------------------------- form ----
    internal class MainForm : Form
    {
        private TextBox txtPath;
        private Button btnBrowse, btnRec, btnNone, btnAll, btnRescan, btnInstall, btnUninstall, btnClose;
        private ListView list;
        private Label lblCount;
        private TextBox txtLog;
        private bool suppressCount;

        private string defaultOut, defaultIn;

        public MainForm(string prefillPath)
        {
            defaultOut = AudioDefaults.GetDefaultName(0);
            defaultIn = AudioDefaults.GetDefaultName(1);

            Text = "Black Ops III Audio Fix - Installer";
            ClientSize = new Size(860, 660);
            MinimumSize = new Size(760, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            Label intro = new Label();
            intro.Text = "Black Ops III hangs on a black screen (with sound) when too many audio devices are enabled. "
                       + "Tick only the devices the game needs to see. Nothing is disabled in Windows - every other "
                       + "program still sees all of your devices.";
            intro.SetBounds(12, 10, 836, 52);
            intro.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(intro);

            Label lblPath = new Label();
            lblPath.Text = "Black Ops III folder:";
            lblPath.AutoSize = true;   // never clip, whatever the DPI
            lblPath.SetBounds(12, 70, 150, 20);
            Controls.Add(lblPath);

            txtPath = new TextBox();
            txtPath.SetBounds(175, 68, 571, 22);
            txtPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(txtPath);

            btnBrowse = new Button();
            btnBrowse.Text = "Browse...";
            btnBrowse.SetBounds(754, 66, 94, 26);
            btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowse.Click += OnBrowse;
            Controls.Add(btnBrowse);

            Label lblList = new Label();
            lblList.Text = "Audio devices - ticked = Black Ops III can see it:";
            lblList.AutoSize = true;
            lblList.SetBounds(12, 100, 500, 20);
            Controls.Add(lblList);

            // Created before the ListView: the native list can raise
            // ItemChecked as soon as its handle exists, and that reaches
            // UpdateCount(), which needs this label.
            lblCount = new Label();
            lblCount.AutoSize = true;
            lblCount.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            // Constructed here so it is never null when the native list raises
            // ItemChecked, but parented to the button row further down so it
            // flows after the buttons instead of clipping at a fixed x.

            list = new ListView();
            list.SetBounds(12, 122, 836, 332);
            list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            list.View = View.Details;
            list.CheckBoxes = true;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.HideSelection = true;
            list.Columns.Add("Device (as the game sees it)", 400);
            list.Columns.Add("Type", 160);
            list.Columns.Add("Notes", 250);
            Controls.Add(list);

            // Flow layout + AutoSize buttons: the row sizes itself to the text
            // at any DPI or font scale, so nothing gets clipped.
            FlowLayoutPanel btnPanel = new FlowLayoutPanel();
            btnPanel.SetBounds(12, 462, 836, 34);
            btnPanel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            btnPanel.FlowDirection = FlowDirection.LeftToRight;
            btnPanel.WrapContents = false;
            Controls.Add(btnPanel);

            btnRec = FlowButton(btnPanel, "Recommended", OnRecommended);
            btnNone = FlowButton(btnPanel, "Untick all", OnNone);
            btnAll = FlowButton(btnPanel, "Tick all", OnAll);
            btnRescan = FlowButton(btnPanel, "Rescan devices", OnRescan);

            // Flows immediately after the buttons, whatever width they end up.
            lblCount.Margin = new Padding(12, 7, 0, 0);
            btnPanel.Controls.Add(lblCount);

            // (lblCount is created earlier, before the ListView)

            txtLog = new TextBox();
            txtLog.SetBounds(12, 498, 836, 76);
            txtLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.WhiteSmoke;
            Controls.Add(txtLog);

            btnUninstall = MakeButton("Uninstall", 460, 588, 120, OnUninstall);
            btnInstall = MakeButton("Install", 590, 588, 120, OnInstall);
            btnClose = MakeButton("Close", 720, 588, 128, delegate { Close(); });
            btnUninstall.Height = 32; btnInstall.Height = 32; btnClose.Height = 32;
            btnUninstall.Anchor = btnInstall.Anchor = btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnInstall.Font = new Font("Segoe UI", 9f, FontStyle.Bold);

            string found = !string.IsNullOrEmpty(prefillPath) ? prefillPath : Bo3Finder.Find();
            if (!string.IsNullOrEmpty(found))
            {
                txtPath.Text = found;
                Log("Found Black Ops III at: " + found);
            }
            else Log("Could not find Black Ops III automatically - use Browse...");

            // Wired only once every control exists, so a notification from the
            // native list can never reach UpdateCount() before it is ready.
            list.ItemChecked += delegate { UpdateCount(); };

            // Once the window is up and the list has settled, recompute the
            // count so it is accurate regardless of notification timing.
            Shown += delegate { UpdateCount(); };

            RefreshDevices();
        }

        private Button MakeButton(string text, int x, int y, int w, EventHandler handler)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, 26);
            b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            b.Click += handler;
            Controls.Add(b);
            return b;
        }

        // Button that sizes itself to its caption and is positioned by the
        // flow panel, so captions never clip at any DPI or font scale.
        private Button FlowButton(FlowLayoutPanel host, string text, EventHandler handler)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(0, 26);
            b.Margin = new Padding(0, 0, 6, 0);
            b.Click += handler;
            host.Controls.Add(b);
            return b;
        }

        private void Log(string s)
        {
            txtLog.AppendText(s + "\r\n");
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        private bool IsDefault(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            if (!string.IsNullOrEmpty(defaultOut) && n.StartsWith(defaultOut.ToLowerInvariant())) return true;
            if (!string.IsNullOrEmpty(defaultIn) && n.StartsWith(defaultIn.ToLowerInvariant())) return true;
            return false;
        }

        private bool Recommended(string name)
        {
            return !DeviceScan.IsVirtual(name) || IsDefault(name);
        }

        private List<string> ReadExistingKeeps(string gameDir)
        {
            List<string> keeps = new List<string>();
            try
            {
                string cfg = Path.Combine(gameDir, "bo3_audio.cfg");
                if (!File.Exists(cfg)) return keeps;
                foreach (string raw in File.ReadAllLines(cfg))
                {
                    string t = raw.Trim();
                    if (t.Length == 0 || t[0] == '#' || t[0] == ';') continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = t.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = t.Substring(eq + 1).Trim();
                    if (k == "keep" && v.Length > 0) keeps.Add(v);
                }
            }
            catch { }
            return keeps;
        }

        private void RefreshDevices()
        {
            List<Dev> devs;
            try { devs = DeviceScan.Scan(); }
            catch (Exception ex) { devs = new List<Dev>(); Log("ERROR reading audio devices: " + ex.Message); }

            List<string> existing = new List<string>();
            string game = txtPath.Text.Trim();
            if (game.Length > 0 && Directory.Exists(game)) existing = ReadExistingKeeps(game);

            suppressCount = true;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Dev d in devs)
            {
                ListViewItem it = new ListViewItem(d.Name);
                it.SubItems.Add(d.TypeText);

                List<string> notes = new List<string>();
                if (IsDefault(d.Name)) notes.Add("Windows default");
                if (DeviceScan.IsVirtual(d.Name)) notes.Add("virtual");
                it.SubItems.Add(string.Join(", ", notes.ToArray()));

                if (existing.Count > 0)
                {
                    foreach (string k in existing)
                        if (d.Name.ToLowerInvariant().Contains(k.ToLowerInvariant())) { it.Checked = true; break; }
                }
                else it.Checked = Recommended(d.Name);

                if (IsDefault(d.Name)) it.ForeColor = Color.DarkGreen;
                list.Items.Add(it);
            }
            list.EndUpdate();
            suppressCount = false;
            UpdateCount();

            Log(string.Format("Found {0} audio device name(s) - {1}.", devs.Count,
                existing.Count > 0 ? "ticks loaded from the installed bo3_audio.cfg"
                                   : "ticks set to the recommended selection"));
        }

        private void UpdateCount()
        {
            // The native ListView can call this via ItemChecked before the
            // rest of the UI exists, so stay defensive.
            if (suppressCount || lblCount == null || list == null) return;
            int n = 0;
            try
            {
                // While the native list repopulates (handle creation, Show),
                // ItemChecked fires with the collection in a transient state
                // and can yield null entries - never let that crash the app.
                foreach (ListViewItem it in list.Items)
                    if (it != null && it.Checked) n++;
            }
            catch { return; }
            string suffix;
            Color col;
            if (n == 0) { suffix = "  - the game needs at least one!"; col = Color.Firebrick; }
            else if (n <= 8) { suffix = "  - good"; col = Color.ForestGreen; }
            else if (n <= 14) { suffix = "  - should be OK, fewer is safer"; col = Color.DarkOrange; }
            else { suffix = "  - too many, the game may still hang"; col = Color.Firebrick; }
            lblCount.Text = string.Format("Black Ops III will see {0} device(s){1}", n, suffix);
            lblCount.ForeColor = col;
        }

        private void OnBrowse(object s, EventArgs e)
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "Select the Call of Duty Black Ops III folder (the one containing BlackOps3.exe)";
            if (txtPath.Text.Trim().Length > 0 && Directory.Exists(txtPath.Text.Trim()))
                dlg.SelectedPath = txtPath.Text.Trim();
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                txtPath.Text = dlg.SelectedPath;
                if (!File.Exists(Path.Combine(dlg.SelectedPath, "BlackOps3.exe")))
                    Log("WARNING: BlackOps3.exe was not found in that folder.");
                RefreshDevices();
            }
        }

        private void OnRecommended(object s, EventArgs e)
        {
            suppressCount = true;
            foreach (ListViewItem it in list.Items) it.Checked = Recommended(it.Text);
            suppressCount = false;
            UpdateCount();
        }
        private void OnNone(object s, EventArgs e)
        {
            suppressCount = true;
            foreach (ListViewItem it in list.Items) it.Checked = false;
            suppressCount = false;
            UpdateCount();
        }
        private void OnAll(object s, EventArgs e)
        {
            suppressCount = true;
            foreach (ListViewItem it in list.Items) it.Checked = true;
            suppressCount = false;
            UpdateCount();
        }
        private void OnRescan(object s, EventArgs e)
        {
            defaultOut = AudioDefaults.GetDefaultName(0);
            defaultIn = AudioDefaults.GetDefaultName(1);
            RefreshDevices();
        }

        private static bool GameRunning()
        {
            try { return Process.GetProcessesByName("BlackOps3").Length > 0; }
            catch { return false; }
        }

        private static void ExtractPayload(string destPath)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream src = asm.GetManifestResourceStream("winmm.dll"))
            {
                if (src == null)
                    throw new Exception("The embedded winmm.dll payload is missing from this executable.");
                using (FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                    src.CopyTo(fs);
            }
        }

        private void OnInstall(object s, EventArgs e)
        {
            string game = txtPath.Text.Trim();
            if (game.Length == 0 || !File.Exists(Path.Combine(game, "BlackOps3.exe")))
            {
                MessageBox.Show("Pick the Black Ops III folder first - it must contain BlackOps3.exe.",
                    "Installer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (GameRunning())
            {
                MessageBox.Show("Black Ops III is running. Close the game first.",
                    "Installer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            List<string> keeps = new List<string>();
            foreach (ListViewItem it in list.Items) if (it.Checked) keeps.Add(it.Text);

            if (keeps.Count == 0)
            {
                MessageBox.Show("Tick at least one device, or the game will have no audio devices at all.",
                    "Installer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!string.IsNullOrEmpty(defaultOut))
            {
                bool haveDefault = false;
                foreach (string k in keeps)
                    if (k.ToLowerInvariant().StartsWith(defaultOut.ToLowerInvariant())) { haveDefault = true; break; }
                if (!haveDefault)
                {
                    DialogResult r = MessageBox.Show(
                        "Your Windows default playback device (" + defaultOut + ") is not ticked.\r\n\r\n"
                        + "The game will normally still play through it, but ticking it is safer.\r\n\r\nInstall anyway?",
                        "Installer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) return;
                }
            }

            try
            {
                string cfgPath = Path.Combine(game, "bo3_audio.cfg");
                if (File.Exists(cfgPath))
                {
                    string bak = cfgPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(cfgPath, bak, true);
                    Log("Backed up existing config -> " + Path.GetFileName(bak));
                }

                ExtractPayload(Path.Combine(game, "winmm.dll"));
                Log("Installed winmm.dll (the filter).");

                string sysWinmm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "winmm.dll");
                File.Copy(sysWinmm, Path.Combine(game, "winmm_real.dll"), true);
                Log("Installed winmm_real.dll (fresh copy of the real Windows winmm).");

                List<string> lines = new List<string>();
                lines.Add("# Black Ops III audio-device filter - generated by the installer");
                lines.Add("# " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                lines.Add("# Only devices matching a keep= line are visible to the game.");
                lines.Add("# Re-run the installer to change this, or edit by hand.");
                lines.Add("");
                foreach (string k in keeps) lines.Add("keep=" + k);
                lines.Add("");
                lines.Add("# set log=1 to have the game write bo3_audio.log listing what it saw");
                lines.Add("log=0");

                // ANSI: the DLL reads this with a classic-locale wide stream.
                File.WriteAllLines(cfgPath, lines.ToArray(), Encoding.Default);
                Log("Wrote bo3_audio.cfg with " + keeps.Count + " allowed device(s).");
                Log("DONE - launch Black Ops III normally.");

                MessageBox.Show("Installed.\r\n\r\nBlack Ops III will now see " + keeps.Count
                    + " audio device(s). Everything stays enabled in Windows.",
                    "Installer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (UnauthorizedAccessException)
            {
                OfferElevation(game);
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                MessageBox.Show("Install failed:\r\n" + ex.Message, "Installer",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OfferElevation(string game)
        {
            DialogResult r = MessageBox.Show(
                "Windows denied write access to the game folder.\r\n\r\n"
                + "This usually means Steam is installed under Program Files. "
                + "Restart the installer as administrator and try again?",
                "Administrator rights needed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.Arguments = "--path \"" + game + "\"";
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                Application.Exit();
            }
            catch (Exception ex)
            {
                Log("Could not restart as administrator: " + ex.Message);
            }
        }

        private void OnUninstall(object s, EventArgs e)
        {
            string game = txtPath.Text.Trim();
            if (game.Length == 0 || !Directory.Exists(game))
            {
                MessageBox.Show("Pick the Black Ops III folder first.", "Installer",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (GameRunning())
            {
                MessageBox.Show("Black Ops III is running. Close the game first.", "Installer",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult r = MessageBox.Show(
                "Remove winmm.dll, winmm_real.dll, bo3_audio.cfg and bo3_audio.log from:\r\n" + game
                + "\r\n\r\nThe game goes back to stock behaviour (and may black-screen again).",
                "Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            bool denied = false;
            foreach (string f in new string[] { "winmm.dll", "winmm_real.dll", "bo3_audio.cfg", "bo3_audio.log" })
            {
                string p = Path.Combine(game, f);
                try { if (File.Exists(p)) { File.Delete(p); Log("Removed " + f); } }
                catch (UnauthorizedAccessException) { denied = true; Log("Access denied removing " + f); }
                catch (Exception ex) { Log("Could not remove " + f + ": " + ex.Message); }
            }
            if (denied) OfferElevation(game);
            else Log("Uninstall finished.");
        }
    }

    internal static class Program
    {
        private static string LogPath()
        {
            try { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "bo3_installer_error.log"); }
            catch { return Path.Combine(Path.GetTempPath(), "bo3_installer_error.log"); }
        }

        private static void Report(Exception ex, string where)
        {
            string path = LogPath();
            try
            {
                File.AppendAllText(path,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + where + "\r\n"
                    + (ex == null ? "(no exception object)" : ex.ToString()) + "\r\n\r\n");
            }
            catch { }
            try
            {
                MessageBox.Show(
                    "The installer hit an unexpected error:\r\n\r\n"
                    + (ex == null ? "(unknown)" : ex.Message)
                    + "\r\n\r\nFull details were written to:\r\n" + path,
                    "BO3 Audio Fix Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); }
            catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Report(e.Exception, "UI thread exception");
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Report(e.ExceptionObject as Exception, "Unhandled exception");
            };

            string prefill = null;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--path" && i + 1 < args.Length) prefill = args[i + 1];

            try { Application.Run(new MainForm(prefill)); }
            catch (Exception ex) { Report(ex, "Startup"); }
        }
    }
}
