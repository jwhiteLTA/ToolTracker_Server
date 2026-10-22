// ToolTracker NFC Tool -- a standalone Windows desktop app (not a web page) for writing a
// user's login token or a tool's barcode onto a physical NFC tag via a PC/SC contactless
// reader/writer (e.g. the CIR615A) plugged into this PC.
//
// Why standalone rather than a browser button calling a local helper (the previous design,
// nfc-bridge/NfcBridge.cs, now removed): a webpage hitting http://127.0.0.1 ran straight into
// browser security policy (Content-Security-Policy, CORS, Chrome's Private Network Access) --
// real friction that cost real debugging time, and would keep needing attention as those
// policies keep tightening. A desktop app isn't a webpage, so none of that applies, and it logs
// into the real ToolTracker API the same way the admin panel does, which means it inherits the
// exact same server-side permission checks (tool_rep+ for tools, hierarchy rules for user
// tokens) for free -- no separate authorization model to invent or maintain here.
//
// Build with build.bat (plain csc.exe, nothing extra to install). Run NfcTool.exe, log in with
// a normal ToolTracker badge/PIN, and use the tabs to search for a person or tool and write
// their value to a tag. See README.md for day-to-day usage.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Without this, HttpClient's TLS handshake against a modern HTTPS server can fail
        // outright on older .NET Framework configurations, which don't all enable TLS 1.2 by
        // default -- surfacing only as HttpClient's generic "An error occurred while sending
        // the request", with the real cause buried in an inner exception never shown anywhere.
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new LoginForm());
    }
}

/// <summary>Everything the app knows once logged in: the authenticated HttpClient (its cookie
/// container holds the session cookie, same as a browser would), and the viewer's own identity
/// -- role/weight gates which tabs and actions this exact person gets, mirroring the web admin
/// panel's own currentAdminWeight checks.</summary>
class ApiSession
{
    public HttpClient Client;
    public string BaseUrl;
    public string BadgeId;
    public string FullName;
    public string Role;
    public int Weight;

    static readonly Dictionary<string, int> ROLE_WEIGHTS = new Dictionary<string, int> {
        { "super_admin", 4 }, { "dept_admin", 3 }, { "tool_rep", 2 }, { "technician", 1 }
    };

    public static int WeightOf(string role)
    {
        int w;
        return role != null && ROLE_WEIGHTS.TryGetValue(role, out w) ? w : 0;
    }

    public async Task<Dictionary<string, object>> GetJson(string path)
    {
        var res = await Client.GetAsync(path);
        var text = await res.Content.ReadAsStringAsync();
        var serializer = new JavaScriptSerializer();
        Dictionary<string, object> parsed;
        try { parsed = (Dictionary<string, object>)serializer.DeserializeObject(text); }
        catch { parsed = new Dictionary<string, object>(); }
        if (!res.IsSuccessStatusCode && !parsed.ContainsKey("error")) parsed["error"] = "Request failed (HTTP " + (int)res.StatusCode + ").";
        return parsed;
    }
}

class LoginForm : Form
{
    TextBox serverBox, badgeBox, pinBox;
    Button loginBtn;
    Label statusLabel;

