using AzureManagement.Models;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json.Linq;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.ResourceGroup;
using Microsoft.Azure.Management.ResourceManager.Fluent.GenericResource;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.ResourceManager.Fluent.Models;
using Microsoft.Rest;
using System.Collections.Generic;
using System;
using System.Linq;
using Microsoft.WindowsAzure.Storage.Table;
using System.Threading.Tasks;
using Microsoft.Rest.Azure.OData;

namespace AzureManagement.Function
{
    public static class AddTagsOld
    {
        [FunctionName("AddTagsOld")]
        public static async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequest req,
            [Table("RequiredTags")] CloudTable requiredTags,
            [Table("InvalidTagResources")] CloudTable invalidResources,
            TraceWriter log)
        {

            string resourceGroupName = null;
            string resourceId = null;
            string resourceProvider = null;
            string subscriptionId = null;
            string resourceTypeShortName = null;

            string requestBody = new StreamReader(req.Body).ReadToEnd();
            JObject data;
            try
            {
                data = JObject.Parse(requestBody);
                resourceGroupName = data.SelectToken("data.context.activityLog.resourceGroupName").Value<string>();
                resourceId = data.SelectToken("data.context.activityLog.resourceId").Value<string>();
                resourceProvider = data.SelectToken("data.context.activityLog.resourceProviderName.value").Value<string>();
                subscriptionId = data.SelectToken("data.context.activityLog.subscriptionId").Value<string>();
            }
            catch(Exception ex)
            {
                log.Error("Json parse error: " + ex.Message);
                return (ActionResult) new BadRequestObjectResult("Invalid content in body");
            }

            log.Info(message: "Resource ID is: " + resourceId);

            // If the resource provider is listed in the invalid resources table, terminate
            // TODO: Consider removing this. What really matters is the resource 'type' (see later)
            var invalidTagResourcesQuery = await invalidResources.ExecuteQuerySegmentedAsync(new TableQuery<InvalidTagResource>(), null);
            InvalidTagResource matchingInvalidResource = invalidTagResourcesQuery.Results.Where(x => x.Type == resourceId).FirstOrDefault();
            if (matchingInvalidResource != null)
            {
                log.Error("Resource provider is listed as invalid for tags");
                return (ActionResult)new OkObjectResult("OK");
            }

            var client = new ResourceManagementClient(GetAccessToken());
            client.SubscriptionId = subscriptionId;

            // Get the Resource Group and its tags
            ResourceGroupInner rg = await client.ResourceGroups.GetAsync(resourceGroupName);

            if (rg.Tags == null)
            {
                log.Warning("Resource group does not have tags. Exiting.");
                return (ActionResult)new OkObjectResult("OK");
            }

            var resource = client.Resources.ListByResourceGroupAsync(resourceGroupName, new ODataQuery<GenericResourceFilterInner>(x => x.ResourceType == "Microsoft.Batch/batchAccounts")).Result;

            // client.Resources.ListByResourceGroup(rg.Name)

            ProviderInner provider = await client.Providers.GetAsync(resourceProvider);

            var matchingType = provider.ResourceTypes.Where(x => x.ResourceType == resourceTypeShortName).FirstOrDefault();
            string apiVersion = matchingType.ApiVersions[0];
            // string apiVersion = provider.ResourceTypes[0].ApiVersions[0];  // Get latest API version for the resource type

            GenericResourceInner targetItem = null;
            try
            {
                targetItem = await client.Resources.GetByIdAsync(resourceId, apiVersion);
            }
            catch(Exception ex)
            {
                log.Error(ex.Message);
                return (ActionResult) new BadRequestObjectResult("Failed to get object from ARM");
            }
                 
            if (targetItem.Type == null)
            {
                log.Error("Resource type is listed as invalid for tags.");
                return (ActionResult)new OkObjectResult("OK");
            }

            log.Info("Item type is: " + targetItem.Type);

            // See if the object type is invalid for tags. If so, terminate
            var invalidForTagsMatch = invalidTagResourcesQuery.Results.Where(x => x.Type == targetItem.Type).FirstOrDefault();
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
                if (rg.Tags.ContainsKey(requiredTagItem.Name))
                {
                    chargebackTags.Add(requiredTagItem.Name, rg.Tags[requiredTagItem.Name]);
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
                await client.Resources.CreateOrUpdateByIdAsync(resourceId, apiVersion, targetItem );
            }
            catch(Exception ex)
            {
                log.Error(ex.Message);
                InvalidTagResource invalidItem = new InvalidTagResource { 
                    Type = targetItem.Type, 
                    Message = ex.Message,
                    RowKey = Guid.NewGuid().ToString(),
                    PartitionKey = "test" };

                TableOperation insertOperation = TableOperation.InsertOrMerge(invalidItem);
                string result = await WriteInvalidTagResource(invalidResources, insertOperation);
                return (ActionResult) new BadRequestObjectResult("Failed to update object: " + ex.Message);
            }

            log.Info("Tags added");
            return (ActionResult) new OkObjectResult("Accepted");
        }

        static async Task<string> WriteInvalidTagResource(CloudTable table, TableOperation op)
        {
            try
            {
                await table.ExecuteAsync(op);
                return "";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

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