using System;
using System.Threading.Tasks;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace TagSync.Services
{
    public static class AuthenticationService
    {

        public static async Task<string> GetAccessTokenAsync()
        {
            string token;

            if (Environment.GetEnvironmentVariable("MSI_ENDPOINT") == null)
            {
                string appId = Environment.GetEnvironmentVariable("appId");
                string appSecret = Environment.GetEnvironmentVariable("appSecret");
                string tenantId = Environment.GetEnvironmentVariable("tenantId");

                if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(tenantId))
                {
                    throw new Exception("Missing value for service principal. Check App Settings.");
                }

                try
                {
                    token = GetTokenServicePrincipal(appId, appSecret, tenantId);
                }
                catch (Exception ex) { throw ex; }

            }
            else
            {
                try
                {
                    var azureServiceTokenProvider = new AzureServiceTokenProvider();
                    token = await azureServiceTokenProvider.GetAccessTokenAsync("https://management.core.windows.net/");
                }
                catch (Exception ex) { throw ex; }
            }

            return token;
        }

        static string GetTokenServicePrincipal(string appId, string appSecret, string tenantId)
        {
            var authContext = new AuthenticationContext(string.Format("https://login.windows.net/{0}", tenantId));
            var credential = new ClientCredential(appId, appSecret);
            AuthenticationResult token = authContext.AcquireTokenAsync("https://management.azure.com/", credential).Result;
            return token.AccessToken;
        }

    }
}