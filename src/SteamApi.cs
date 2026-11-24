using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalSteamGuard
{
    [DataContract]
    internal sealed class SteamRegistration
    {
        [DataMember] public ulong Id;
        [DataMember] public string Login;
        [DataMember] public string RefreshToken;
        [DataMember] public string RecoveryCode;
        [DataMember] public byte[] IdentitySecret;
        [DataMember] public string Device;
    }

    internal sealed class SteamApiError : Exception
    {
        public readonly int Result;
        public SteamApiError(int result) : base("Steam вернул ошибку " + result + ".") { Result = result; }
    }

    internal sealed class SteamLogin
    {
        public string Name;
        public ulong ClientId;
        public ulong SteamId;
        public byte[] RequestId;
        public TimeSpan Interval;
        public HashSet<ulong> Confirmations;
        public string AccessToken;
        public string RefreshToken;
    }

    internal sealed class LoginRequest
    {
        public ulong Id;
        public ushort Version;
        public ulong Persistence;
        public string Device;
        public string Ip;
        public string City;
        public string Country;

        public override string ToString() { return string.IsNullOrWhiteSpace(Device) ? "Запрос на вход" : Device; }
        public string Details { get { return string.Join(" · ", new[] { Ip, City, Country }.Where(x => !string.IsNullOrWhiteSpace(x))); } }
    }

    [DataContract]
    internal sealed class CommunityRequest
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "nonce")] public string Nonce { get; set; }
        [DataMember(Name = "headline")] public string Title { get; set; }
        [DataMember(Name = "summary")] public string[] Summary { get; set; }

        public override string ToString() { return string.IsNullOrWhiteSpace(Title) ? "Запрос Steam" : Title; }
        public string Details { get { return Summary == null ? "" : string.Join(Environment.NewLine, Summary); } }
    }

    internal sealed class SteamApi : IDisposable
    {
        private const string Auth = "IAuthenticationService";
        private const string Guard = "ITwoFactorService";
        private readonly HttpClient http;
        private readonly Func<string> deviceName;

        public SteamApi() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, () => "Steam Guard (Windows)") { }

        internal SteamApi(HttpMessageHandler handler, Func<string> deviceName)
        {
            this.deviceName = deviceName;
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamGuardWindows/1.0");
        }

        public void Dispose() { http.Dispose(); }

        public async Task<SteamLogin> BeginLogin(string login, byte[] password, CancellationToken cancel)
        {
            var rsa = await Call(Auth, "GetPasswordRSAPublicKey", new SteamWire().Text(1, login), null, true, cancel)
                .ConfigureAwait(false);
            string encrypted;
            using (var provider = new RSACryptoServiceProvider())
            {
                provider.PersistKeyInCsp = false;
                provider.ImportParameters(new RSAParameters { Modulus = Hex(rsa.Text(1)), Exponent = Hex(rsa.Text(2)) });
                encrypted = Convert.ToBase64String(provider.Encrypt(password, false));
            }

            string description = deviceName();
            // Steam's authenticator service issues enrolment sessions for the mobile platform.
            var device = new SteamWire().Text(1, description).Number(2, 3).Number(3, unchecked((ulong)-500)).Number(4, 528);
            var request = new SteamWire().Text(1, description).Text(2, login).Text(3, encrypted)
                .Number(4, rsa.Number(3)).Number(5, 1).Number(6, 3).Number(7, 1)
                .Text(8, "Mobile").Bytes(9, device.ToArray()).Number(11, 0).Number(12, 2);
            var reply = await Call(Auth, "BeginAuthSessionViaCredentials", request, null, false, cancel).ConfigureAwait(false);
            var result = new SteamLogin
            {
                Name = login, ClientId = reply.Number(1), RequestId = reply.Bytes(2), SteamId = reply.Number(5),
                Confirmations = new HashSet<ulong>(reply.Messages(4).Select(x => x.Number(1))),
                Interval = TimeSpan.FromSeconds(Math.Max(2, Math.Min(15, reply.Float(3))))
            };
            if (result.SteamId == 0 || result.ClientId == 0 || result.RequestId.Length == 0 || !string.IsNullOrEmpty(reply.Text(7)))
                throw new InvalidDataException("Steam не открыл сеанс. Проверь аккаунт в официальном Steam.");
            return result;
        }

        public async Task SubmitLoginCode(SteamLogin login, string code, ulong method, CancellationToken cancel)
        {
            var request = new SteamWire().Number(1, login.ClientId).Fixed64(2, login.SteamId).Text(3, code).Number(4, method);
            await Call(Auth, "UpdateAuthSessionWithSteamGuardCode", request, null, false, cancel).ConfigureAwait(false);
        }

        public async Task WaitForLogin(SteamLogin login, CancellationToken cancel)
        {
            for (int attempt = 0; attempt < 90; attempt++)
            {
                var request = new SteamWire().Number(1, login.ClientId).Bytes(2, login.RequestId);
                var reply = await Call(Auth, "PollAuthSessionStatus", request, null, false, cancel).ConfigureAwait(false);
                ulong newId = reply.Number(1);
                if (newId != 0)
                    login.ClientId = newId;
                string refresh = reply.Text(3);
                if (!string.IsNullOrEmpty(refresh))
                {
                    login.RefreshToken = refresh;
                    login.AccessToken = reply.Text(4);
                    if (string.IsNullOrEmpty(login.AccessToken))
                        login.AccessToken = await AccessToken(login.SteamId, refresh, cancel).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(reply.Text(6)))
                        login.Name = reply.Text(6);
                    return;
                }
                if (!string.IsNullOrEmpty(reply.Text(8)))
                    throw new InvalidDataException("Steam просит завершить настройку аккаунта в официальном клиенте.");
                await Task.Delay(login.Interval, cancel).ConfigureAwait(false);
            }
            throw new TimeoutException("Срок ожидания подтверждения истёк.");
        }

        public async Task StartTransfer(SteamLogin login, CancellationToken cancel)
        {
            await Call(Guard, "RemoveAuthenticatorViaChallengeStart", new SteamWire(), login.AccessToken, false, cancel)
                .ConfigureAwait(false);
        }

        public async Task<Account> FinishTransfer(SteamLogin login, string sms)
        {
            var request = new SteamWire().Text(1, sms).Number(2, 1).Number(3, 2);
            var reply = await Call(Guard, "RemoveAuthenticatorViaChallengeContinue", request, login.AccessToken, false, CancellationToken.None)
                .ConfigureAwait(false);
            var newToken = new SteamWire.Reply(reply.Bytes(2));
            byte[] secret = newToken.Bytes(1);
            byte[] identity = newToken.Bytes(8);
            string recovery = newToken.Text(3);
            if (secret.Length != 20 || identity.Length > 128
                || (newToken.Number(12) != 0 && newToken.Number(12) != login.SteamId))
                throw new InvalidDataException("Steam не прислал полный новый ключ.");

            string name = newToken.Text(6);
            if (!Account.IsValidName(name))
                name = Account.IsValidName(login.Name) ? login.Name : "Аккаунт Steam";
            return new Account(name, secret, new SteamRegistration
            {
                Id = login.SteamId, Login = login.Name, IdentitySecret = identity, RecoveryCode = recovery,
                RefreshToken = login.RefreshToken, Device = "android:" + Guid.NewGuid().ToString()
            });
        }

        public Task<string> AccessToken(SteamRegistration account, CancellationToken cancel)
        {
            return AccessToken(account.Id, account.RefreshToken, cancel);
        }

        private async Task<string> AccessToken(ulong steamId, string refresh, CancellationToken cancel)
        {
            var request = new SteamWire().Text(1, refresh).Fixed64(2, steamId).Number(3, 0);
            var reply = await Call(Auth, "GenerateAccessTokenForApp", request, refresh, false, cancel).ConfigureAwait(false);
            string token = reply.Text(1);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidDataException("Steam не обновил сеанс.");
            return token;
        }

        public async Task<List<LoginRequest>> LoginRequests(string token, CancellationToken cancel)
        {
            var list = await Call(Auth, "GetAuthSessionsForAccount", new SteamWire(), token, true, cancel).ConfigureAwait(false);
            var result = new List<LoginRequest>();
            foreach (ulong id in list.RepeatedNumbers(1).Distinct().Take(20))
            {
                try
                {
                    var info = await Call(Auth, "GetAuthSessionInfo", new SteamWire().Number(1, id), token, false, cancel).ConfigureAwait(false);
                    ulong version = info.Number(8);
                    if (version == 0 || version > ushort.MaxValue)
                        continue;
                    result.Add(new LoginRequest
                    {
                        Id = id, Version = (ushort)version, Persistence = info.Number(12),
                        Device = info.Text(7), Ip = info.Text(1), City = info.Text(3), Country = info.Text(5)
                    });
                }
                catch (SteamApiError error)
                {
                    // A request can expire between listing it and opening its details.
                    if (error.Result != 9 && error.Result != 11 && error.Result != 16)
                        throw;
                }
            }
            return result;
        }

        public async Task ResolveLogin(Account account, string token, LoginRequest login, bool approve, CancellationToken cancel)
        {
            byte[] signature;
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes))
            using (var hmac = new HMACSHA256(account.Secret))
            {
                writer.Write(login.Version);
                writer.Write(login.Id);
                writer.Write(account.Registration.Id);
                signature = hmac.ComputeHash(bytes.ToArray());
            }
            var request = new SteamWire().Number(1, login.Version).Number(2, login.Id)
                .Fixed64(3, account.Registration.Id).Bytes(4, signature).Number(5, approve ? 1UL : 0UL)
                .Number(6, login.Persistence == 1 ? 1UL : 0UL);
            await Call(Auth, "UpdateAuthSessionWithMobileConfirmation", request, token, false, cancel).ConfigureAwait(false);
        }

        [DataContract]
        private sealed class CommunityReply
        {
            [DataMember(Name = "success")] public bool Success { get; set; }
            [DataMember(Name = "needauth")] public bool NeedAuth { get; set; }
            [DataMember(Name = "needsauth")] public bool NeedsAuth { get; set; }
            [DataMember(Name = "conf")] public List<CommunityRequest> Requests { get; set; }
        }

        public async Task<List<CommunityRequest>> CommunityRequests(SteamRegistration account, string token, CancellationToken cancel)
        {
            var reply = await CommunityCall(account, token, "getlist", "", cancel).ConfigureAwait(false);
            return (reply.Requests ?? new List<CommunityRequest>()).Where(x => x != null && Digits(x.Id) && Digits(x.Nonce)).ToList();
        }

        public async Task ResolveCommunity(SteamRegistration account, string token, CommunityRequest item, bool approve, CancellationToken cancel)
        {
            string query = "&op=" + (approve ? "allow" : "cancel") + "&cid=" + Escape(item.Id) + "&ck=" + Escape(item.Nonce);
            await CommunityCall(account, token, "ajaxop", query, cancel).ConfigureAwait(false);
        }

        private async Task<CommunityReply> CommunityCall(SteamRegistration account, string token, string endpoint,
            string extra, CancellationToken cancel)
        {
            var clock = await Call(Guard, "QueryTime", new SteamWire().Number(1, (ulong)GuardCode.CurrentUnixSeconds()), null, false, cancel).ConfigureAwait(false);
            ulong utc = clock.Number(1);
            if (utc == 0)
                throw new InvalidDataException();
            byte[] stamp = new byte[12];
            for (int i = 0; i < 8; i++)
                stamp[7 - i] = (byte)(utc >> (8 * i));
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("conf"), 0, stamp, 8, 4);
            string proof;
            using (var hmac = new HMACSHA1(account.IdentitySecret))
                proof = Convert.ToBase64String(hmac.ComputeHash(stamp));

            string id = account.Id.ToString(CultureInfo.InvariantCulture);
            string query = "?p=" + Escape(account.Device) + "&a=" + id + "&k=" + Escape(proof)
                + "&t=" + utc.ToString(CultureInfo.InvariantCulture) + "&m=react&tag=conf" + extra;
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://steamcommunity.com/mobileconf/" + endpoint + query))
            {
                if (token.Any(c => !(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')))
                    throw new InvalidDataException();
                request.Headers.TryAddWithoutValidation("Cookie", "steamid=" + id + "; steamLoginSecure=" + id + "||" + token);
                request.Headers.TryAddWithoutValidation("Origin", "https://steamcommunity.com");
                request.Headers.AcceptLanguage.ParseAdd("ru");
                using (var response = await http.SendAsync(request, cancel).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException("Steam Community недоступен.");
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    using (var body = new MemoryStream(bytes, false))
                    {
                        var data = (CommunityReply)new DataContractJsonSerializer(typeof(CommunityReply)).ReadObject(body);
                        if (data == null || data.NeedAuth || data.NeedsAuth || !data.Success)
                            throw new InvalidDataException("Steam не вернул подтверждения. Возможно, сеанс истёк.");
                        return data;
                    }
                }
            }
        }

        private async Task<SteamWire.Reply> Call(string service, string method, SteamWire fields, string token,
            bool get, CancellationToken cancel)
        {
            string url = "https://api.steampowered.com/" + service + "/" + method + "/v1/";
            byte[] body = fields.ToArray();
            string payload = Convert.ToBase64String(body);
            Array.Clear(body, 0, body.Length);
            if (token != null)
                url += "?access_token=" + Escape(token);
            if (get)
                url += (token == null ? "?" : "&") + "input_protobuf_encoded=" + Escape(payload.Replace('+', '-').Replace('/', '_'));

            using (var request = new HttpRequestMessage(get ? HttpMethod.Get : HttpMethod.Post, url))
            {
                if (!get)
                {
                    var form = new MultipartFormDataContent();
                    form.Add(new StringContent(payload), "input_protobuf_encoded");
                    request.Content = form;
                }
                using (var response = await http.SendAsync(request, cancel).ConfigureAwait(false))
                {
                    IEnumerable<string> headers;
                    int result;
                    if (response.Headers.TryGetValues("x-eresult", out headers)
                        && int.TryParse(headers.FirstOrDefault(), out result) && result != 1)
                        throw new SteamApiError(result);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException("Steam недоступен (HTTP " + (int)response.StatusCode + ").");
                    return new SteamWire.Reply(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                }
            }
        }

        private static string Escape(string value) { return Uri.EscapeDataString(value); }

        private static bool Digits(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length <= 30 && value.All(c => c >= '0' && c <= '9');
        }

        private static byte[] Hex(string value)
        {
            if (value.Length == 0 || value.Length % 2 != 0 || value.Length > 1024)
                throw new InvalidDataException();
            byte[] bytes = new byte[value.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(value.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
    }
}
