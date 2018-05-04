using AzureManagement.Models;
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
using System.Linq;
using Microsoft.WindowsAzure.Storage.Table;

namespace AzureManagement.Function
{
    public static class AddTags
    {
        [FunctionName("AddTags")]
        public static IActionResult Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequest req,
            [Table("RequiredTags")] CloudTable requiredTags,
            [Table("InvalidTagResources")] CloudTable invalidResources,
            TraceWriter log)
        {
            const string API_VERSION = "2017-05-01";
            log.Info("C# HTTP trigger function processed a request.");

            // List<string> targetTags = new List<string> { "costCenter", "businessUnit" };

            // Parse the incoming event data
            string requestBody = new StreamReader(req.Body).ReadToEnd();

            JObject data;
            try
            {
                data = JObject.Parse(requestBody);
            }
            catch(Exception ex)
            {
                log.Error("Json parse error: " + ex.Message);
                return (ActionResult) new BadRequestObjectResult("Invalid content in body");
            }

            string resourceGroupName = (string) data["resourceGroupName"];
            string resourceId = (string) data["resourceId"];
            string resourceType = (string) data["resourceProviderName"]["value"];
            string subscriptionId = (string) data["subscriptionId"];

            log.Info(message: "Resource type is: " + resourceType);

            // If the resource type is listed in the invalid resources table, terminate
            var invalidTagResourcesQuery = invalidResources.ExecuteQuerySegmentedAsync(new TableQuery<InvalidTagResource>(), null);
            InvalidTagResource matchingInvalidResource = invalidTagResourcesQuery.Result.Where(x => x.Id == resourceId).FirstOrDefault();
            if (matchingInvalidResource != null)
            {
                log.Info("Resource is listed as invalid for tags");
                return (ActionResult)new OkObjectResult("OK");
            }

            // Get the Resource Group and its tags
            var client = new ResourceManagementClient(GetAccessToken());
            client.SubscriptionId = subscriptionId;
            ResourceGroup rg = client.ResourceGroups.Get(resourceGroupName);
            var rgTags = rg.Tags;

            var requiredTagsQuery = requiredTags.ExecuteQuerySegmentedAsync(new TableQuery<RequiredTag>(), null);

            Dictionary<string, string> chargebackTags = new Dictionary<string, string>();
            foreach (RequiredTag requiredTagItem in requiredTagsQuery.Result)
            {
                if (rgTags.ContainsKey(requiredTagItem.Name))
                {
                    chargebackTags.Add(requiredTagItem.Name, rgTags[requiredTagItem.Name]);
                }
            }

            // Write the tag to the audit object
            var targetItem = client.Resources.GetById(resourceId, API_VERSION);
            var targetItemTags = targetItem.Tags;

            foreach(var chargebackTag in chargebackTags)
            {
                targetItemTags.Add(chargebackTag.Key, chargebackTag.Value);
            }

            try
            {
                client.Resources.CreateOrUpdateById(resourceId, API_VERSION, new GenericResource { Tags = targetItemTags } );
            }
            catch(Exception ex)
            {
                log.Error(ex.Message);
                new BadRequestObjectResult("Failed to update object: " + ex.Message);
            }

            return (ActionResult) new OkObjectResult("Accepted");
        }

        static TokenCredentials GetAccessToken()
        {
            string appId = Environment.GetEnvironmentVariable("appId");
            string appSecret = Environment.GetEnvironmentVariable("appSecret");
            string tenantId = Environment.GetEnvironmentVariable("tenantId");
            var authContext = new AuthenticationContext(string.Format("https://login.windows.net/{0}", tenantId));
            var credential = new ClientCredential(appId, appSecret);
            AuthenticationResult token = authContext.AcquireTokenAsync("https://management.core.windows.net/", credential).Result;
            return new TokenCredentials(token.AccessToken);
        }
    }
}