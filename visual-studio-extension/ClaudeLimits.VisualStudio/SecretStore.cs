using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ClaudeLimits.VisualStudio
{
    /// <summary>
    /// Stores the API key, Admin key and manual OAuth token encrypted with DPAPI (current user) in
    /// %LOCALAPPDATA%\ClaudeLimits\secrets.dat. The plain JSON never touches the disk or the registry.
    /// </summary>
    internal static class SecretStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClaudeLimits.VisualStudio/secrets/v1");

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeLimits", "secrets.dat");

        public sealed class Secrets
        {
            public string ApiKey { get; set; } = "";
            public string AdminKey { get; set; } = "";
            public string ManualOAuthToken { get; set; } = "";
        }

        public static Secrets Load()
        {
            var result = new Secrets();
            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return result;
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                var json = JObject.Parse(Encoding.UTF8.GetString(plain));
                result.ApiKey = (string)json["apiKey"] ?? "";
                result.AdminKey = (string)json["adminKey"] ?? "";
                result.ManualOAuthToken = (string)json["manualOAuthToken"] ?? "";
            }
            catch (Exception)
            {
                // Unreadable (other user / corrupted): behave as if nothing was stored.
            }
            return result;
        }

        public static void Save(Secrets secrets)
        {
            var path = FilePath;
            var empty = string.IsNullOrEmpty(secrets.ApiKey) && string.IsNullOrEmpty(secrets.AdminKey) && string.IsNullOrEmpty(secrets.ManualOAuthToken);
            if (empty)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* ignore */ }
                return;
            }
            var json = new JObject
            {
                ["apiKey"] = secrets.ApiKey ?? "",
                ["adminKey"] = secrets.AdminKey ?? "",
                ["manualOAuthToken"] = secrets.ManualOAuthToken ?? "",
            };
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None)), Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            if (File.Exists(path)) File.Replace(tmp, path, null, true);
            else File.Move(tmp, path);
        }
    }
}