    public LoginForm()
    {
        Text = "ToolTracker NFC Tool -- Login";
        Width = 420; Height = 280;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var serverLbl = new Label { Text = "Server URL", Left = 20, Top = 15, Width = 370 };
        serverBox = new TextBox { Left = 20, Top = 37, Width = 370, Text = "https://lta-tooltracker.duckdns.org" };

        var badgeLbl = new Label { Text = "Badge ID or Username", Left = 20, Top = 68, Width = 370 };
        badgeBox = new TextBox { Left = 20, Top = 90, Width = 370 };

        var pinLbl = new Label { Text = "PIN", Left = 20, Top = 121, Width = 370 };
        pinBox = new TextBox { Left = 20, Top = 143, Width = 370, UseSystemPasswordChar = true };
        pinBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoLogin(); } };

        loginBtn = new Button { Text = "Log In", Left = 20, Top = 176, Width = 370, Height = 32 };
        loginBtn.Click += (s, e) => DoLogin();

        statusLabel = new Label { Left = 20, Top = 215, Width = 370, Height = 40, ForeColor = Color.Firebrick };

        Controls.Add(serverLbl); Controls.Add(serverBox);
        Controls.Add(badgeLbl); Controls.Add(badgeBox);
        Controls.Add(pinLbl); Controls.Add(pinBox);
        Controls.Add(loginBtn);
        Controls.Add(statusLabel);

        AcceptButton = loginBtn;
    }

    async void DoLogin()
    {
        loginBtn.Enabled = false;
        statusLabel.ForeColor = Color.DimGray;
        statusLabel.Text = "Logging in...";
        try
        {
            string baseUrl = serverBox.Text.Trim().TrimEnd('/');
            var cookies = new CookieContainer();
            var handler = new HttpClientHandler { CookieContainer = cookies };
            var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl + "/") };

            var serializer = new JavaScriptSerializer();
            var body = serializer.Serialize(new Dictionary<string, object> {
                { "login_id", badgeBox.Text.Trim() }, { "pin", pinBox.Text.Trim() }
            });
            var res = await client.PostAsync("api/login", new StringContent(body, Encoding.UTF8, "application/json"));
            var text = await res.Content.ReadAsStringAsync();
            var parsed = (Dictionary<string, object>)serializer.DeserializeObject(text);

            if (!res.IsSuccessStatusCode)
            {
                statusLabel.ForeColor = Color.Firebrick;
                statusLabel.Text = parsed.ContainsKey("error") ? (string)parsed["error"] : "Login failed.";
                loginBtn.Enabled = true;
                return;
            }

            var user = (Dictionary<string, object>)parsed["user"];
            var session = new ApiSession
            {
                Client = client,
                BaseUrl = baseUrl,
                BadgeId = (string)user["badge_id"],
                FullName = (string)user["full_name"],
                Role = (string)user["role"],
            };
            session.Weight = ApiSession.WeightOf(session.Role);

            var main = new MainForm(session);
            main.FormClosed += (s, e2) => Close();
            main.Show();
            Hide();
        }
        catch (Exception ex)
        {
            statusLabel.ForeColor = Color.Firebrick;
            statusLabel.Text = "Connection error: " + Describe(ex);
            loginBtn.Enabled = true;
        }
    }

    /// <summary>HttpClient wraps the real cause in a generic "An error occurred while sending
    /// the request" HttpRequestException -- the actually useful message (TLS failure, DNS
    /// failure, connection refused, etc.) is on .InnerException, sometimes nested further.
    /// Walks to the deepest one so the status label shows something a person can act on.</summary>
    internal static string Describe(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;
        return ex.Message;
    }
}

class MainForm : Form
{
    readonly ApiSession session;
    Label readerLabel;

    TextBox userSearchBox;
    ListView userList;
    Button writeUserBtn;
    Label userStatusLabel;
    List<Dictionary<string, object>> allUsers = new List<Dictionary<string, object>>();

    TextBox toolSearchBox;
    ListView toolList;
    Button writeToolBtn;
    Label toolStatusLabel;
    List<Dictionary<string, object>> allTools = new List<Dictionary<string, object>>();

    Button writeMyBadgeBtn;
    Label myBadgeStatusLabel;

    public MainForm(ApiSession session)
    {
        this.session = session;
        Text = "ToolTracker NFC Tool";
        Width = 660; Height = 580;
        StartPosition = FormStartPosition.CenterScreen;

        var headerLabel = new Label {
            Left = 15, Top = 12, Width = 620,
            Text = "Logged in as " + session.FullName + " (" + session.Role + ")",
            Font = new Font(Font, FontStyle.Bold)
        };
        readerLabel = new Label { Left = 15, Top = 34, Width = 620, ForeColor = Color.DimGray, Text = "Checking for a reader..." };

        var tabs = new TabControl { Left = 15, Top = 60, Width = 615, Height = 470 };
        var myTab = BuildMyBadgeTab();
        var usersTab = BuildUsersTab();
        tabs.TabPages.Add(myTab);
        tabs.TabPages.Add(usersTab);
        // tool_rep+ only -- matches canEditTools in the web admin panel (PUT /api/tools/:id's
        // own requireRole(2)). Writing a tool's tag needs no server round trip at all (its
        // qr_code is already public, unlike a user's login_token), so there's no server-side
        // gate to lean on here the way login-token-value provides for the Users tab -- this
        // client-side check is the only thing standing in for one, same as the web panel's own
        // canEditTools hides its button client-side for the identical reason.
        if (session.Weight >= 2) tabs.TabPages.Add(BuildToolsTab());

        Controls.Add(headerLabel);
        Controls.Add(readerLabel);
        Controls.Add(tabs);

        Load += async (s, e) => { await RefreshReaderStatus(); await LoadUsers(); await LoadTools(); };
    }

