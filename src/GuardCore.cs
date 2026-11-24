using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Xml;

namespace LocalSteamGuard
{
    internal static class GuardCode
    {
        public const int Period = 30;
        private const string Alphabet = "23456789BCDFGHJKMNPQRTVWXY";
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long CurrentUnixSeconds()
        {
            return Math.Max(0, (DateTime.UtcNow.Ticks - Epoch.Ticks) / TimeSpan.TicksPerSecond);
        }

        public static byte[] ReadSecret(string input)
        {
            if (string.IsNullOrWhiteSpace(input) || input.Length > 256)
                throw new FormatException();

            string encoded = new string(input.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (encoded.Length == 27)
                encoded += "=";

            byte[] secret = Convert.FromBase64String(encoded);
            if (secret.Length == 20)
                return secret;

            Array.Clear(secret, 0, secret.Length);
            throw new FormatException();
        }

        public static string Generate(byte[] secret, long unixSeconds)
        {
            byte[] counter = new byte[8];
            long period = unixSeconds / Period;
            for (int index = counter.Length - 1; index >= 0; index--)
            {
                counter[index] = (byte)(period & 255);
                period >>= 8;
            }

            using (var hmac = new HMACSHA1(secret))
            {
                byte[] digest = hmac.ComputeHash(counter);
                int offset = digest[19] & 15;
                int value = ((digest[offset] & 127) << 24) | (digest[offset + 1] << 16)
                    | (digest[offset + 2] << 8) | digest[offset + 3];

                char[] result = new char[5];
                for (int index = 0; index < result.Length; index++)
                {
                    result[index] = Alphabet[value % Alphabet.Length];
                    value /= Alphabet.Length;
                }

                Array.Clear(digest, 0, digest.Length);
                return new string(result);
            }
        }
    }

    [DataContract]
    internal sealed class Account
    {
        public const int NameLimit = 80;

        [DataMember(IsRequired = true)]
        public string Name { get; private set; }

        [DataMember(IsRequired = true)]
        public byte[] Secret { get; private set; }

        [DataMember(EmitDefaultValue = false)]
        public SteamRegistration Registration { get; private set; }

        public Account(string name, byte[] secret, SteamRegistration registration = null)
        {
            Name = name;
            Secret = secret;
            Registration = registration;
        }

        public static bool IsValidName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name.Length <= NameLimit
                && name == name.Trim() && !name.Any(char.IsControl);
        }

        public override string ToString()
        {
            return Name;
        }
    }

    internal static class AccountImport
    {
        [DataContract]
        private sealed class ExportedAccount
        {
            [DataMember(Name = "account_name")]
            public string Name { get; set; }

            [DataMember(Name = "shared_secret", IsRequired = true)]
            public string Secret { get; set; }
        }

        public static Account Read(string path)
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (file.Length > 1024 * 1024)
                        throw new InvalidDataException("Файл больше 1 МБ.");

                    // The framework's JSON reader expects the stream without a UTF-8 BOM.
                    if (file.ReadByte() != 0xEF || file.ReadByte() != 0xBB || file.ReadByte() != 0xBF)
                        file.Position = 0;

                    var parser = new DataContractJsonSerializer(typeof(ExportedAccount));
                    var exported = (ExportedAccount)parser.ReadObject(file);
                    if (exported == null)
                        throw new FormatException();

                    string name = (exported.Name ?? "").Trim();
                    if (!Account.IsValidName(name))
                        name = Path.GetFileNameWithoutExtension(path).Trim();
                    if (!Account.IsValidName(name))
                        name = "Аккаунт Steam";

                    return new Account(name, GuardCode.ReadSecret(exported.Secret));
                }
            }
            catch (Exception error)
            {
                if (!(error is SerializationException || error is XmlException || error is FormatException))
                    throw;

                throw new InvalidDataException("Нужен незашифрованный .maFile с корректным shared_secret.", error);
            }
        }
    }

    internal sealed class Vault : IDisposable
    {
        private const int MaxBytes = 8 * 1024 * 1024;
        private readonly FileStream instanceLock;
        private readonly string vaultFile;
        private readonly string pendingFile;
        private readonly DataContractJsonSerializer serializer =
            new DataContractJsonSerializer(typeof(VaultContents));

        [DataContract]
        private sealed class VaultContents
        {
            [DataMember(IsRequired = true)]
            public int Version { get; set; }

            [DataMember(IsRequired = true)]
            public List<Account> Accounts { get; set; }
        }

        public Vault(string folder)
        {
            Directory.CreateDirectory(folder);
            vaultFile = Path.Combine(folder, "accounts.dat");
            pendingFile = Path.Combine(folder, "accounts.pending");
            try
            {
                instanceLock = new FileStream(Path.Combine(folder, "instance.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error)
            {
                int code = error.HResult & 0xffff;
                if (code != 32 && code != 33)
                    throw;

                throw new InvalidOperationException("Приложение уже открыто в этом профиле Windows.");
            }
        }

        public List<Account> Load()
        {
            try
            {
                using (var file = new FileStream(vaultFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (file.Length < 1 || file.Length > MaxBytes)
                        throw new InvalidDataException();

                    byte[] encrypted;
                    using (var reader = new BinaryReader(file))
                    {
                        encrypted = reader.ReadBytes((int)file.Length);
                    }

                    byte[] plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    try
                    {
                        using (var buffer = new MemoryStream(plaintext, false))
                        {
                            var contents = (VaultContents)serializer.ReadObject(buffer);
                            if (contents == null || contents.Version != 1 || contents.Accounts == null
                                || contents.Accounts.Any(account => account == null
                                    || !Account.IsValidName(account.Name)
                                    || account.Secret == null || account.Secret.Length != 20
                                    || (account.Registration != null
                                        && (account.Registration.Id == 0
                                            || string.IsNullOrWhiteSpace(account.Registration.RefreshToken)
                                            || string.IsNullOrWhiteSpace(account.Registration.Device)
                                            || account.Registration.IdentitySecret == null
                                            || account.Registration.IdentitySecret.Length > 128))))
                                throw new InvalidDataException();

                            return contents.Accounts;
                        }
                    }
                    finally
                    {
                        Array.Clear(plaintext, 0, plaintext.Length);
                    }
                }
            }
            catch (FileNotFoundException)
            {
                return new List<Account>();
            }
            catch (Exception error)
            {
                if (!(error is CryptographicException || error is SerializationException
                    || error is XmlException || error is InvalidDataException))
                    throw;

                throw new InvalidDataException("Хранилище не удалось прочитать в этом профиле Windows.", error);
            }
        }

        public void Save(List<Account> accounts)
        {
            byte[] encrypted;
            using (var buffer = new MemoryStream())
            {
                try
                {
                    serializer.WriteObject(buffer, new VaultContents { Version = 1, Accounts = accounts });
                    byte[] plaintext = buffer.ToArray();
                    try
                    {
                        encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
                    }
                    finally
                    {
                        Array.Clear(plaintext, 0, plaintext.Length);
                    }
                }
                finally
                {
                    Array.Clear(buffer.GetBuffer(), 0, (int)buffer.Length);
                }
            }

            if (encrypted.Length > MaxBytes)
                throw new InvalidDataException();

            try
            {
                using (var file = new FileStream(pendingFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(encrypted, 0, encrypted.Length);
                    file.Flush(true);
                }

                if (File.Exists(vaultFile))
                    File.Replace(pendingFile, vaultFile, null);
                else
                    File.Move(pendingFile, vaultFile);
            }
            finally
            {
                try { File.Delete(pendingFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public void Dispose()
        {
            instanceLock.Dispose();
        }
    }
}
