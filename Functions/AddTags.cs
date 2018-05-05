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
using System.Threading.Tasks;

namespace AzureManagement.Function
{
    public static class AddTags
    {
        [FunctionName("AddTags")]
        public static async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequest req,
            [Table("RequiredTags")] CloudTable requiredTags,
            [Table("InvalidTagResources")] CloudTable invalidResources,
            TraceWriter log)
        {
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

            string resourceGroupName = data.SelectToken("data.context.activityLog.resourceGroupName").Value<string>();
            string resourceId = data.SelectToken("data.context.activityLog.resourceId").Value<string>();
            string resourceProvider = data.SelectToken("data.context.activityLog.resourceProviderName.value").Value<string>();
            string subscriptionId = data.SelectToken("data.context.activityLog.subscriptionId").Value<string>();
 
            if (string.IsNullOrEmpty(resourceGroupName))
            {
                log.Error("Failed to parse resourceGroupName in JSON");
                return (ActionResult) new BadRequestObjectResult("Invalid content in body");
            }    

            log.Info(message: "Resource ID is: " + resourceId);
            log.Info(message: "Resource provider is: " + resourceProvider);

            // If the resource provider is listed in the invalid resources table, terminate
            // TODO: Consider removing this. What really matters is the resource 'type' (see later)
            var invalidTagResourcesQuery = await invalidResources.ExecuteQuerySegmentedAsync(new TableQuery<InvalidTagResource>(), null);
            InvalidTagResource matchingInvalidResource = invalidTagResourcesQuery.Results.Where(x => x.Id == resourceId).FirstOrDefault();
            if (matchingInvalidResource != null)
            {
                log.Error("Resource provider is listed as invalid for tags");
                return (ActionResult)new OkObjectResult("OK");
            }

            var client = new ResourceManagementClient(GetAccessToken());
            client.SubscriptionId = subscriptionId;

            // Get the Resource Group and its tags
            ResourceGroup rg = client.ResourceGroups.Get(resourceGroupName);
            var rgTags = rg.Tags;

            if (rgTags == null)
            {
                log.Warning("Resource group does not have tags. Exiting.");
                return (ActionResult)new OkObjectResult("OK");
            }

            var provider = client.Providers.Get(resourceProvider);
            string apiVersion = provider.ResourceTypes[0].ApiVersions[0];  // Get latest API version for the resource type
            var targetItem = client.Resources.GetById(resourceId, apiVersion);

            if (targetItem.Type == null)
            {
                log.Error("Resource type is listed as invalid for tags.");
                return (ActionResult)new OkObjectResult("OK");
            }

            log.Info("Item type is: " + targetItem.Type);

            // See if the object type is invalid for tags. If so, terminate
            var invalidForTagsMatch = invalidTagResourcesQuery.Results.Where(x => x.Id == targetItem.Type).FirstOrDefault();
            if(invalidForTagsMatch != null)
            {
                log.Error("Resource type is listed as invalid for tags");
                return (ActionResult)new OkObjectResult("OK");
            }

            // Apply tags
            var requiredTagsQuery = await requiredTags.ExecuteQuerySegmentedAsync(new TableQuery<RequiredTag>(), null);

            Dictionary<string, string> chargebackTags = new Dictionary<string, string>();
            foreach (RequiredTag requiredTagItem in requiredTagsQuery.Results)
            {
                if (rgTags.ContainsKey(requiredTagItem.Name))
                {
                    chargebackTags.Add(requiredTagItem.Name, rgTags[requiredTagItem.Name]);
                }
            }

            var targetItemTags = new Dictionary<string, string>();
            if (targetItem.Tags != null)
            {
                targetItemTags = (Dictionary<string, string>) targetItem.Tags;
            }

            foreach(var chargebackTag in chargebackTags)
            {
                // TODO: only update with new key if it doesn't exist
                targetItemTags.Add(chargebackTag.Key, chargebackTag.Value);
            }

            targetItem.Tags = targetItemTags;

            try
            {
                client.Resources.CreateOrUpdateById(resourceId, apiVersion, targetItem );
            }
            catch(Exception ex)
            {
                log.Error(ex.Message);
                InvalidTagResource invalidItem = new InvalidTagResource { 
                    Id = targetItem.Type, 
                    Message = ex.Message,
                    RowKey = Guid.NewGuid().ToString(),
                    PartitionKey = "test" };

                // TODO: re-factor to handle write error
                TableOperation insertOperation = TableOperation.InsertOrMerge(invalidItem);
                var result = await invalidResources.ExecuteAsync(insertOperation);

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