    async Task RefreshReaderStatus()
    {
        string reader = await Task.Run(() => NfcWriter.FindReaderName());
        readerLabel.Text = reader != null ? ("Reader: " + reader) : "No NFC reader found -- plug one in and restart this app.";
        readerLabel.ForeColor = reader != null ? Color.SeaGreen : Color.Firebrick;
    }

    TabPage BuildMyBadgeTab()
    {
        var tab = new TabPage("My Badge");
        var desc = new Label {
            Left = 15, Top = 15, Width = 560, Height = 40,
            Text = "Writes your own login card onto a tag -- the same credential your QR login card already encodes."
        };
        writeMyBadgeBtn = new Button { Left = 15, Top = 60, Width = 220, Height = 32, Text = "Write My Badge to Tag" };
        myBadgeStatusLabel = new Label { Left = 15, Top = 100, Width = 560, Height = 60 };

        writeMyBadgeBtn.Click += async (s, e) => await DoWrite(async () => {
            var json = await session.GetJson("api/users/me/login-token-value");
            if (json.ContainsKey("error")) throw new Exception((string)json["error"]);
            return (string)json["login_token"];
        }, writeMyBadgeBtn, myBadgeStatusLabel, "Badge");

        tab.Controls.Add(desc);
        tab.Controls.Add(writeMyBadgeBtn);
        tab.Controls.Add(myBadgeStatusLabel);
        return tab;
    }

    TabPage BuildUsersTab()
    {
        var tab = new TabPage("Users");
        userSearchBox = new TextBox { Left = 15, Top = 15, Width = 560 };
        userSearchBox.TextChanged += (s, e) => FilterUserList();

        userList = new ListView { Left = 15, Top = 45, Width = 560, Height = 290, View = View.Details, FullRowSelect = true, MultiSelect = false };
        userList.Columns.Add("Badge ID", 130);
        userList.Columns.Add("Name", 280);
        userList.Columns.Add("Login Card", 130);
        userList.SelectedIndexChanged += (s, e) => { writeUserBtn.Enabled = userList.SelectedItems.Count > 0; };

        writeUserBtn = new Button { Left = 15, Top = 345, Width = 240, Height = 32, Text = "Write Selected Badge to Tag", Enabled = false };
        userStatusLabel = new Label { Left = 15, Top = 385, Width = 560, Height = 60 };

        writeUserBtn.Click += async (s, e) => {
            if (userList.SelectedItems.Count == 0) return;
            string badgeId = (string)userList.SelectedItems[0].Tag;
            await DoWrite(async () => {
                var json = await session.GetJson("api/users/" + Uri.EscapeDataString(badgeId) + "/login-token-value");
                if (json.ContainsKey("error")) throw new Exception((string)json["error"]);
                return (string)json["login_token"];
            }, writeUserBtn, userStatusLabel, "Badge");
        };

        tab.Controls.Add(userSearchBox);
        tab.Controls.Add(userList);
        tab.Controls.Add(writeUserBtn);
        tab.Controls.Add(userStatusLabel);
        return tab;
    }

