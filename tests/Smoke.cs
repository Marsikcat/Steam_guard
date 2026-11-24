using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LocalSteamGuard;

internal static class Smoke
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--probe-qr")
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var steam = new SteamApi())
            {
                var session = steam.BeginQrLogin(CancellationToken.None).GetAwaiter().GetResult();
                Check(session.QrUrl.StartsWith("https://s.team/q/", StringComparison.Ordinal), "Live Steam QR origin");
                new QrCode(session.QrUrl);
            }
            Console.WriteLine("Steam issued a live QR login challenge. No account signed in.");
            return;
        }

        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Check(GuardCode.Generate(secret, 29) == "GG5F5" && GuardCode.Generate(secret, 30) == "PV9M4"
            && GuardCode.Generate(secret, 60) == "B26KJ", "Code window boundaries");
        Check(new SteamWire.Reply(new SteamWire().Number(1, ulong.MaxValue).Fixed64(2, 76561198000000000)
            .Text(3, "Код").ToArray()).Number(1) == ulong.MaxValue, "Protocol large integer");
        Check(new SteamWire.Reply(new byte[] { 10, 3, 7, 150, 1 }).RepeatedNumbers(1).SequenceEqual(new ulong[] { 7, 150 }),
            "Packed list");
        try { new SteamWire.Reply(new byte[] { 10, 10, 1 }); throw new Exception("Invalid message accepted"); }
        catch (InvalidDataException) { }

        string isolated = Path.Combine(args[0], "smoke-" + Guid.NewGuid().ToString("N"));
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var fake = new SteamFixture(secret))
        using (var api = new SteamApi(fake, () => "Synthetic Windows client"))
        {
            Account transferred = TestTransferAndRequests(api, fake).GetAwaiter().GetResult();
            using (var vault = new Vault(isolated))
            {
                vault.Save(new System.Collections.Generic.List<Account> {
                    transferred, new Account("Другой аккаунт", secret.ToArray())
                });
                byte[] encrypted = File.ReadAllBytes(Path.Combine(isolated, "accounts.dat"));
                Check(!Encoding.UTF8.GetString(encrypted).Contains("refresh-fixture"), "Token protected at rest");
                var accounts = vault.Load();
                Check(accounts.Count == 2 && accounts[0].Registration.Id == SteamFixture.SteamId
                    && accounts[0].Registration.IdentitySecret.SequenceEqual(secret) && accounts[1].Registration == null,
                    "Both account types reload");
                using (var window = new GuardWindow(vault, accounts, api))
                {
                    ShowOffscreen(window);
                    var list = Field<ListBox>(window, "accountList");
                    list.SelectedItem = accounts[0];
                    Check(Field<Button>(window, "requests").Visible && Field<Label>(window, "codeText").Text.Length == 5,
                        "Connected account has codes and confirmations");
                    window.Size = window.MinimumSize;
                    window.PerformLayout();
                    Application.DoEvents();
                    Capture(window, Path.Combine(isolated, "window.png"));
                }
            }


        }
        using (var api = new SteamApi(new QrFixture(FirstQr), () => "Fixture QR client"))
        using (var dialog = new ConnectionDialog(api, id => false, () => { }, account => account))
        {
            ShowOffscreen(dialog);
            Application.DoEvents();
            var panel = Field<QrSignInPanel>(dialog, "qrPanel");
            var password = Field<TextBox>(dialog, "second");
            Check(panel.Visible && password.Visible && password.UseSystemPasswordChar,
                "QR and masked password are available in the same window");
            Check(Field<Label>(panel, "status").Text.StartsWith("Ожидаю", StringComparison.Ordinal),
                "QR renders while waiting for Steam Mobile");
            Capture(dialog, Path.Combine(isolated, "connect.png"));
            Field<TextBox>(dialog, "first").Text = "unsent-login";
            password.Text = "unsent-test-password";
            var until = System.Diagnostics.Stopwatch.StartNew();
            while (panel.Visible && until.Elapsed < TimeSpan.FromSeconds(5))
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }
            Check(!panel.Visible && password.TextLength == 0
                && Field<Label>(dialog, "heading").Text == "Перенос на этот ПК"
                && Field<Label>(dialog, "instruction").Text.Contains("fixture"),
                "Confirmed QR advances to SMS consent and clears unsent credentials");
            dialog.Close();
            Application.DoEvents();
        }
        TestQr(isolated).GetAwaiter().GetResult();
        Console.WriteLine("QR sign-in, protocol, transfer, approvals, DPAPI, and Windows UI checks passed with fixtures.");
        Console.WriteLine(isolated);
    }

    private const string FirstQr = "https://s.team/q/1/4242424242";
    private const string NextQr = "https://s.team/q/1/4242424243";

    private static async Task TestQr(string isolated)
    {
        using (var fixture = new QrFixture(FirstQr))
        using (var api = new SteamApi(fixture, () => "Fixture QR client"))
        {
            var cancel = CancellationToken.None;
            var login = await api.BeginQrLogin(cancel).ConfigureAwait(false);
            Check(login.QrUrl == FirstQr && login.SteamId == 0, "QR starts without an account");
            login.Interval = TimeSpan.Zero;
            var changes = new System.Collections.Generic.List<string>();
            await api.WaitForLogin(login, cancel, new QrChanges(changes)).ConfigureAwait(false);
            Check(changes.SequenceEqual(new[] { NextQr }) && fixture.Polls == 2, "Steam rotates active QR code");
            Check(login.SteamId == SteamFixture.SteamId && login.Name == "fixture"
                && login.AccessToken == "access-fixture", "Steam identifies QR-authenticated account");
        }

        using (var fixture = new QrFixture("https://s.team.example/q/1/4242424242"))
        using (var api = new SteamApi(fixture, () => "Fixture QR client"))
        {
            try
            {
                await api.BeginQrLogin(CancellationToken.None).ConfigureAwait(false);
                throw new Exception("Untrusted QR origin accepted");
            }
            catch (InvalidDataException) { }
        }

        WriteQr(FirstQr, Path.Combine(isolated, "qr.png"));
        string max = FirstQr + "?" + new string('a', QrCode.MaxBytes - FirstQr.Length - 1);
        WriteQr(max, Path.Combine(isolated, "qr-max.png"));
        try { new QrCode(max + "z"); throw new Exception("Oversized QR payload accepted"); }
        catch (ArgumentException) { }
    }

    private static void WriteQr(string data, string path)
    {
        var code = new QrCode(data);
        const int scale = 6;
        const int border = 4;
        int width = (QrCode.Size + 2 * border) * scale;
        using (var image = new Bitmap(width, width))
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            for (int y = 0; y < QrCode.Size; y++)
                for (int x = 0; x < QrCode.Size; x++)
                    if (code.IsDark(x, y))
                        graphics.FillRectangle(Brushes.Black, (x + border) * scale, (y + border) * scale, scale, scale);
            image.Save(path, ImageFormat.Png);
        }
    }

    private sealed class QrChanges : IProgress<string>
    {
        private readonly System.Collections.Generic.List<string> changes;
        public QrChanges(System.Collections.Generic.List<string> changes) { this.changes = changes; }
        public void Report(string value) { changes.Add(value); }
    }

    private sealed class QrFixture : HttpMessageHandler
    {
        private readonly string qr;
        public int Polls;
        public QrFixture(string qr) { this.qr = qr; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri.Host == "api.steampowered.com" && request.Method == HttpMethod.Post, "QR uses Steam API");
            string payload = await ReadForm(request.Content).ConfigureAwait(false);
            var input = new SteamWire.Reply(Convert.FromBase64String(payload));
            var output = new SteamWire();
            string method = request.RequestUri.Segments[2].Trim('/');
            if (method == "BeginAuthSessionViaQR")
            {
                Check(input.Text(1) == "Fixture QR client" && input.Number(2) == 3
                    && new SteamWire.Reply(input.Bytes(3)).Number(2) == 3, "QR opens mobile login without credentials");
                output.Number(1, 11).Text(2, qr).Bytes(3, new byte[] { 8, 9 });
            }
            else
            {
                Check(method == "PollAuthSessionStatus" && input.Bytes(2).SequenceEqual(new byte[] { 8, 9 }), "QR polling request");
                Polls++;
                if (Polls == 1)
                {
                    Check(input.Number(1) == 11, "Original client ID");
                    output.Number(1, 12).Text(2, NextQr);
                }
                else
                {
                    Check(input.Number(1) == 12, "New client ID after rotation");
                    string body = "{\"sub\":\"76561198000000000\"}";
                    string subject = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)).TrimEnd('=');
                    output.Text(3, "fixture." + subject + ".test").Text(4, "access-fixture").Text(6, "fixture");
                }
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) };
            response.Headers.Add("x-eresult", "1");
            return response;
        }
    }

    private static async Task<Account> TestTransferAndRequests(SteamApi api, SteamFixture fake)
    {
        var cancel = CancellationToken.None;
        var login = await api.BeginLogin("fixture", Encoding.ASCII.GetBytes("test-only"), cancel).ConfigureAwait(false);
        Check(login.Confirmations.Contains(4) && login.Confirmations.Contains(3), "Mobile confirmation options");
        await api.SubmitLoginCode(login, "P2V89", 3, cancel).ConfigureAwait(false);
        await api.WaitForLogin(login, cancel).ConfigureAwait(false);
        await api.StartTransfer(login, cancel).ConfigureAwait(false);
        var account = await api.FinishTransfer(login, "123456").ConfigureAwait(false);
        Check(account.Name == "fixture" && account.Registration.RecoveryCode == "R00000", "Returned registration");
        string token = await api.AccessToken(account.Registration, cancel).ConfigureAwait(false);
        var logins = await api.LoginRequests(token, cancel).ConfigureAwait(false);
        Check(logins.Count == 1 && logins[0].Device == "Fixture browser", "Login details");
        await api.ResolveLogin(account, token, logins[0], false, cancel).ConfigureAwait(false);
        var confirmations = await api.CommunityRequests(account.Registration, token, cancel).ConfigureAwait(false);
        Check(confirmations.Count == 1 && confirmations[0].Title == "Synthetic trade", "Trade list");
        await api.ResolveCommunity(account.Registration, token, confirmations[0], true, cancel).ConfigureAwait(false);
        Check(fake.RequestCount == 14, "Expected request count");
        return account;
    }

    private sealed class SteamFixture : HttpMessageHandler
    {
        public const ulong SteamId = 76561198000000000;
        private const ulong Client = 4242424242;
        private readonly byte[] secret;
        private readonly RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048);
        public int RequestCount;

        public SteamFixture(byte[] secret)
        {
            this.secret = secret;
            rsa.PersistKeyInCsp = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                rsa.Dispose();
            base.Dispose(disposing);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequestCount++;
            var uri = request.RequestUri;
            Check(uri.Scheme == "https", "Always encrypted transport");
            if (uri.Host == "steamcommunity.com")
            {
                Check(request.Headers.GetValues("Cookie").Single().Contains("access-fixture"), "Community session cookie");
                byte[] stamp = { 0, 0, 0, 0, 0, 0, 0, 60, 99, 111, 110, 102 };
                using (var hmac = new HMACSHA1(secret))
                    Check(Query(uri, "k") == Convert.ToBase64String(hmac.ComputeHash(stamp)), "Trade signature");
                if (uri.AbsolutePath.EndsWith("/ajaxop", StringComparison.Ordinal))
                {
                    Check(Query(uri, "cid") == "101" && Query(uri, "op") == "allow", "Only selected trade confirmed");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"success\":true}") };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"success\":true,\"conf\":[{\"id\":\"101\",\"nonce\":\"202\",\"headline\":\"Synthetic trade\",\"summary\":[\"Details\"],\"type\":2}]}")
                };
            }

            Check(uri.Host == "api.steampowered.com", "Only Steam API hosts");
            string payload;
            if (request.Method == HttpMethod.Get)
                payload = Query(uri, "input_protobuf_encoded").Replace('-', '+').Replace('_', '/');
            else
                payload = await ReadForm(request.Content).ConfigureAwait(false);
            var input = new SteamWire.Reply(Convert.FromBase64String(payload));
            var output = new SteamWire();
            string call = uri.Segments[2].Trim('/');
            string access = Query(uri, "access_token");
            if (new[] { "RemoveAuthenticatorViaChallengeStart", "RemoveAuthenticatorViaChallengeContinue",
                "GetAuthSessionsForAccount", "GetAuthSessionInfo", "UpdateAuthSessionWithMobileConfirmation" }.Contains(call))
                Check(access == "access-fixture", "Authenticated API call");
            switch (call)
            {
                case "GetPasswordRSAPublicKey":
                    Check(request.Method == HttpMethod.Get && input.Text(1) == "fixture", "RSA request");
                    RSAParameters key = rsa.ExportParameters(false);
                    output.Text(1, Hex(key.Modulus)).Text(2, Hex(key.Exponent)).Number(3, 42);
                    break;
                case "BeginAuthSessionViaCredentials":
                    Check(input.Text(2) == "fixture" && input.Number(4) == 42
                        && Encoding.ASCII.GetString(rsa.Decrypt(Convert.FromBase64String(input.Text(3)), false)) == "test-only",
                        "Steam RSA password format");
                    output.Number(1, Client).Bytes(2, new byte[] { 9, 8, 7 }).Number(5, SteamId)
                        .Bytes(4, new SteamWire().Number(1, 3).ToArray()).Bytes(4, new SteamWire().Number(1, 4).ToArray());
                    break;
                case "UpdateAuthSessionWithSteamGuardCode":
                    Check(input.Number(1) == Client && input.Number(2) == SteamId && input.Text(3) == "P2V89",
                        "Mobile sign-in challenge");
                    break;
                case "PollAuthSessionStatus":
                    Check(input.Number(1) == Client && input.Bytes(2).SequenceEqual(new byte[] { 9, 8, 7 }), "Poll session");
                    output.Text(3, "refresh-fixture").Text(4, "access-fixture").Text(6, "fixture");
                    break;
                case "RemoveAuthenticatorViaChallengeStart":
                    break;
                case "RemoveAuthenticatorViaChallengeContinue":
                    Check(input.Text(1) == "123456" && input.Number(2) == 1, "SMS transfer challenge");
                    output.Number(1, 1).Bytes(2, new SteamWire().Bytes(1, secret).Text(3, "R00000")
                        .Text(6, "fixture").Bytes(8, secret).Fixed64(12, SteamId).ToArray());
                    break;
                case "GenerateAccessTokenForApp":
                    Check(input.Number(2) == SteamId && access == "refresh-fixture", "Refresh respects account");
                    output.Text(1, "access-fixture");
                    break;
                case "GetAuthSessionsForAccount":
                    output.Number(1, Client);
                    break;
                case "GetAuthSessionInfo":
                    output.Text(1, "192.0.2.1").Text(7, "Fixture browser").Number(8, 1).Number(12, 1);
                    break;
                case "UpdateAuthSessionWithMobileConfirmation":
                    Check(input.Number(2) == Client && input.Number(3) == SteamId && input.Number(5) == 0,
                        "Only selected login denied");
                    using (var hmac = new HMACSHA256(secret))
                    {
                        var proof = BitConverter.GetBytes((ushort)1).Concat(BitConverter.GetBytes(Client))
                            .Concat(BitConverter.GetBytes(SteamId)).ToArray();
                        Check(input.Bytes(4).SequenceEqual(hmac.ComputeHash(proof)), "Login signature");
                    }
                    break;
                case "QueryTime":
                    output.Number(1, 60);
                    break;
                default: throw new Exception("Unexpected API method " + call);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) };
            response.Headers.Add("x-eresult", "1");
            return response;
        }

        private static string Query(Uri uri, string key)
        {
            string pair = uri.Query.TrimStart('?').Split('&').FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.Ordinal));
            return pair == null ? "" : Uri.UnescapeDataString(pair.Substring(key.Length + 1));
        }

        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", ""); }
    }

    private static async Task<string> ReadForm(HttpContent content)
    {
        const string field = "input_protobuf_encoded=";
        string form = await content.ReadAsStringAsync().ConfigureAwait(false);
        Check(form.StartsWith(field, StringComparison.Ordinal), "Steam Web API form");
        return Uri.UnescapeDataString(form.Substring(field.Length));
    }

    private static void ShowOffscreen(Form window)
    {
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-10000, -10000);
        window.ShowInTaskbar = false;
        window.Show();
        Application.DoEvents();
    }

    private static void Capture(Form window, string path)
    {
        using (var image = new Bitmap(window.Width, window.Height))
        {
            window.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
            image.Save(path, ImageFormat.Png);
        }
    }

    private static T Field<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }

    private static void Check(bool result, string message)
    {
        if (!result)
            throw new Exception(message);
    }
}
