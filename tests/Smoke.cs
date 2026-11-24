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

            using (var dialog = new ConnectionDialog(api, id => false, () => { }, account => account))
            {
                ShowOffscreen(dialog);
                Check(Field<TextBox>(dialog, "second").UseSystemPasswordChar, "Password is masked");
                Capture(dialog, Path.Combine(isolated, "connect.png"));
            }
        }
        Console.WriteLine("Protocol, Steam transfer and approvals with fixtures, DPAPI, and Windows UI checks passed.");
        Console.WriteLine(isolated);
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
                payload = await ((MultipartFormDataContent)request.Content).Single().ReadAsStringAsync().ConfigureAwait(false);
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