    TabPage BuildToolsTab()
    {
        var tab = new TabPage("Tools");
        toolSearchBox = new TextBox { Left = 15, Top = 15, Width = 560 };
        toolSearchBox.TextChanged += (s, e) => FilterToolList();

        toolList = new ListView { Left = 15, Top = 45, Width = 560, Height = 290, View = View.Details, FullRowSelect = true, MultiSelect = false };
        toolList.Columns.Add("Barcode", 160);
        toolList.Columns.Add("Name", 380);
        toolList.SelectedIndexChanged += (s, e) => { writeToolBtn.Enabled = toolList.SelectedItems.Count > 0; };

        writeToolBtn = new Button { Left = 15, Top = 345, Width = 200, Height = 32, Text = "Write Selected Tag", Enabled = false };
        toolStatusLabel = new Label { Left = 15, Top = 385, Width = 560, Height = 60 };

        // No fetch needed before writing -- a tool's qr_code isn't a secret (it's already
        // printed on the tool's own barcode label) and is already sitting in allTools from
        // LoadTools(), unlike a user's login_token which has to be fetched fresh and under a
        // hierarchy check every time (see writeUserBtn's handler above).
        writeToolBtn.Click += async (s, e) => {
            if (toolList.SelectedItems.Count == 0) return;
            string qrCode = (string)toolList.SelectedItems[0].Tag;
            await DoWrite(() => Task.FromResult(qrCode), writeToolBtn, toolStatusLabel, "Tag");
        };

        tab.Controls.Add(toolSearchBox);
        tab.Controls.Add(toolList);
        tab.Controls.Add(writeToolBtn);
        tab.Controls.Add(toolStatusLabel);
        return tab;
    }

    async Task DoWrite(Func<Task<string>> getValue, Button btn, Label statusLabel, string label)
    {
        btn.Enabled = false;
        statusLabel.ForeColor = Color.DimGray;
        statusLabel.Text = "Fetching...";
        try
        {
            string value = await getValue();
            statusLabel.Text = "Tap the tag on the reader now (up to 20 seconds)...";
            var result = await Task.Run(() => NfcWriter.WriteValue(value));
            if (result.Ok)
            {
                statusLabel.ForeColor = Color.SeaGreen;
                statusLabel.Text = label + " written successfully (tag UID " + result.Uid + ").";
            }
            else
            {
                statusLabel.ForeColor = Color.Firebrick;
                statusLabel.Text = result.Error;
            }
        }
        catch (Exception ex)
        {
            statusLabel.ForeColor = Color.Firebrick;
            statusLabel.Text = LoginForm.Describe(ex);
        }
        finally
        {
            btn.Enabled = true;
        }
    }

    async Task LoadUsers()
    {
        try
        {
            var json = await session.GetJson("api/users");
            if (json.ContainsKey("error")) { userStatusLabel.Text = (string)json["error"]; return; }
            allUsers = ((object[])json["users"]).Select(o => (Dictionary<string, object>)o).ToList();
            FilterUserList();
        }
        catch (Exception ex) { userStatusLabel.Text = "Failed to load users: " + LoginForm.Describe(ex); }
    }

    void FilterUserList()
    {
        string q = (userSearchBox.Text ?? "").Trim().ToLowerInvariant();
        userList.Items.Clear();
        foreach (var u in allUsers)
        {
            string badgeId = (string)u["badge_id"];
            string name = (string)u["full_name"];
            if (q.Length > 0 && !badgeId.ToLowerInvariant().Contains(q) && !name.ToLowerInvariant().Contains(q)) continue;
            bool hasCard = (bool)u["has_login_card"];
            var item = new ListViewItem(new[] { badgeId, name, hasCard ? "Issued" : "Not issued" });
            item.Tag = badgeId;
            userList.Items.Add(item);
        }
    }

    async Task LoadTools()
    {
        if (toolList == null) return; // tab not built for this role (see constructor)
        try
        {
            var json = await session.GetJson("api/tools");
            if (json.ContainsKey("error")) { toolStatusLabel.Text = (string)json["error"]; return; }
            allTools = ((object[])json["tools"]).Select(o => (Dictionary<string, object>)o).ToList();
            FilterToolList();
        }
        catch (Exception ex) { toolStatusLabel.Text = "Failed to load tools: " + LoginForm.Describe(ex); }
    }

    void FilterToolList()
    {
        string q = (toolSearchBox.Text ?? "").Trim().ToLowerInvariant();
        toolList.Items.Clear();
        foreach (var t in allTools)
        {
            string qr = (string)t["qr_code"];
            string name = (string)t["name"];
            if (q.Length > 0 && !qr.ToLowerInvariant().Contains(q) && !name.ToLowerInvariant().Contains(q)) continue;
            var item = new ListViewItem(new[] { qr, name });
            item.Tag = qr;
            toolList.Items.Add(item);
        }
    }
}

class WriteResult
{
    public bool Ok;
    public string Error;
    public string Uid;
}

