// NFC Bridge -- a small local HTTP server that lets the admin panel (running in a browser,
// over HTTPS, on lta-tooltracker.duckdns.org) write a user's login_token onto a physical NFC
// tag via a PC/SC contactless reader (e.g. the CIR615A) plugged into THIS machine.
//
// Why this exists at all: a web page cannot talk to arbitrary USB/PC-SC hardware directly, and
// there is no portable way to call Windows' native smart-card API (winscard.dll) from Node
// without a native addon -- which needs a C/C++ toolchain (Visual Studio Build Tools) this
// machine doesn't have. C#/.NET ships its own compiler (csc.exe, bundled with every Windows
// install) and can P/Invoke winscard.dll directly, so this needs nothing extra installed.
//
// Run with: build.bat, then NfcBridge.exe (see README.md). Leave the console window open --
// the admin panel's "Write to NFC Badge" button calls this over http://127.0.0.1:8732 for as
// long as it's running, and does nothing (with a clear error) if it isn't.
//
// Security note: this only binds to 127.0.0.1 (loopback), never the network -- nothing outside
// this one PC can ever reach it. It has no auth of its own (anyone with JS running in a browser
// on this same PC could call it), which is an acceptable tradeoff for a tool that only writes
// whatever token the admin panel already decided to issue -- it can't read out the database, it
// can only write one token (already fetched under the app's own hierarchy check) to whatever
// physical tag happens to be sitting on the reader at that moment.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

class NfcBridge
{
    const string LISTEN_PREFIX = "http://127.0.0.1:8732/";
    const int WRITE_TIMEOUT_MS = 20000;
    const int NTAG_FIRST_USER_PAGE = 4; // pages 0-3 are UID/lock/capability-container on NTAG21x/Ultralight

