
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;

namespace AzureManagement.Services
{
    public static class AuthenticationService
    {
        public static TokenCredentials GetAccessToken(string appId, string appSecret, string tenantId)
        {
            var authContext = new AuthenticationContext(string.Format("https://login.windows.net/{0}", tenantId));
            var credential = new ClientCredential(appId, appSecret);
            AuthenticationResult token = authContext.AcquireTokenAsync("https://management.core.windows.net/", credential).Result;
            return new TokenCredentials(token.AccessToken);
        }

    }
}