/// <summary>High-level "write this value to whatever tag is presented" entry point used by every
/// tab -- a user's login_token and a tool's qr_code are both just strings by the time they get
/// here; this has no idea which kind of thing it's writing, same as the old NfcBridge.cs's /write
/// endpoint never did either.</summary>
static class NfcWriter
{
    const int WRITE_TIMEOUT_MS = 20000;
    const int NTAG_FIRST_USER_PAGE = 4; // pages 0-3 are UID/lock/capability-container on NTAG21x/Ultralight

    public static string FindReaderName()
    {
        IntPtr ctx;
        if (Pcsc.SCardEstablishContext(Pcsc.SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out ctx) != 0) return null;
        try
        {
            var readers = Pcsc.ListReaders(ctx);
            return readers.Length > 0 ? readers[0] : null;
        }
        finally { Pcsc.SCardReleaseContext(ctx); }
    }

    public static WriteResult WriteValue(string value)
    {
        var result = new WriteResult();
        IntPtr ctx;
        int rc = Pcsc.SCardEstablishContext(Pcsc.SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out ctx);
        if (rc != 0) { result.Error = "Could not reach Windows' smart card service."; return result; }

        try
        {
            var readers = Pcsc.ListReaders(ctx);
            if (readers.Length == 0) { result.Error = "No NFC/smart-card reader found."; return result; }
            string readerName = readers[0];

            string waitError;
            if (!Pcsc.WaitForCard(ctx, readerName, WRITE_TIMEOUT_MS, out waitError)) { result.Error = waitError; return result; }

            IntPtr card;
            uint activeProtocol;
            rc = Pcsc.SCardConnect(ctx, readerName, Pcsc.SCARD_SHARE_SHARED, Pcsc.SCARD_PROTOCOL_T0 | Pcsc.SCARD_PROTOCOL_T1, out card, out activeProtocol);
            if (rc != 0) { result.Error = "Could not connect to the tag: " + Pcsc.DescribeError(rc); return result; }

            try
            {
                var ioReq = new Pcsc.SCARD_IO_REQUEST { dwProtocol = activeProtocol, cbPciLength = (uint)Marshal.SizeOf(typeof(Pcsc.SCARD_IO_REQUEST)) };

                byte[] uidResp; string uidErr;
                if (!Pcsc.Transmit(card, ioReq, new byte[] { 0xFF, 0xCA, 0x00, 0x00, 0x00 }, out uidResp, out uidErr))
                { result.Error = "Could not read tag UID: " + uidErr; return result; }
                result.Uid = BitConverter.ToString(uidResp, 0, Math.Max(0, uidResp.Length - 2)).Replace("-", "");

                byte[] pageData = Ndef.BuildTextTlv(value);
                int pageCount = pageData.Length / 4;
                for (int i = 0; i < pageCount; i++)
                {
                    int page = NTAG_FIRST_USER_PAGE + i;
                    byte[] apdu = new byte[] { 0xFF, 0xD6, 0x00, (byte)page, 0x04, pageData[i * 4], pageData[i * 4 + 1], pageData[i * 4 + 2], pageData[i * 4 + 3] };
                    byte[] writeResp; string writeErr;
                    if (!Pcsc.Transmit(card, ioReq, apdu, out writeResp, out writeErr))
                    { result.Error = "Write failed at page " + page + ": " + writeErr; return result; }
                }

                byte[] verifyBuf = new byte[pageCount * 4];
                for (int i = 0; i < pageCount; i++)
                {
                    int page = NTAG_FIRST_USER_PAGE + i;
                    byte[] readResp; string readErr;
                    if (!Pcsc.Transmit(card, ioReq, new byte[] { 0xFF, 0xB0, 0x00, (byte)page, 0x04 }, out readResp, out readErr))
                    { result.Error = "Could not verify page " + page + ": " + readErr; return result; }
                    Array.Copy(readResp, 0, verifyBuf, i * 4, 4);
                }
                for (int i = 0; i < pageData.Length; i++)
                {
                    if (verifyBuf[i] != pageData[i]) { result.Error = "Write did not verify -- tag may be read-only or locked."; return result; }
                }

                result.Ok = true;
                return result;
            }
            finally { Pcsc.SCardDisconnect(card, Pcsc.SCARD_LEAVE_CARD); }
        }
        finally { Pcsc.SCardReleaseContext(ctx); }
    }
}