    static void Main()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(LISTEN_PREFIX);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine("Could not start on " + LISTEN_PREFIX + " -- " + ex.Message);
            Console.WriteLine("(Another copy of NfcBridge.exe may already be running -- check your taskbar.)");
            Console.WriteLine("Press Enter to exit.");
            Console.ReadLine();
            return;
        }

        Console.WriteLine("NFC Bridge running on " + LISTEN_PREFIX);
        Console.WriteLine("Leave this window open while using \"Write to NFC Badge\" in the admin panel.");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { break; }

            try { HandleRequest(ctx); }
            catch (Exception ex) { Console.WriteLine("Request error: " + ex.Message); }
        }
    }

    static void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // Every response needs standard CORS plus Chrome's Private Network Access header -- the
        // admin page's own origin is itself a private (RFC1918) address, and PNA still gates a
        // private-address page calling a loopback ("local" tier) address like this one.
        res.Headers.Add("Access-Control-Allow-Origin", "*");
        res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        res.Headers.Add("Access-Control-Allow-Private-Network", "true");

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/status")
        {
            HandleStatus(res);
        }
        else if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/write")
        {
            HandleWrite(req, res);
        }
        else
        {
            WriteJson(res, 404, new Dictionary<string, object> { { "ok", false }, { "error", "not found" } });
        }
    }

    static void HandleStatus(HttpListenerResponse res)
    {
        IntPtr ctx;
        int rc = Pcsc.SCardEstablishContext(Pcsc.SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out ctx);
        if (rc != 0)
        {
            WriteJson(res, 200, new Dictionary<string, object> { { "ok", false }, { "error", "Could not reach Windows' smart card service." } });
            return;
        }
        try
        {
            string[] readers = Pcsc.ListReaders(ctx);
            if (readers.Length == 0)
            {
                WriteJson(res, 200, new Dictionary<string, object> { { "ok", false }, { "error", "No NFC/smart-card reader found." } });
            }
            else
            {
                WriteJson(res, 200, new Dictionary<string, object> { { "ok", true }, { "reader", readers[0] } });
            }
        }
        finally
        {
            Pcsc.SCardReleaseContext(ctx);
        }
    }

    static void HandleWrite(HttpListenerRequest req, HttpListenerResponse res)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = reader.ReadToEnd();

        var serializer = new JavaScriptSerializer();
        Dictionary<string, object> parsed;
        try { parsed = (Dictionary<string, object>)serializer.DeserializeObject(body); }
        catch { WriteJson(res, 400, new Dictionary<string, object> { { "ok", false }, { "error", "Malformed request." } }); return; }

        object tokenObj;
        if (parsed == null || !parsed.TryGetValue("token", out tokenObj) || string.IsNullOrEmpty(tokenObj as string))
        {
            WriteJson(res, 400, new Dictionary<string, object> { { "ok", false }, { "error", "No token given." } });
            return;
        }
        string token = (string)tokenObj;

        IntPtr ctx;
        int rc = Pcsc.SCardEstablishContext(Pcsc.SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out ctx);
        if (rc != 0) { WriteJson(res, 200, new Dictionary<string, object> { { "ok", false }, { "error", "Could not reach Windows' smart card service." } }); return; }

        try
        {
            string[] readers = Pcsc.ListReaders(ctx);
            if (readers.Length == 0) { WriteJson(res, 200, new Dictionary<string, object> { { "ok", false }, { "error", "No NFC/smart-card reader found." } }); return; }
            string readerName = readers[0];

            string waitError;
            if (!Pcsc.WaitForCard(ctx, readerName, WRITE_TIMEOUT_MS, out waitError))
            {
                WriteJson(res, 200, new Dictionary<string, object> { { "ok", false }, { "error", waitError } });
                return;
            }

            IntPtr card;
            uint activeProtocol;
            rc = Pcsc.SCardConnect(ctx, readerName, Pcsc.SCARD_SHARE_SHARED, Pcsc.SCARD_PROTOCOL_T0 | Pcsc.SCARD_PROTOCOL_T1, out card, out activeProtocol);
            if (rc != 0) { WriteJson(res, 200, new Dictionary<string, object> { { "ok", false }, { "error", "Could not connect to the tag: " + Pcsc.DescribeError(rc) } }); return; }

            try
            {
                var ioReq = new Pcsc.SCARD_IO_REQUEST { dwProtocol = activeProtocol, cbPciLength = (uint)Marshal.SizeOf(typeof(Pcsc.SCARD_IO_REQUEST)) };

                byte[] uidResp;
                string uidErr;
                if (!Pcsc.Transmit(card, ioReq, new byte[] { 0xFF, 0xCA, 0x00, 0x00, 0x00 }, out uidResp, out uidErr))
                {
                    WriteJson(res, 200, new Dictionary<string, object> { { "ok", false }, { "error", "Could not read tag UID: " + uidErr } });
                    return;
                }
                string uidHex = BitConverter.ToString(uidResp, 0, Math.Max(0, uidResp.Length - 2)).Replace("-", "");

                byte[] pageData = Ndef.BuildTextTlv(token);
                int pageCount = pageData.Length / 4;
                for (int i = 0; i < pageCount; i++)
                {
                    int page = NTAG_FIRST_USER_PAGE + i;
                    byte[] apdu = new byte[] { 0xFF, 0xD6, 0x00, (byte)page, 0x04, pageData[i * 4], pageData[i * 4 + 1], pageData[i * 4 + 2], pageData[i * 4 + 3] };
                    byte[] writeResp; string writeErr;
                    if (!Pcsc.Transmit(card, ioReq, apdu, out writeResp, out writeErr))
                    {
                        WriteJson(res, 200, new Dictionary<string, object> { { "ok", false }, { "error", "Write failed at page " + page + ": " + writeErr } });
                        return;
                    }
                }

                // Verify by reading back the same pages and comparing.
                byte[] verifyBuf = new byte[pageCount * 4];
                for (int i = 0; i < pageCount; i++)
                {
                    int page = NTAG_FIRST_USER_PAGE + i;
                    byte[] readResp; string readErr;
                    if (!Pcsc.Transmit(card, ioReq, new byte[] { 0xFF, 0xB0, 0x00, (byte)page, 0x04 }, out readResp, out readErr))
                    {
                        WriteJson(res, 200, new Dictionary<string, object> { { "ok", false }, { "error", "Could not verify page " + page + ": " + readErr } });
                        return;
                    }
                    Array.Copy(readResp, 0, verifyBuf, i * 4, 4);
                }
                for (int i = 0; i < pageData.Length; i++)
                {
                    if (verifyBuf[i] != pageData[i])
                    {
                        WriteJson(res, 200, new Dictionary<string, object> { { "ok", false }, { "error", "Write did not verify -- tag may be read-only or locked." } });
                        return;
                    }
                }

                Console.WriteLine("Wrote badge for token ending ..." + token.Substring(Math.Max(0, token.Length - 6)) + " (tag UID " + uidHex + ")");
                WriteJson(res, 200, new Dictionary<string, object> { { "ok", true }, { "uid", uidHex } });
            }
            finally
            {
                Pcsc.SCardDisconnect(card, Pcsc.SCARD_LEAVE_CARD);
            }
        }
        finally
        {
            Pcsc.SCardReleaseContext(ctx);
        }
    }

    static void WriteJson(HttpListenerResponse res, int statusCode, Dictionary<string, object> obj)
    {
        var serializer = new JavaScriptSerializer();
        byte[] buf = Encoding.UTF8.GetBytes(serializer.Serialize(obj));
        res.StatusCode = statusCode;
        res.ContentType = "application/json";
        res.ContentLength64 = buf.Length;
        res.OutputStream.Write(buf, 0, buf.Length);
        res.OutputStream.Close();
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
        if (payload.Length > 255) throw new InvalidOperationException("Token too long for a short NDEF record.");

        byte[] type = Encoding.ASCII.GetBytes("T");
        // Record header: MB=1 ME=1 CF=0 SR=1 IL=0, TNF=0x01 (well-known) -> 0xD1
        byte[] ndef = new byte[1 + 1 + 1 + type.Length + payload.Length];
        int p = 0;
        ndef[p++] = 0xD1;
        ndef[p++] = (byte)type.Length;
        ndef[p++] = (byte)payload.Length;
        Array.Copy(type, 0, ndef, p, type.Length); p += type.Length;
        Array.Copy(payload, 0, ndef, p, payload.Length);

        if (ndef.Length > 255) throw new InvalidOperationException("Token too long for a single-byte TLV length.");

        byte[] tlv = new byte[1 + 1 + ndef.Length + 1];
        p = 0;
        tlv[p++] = 0x03; // NDEF Message TLV
        tlv[p++] = (byte)ndef.Length;
        Array.Copy(ndef, 0, tlv, p, ndef.Length); p += ndef.Length;
        tlv[p] = 0xFE; // Terminator TLV

        // Pad to a whole number of 4-byte pages with 0x00 (NDEF "NULL TLV" -- harmless filler).
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
    /// event-driven PC/SC pattern. Polling SCardConnect directly (an earlier quick test here
    /// did exactly that) proved unreliable: repeated immediate reconnect attempts can race the
    /// reader's own antenna/poll cycle and report "card removed" even when a tag is sitting
    /// right on it.
    /// </summary>
    public static bool WaitForCard(IntPtr ctx, string readerName, int timeoutMs, out string error)
    {
        error = null;
        var states = new SCARD_READERSTATE[1];
        states[0].szReader = readerName;
        states[0].dwCurrentState = SCARD_STATE_UNAWARE;
        states[0].rgbAtr = new byte[36];

        // Baseline read (0ms) -- just finds out what's true right now; a timeout here is normal
        // and not a real failure, so its return code is ignored.
        SCardGetStatusChange(ctx, 0, states, 1);

        if ((states[0].dwEventState & SCARD_STATE_PRESENT) != 0) return true; // already sitting there

        states[0].dwCurrentState = states[0].dwEventState & ~SCARD_STATE_CHANGED;
        int rc = SCardGetStatusChange(ctx, (uint)timeoutMs, states, 1);
        // SCARD_E_TIMEOUT (0x8010000A) is the normal, expected outcome of nobody presenting a
        // tag in time -- not a real error -- so it gets the same friendly message as the
        // (equally normal) case where the call returns but dwEventState never set PRESENT.
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
