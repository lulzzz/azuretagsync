using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json.Linq;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.ResourceManager.Models;
using Microsoft.Rest;
using System.Collections.Generic;
using System;

namespace AzureManagement.Function
{
    public static class AddCostCenterTags
    {
        [FunctionName("AddCostCenterTags")]
        public static IActionResult Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequest req, TraceWriter log)
        {
            const string API_VERSION = "2017-05-01";
            log.Info("C# HTTP trigger function processed a request.");

            List<string> targetTags = new List<string> { "costCenter", "businessUnit" };

            string requestBody = new StreamReader(req.Body).ReadToEnd();
            JObject data = JObject.Parse(requestBody);
            string resourceGroupName = (string) data["resourceGroupName"];
            string resourceId = (string) data["resourceId"];
            string resourceType = (string) data["resourceProviderName"]["value"];
            string subscriptionId = (string) data["subscriptionId"];

            // TODO: Check resource type. Terminate if type does not support tags

            var client = new ResourceManagementClient(GetAccessToken());
        
            client.SubscriptionId = subscriptionId;
            ResourceGroup rg = client.ResourceGroups.Get(resourceGroupName);
            var rgTags = rg.Tags;

            Dictionary<string, string> chargebackTags = new Dictionary<string, string>();

            foreach(var targetTagKey in targetTags)
            {
                if (rgTags.ContainsKey(targetTagKey))
                {
                    chargebackTags.Add(targetTagKey, rgTags[targetTagKey]);
                }
            }

            // Write the tag to the audit object
            var targetItem = client.Resources.GetById(resourceId, "2017-05-01");
            var targetItemTags = targetItem.Tags;

            foreach(var chargebackTag in chargebackTags)
            {
                targetItemTags.Add(chargebackTag.Key, chargebackTag.Value);
            }

            try
            {
                client.Resources.CreateOrUpdateById(resourceId, "2017-05-01", new GenericResource { Tags = targetItemTags } );
            }
            catch(Exception ex)
            {
                log.Error(ex.Message);
                new BadRequestObjectResult("Failed to update object: " + ex.Message);
            }

            return data != null
                ? (ActionResult)new OkObjectResult("Accepted") : new BadRequestObjectResult("Invalid content in body");
        }

        static TokenCredentials GetAccessToken()
        {
            string applicationId = "";
            string password = "";
            string tenantId = "";

            var authContext = new AuthenticationContext(string.Format("https://login.windows.net/{0}", tenantId));
            var credential = new ClientCredential(applicationId, password);
            AuthenticationResult token = authContext.AcquireTokenAsync("https://management.core.windows.net/", credential).Result;
            return new TokenCredentials(token.AccessToken);
        }

    }
}