/// <summary>
/// NDEF Text-record encoding, wrapped in the Type-2-tag TLV format NTAG/Ultralight tags expect
/// in their user memory (starting at page 4). See NFC Forum's "Type 2 Tag Operation" and "Text
/// Record Type Definition" specs -- this hand-rolls the handful of bytes those define rather
/// than pulling in a dependency, since it's a fixed, small, well-documented format.
/// </summary>
static class Ndef
{
    public static byte[] BuildTextTlv(string text)
    {
        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        byte[] lang = Encoding.ASCII.GetBytes("en");
        byte statusByte = (byte)lang.Length; // high bit 0 = UTF-8 (not UTF-16)

        byte[] payload = new byte[1 + lang.Length + textBytes.Length];
        payload[0] = statusByte;
        Array.Copy(lang, 0, payload, 1, lang.Length);
        Array.Copy(textBytes, 0, payload, 1 + lang.Length, textBytes.Length);
        if (payload.Length > 255) throw new InvalidOperationException("Value too long for a short NDEF record.");

        byte[] type = Encoding.ASCII.GetBytes("T");
        // Record header: MB=1 ME=1 CF=0 SR=1 IL=0, TNF=0x01 (well-known) -> 0xD1
        byte[] ndef = new byte[1 + 1 + 1 + type.Length + payload.Length];
        int p = 0;
        ndef[p++] = 0xD1;
        ndef[p++] = (byte)type.Length;
        ndef[p++] = (byte)payload.Length;
        Array.Copy(type, 0, ndef, p, type.Length); p += type.Length;
        Array.Copy(payload, 0, ndef, p, payload.Length);

        if (ndef.Length > 255) throw new InvalidOperationException("Value too long for a single-byte TLV length.");

        byte[] tlv = new byte[1 + 1 + ndef.Length + 1];
        p = 0;
        tlv[p++] = 0x03; // NDEF Message TLV
        tlv[p++] = (byte)ndef.Length;
        Array.Copy(ndef, 0, tlv, p, ndef.Length); p += ndef.Length;
        tlv[p] = 0xFE; // Terminator TLV

        int padded = ((tlv.Length + 3) / 4) * 4;
        if (padded == tlv.Length) return tlv;
        byte[] result = new byte[padded];
        Array.Copy(tlv, result, tlv.Length);
        return result;
    }
}

