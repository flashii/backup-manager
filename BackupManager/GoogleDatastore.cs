using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Json;
using Google.Apis.Util.Store;
using Newtonsoft.Json.Linq;
using System;
using System.Text;
using System.Threading.Tasks;

namespace BackupManager
{
    public sealed class GoogleDatastore : IDataStore
    {
        public Config Config { get; set; }

        public GoogleDatastore(Config config)
        {
            Config = config;
        }

        public Task<T> GetAsync<T>(string key)
        {
            TaskCompletionSource<T> tcs = new TaskCompletionSource<T>();

            TokenResponse tr = new TokenResponse {
                AccessToken = Config.GoogleAccessToken,
                TokenType = Config.GoogleTokenType,
                ExpiresInSeconds = Config.GoogleTokenExpires,
                RefreshToken = Config.GoogleRefreshToken,
                IssuedUtc = Config.GoogleTokenIssued == null
                    ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    : DateTime.Parse(Config.GoogleTokenIssued)
            };
            
            tcs.SetResult((T)(object)tr); // fuck it
            return tcs.Task;
        }

        public Task DeleteAsync<T>(string key)
            => ClearAsync();

        public Task ClearAsync()
        {
            Config.GoogleAccessToken = null;
            Config.GoogleTokenType = null;
            Config.GoogleTokenExpires = null;
            Config.GoogleTokenIssued = null;
            Config.GoogleTokenIssued = null;
            return Task.Delay(0);
        }

        public Task StoreAsync<T>(string key, T value)
        {
            JObject json = JObject.Parse(NewtonsoftJsonSerializer.Instance.Serialize(value));

            JToken accessToken = json.SelectToken(@"access_token");
            if (accessToken != null)
                Config.GoogleAccessToken = (string)accessToken;

            JToken tokenType = json.SelectToken(@"token_type");
            if (tokenType != null)
                Config.GoogleTokenType = (string)tokenType;

            JToken expiresIn = json.SelectToken(@"expires_in");
            if (expiresIn != null)
                Config.GoogleTokenExpires = (long?)expiresIn;

            JToken refreshToken = json.SelectToken(@"refresh_token");
            if (refreshToken != null)
                Config.GoogleRefreshToken = (string)refreshToken;

            JToken tokenIssued = json.SelectToken(@"Issued");
            if (refreshToken != null)
                Config.GoogleTokenIssued = (string)tokenIssued;

            return Task.Delay(0);
        }
    }
}