/// <summary>
/// Thin P/Invoke wrapper over winscard.dll -- just the handful of calls this tool needs.
/// Deliberately not a general-purpose PC/SC library: no dependency beyond what Windows itself
/// ships, which is the whole point (see the file header comment).
/// </summary>
static class Pcsc
{
    public const uint SCARD_SCOPE_USER = 0;
    public const uint SCARD_SHARE_SHARED = 2;
    public const uint SCARD_PROTOCOL_T0 = 1;
    public const uint SCARD_PROTOCOL_T1 = 2;
    public const uint SCARD_LEAVE_CARD = 0;
    const uint SCARD_STATE_UNAWARE = 0x0000;
    const uint SCARD_STATE_CHANGED = 0x0002;
    const uint SCARD_STATE_PRESENT = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct SCARD_IO_REQUEST
    {
        public uint dwProtocol;
        public uint cbPciLength;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct SCARD_READERSTATE
    {
        [MarshalAs(UnmanagedType.LPTStr)] public string szReader;
        public IntPtr pvUserData;
        public uint dwCurrentState;
        public uint dwEventState;
        public uint cbAtr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)] public byte[] rgbAtr;
    }

    [DllImport("winscard.dll")]
    public static extern int SCardEstablishContext(uint dwScope, IntPtr pvReserved1, IntPtr pvReserved2, out IntPtr phContext);

    [DllImport("winscard.dll")]
    public static extern int SCardReleaseContext(IntPtr hContext);

    [DllImport("winscard.dll", CharSet = CharSet.Auto)]
    static extern int SCardListReaders(IntPtr hContext, byte[] mszGroups, char[] mszReaders, ref int pcchReaders);

    [DllImport("winscard.dll", CharSet = CharSet.Auto)]
    public static extern int SCardConnect(IntPtr hContext, string szReader, uint dwShareMode, uint dwPreferredProtocols, out IntPtr phCard, out uint pdwActiveProtocol);

    [DllImport("winscard.dll")]
    public static extern int SCardDisconnect(IntPtr hCard, uint dwDisposition);

    [DllImport("winscard.dll")]
    static extern int SCardTransmit(IntPtr hCard, ref SCARD_IO_REQUEST pioSendPci, byte[] pbSendBuffer, int cbSendLength, IntPtr pioRecvPci, byte[] pbRecvBuffer, ref int pcbRecvLength);

    [DllImport("winscard.dll", CharSet = CharSet.Auto)]
    static extern int SCardGetStatusChange(IntPtr hContext, uint dwTimeout, [In, Out] SCARD_READERSTATE[] rgReaderStates, int cReaders);

    public static string[] ListReaders(IntPtr ctx)
    {
        int pcchReaders = 0;
        SCardListReaders(ctx, null, null, ref pcchReaders);
        if (pcchReaders <= 0) return new string[0];
        char[] buf = new char[pcchReaders];
        int rc = SCardListReaders(ctx, null, buf, ref pcchReaders);
        if (rc != 0) return new string[0];
        string all = new string(buf, 0, pcchReaders);
        var list = new List<string>();
        foreach (var s in all.Split('\0')) if (!string.IsNullOrEmpty(s)) list.Add(s);
        return list.ToArray();
    }

    /// <summary>
    /// Blocks until a tag is presented to the named reader (or already is), using
    /// SCardGetStatusChange rather than polling SCardConnect in a loop -- the correct,
    /// event-driven PC/SC pattern.
    /// </summary>
    public static bool WaitForCard(IntPtr ctx, string readerName, int timeoutMs, out string error)
    {
        error = null;
        var states = new SCARD_READERSTATE[1];
        states[0].szReader = readerName;
        states[0].dwCurrentState = SCARD_STATE_UNAWARE;
        states[0].rgbAtr = new byte[36];

        SCardGetStatusChange(ctx, 0, states, 1); // baseline read -- a timeout here is normal, not a failure

        if ((states[0].dwEventState & SCARD_STATE_PRESENT) != 0) return true; // already sitting there

        states[0].dwCurrentState = states[0].dwEventState & ~SCARD_STATE_CHANGED;
        int rc = SCardGetStatusChange(ctx, (uint)timeoutMs, states, 1);
        // SCARD_E_TIMEOUT (0x8010000A) is the normal, expected outcome of nobody presenting a
        // tag in time, not a real error -- same friendly message as the dwEventState check below.
        if (rc != 0 && unchecked((uint)rc) != 0x8010000A) { error = "Error waiting for a tag: " + DescribeError(rc); return false; }
        if ((states[0].dwEventState & SCARD_STATE_PRESENT) == 0) { error = "No tag was presented within " + (timeoutMs / 1000) + " seconds."; return false; }
        return true;
    }

    public static bool Transmit(IntPtr card, SCARD_IO_REQUEST ioReq, byte[] apdu, out byte[] response, out string error)
    {
        response = null;
        byte[] recvBuf = new byte[256];
        int recvLen = recvBuf.Length;
        int rc = SCardTransmit(card, ref ioReq, apdu, apdu.Length, IntPtr.Zero, recvBuf, ref recvLen);
        if (rc != 0) { error = DescribeError(rc); return false; }
        if (recvLen < 2 || recvBuf[recvLen - 2] != 0x90 || recvBuf[recvLen - 1] != 0x00)
        {
            error = recvLen >= 2
                ? "card returned status " + recvBuf[recvLen - 2].ToString("X2") + recvBuf[recvLen - 1].ToString("X2")
                : "card returned an unreadable response";
            return false;
        }
        byte[] result = new byte[recvLen];
        Array.Copy(recvBuf, result, recvLen);
        response = result;
        error = null;
        return true;
    }

    public static string DescribeError(int rc)
    {
        switch ((uint)rc)
        {
            case 0x80100069: return "the tag was removed before the operation finished -- hold it steady on the reader";
            case 0x8010000C: return "no tag is present";
            case 0x80100010: return "reader not ready";
            case 0x80100066: return "tag unresponsive -- try repositioning it";
            case 0x8010000B: return "the reader is in use by another program";
            default: return "error 0x" + rc.ToString("X");
        }
    }
}
